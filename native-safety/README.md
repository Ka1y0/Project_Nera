# Nera native safety source preview

This is a small, standalone source preview of value-only safety policies and
synthetic tests. It is **not the Nera application**, a display processor, a
runtime adapter, or a working DLSS5 implementation.

It neither captures nor returns screen pixels, creates windows, changes display
settings, registers input, launches processes, opens pipes, loads a runtime, nor
uses the network. It contains no external SDK headers, libraries, runtime
binaries, caller-identity substitutions, private parameter contracts, or
application identifiers.

## Build and test

A C++20 compiler, its standard library, CMake 3.20 or later, and a supported build
tool are required. No package download is performed.

```sh
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure
```

The CMake project only constructs five console test executables. Windows
compilers can require their normal platform toolchain and CRT libraries; these
are not third-party runtime adapter dependencies and are not shipped here.
MSVC has been tested. Other C++20 toolchains are not yet verified locally.

## Included behavior

- Capture availability decisions preserve observed non-black pixels even under
  an owned, capture-excluded control region. Only exact-black samples inside
  that verified region become ambiguous. Insufficient coverage is not evidence
  of a usable frame. Sustained-black decisions use both count and elapsed time.
- Automatic startup retry defaults to disabled until one end-to-end deadline
  contract is qualified. A timeout is not presumed transient.
- Safe OFF requires complete output/capture/process restoration evidence.
  Confirmed exit of an exclusively owned process can reclaim its process-local
  resources after a crash, without fabricating a clean release acknowledgement.
- HUD evidence checks keep alpha coverage and capture region identity distinct
  from merely accepted API calls.
- Rolling timing only counts successful-present timestamps supplied by the
  caller, expires stale samples, and handles paused collection explicitly.

## Evidence boundaries

These tests supply synthetic evidence. They do **not** prove physical monitor
output, protected-content detection, actual window capture exclusion, input
passthrough, process ownership, cleanup, frame rate, HDR correctness, or a live
graphics feature. An integration must establish those facts independently.

The capture policy does not identify DRM. Legitimate dark content may trigger
conservative bypass. Its owned-region mask must be produced from independently
verified owned-window identity, current rectangle, and capture-exclusion status,
using the same sample's coordinate snapshot. Caller-supplied window names are
not ownership proof.

`ProcessReleasePolicy.h` is a neutral extraction of a cleanup invariant, not a
public IPC or runtime ABI. Exit evidence must concern one retained owned process
handle/incarnation, with signaled completion and a successful exit-code query.
The reclamation rule is invalid if relevant resources are owned elsewhere.
An exit code and original failure reason remain diagnostic failures even after
normal display restoration succeeds. The policy never erases either reason.

## Rights and publication status

These selected Nera-owned source files are covered by the public tree's root
MIT LICENSE, under the project owner's explicit publication authorization.
Existing SPDX declarations are preserved. No internal repository license is
changed, and technical dependency exclusion grants no rights to the omitted
compatibility implementation, third-party SDKs or any Runtime.

`SOURCE_AUDIT.md` records exact included files, transformations, dependencies,
and the deliberately omitted internal build graph. Do not recursively publish
the surrounding repository or its Git history as part of this preview.
