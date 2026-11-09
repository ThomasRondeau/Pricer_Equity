// Monte Carlo batches: the twins of BlackScholesMonteCarloEngine.RunBatch and of the LocalVolBatch and
// HestonBatch of PathSimulationEngine. Random numbers, bridge and event order are the managed ones, so a
// native run reproduces a managed run up to the rounding of exp and log.
#include "payoff_vm.hpp"

namespace pc {
namespace {

enum Stat { PV, PATHWISE_DELTA, PATHWISE_VEGA, MIXED_GAMMA, SCORE_DELTA, SCORE_GAMMA, SCORE_VEGA };
const int DELTA = 0;
const int VEGA = 1;

int check_program(const pc_program* program, std::vector<Event>& events) {
    PC_REQUIRE(program != nullptr && program->code != nullptr, "Missing payoff program.");
    PC_REQUIRE(program->assets >= 1 && program->dates >= 1 && program->slots >= 0, "Invalid program sizes.");
    PC_REQUIRE(program->slots == 0 || program->initial_slots != nullptr, "Missing initial slots.");
    PC_REQUIRE(decode(*program, events), "Malformed payoff program.");
    PC_REQUIRE(!events.empty(), "The payoff program has no event.");
    return PC_OK;
}

int check_batches(const pc_batches* batches) {
    PC_REQUIRE(batches != nullptr && batches->batch_seeds != nullptr, "Missing batch description.");
    PC_REQUIRE(batches->paths >= 1 && batches->batches >= 1, "Paths and batches must be positive.");
    return PC_OK;
}

void black_scholes_batch(
    const pc_program& program, const std::vector<Event>& events, const pc_sobol* sobol, const pc_black_scholes& s,
    int n, uint64_t seed, double* stats) {
    int m = program.dates;
    SplitMix random(seed);
    std::vector<double> uniforms(n), normals(n), brownian(static_cast<size_t>(m) * n);
    for (int dimension = 0; dimension < s.bridge_length; dimension++) {
        normals_for(sobol, dimension, random, uniforms.data(), normals.data(), n);
        bridge_apply(s.bridge[dimension], brownian.data(), n, normals.data());
    }

    int tangents = s.want_tangents ? 2 : 0;
    PayoffVm vm(program, n, tangents, s.smoothing);
    double* spot = vm.spot(0);

    int current = -1;
    for (const Event& e : events) {
        if (e.date != current) {
            current = e.date;
            const double* w = brownian.data() + static_cast<size_t>(current) * n;
            double t = s.times[current];
            double forward = s.forwards[current];
            double drift = -0.5 * s.vol * s.vol * t;
            for (int i = 0; i < n; i++) spot[i] = forward * std::exp(s.vol * w[i] + drift);
            if (tangents > 0) {
                double* delta = vm.spot_tangent(0, DELTA);
                double* vega = vm.spot_tangent(0, VEGA);
                for (int i = 0; i < n; i++) {
                    delta[i] = spot[i] / s.spot;
                    vega[i] = spot[i] * (w[i] - s.vol * t);
                }
            }
        }
        vm.run(e, s.discounts[e.date]);
    }

    for (int k = 0; k < PC_BS_STATS; k++) stats[k] = 0;
    const double* pv = vm.cash();
    stats[PV] = mean(pv, n);

    if (s.want_tangents) {
        const double* first = brownian.data();
        double root = std::sqrt(s.times[0]);
        double score = 1 / (s.spot * s.vol * root * root);
        const double* delta = vm.cash_tangent(DELTA);
        double crossed = 0;
        for (int i = 0; i < n; i++) crossed += delta[i] * first[i];
        double mean_delta = mean(delta, n);
        stats[PATHWISE_DELTA] = mean_delta;
        stats[PATHWISE_VEGA] = mean(vm.cash_tangent(VEGA), n);
        // With S proportional to S0, gamma = E[pathwise delta * delta score] - delta / S0.
        stats[MIXED_GAMMA] = crossed / n * score - mean_delta / s.spot;
    }

    if (s.want_scores) {
        double t1 = s.times[0];
        double root = std::sqrt(t1);
        double vol = s.vol;
        double delta = 0, gamma = 0, vega = 0;
        for (int i = 0; i < n; i++) {
            double z = brownian[i] / root;
            delta += pv[i] * z;
            gamma += pv[i] * (z * z - z * vol * root - 1);
        }
        std::vector<double> weights(n, 0.0);
        for (int step = 0; step < m; step++) {
            double previous = step == 0 ? 0 : s.times[step - 1];
            double dt = std::sqrt(s.times[step] - previous);
            const double* end = brownian.data() + static_cast<size_t>(step) * n;
            const double* start = step == 0 ? nullptr : brownian.data() + static_cast<size_t>(step - 1) * n;
            for (int i = 0; i < n; i++) {
                double z = (end[i] - (start != nullptr ? start[i] : 0)) / dt;
                weights[i] += (z * z - 1) / vol - z * dt;
            }
        }
        for (int i = 0; i < n; i++) vega += pv[i] * weights[i];
        stats[SCORE_DELTA] = delta / n / (s.spot * vol * root);
        stats[SCORE_GAMMA] = gamma / n / (s.spot * s.spot * vol * vol * t1);
        stats[SCORE_VEGA] = vega / n;
    }
}

/// One stepped batch: the process advances, and the payoff runs at each event date.
template <typename Process>
double stepped_batch(const pc_program& program, const std::vector<Event>& events, const pc_schedule& schedule, int n, Process& process) {
    PayoffVm vm(program, n, 0, 0);
    size_t next = 0;
    int date = 0;
    for (int step = 0; step < schedule.steps; step++) {
        process.advance(step);
        if (schedule.step_of_date[date] != step) continue;
        for (int asset = 0; asset < program.assets; asset++) {
            process.fill_spot(asset, schedule.forwards[static_cast<size_t>(date) * program.assets + asset], vm.spot(asset));
        }
        for (; next < events.size() && events[next].date == date; next++) vm.run(events[next], schedule.discounts[date]);
        date++;
        if (date == program.dates) break;
    }
    return mean(vm.cash(), n);
}

/// Euler on y = ln(S / F) with a common factor for the correlation.
class LocalVolProcess {
public:
    LocalVolProcess(const pc_local_vol& model, const pc_schedule& schedule, const pc_sobol* sobol, int assets, int n, uint64_t seed)
        : model_(model), schedule_(schedule), assets_(assets), n_(n),
          factors_(assets == 1 ? 1 : assets + 1),
          common_(std::sqrt(model.correlation)), own_(std::sqrt(1 - model.correlation)),
          log_(static_cast<size_t>(assets) * n, 0.0),
          brownian_(static_cast<size_t>(factors_) * schedule.steps * n, 0.0) {
        SplitMix random(seed);
        std::vector<double> uniforms(n), normals(n);
        for (int rank = 0; rank < model.bridge_length; rank++) {
            for (int f = 0; f < factors_; f++) {
                normals_for(sobol, rank * factors_ + f, random, uniforms.data(), normals.data(), n);
                bridge_apply(model.bridge[rank], factor(f), n, normals.data());
            }
        }
    }

