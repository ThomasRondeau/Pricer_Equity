cmake_minimum_required(VERSION 3.20)
project(pricer_core LANGUAGES CXX)

# Desktop and Linux build of the native kernels, loaded by Pricer.Native through P/Invoke.
# The WebAssembly build does not use this file: Pricer.Web hands the sources to the .NET Emscripten toolchain.

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release)
endif()

add_library(pricer_core SHARED
    src/pricer_core.cpp
    src/pde.cpp
    src/monte_carlo.cpp
    src/local_vol.cpp)
target_include_directories(pricer_core PUBLIC include)
set_target_properties(pricer_core PROPERTIES CXX_VISIBILITY_PRESET hidden PREFIX "")

# The managed code is the reference: no fused multiply-add and no reassociation, so that both sides
# round the same operations the same way.
if(MSVC)
    target_compile_options(pricer_core PRIVATE /W4 /WX /O2 /fp:precise)
else()
    target_compile_options(pricer_core PRIVATE -Wall -Wextra -Werror -O3 -ffp-contract=off -fno-fast-math)
    find_package(Threads REQUIRED)
    target_link_libraries(pricer_core PRIVATE Threads::Threads)
endif()

# One output folder whatever the generator, read by Pricer.Native.csproj.
set(PRICER_CORE_OUTPUT "${CMAKE_CURRENT_SOURCE_DIR}/out" CACHE PATH "Folder of the built library")
foreach(config IN ITEMS "" _DEBUG _RELEASE _RELWITHDEBINFO _MINSIZEREL)
    set_target_properties(pricer_core PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY${config} "${PRICER_CORE_OUTPUT}"
        LIBRARY_OUTPUT_DIRECTORY${config} "${PRICER_CORE_OUTPUT}")
endforeach()
