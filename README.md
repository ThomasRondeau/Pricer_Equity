# Equity Pricer

An equity derivatives pricing library in C# / .NET 9, with a WPF desktop front end and a
browser demo that runs the same library in WebAssembly.

**Live demo: https://thomasrondeau.github.io/Pricer_Equity/** — no install. Every price is computed
in your browser; there is no server.

## What it does

- **Products**: stocks, forwards, European and American options, digitals, barriers, Asians,
  reverse convertibles, autocalls, Phoenix notes and worst-of Phoenix.
- **Models**: Black-Scholes, Dupire local vol built from an arbitrage-checked SVI surface, Heston.
- **Engines**: closed forms, Crank-Nicolson PDE with Rannacher start-up, Monte Carlo with Sobol
  sequences and a Brownian bridge (SIMD, deterministic seeds, standard error always reported).
- **Native C++ core**: the PDE solver, the Monte Carlo batches and the Dupire local vol grid also run as C++ kernels behind a
  C ABI, loaded by P/Invoke on the desktop and linked into the WebAssembly build of the demo. They
  reproduce the managed engines to the last bits and the engine table times both side by side.
- **Greeks by the best method available** — closed form, semi-analytic, PDE grid, pathwise,
  likelihood ratio — each one labelled with its method and convention and checked against a
  bump-and-reprice control.
- **Book and runs** (desktop): positions from IBKR plus local trades, cached runs, run diffs down
  to the parameter, and a full-revaluation **PnL explain** waterfall cross-checked with a greeks
  explain.

## Design

A pricing call is a pure function of immutable, hashed inputs:

```
Price(trade, market snapshot, model config, engine config, valuation date, measures) -> result
```

Bumped greeks, sliders, stress scenarios, run comparisons and the PnL explain are all this one
function called with modified inputs, so every run can be replayed exactly. Models and engines
describe their parameters with metadata, from which both user interfaces generate their forms.

```
src/
  Pricer.Core          day counts, content hashing, parameter metadata
  Pricer.Market        snapshots, curves, SVI surfaces, market bumps
  Pricer.Instruments   term sheets and a payoff language compiled to dated events
  Pricer.Models        Black-Scholes, local vol, Heston, SVI calibration
  Pricer.Engines       analytic, PDE and Monte Carlo engines
  Pricer.Native        bindings to the C++ kernels of native/
  Pricer.Risk          greeks, model comparison
  Pricer.Book          positions, runs, PnL explain
  Pricer.Data          SQLite storage, IBKR and Yahoo capture
  Pricer.Presentation  view models shared by both front ends
  Pricer.App           WPF desktop application
  Pricer.Web           Blazor WebAssembly demo (Pricer and Model Lab pages)
  Pricer.Capture       daily market capture and web data export
tests/Pricer.Tests     reference tests: closed forms, convergence, greek consistency, PnL explain
```

## Native core

The C# engines keep products, models, calibration and the choice of greeks; they evaluate everything
a loop reads into flat arrays and hand those to the kernels of `native/`:

```
native/
  include/pricer_core.h   the C ABI: plain structs, error codes, no C++ type crosses it
  src/pde.cpp             theta scheme with Rannacher steps, Thomas solver, early exercise, local vol
  src/monte_carlo.cpp     Black-Scholes batches with pathwise and likelihood ratio greeks,
                          local vol (Euler, correlated baskets) and Heston (Andersen QE) batches
  src/local_vol.cpp       Dupire local vol from Gatheral's formula on the SVI surface
  src/payoff_vm.hpp       column evaluator of the payoff programs, with forward-mode tangents
  src/common.hpp          SplitMix, Sobol, inverse normal, Brownian bridge, batch threads
src/Pricer.Native         P/Invoke bindings and the CMake build hook
```

Payoffs reach C++ as postfix bytecode compiled from the payoff language, so structured products
need no native code of their own. Random numbers, bridge and event order are the managed ones, so
`tests/Pricer.Tests/NativeParityTests.cs` requires the same PV, standard error and greeks to 1e-12
on the PDE and the local vol grid, and 1e-9 on Monte Carlo, for every engine and product family. The engine forms carry a
`Backend` choice (Managed or Native); Native is the default and falls back to Managed when the
library is missing.

## Running it

```powershell
dotnet test tests/Pricer.Tests -c Release
dotnet run --project src/Pricer.App -c Release      # desktop (Windows)
dotnet run --project src/Pricer.Web                 # web demo on http://localhost:5145
```

Building needs CMake and a C++17 compiler for the native core: on Windows, Visual Studio with the
C++ workload (its bundled CMake is used); on Linux, `cmake` and `g++`. `dotnet build` runs
`native/build.ps1` or `native/build.sh` itself; set `-p:SkipNativeBuild=true` to skip it.
The web demo needs `dotnet workload install wasm-tools-net9`, whose Emscripten compiles the same
C++ sources; publishing in Release also compiles the .NET code ahead of time to WebAssembly. The demo's market data is regenerated with
`dotnet run --project src/Pricer.Capture -- --export-web src/Pricer.Web/wwwroot/data --ids <capture ids>`.

## Data

The demo ships a synthetic SVI market and two Yahoo Finance option chain captures from
2026-10-30, recalibrated by the library. They are for demonstration only.