    void advance(int step) {
        double dt = schedule_.times[step] - (step == 0 ? 0 : schedule_.times[step - 1]);
        bool single = assets_ == 1;
        const double* common_end = factor(factors_ - 1) + static_cast<size_t>(step) * n_;
        const double* common_start = step == 0 ? nullptr : common_end - n_;
        for (int a = 0; a < assets_; a++) {
            const double* row = model_.rows + (static_cast<size_t>(a) * schedule_.steps + step) * model_.points;
            double low = model_.lows[a];
            double width = model_.step_sizes[a];
            int last = model_.points - 1;
            double* y = log_.data() + static_cast<size_t>(a) * n_;
            const double* end = factor(a) + static_cast<size_t>(step) * n_;
            const double* start = step == 0 ? nullptr : end - n_;
            for (int i = 0; i < n_; i++) {
                double own = end[i] - (start != nullptr ? start[i] : 0);
                double move = single ? own : common_ * (common_end[i] - (common_start != nullptr ? common_start[i] : 0)) + own_ * own;
                // LocalVolGrid.Lookup: linear between nodes, flat outside.
                double position = (y[i] - low) / width;
                double vol;
                if (position <= 0) {
                    vol = row[0];
                } else if (position >= last) {
                    vol = row[last];
                } else {
                    int node = static_cast<int>(position);
                    double weight = position - node;
                    vol = row[node] + weight * (row[node + 1] - row[node]);
                }
                y[i] += vol * move - 0.5 * vol * vol * dt;
            }
        }
    }

