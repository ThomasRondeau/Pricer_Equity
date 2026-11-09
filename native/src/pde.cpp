// Crank-Nicolson with Rannacher start-up on a uniform log-spot grid: the twin of FiniteDifferenceEngine.Solve.
// The caller has already turned the product into node values and edge values per sub-step, so the kernel
// knows nothing of payoffs and curves.
#include "common.hpp"

namespace pc {
namespace {

/// One theta-scheme step with Dirichlet edges, solved by the Thomas algorithm (FiniteDifferenceEngine.Scratch).
struct Scratch {
    explicit Scratch(int nodes) : below(nodes), centre(nodes), above(nodes), rhs(nodes), upper(nodes) {}

    void advance(std::vector<double>& v, double dt, double theta, double edge_low, double edge_high) {
        int n = static_cast<int>(v.size()) - 1;
        double explicit_weight = (1 - theta) * dt;
        for (int j = 1; j < n; j++) {
            rhs[j] = v[j] + explicit_weight * (below[j] * v[j - 1] + centre[j] * v[j] + above[j] * v[j + 1]);
        }

        double implicit_weight = theta * dt;
        rhs[1] += implicit_weight * below[1] * edge_low;
        rhs[n - 1] += implicit_weight * above[n - 1] * edge_high;

        double b = 1 - implicit_weight * centre[1];
        upper[1] = -implicit_weight * above[1] / b;
        rhs[1] /= b;
        for (int j = 2; j < n; j++) {
            double a = -implicit_weight * below[j];
            double pivot = 1 - implicit_weight * centre[j] - a * upper[j - 1];
            upper[j] = -implicit_weight * above[j] / pivot;
            rhs[j] = (rhs[j] - a * rhs[j - 1]) / pivot;
        }

        v[n - 1] = rhs[n - 1];
        for (int j = n - 2; j >= 1; j--) v[j] = rhs[j] - upper[j] * v[j + 1];
        v[0] = edge_low;
        v[n] = edge_high;
    }

    std::vector<double> below, centre, above;

private:
    std::vector<double> rhs, upper;
};

double lookup(const double* row, int points, double low, double step, double y) {
    double position = (y - low) / step;
    if (position <= 0) return row[0];
    if (position >= points - 1) return row[points - 1];
    int node = static_cast<int>(position);
    double weight = position - node;
    return row[node] + weight * (row[node + 1] - row[node]);
}

}  // namespace
}  // namespace pc

using namespace pc;

extern "C" int32_t pc_pde_solve(const pc_pde_problem* p, double* out) {
    clear_error();
    PC_REQUIRE(p != nullptr && out != nullptr, "Missing PDE problem.");
    PC_REQUIRE(p->nodes >= 4, "The grid needs at least four nodes.");
    PC_REQUIRE(p->time_steps >= 1 && p->rannacher_steps >= 0 && p->dt > 0 && p->h > 0, "Invalid grid steps.");
    PC_REQUIRE(p->payoff != nullptr && p->rates != nullptr && p->carries != nullptr && p->edge_low != nullptr &&
                   p->edge_high != nullptr,
               "Missing PDE arrays.");
    bool local = p->local_rows != nullptr;
    PC_REQUIRE(!local || (p->log_forwards != nullptr && p->local_points >= 2 && p->local_step > 0), "Invalid local vol grid.");

    int nodes = p->nodes;
    int n = nodes - 1;
    double h = p->h;
    std::vector<double> v(p->payoff, p->payoff + nodes);
    std::vector<double> previous(nodes);
    Scratch scratch(nodes);
    int sub = 0;

    auto coefficients = [&](int index) {
        double rate = p->rates[index];
        double carry = p->carries[index];
        const double* row = local ? p->local_rows + static_cast<size_t>(index) * p->local_points : nullptr;
        for (int j = 1; j < n; j++) {
            double vol = p->vol;
            if (local) vol = lookup(row, p->local_points, p->local_low, p->local_step, p->low + j * h - p->log_forwards[index]);
            double variance = vol * vol;
            double drift = rate - carry - 0.5 * variance;
            scratch.below[j] = 0.5 * variance / (h * h) - 0.5 * drift / h;
            scratch.above[j] = 0.5 * variance / (h * h) + 0.5 * drift / h;
            scratch.centre[j] = -variance / (h * h) - rate;
        }
    };

    auto step = [&](double length, double theta) {
        scratch.advance(v, length, theta, p->edge_low[sub], p->edge_high[sub]);
        sub++;
        if (p->exercise != nullptr) {
            for (int j = 0; j < nodes; j++) v[j] = std::max(v[j], p->exercise[j]);
        }
    };

    for (int i = 0; i < p->time_steps; i++) {
        previous = v;
        if (i == 0 || local) coefficients(i);
        if (i < p->rannacher_steps) {
            step(p->dt / 2, 1);
            step(p->dt / 2, 1);
        } else {
            step(p->dt, 0.5);
        }
    }

    // Math.Round rounds half to even, as nearbyint does in the default rounding mode.
    int c = static_cast<int>(std::nearbyint((p->log_spot - p->low) / h));
    c = std::min(std::max(c, 1), n - 1);
    double u = p->log_spot - (p->low + c * h);
    double first = (v[c + 1] - v[c - 1]) / (2 * h);
    double second = (v[c + 1] - 2 * v[c] + v[c - 1]) / (h * h);
    auto interpolate = [&](const std::vector<double>& w) {
        return w[c] + u * (w[c + 1] - w[c - 1]) / (2 * h) + 0.5 * u * u * (w[c + 1] - 2 * w[c] + w[c - 1]) / (h * h);
    };

    double slope = first + u * second;
    double spot2 = p->spot * p->spot;
    out[0] = interpolate(v);
    out[1] = slope / p->spot;
    out[2] = (second - slope) / spot2;
    out[3] = (interpolate(previous) - interpolate(v)) / p->dt;
    return PC_OK;
}
