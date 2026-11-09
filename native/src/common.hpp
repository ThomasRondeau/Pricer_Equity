// Shared helpers of the kernels: error reporting, random numbers, Brownian bridge and batch scheduling.
// Each function mirrors a managed twin bit for bit where it can (generators, bridge); the managed names are
// given so that the two stay in step.
#pragma once

#include "../include/pricer_core.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#if defined(_MSC_VER)
#include <intrin.h>
#endif

#if !defined(__EMSCRIPTEN__) || defined(__EMSCRIPTEN_PTHREADS__)
#define PC_THREADS 1
#include <atomic>
#include <thread>
#endif

namespace pc {

void set_error(const std::string& message);
void clear_error();

/// Fails the call with PC_INVALID_ARGUMENT when a precondition does not hold.
#define PC_REQUIRE(condition, message) \
    do { \
        if (!(condition)) { \
            ::pc::set_error(message); \
            return PC_INVALID_ARGUMENT; \
        } \
    } while (0)

inline int trailing_zeros(uint32_t x) {
#if defined(_MSC_VER)
    unsigned long index;
    _BitScanForward(&index, x);
    return static_cast<int>(index);
#else
    return __builtin_ctz(x);
#endif
}

/// SplitMix in Sobol.cs.
struct SplitMix {
    uint64_t state;

    explicit SplitMix(uint64_t seed) : state(seed) {}

    uint64_t next() {
        uint64_t z = state += 0x9E3779B97F4A7C15ull;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
        return z ^ (z >> 31);
    }

    /// Strictly inside (0, 1).
    double uniform() { return (static_cast<double>(next() >> 11) + 0.5) / 9007199254740992.0; }
};

/// Sobol.Fill: the first points of one dimension, digitally shifted.
inline void sobol_fill(const pc_sobol& sobol, int dimension, uint32_t shift, double* uniforms, int count) {
    const uint32_t* v = sobol.directions + 32 * dimension;
    const double scale = 1.0 / 4294967296.0;
    uint32_t x = 0;
    for (int i = 0; i < count; i++) {
        uniforms[i] = (static_cast<double>(x ^ shift) + 0.5) * scale;
        x ^= v[trailing_zeros(static_cast<uint32_t>(i + 1))];
    }
}

/// InverseNormal.Evaluate: Acklam's rational approximation.
inline double inverse_normal(double p) {
    static const double a[] = {-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                               1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00};
    static const double b[] = {-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                               6.680131188771972e+01, -1.328068155288572e+01};
    static const double c[] = {-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                               -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00};
    static const double d[] = {7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                               3.754408661907416e+00};
    const double low_tail = 0.02425;

    auto tail = [&](double q) {
        return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
               ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    };
    if (p < low_tail) return tail(std::sqrt(-2 * std::log(p)));
    if (p > 1 - low_tail) return -tail(std::sqrt(-2 * std::log(1 - p)));

    double q = p - 0.5;
    double r = q * q;
    return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
           (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
}

/// BrownianBridge.Apply on paths stored date by date: brownian + date * paths.
inline void bridge_apply(const pc_bridge_step& step, double* brownian, int paths, const double* normals) {
    double* target = brownian + static_cast<size_t>(step.target) * paths;
    if (step.left < 0 && step.right < 0) {
        for (int i = 0; i < paths; i++) target[i] = step.noise * normals[i];
        return;
    }
    const double* right = brownian + static_cast<size_t>(step.right) * paths;
    if (step.left < 0) {
        for (int i = 0; i < paths; i++) target[i] = step.right_weight * right[i] + step.noise * normals[i];
        return;
    }
    const double* left = brownian + static_cast<size_t>(step.left) * paths;
    for (int i = 0; i < paths; i++) {
        target[i] = step.left_weight * left[i] + step.right_weight * right[i] + step.noise * normals[i];
    }
}

/// Fills one Brownian dimension: Sobol while the table lasts, then the batch generator, as in the engines.
inline void normals_for(const pc_sobol* sobol, int dimension, SplitMix& random, double* uniforms, double* normals, int paths) {
    uint32_t shift = static_cast<uint32_t>(random.next());
    if (sobol != nullptr && dimension < sobol->dimensions) {
        sobol_fill(*sobol, dimension, shift, uniforms, paths);
    } else {
        for (int i = 0; i < paths; i++) uniforms[i] = random.uniform();
    }
    for (int i = 0; i < paths; i++) normals[i] = inverse_normal(uniforms[i]);
}

inline double mean(const double* values, int count) {
    double sum = 0;
    for (int i = 0; i < count; i++) sum += values[i];
    return sum / count;
}

/// Runs batch(0..count-1) over the requested threads. Each batch writes only its own output, so the result
/// does not depend on the thread count.
template <typename Batch>
void for_batches(int count, int threads, Batch&& batch) {
#ifdef PC_THREADS
    int hardware = static_cast<int>(std::thread::hardware_concurrency());
    int workers = std::min(count, threads > 0 ? threads : (hardware > 0 ? hardware : 1));
    if (workers > 1) {
        std::atomic<int> next{0};
        std::vector<std::thread> pool;
        pool.reserve(workers);
        for (int w = 0; w < workers; w++) {
            pool.emplace_back([&] {
                for (int b = next++; b < count; b = next++) batch(b);
            });
        }
        for (auto& worker : pool) worker.join();
        return;
    }
#else
    (void)threads;
#endif
    for (int b = 0; b < count; b++) batch(b);
}

}  // namespace pc