    void fill_spot(int asset, double forward, double* spot) const {
        const double* y = log_.data() + static_cast<size_t>(asset) * n_;
        for (int i = 0; i < n_; i++) spot[i] = forward * std::exp(y[i]);
    }

private:
    double* factor(int f) { return brownian_.data() + static_cast<size_t>(f) * schedule_.steps * n_; }

    const pc_local_vol& model_;
    const pc_schedule& schedule_;
    int assets_;
    int n_;
    int factors_;
    double common_;
    double own_;
    std::vector<double> log_;
    std::vector<double> brownian_;
};

/// Andersen's quadratic-exponential scheme.
class HestonProcess {
public:
    HestonProcess(const pc_heston& p, const pc_schedule& schedule, int n, uint64_t seed)
        : p_(p), schedule_(schedule), y_(n, 0.0), v_(n, p.v0), random_(seed) {}

    void advance(int step) {
        const double switch_level = 1.5;
        double dt = schedule_.times[step] - (step == 0 ? 0 : schedule_.times[step - 1]);
        double kappa = p_.kappa, theta = p_.theta, xi = p_.xi, rho = p_.rho;
        double decay = std::exp(-kappa * dt);
        double c1 = xi * xi * decay * (1 - decay) / kappa;
        double c2 = theta * xi * xi * (1 - decay) * (1 - decay) / (2 * kappa);
        double k0 = -rho * kappa * theta / xi * dt;
        double k1 = 0.5 * dt * (kappa * rho / xi - 0.5) - rho / xi;
        double k2 = 0.5 * dt * (kappa * rho / xi - 0.5) + rho / xi;
        double k3 = 0.5 * dt * (1 - rho * rho);

        for (size_t i = 0; i < y_.size(); i++) {
            double v = v_[i];
            double mean = theta + (v - theta) * decay;
            double variance = v * c1 + c2;
            double psi = variance / (mean * mean);
            double u = random_.uniform();
            double next;
            if (psi <= switch_level) {
                double inverse = 2 / psi;
                double b2 = inverse - 1 + std::sqrt(inverse) * std::sqrt(inverse - 1);
                double a = mean / (1 + b2);
                double z = std::sqrt(b2) + inverse_normal(u);
                next = a * z * z;
            } else {
                double p0 = (psi - 1) / (psi + 1);
                double beta = (1 - p0) / mean;
                next = u <= p0 ? 0 : std::log((1 - p0) / (1 - u)) / beta;
            }
            double normal = inverse_normal(random_.uniform());
            y_[i] += k0 + k1 * v + k2 * next + std::sqrt(k3 * (v + next)) * normal;
            v_[i] = next;
        }
    }

