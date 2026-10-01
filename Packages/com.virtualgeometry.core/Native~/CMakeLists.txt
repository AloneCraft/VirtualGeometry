cmake_minimum_required(VERSION 3.20)

project(VirtualGeometryMeshOptimizer LANGUAGES CXX)

set(MESHOPT_BUILD_DEMO OFF CACHE BOOL "" FORCE)
set(MESHOPT_BUILD_GLTFPACK OFF CACHE BOOL "" FORCE)
set(MESHOPT_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(MESHOPT_INSTALL OFF CACHE BOOL "" FORCE)
set(MESHOPT_WERROR OFF CACHE BOOL "" FORCE)
add_subdirectory(meshoptimizer EXCLUDE_FROM_ALL)

add_library(VirtualGeometryMeshOptimizer SHARED VirtualGeometryMeshOptimizer.cpp)
target_compile_features(VirtualGeometryMeshOptimizer PRIVATE cxx_std_17)
target_include_directories(VirtualGeometryMeshOptimizer PRIVATE meshoptimizer/src)
target_link_libraries(VirtualGeometryMeshOptimizer PRIVATE meshoptimizer)

if(MSVC)
    target_compile_options(VirtualGeometryMeshOptimizer PRIVATE /W4 /WX)
endif()

if(NOT DEFINED VG_OUTPUT_DIR)
    set(VG_OUTPUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../Editor/Plugins/x86_64")
endif()

set_target_properties(VirtualGeometryMeshOptimizer PROPERTIES
    PREFIX ""
    RUNTIME_OUTPUT_DIRECTORY "${VG_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${VG_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${VG_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${VG_OUTPUT_DIR}"
)

