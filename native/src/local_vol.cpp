// Dupire local vol from Gatheral's formula in total variance: the twin of LocalVolGrid (Pricer.Models/LocalVol.cs).
// The caller fixes the grid and evaluates the forward at the three times each row reads; the kernel reads the
// implied surface itself, as SVI slices interpolated in time (SviVolSurface) or a flat level.
#include "common.hpp"

namespace pc {
namespace {

const double MONEYNESS_STEP = 0.01;
const double MIN_DENOMINATOR = 1e-3;

double total_variance(const pc_svi_slice& s, double k) {
    double x = k - s.m;
    return s.a + s.b * (s.rho * x + std::sqrt(x * x + s.sigma * s.sigma));
}

/// SviVolSurface.Vol, or FlatVolSurface.Vol without slices.
double implied_vol(const pc_vol_surface& surface, double strike, double t) {
    if (surface.slice_count == 0) return surface.flat_vol;
    const pc_svi_slice* slices = surface.slices;
    const pc_svi_slice& first = slices[0];
    const pc_svi_slice& last = slices[surface.slice_count - 1];
    if (t <= first.t) return std::sqrt(total_variance(first, std::log(strike / first.forward)) / first.t) + surface.shift;
    if (t >= last.t) return std::sqrt(total_variance(last, std::log(strike / last.forward)) / last.t) + surface.shift;

    int i = 1;
    while (slices[i].t < t) i++;
    const pc_svi_slice& left = slices[i - 1];
    const pc_svi_slice& right = slices[i];
    double weight = (t - left.t) / (right.t - left.t);
    double forward = std::exp(std::log(left.forward) + weight * (std::log(right.forward) - std::log(left.forward)));
    double k = std::log(strike / forward);
    double w = total_variance(left, k) + weight * (total_variance(right, k) - total_variance(left, k));
    return std::sqrt(w / t) + surface.shift;
}

/// LocalVolGrid.LocalVariance: Gatheral's formula, falling back on implied variance where it fails.
double local_variance(const pc_vol_surface& surface, const pc_local_vol_request& r, int row, double y, int64_t& clamped) {
    double t = r.times[row], up_time = r.up_times[row], down_time = r.down_times[row];
    auto total = [&](double k, double time, double forward) {
        double vol = implied_vol(surface, forward * std::exp(k), time);
        return vol * vol * time;
    };

    double w = total(y, t, r.forwards[row]);
    double up = total(y + MONEYNESS_STEP, t, r.forwards[row]);
    double down = total(y - MONEYNESS_STEP, t, r.forwards[row]);
    double slope = (up - down) / (2 * MONEYNESS_STEP);
    double curvature = (up - 2 * w + down) / (MONEYNESS_STEP * MONEYNESS_STEP);
    double dwdt = (total(y, up_time, r.up_forwards[row]) - total(y, down_time, r.down_forwards[row])) / (up_time - down_time);
    double denominator = 1 - y / w * slope + 0.25 * (-0.25 - 1 / w + y * y / (w * w)) * slope * slope + 0.5 * curvature;

    if (dwdt > 0 && denominator > MIN_DENOMINATOR) return dwdt / denominator;
    clamped++;
    return w / t;
}

}  // namespace
}  // namespace pc

using namespace pc;

extern "C" int32_t pc_local_vol_grid(
    const pc_vol_surface* surface, const pc_local_vol_request* request, double* rows, int64_t* clamped) {
    clear_error();
    PC_REQUIRE(surface != nullptr && request != nullptr && rows != nullptr && clamped != nullptr, "Missing local vol grid inputs.");
    PC_REQUIRE(surface->slice_count >= 0 && (surface->slice_count == 0 || surface->slices != nullptr), "Invalid vol surface.");
    PC_REQUIRE(request->rows >= 1 && request->points >= 2 && request->step > 0, "Invalid local vol grid size.");
    PC_REQUIRE(request->times != nullptr && request->up_times != nullptr && request->down_times != nullptr &&
                   request->forwards != nullptr && request->up_forwards != nullptr && request->down_forwards != nullptr,
               "Missing times or forwards.");
    for (int s = 1; s < surface->slice_count; s++) {
        PC_REQUIRE(surface->slices[s].t > surface->slices[s - 1].t, "SVI slices must be in increasing time.");
    }

    // Rows are independent: each worker fills its own and counts its own fallbacks.
    std::vector<int64_t> per_row(request->rows, 0);
    for_batches(request->rows, request->threads, [&](int i) {
        double* row = rows + static_cast<size_t>(i) * request->points;
        for (int j = 0; j < request->points; j++) {
            double vol = std::sqrt(local_variance(*surface, *request, i, request->low + j * request->step, per_row[i]));
            // Math.Clamp: NaN passes through.
            row[j] = vol < request->min_vol ? request->min_vol : (vol > request->max_vol ? request->max_vol : vol);
        }
    });
    int64_t total = 0;
    for (int64_t count : per_row) total += count;
    *clamped = total;
    return PC_OK;
}