    void fill_spot(int, double forward, double* spot) const {
        for (size_t i = 0; i < y_.size(); i++) spot[i] = forward * std::exp(y_[i]);
    }

private:
    const pc_heston& p_;
    const pc_schedule& schedule_;
    std::vector<double> y_;
    std::vector<double> v_;
    SplitMix random_;
};

int check_schedule(const pc_program& program, const pc_schedule* schedule) {
    PC_REQUIRE(schedule != nullptr && schedule->times != nullptr && schedule->step_of_date != nullptr &&
                   schedule->discounts != nullptr && schedule->forwards != nullptr,
               "Missing schedule.");
    PC_REQUIRE(schedule->steps >= 1, "The schedule has no step.");
    for (int d = 0; d < program.dates; d++) {
        PC_REQUIRE(schedule->step_of_date[d] >= 0 && schedule->step_of_date[d] < schedule->steps &&
                       (d == 0 || schedule->step_of_date[d] > schedule->step_of_date[d - 1]),
                   "Dates must fall on increasing steps.");
    }
    return PC_OK;
}

}  // namespace
}  // namespace pc

using namespace pc;

extern "C" int32_t pc_mc_black_scholes(
    const pc_program* program, const pc_sobol* sobol, const pc_black_scholes* model, const pc_batches* batches, double* stats) {
    clear_error();
    std::vector<Event> events;
    if (int status = check_program(program, events)) return status;
    if (int status = check_batches(batches)) return status;
    PC_REQUIRE(model != nullptr && model->times != nullptr && model->forwards != nullptr && model->discounts != nullptr &&
                   model->bridge != nullptr && stats != nullptr,
               "Missing Black-Scholes inputs.");
    PC_REQUIRE(model->bridge_length == program->dates, "The bridge must have one step per date.");
    PC_REQUIRE(program->assets == 1, "Black-Scholes Monte Carlo prices a single underlying.");
    PC_REQUIRE(model->vol > 0 && model->spot > 0 && model->times[0] > 0, "Spot, vol and first time must be positive.");

    int n = batches->paths;
    for_batches(batches->batches, batches->threads, [&](int b) {
        black_scholes_batch(*program, events, sobol, *model, n, batches->batch_seeds[b], stats + static_cast<size_t>(b) * PC_BS_STATS);
    });
    return PC_OK;
}

extern "C" int32_t pc_mc_local_vol(
    const pc_program* program, const pc_sobol* sobol, const pc_schedule* schedule, const pc_local_vol* model,
    const pc_batches* batches, double* means) {
    clear_error();
    std::vector<Event> events;
    if (int status = check_program(program, events)) return status;
    if (int status = check_batches(batches)) return status;
    if (int status = check_schedule(*program, schedule)) return status;
    PC_REQUIRE(model != nullptr && model->rows != nullptr && model->lows != nullptr && model->step_sizes != nullptr &&
                   model->bridge != nullptr && means != nullptr,
               "Missing local vol inputs.");
    PC_REQUIRE(model->points >= 2 && model->bridge_length == schedule->steps, "Invalid local vol grid or bridge.");
    PC_REQUIRE(model->correlation >= 0 && model->correlation <= 1, "The correlation must lie in [0, 1].");

    int n = batches->paths;
    for_batches(batches->batches, batches->threads, [&](int b) {
        LocalVolProcess process(*model, *schedule, sobol, program->assets, n, batches->batch_seeds[b]);
        means[b] = stepped_batch(*program, events, *schedule, n, process);
    });
    return PC_OK;
}

extern "C" int32_t pc_mc_heston(
    const pc_program* program, const pc_schedule* schedule, const pc_heston* model, const pc_batches* batches, double* means) {
    clear_error();
    std::vector<Event> events;
    if (int status = check_program(program, events)) return status;
    if (int status = check_batches(batches)) return status;
    if (int status = check_schedule(*program, schedule)) return status;
    PC_REQUIRE(model != nullptr && means != nullptr, "Missing Heston inputs.");
    PC_REQUIRE(program->assets == 1, "Heston prices a single underlying.");

    int n = batches->paths;
    for_batches(batches->batches, batches->threads, [&](int b) {
        HestonProcess process(*model, *schedule, n, batches->batch_seeds[b]);
        means[b] = stepped_batch(*program, events, *schedule, n, process);
    });
    return PC_OK;
}
