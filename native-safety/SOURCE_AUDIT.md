# Native source preview audit

Scope: only this self-contained staging subtree. No live internal implementation
was edited to create the preview. Public source publication remains the parent
release task's responsibility; this is a technical exclusion audit, not a legal
opinion or an open-source license grant.

## Exact source allowlist and dependency closure

The proposed public destination is `native-safety/`, with this subtree's relative
layout preserved. There is no wildcard import from an internal directory.

| Included relative path | Origin or transformation | Complete non-test direct includes |
| --- | --- | --- |
| `include/nera/safety/ProtectedContentPolicy.h` | `labs/Feature18CompatHost/ProtectedContentPolicy.h`; source text preserved, line endings normalized | Standard `bit`, `cstdint` only |
| `include/nera/safety/StartupRecoveryPolicy.h` | `src/Nera.Core/Global/StartupRecoveryPolicy.h`; source text preserved, line endings normalized | Standard `cstdint`, `string_view` only |
| `include/nera/safety/HudProbeEvidencePolicy.h` | `src/Nera.HudProbe/HudProbeEvidencePolicy.h`; source text preserved, line endings normalized | Standard `cstdint` only |
| `include/nera/safety/RollingPresenterTiming.h` | `labs/Feature18CompatHost/RollingPresenterTiming.h`; retain MIT SPDX; add explicit standard `cstddef` / `iterator` includes only | Standard `algorithm`, `cmath`, `cstddef`, `cstdint`, `deque`, `iterator`, `vector` |
| `include/nera/safety/ProcessReleasePolicy.h` | New neutral value-only extraction of the owned-exit cleanup invariant in `GlobalOffGates`; no internal struct/ABI dependency | None |
| `tests/HudProbeEvidencePolicyTests.cpp` | Existing pure HUD test; text preserved | Local HUD policy; standard `iostream`, `limits` |
| `tests/RollingPresenterTimingTests.cpp` | Existing pure timing test; MIT SPDX preserved | Local timing policy; standard `cmath`, `iostream` |
| `tests/ProtectedContentPolicyTests.cpp` | New focused pure regression cases | Local capture policy and `TestChecks.h` |
| `tests/StartupRecoveryPolicyTests.cpp` | New exhaustive release-default and cleanup combinations | Local retry policy and `TestChecks.h` |
| `tests/ProcessReleasePolicyTests.cpp` | New safe-OFF truth tables and crash cases | Local release policy and `TestChecks.h` |
| `tests/TestChecks.h` | New small assertion helper; checks remain active in Release | Standard `iostream` |
| `CMakeLists.txt` | New standalone C++20 CMake; five executables and one header-only interface target | Only local files listed above |
| `README.md` | New explicit source-only scope and boundaries | None |
| `SOURCE_AUDIT.md` | This audit | None |

`ProcessReleasePolicy` retains the observed-crash fix: complete restoration AND
(clean release ACK OR confirmed owned-process exit). It does not assert a clean
release after a crash. For a never-started process it also leaves ACK false;
the internal idle helper historically supplies a vacuous release flag, but both
models accept the same no-owned-resource safe-OFF state. This extraction is not
a replacement for the actual process controller or its final status evidence.

## Why internal CMake and whole test directories are excluded

- Root `CMakeLists.txt` imports the application Core, RuntimeBroker, graphics
  probes, labs, Win32 tests, and more. It is not this preview's build entrypoint.
- `src/Nera.Core/CMakeLists.txt` pulls `GlobalDisplaySession.cpp`,
  `MediaHostController.cpp`, detector and broker clients, plus private-folder
  include roots. No whole Core source or binary is allowed here.
- `GlobalDisplaySession.h` directly includes display catalog, DLDR settings,
  feature settings and `MediaHostController.h`. The corrected cleanup invariant
  is extracted instead of accidentally importing that dependency chain.
- `src/Nera.GlobalDisplayTests` includes the internal session/runtime model and
  compiles runtime detection and display enumeration. Its whole test main is
  excluded; only focused new pure fixtures are included.
- `labs/Feature18CompatHost/CMakeLists.txt` contains optional external reference
  and proprietary SDK import/link paths, private-adapter targets and identity
  configuration. No part of that build file is reused.
- `DynamicFeature18Runtime`, private-parameter/scanner/settings contracts,
  caller compatibility code, host/controller/protocol, external reference
  sources, GPU shaders, Runtime/library binaries, build outputs, local evidence,
  and local configuration are excluded.
- `PresenterInputPolicy`, hotkey configuration, owned-window identity code and
  overlay monitor may have reusable portions, but depend on Windows platform
  types or implementation integration. They are not needed by this minimum
  standard-library-only preview and are excluded rather than expanded.

## Build graph and verification

The public CMake performs no `find_package`, `FetchContent`, `ExternalProject`,
`add_subdirectory`, script execution or network operation. Its only explicit
link dependency is the local `nera_safety_policies` INTERFACE target. Compiler
platform defaults may still list the operating system's ordinary CRT/import
libraries. No graphics or external vendor SDK/library is requested.

Local MSVC 19.44 x64 Release build: five executables, warnings-as-errors,
0 compile errors and 0 warnings. CTest: 5/5 PASS. Individual checks: capture
27/27, startup retry 89/89, release 326/326, HUD 22/22, timing seven independent
fixture stages. This is synthetic policy verification only. Cross-platform
compiler verification remains pending; no Linux success is claimed.

Code and CMake were scanned for vendor runtime/SDK loader names, private
adapter imports, caller identity APIs, application/project identifiers,
runtime-specific hashes, private machine paths and `Windows.h`: zero matches.
Header include closure was reviewed in full and has no internal relative escape.
The build used this subtree as `-S` with an independent temporary build directory;
the original application's source tree, runtime path and CMake are not inputs.

## Licensing scope

The source inventory is not a legal opinion or proof of every historical line.
The copied timing module and test retain their existing MIT SPDX header. Following
the project owner's explicit publication and license authorization, these selected
Nera-owned policies, fixtures and documentation are covered by the public tree's
root MIT LICENSE. No internal repository license is changed. No rights to omitted
compatibility implementations or any third-party Runtime are inferred.
