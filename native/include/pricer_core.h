/* Native compute kernels of the pricer.
 *
 * The C# side keeps products, models, calibration and greek selection; it prepares flat arrays and calls
 * these kernels for the loops that dominate the run time. Every struct is plain data with the layout of its
 * twin in Pricer.Native/NativeStructs.cs: change both together. No C++ type or exception crosses the boundary;
 * functions return PC_OK or an error code, with a message from pc_last_error(). */
#ifndef PRICER_CORE_H
#define PRICER_CORE_H

#include <stdint.h>

#if defined(_WIN32)
#define PC_API __declspec(dllexport)
#else
#define PC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum { PC_OK = 0, PC_INVALID_ARGUMENT = 1, PC_INTERNAL_ERROR = 2 };

/* Bumped whenever a struct below changes, so a stale library is refused rather than misread. */
#define PC_ABI_VERSION 2

/* ---------- Finite differences ---------- */

/* Theta scheme on a uniform log-spot grid, solved backwards from expiry. The time loop is unrolled by the
 * caller: Rannacher start-up steps are two implicit half steps, so there are more sub-steps than steps. */
typedef struct {
    int32_t nodes;              /* n + 1 */
    double low, h, log_spot, spot;
    const double* payoff;       /* nodes values at expiry, cell averaged */
    const double* exercise;     /* nodes values, or null without early exercise */
    int32_t time_steps, rannacher_steps;
    double dt;
    const double* rates;        /* per time step: short rate over the step */
    const double* carries;      /* per time step: dividend yield over the step */
    const double* edge_low;     /* per sub-step: Dirichlet value at the low edge */
    const double* edge_high;    /* per sub-step: Dirichlet value at the high edge */
    double vol;                 /* Black-Scholes vol, when there is no local vol */
    /* Local vol, optional (rows null otherwise): one row of points per time step, in y = ln(S / F(t)). */
    const double* local_rows;
    int32_t local_points;
    double local_low, local_step;
    const double* log_forwards; /* per time step */
} pc_pde_problem;

/* out: price, delta, gamma, theta per year. */
PC_API int32_t pc_pde_solve(const pc_pde_problem* problem, double* out);

/* ---------- Local vol ---------- */

/* Raw SVI slice: w(k) = a + b (rho (k - m) + sqrt((k - m)^2 + sigma^2)), k = ln(K / forward). */
typedef struct {
    double t, forward, a, b, rho, m, sigma;
} pc_svi_slice;

/* SVI slices in increasing time, interpolated linearly in total variance; no slices means a flat vol. */
typedef struct {
    const pc_svi_slice* slices;
    int32_t slice_count;
    int32_t padding;
    double flat_vol;
    double shift;               /* added to every implied vol */
} pc_vol_surface;

/* A rows x points table in y = ln(S / F(t)), from low by step. Each row reads the surface at its time and at
 * the two times of its time derivative, with the forward at each. */
typedef struct {
    int32_t rows, points;
    double low, step, min_vol, max_vol;
    const double* times;
    const double* up_times;
    const double* down_times;
    const double* forwards;
    const double* up_forwards;
    const double* down_forwards;
    int32_t threads;            /* 0: all hardware threads */
    int32_t padding;
} pc_local_vol_request;

/* rows: rows x points local vols; clamped: nodes where the formula failed and implied variance was used. */
PC_API int32_t pc_local_vol_grid(
    const pc_vol_surface* surface, const pc_local_vol_request* request, double* rows, int64_t* clamped);

/* ---------- Monte Carlo ---------- */

/* A payoff program flattened to postfix code; see PayoffBytecode.cs for the opcodes. */
typedef struct {
    const int32_t* code;
    int32_t code_length;
    const double* constants;
    int32_t slots;
    const double* initial_slots; /* slots values, replayed from the trade's past */
    int32_t assets;
    int32_t dates;
} pc_program;

/* Direction numbers, 32 per dimension. */
typedef struct {
    const uint32_t* directions;
    int32_t dimensions;
} pc_sobol;

typedef struct {
    int32_t target, left, right, padding;
    double left_weight, right_weight, noise;
} pc_bridge_step;

typedef struct {
    int32_t paths, batches;
    const uint64_t* batch_seeds;
    int32_t threads;            /* 0: all hardware threads */
    int32_t padding;
} pc_batches;

/* Black-Scholes with exact moves between event dates. */
typedef struct {
    const double* times;        /* per date */
    const double* forwards;     /* per date */
    const double* discounts;    /* per date */
    const pc_bridge_step* bridge;
    int32_t bridge_length;
    int32_t want_tangents, want_scores;
    int32_t padding;
    double spot, vol, smoothing;
} pc_black_scholes;

enum { PC_BS_STATS = 7 };

/* stats: batches x 7, in the order of BlackScholesMonteCarloEngine.Stat. */
PC_API int32_t pc_mc_black_scholes(
    const pc_program* program, const pc_sobol* sobol, const pc_black_scholes* model, const pc_batches* batches, double* stats);

/* Stepped simulation: dates fall on steps. */
typedef struct {
    const double* times;        /* per step */
    int32_t steps;
    int32_t padding;
    const int32_t* step_of_date;
    const double* discounts;    /* per date */
    const double* forwards;     /* dates x assets */
} pc_schedule;

/* One local vol grid per asset, rows per step; assets share a common factor of weight sqrt(correlation). */
typedef struct {
    const double* rows;         /* assets x steps x points */
    const double* lows;         /* per asset */
    const double* step_sizes;   /* per asset */
    int32_t points;
    int32_t bridge_length;
    const pc_bridge_step* bridge;
    double correlation;
} pc_local_vol;

typedef struct {
    double v0, kappa, theta, xi, rho;
} pc_heston;

/* means: per batch, the average discounted payoff. */
PC_API int32_t pc_mc_local_vol(
    const pc_program* program, const pc_sobol* sobol, const pc_schedule* schedule, const pc_local_vol* model,
    const pc_batches* batches, double* means);

PC_API int32_t pc_mc_heston(
    const pc_program* program, const pc_schedule* schedule, const pc_heston* model, const pc_batches* batches, double* means);

/* ---------- Diagnostics ---------- */

PC_API int32_t pc_abi_version(void);

/* Message of the last failure on this thread; empty when none. */
PC_API const char* pc_last_error(void);

#ifdef __cplusplus
}
#endif

#endif
