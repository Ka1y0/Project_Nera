cmake_minimum_required(VERSION 3.20)
project(NeraNativeSafetySourcePreview VERSION 0.1.0 LANGUAGES CXX)

# Independent source preview: deliberately do not add the application tree,
# discover a graphics SDK, download dependencies, or link a platform runtime.
enable_testing()
add_library(nera_safety_policies INTERFACE)
target_compile_features(nera_safety_policies INTERFACE cxx_std_20)
target_include_directories(nera_safety_policies INTERFACE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/include/nera/safety")

function(add_safety_test name source)
    add_executable(${name} "tests/${source}")
    target_link_libraries(${name} PRIVATE nera_safety_policies)
    set_target_properties(${name} PROPERTIES CXX_EXTENSIONS OFF)
    if(MSVC)
        target_compile_options(${name} PRIVATE /W4 /WX /permissive- /utf-8 /EHsc)
    else()
        target_compile_options(${name} PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(NAME ${name} COMMAND ${name})
    set_tests_properties(${name} PROPERTIES TIMEOUT 10)
endfunction()

add_safety_test(NeraProtectedContentPolicyTests ProtectedContentPolicyTests.cpp)
add_safety_test(NeraStartupRecoveryPolicyTests StartupRecoveryPolicyTests.cpp)
add_safety_test(NeraProcessReleasePolicyTests ProcessReleasePolicyTests.cpp)
add_safety_test(NeraHudEvidencePolicyTests HudProbeEvidencePolicyTests.cpp)
add_safety_test(NeraRollingPresenterTimingTests RollingPresenterTimingTests.cpp)
