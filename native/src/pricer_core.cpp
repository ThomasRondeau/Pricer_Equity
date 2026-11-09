// Diagnostics of the library. The kernels live in pde.cpp, monte_carlo.cpp and local_vol.cpp.
// This file gives the WebAssembly build its module name: P/Invokes to "pricer_core" resolve here.
#include "common.hpp"

namespace pc {

namespace {
thread_local std::string last_error;
}

void set_error(const std::string& message) { last_error = message; }

void clear_error() { last_error.clear(); }

}  // namespace pc

extern "C" int32_t pc_abi_version(void) { return PC_ABI_VERSION; }

extern "C" const char* pc_last_error(void) { return pc::last_error.c_str(); }
