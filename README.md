# Nera v0.5.0-alpha.2 — source preview

Canonical repository: [Ka1y0/Project_Nera](https://github.com/Ka1y0/Project_Nera).
The product name remains **Nera**.

Language choices keep Follow system first, then English, Simplified Chinese, Traditional Chinese.
The shared catalog sorts by canonical English names, independently of the UI language.

**SOURCE_PREVIEW, not a working DLDR application or public Alpha binary.** This repository
contains Nera's real managed control-plane and AgentBridge source with executable contract
tests. It does not include the WinUI application, capture, HDR Presenter, RuntimeBroker,
Feature18CompatHost, NVIDIA SDK libraries, private compatibility adapter, or NVIDIA Runtime.
There is no `Nera.exe` and no end-user binary release in this preview.

The useful part is the shared control design: serialized commands, revision conflict handling,
idempotent requests, first-frame evidence gates, lifecycle recovery, hotkey contracts,
local current-user pipe framing, CLI, stdio MCP, and English / Traditional Chinese /
Simplified Chinese localization. Tests exercise these components with fake backends. A
passing test does **not** prove DLSS5 processing, HDR output, graphics performance, or a
visible image change.

## Build and test

Use Windows 11 x64, 64-bit PowerShell 7, and a .NET 10 SDK. The current source was tested
with SDK 10.0.400. The managed build needs no Visual Studio, GPU SDK, NVIDIA DLL, administrator permission, game,
or cloud key. SDK reference-pack restore may use configured NuGet sources when
the required .NET/Windows reference packs are not already cached; the script never downloads
a graphics Runtime or installs an SDK.

```powershell
pwsh -File .\build-source-preview.ps1
```

The script validates the five-project managed dependency closure, copies only the declared
source selection into a new temporary directory, builds Release x64 there, then runs
`Nera.Control.Tests` and `Nera.AgentBridge.Tests`.
It prints the output directory and writes `source-preview-build-result.json` there. Output
must stay outside the source tree. Existing output is not overwritten or deleted.

```powershell
pwsh -File .\build-source-preview.ps1 -BuildOnly
pwsh -File .\build-source-preview.ps1 -OutputRoot "$env:TEMP\Nera-preview-my-new-run"
```

Tests use temporary local files and uniquely named, current-user pipes. One existing Windows
smoke test briefly registers and unregisters a Ctrl+Alt+Shift+F22–F24 candidate. It does not
send input, show a GUI, launch Nera, change Windows HDR, capture a display, or load a Runtime.

## What can run?

The tests run the actual state machine, command dispatcher, CLI formatting/exit-code logic,
MCP discovery/schema/dispatch logic, and pipe protocol. Their fake successes are test fixtures,
not a neural renderer. The built AgentBridge is a real client/stdio adapter, but this preview
does not supply a product control service or a display-processing backend. Runtime import
cannot turn this stripped preview into the full application.

Do not use a separately installed private Nera instance as accidental proof of this preview's
functionality. The supplied tests use isolated endpoints and do not contact a product instance.

## Source map

| Project | Included purpose |
|---|---|
| `src/Nera.Control` | Canonical state, command/permission/lifecycle contracts, local pipe server, hotkeys |
| `src/Nera.AgentBridge` | Real CLI and stdio MCP adapters; no direct Runtime loading |
| `src/Nera.Localization` | Three-language messages; private feature registry excluded |
| `src/Nera.Control.Tests` | Fake-backend state, recovery, revision, framing and hotkey contracts |
| `src/Nera.AgentBridge.Tests` | CLI/MCP/schema/permission/wire compatibility contracts |

`SOURCE_PREVIEW_BUILD.json` declares the exact build selection. It is not permission to copy
other internal components or generated files. `Nera.Localization.Tests` from the full internal
tree is intentionally not in this build: it links an App settings file; localization is still
compiled and exercised by the included bridge contracts.
`ExperimentalFeatureRegistry.cs` is also excluded: private Runtime key metadata is not
needed to build or test this managed control preview.

## Native safety policies

`native-safety` is a separate C++20 standard-library-only model and test suite. It includes
capture-probe validity, startup-retry qualification, rolling timing, HUD evidence and the
safe-OFF cleanup predicate. It does not execute Windows process termination, capture or HDR.
With CMake and an x64 C++20 compiler installed, build outside the source tree:

```powershell
cmake -S native-safety -B "$env:TEMP/NeraNativePreview" -A x64
cmake --build "$env:TEMP/NeraNativePreview" --config Release
ctest --test-dir "$env:TEMP/NeraNativePreview" -C Release --output-on-failure
```

The native build additionally needs a compiler; the managed build does not. See
[native-safety/README.md](native-safety/README.md) for exact model boundaries.

## License and provenance

This reviewed source subset is released under [MIT](LICENSE), by authorization of the project
owner. It does not grant rights to omitted components or third-party SDKs/Runtime.
Read [LICENSE_AUDIT.md](LICENSE_AUDIT.md), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md),
[PROVENANCE.md](PROVENANCE.md), [SECURITY.md](SECURITY.md) and [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).
Source package/assembly metadata is 0.5.0-alpha.2. Inherited protocol 7 / 0.4.1 handshake
constants identify the separately versioned application contract, not a shipped app binary.

No end-user Portable download exists in this preview. A source archive contains only the
reviewed files, a file manifest and source SPDX SBOM. CI builds/contracts do not download a
NVIDIA Runtime and do not test real neural rendering.

## Known limits and release boundary

- No DLDR ON workflow, monitor processing, GUI, rendered image, game support or performance claim.
- Windows-only qualification; successful compilation elsewhere would not prove Windows IPC or input behavior.
- Public product binaries remain blocked while private compatibility/identity permissions and
  other release gates are unresolved. User-imported Runtime distribution does not resolve those rights.
- The internal experiment leaves the Runtime file on disk unchanged but uses in-memory IAT
  caller compatibility in its isolated Host. None of that adapter is included here; it is not
  represented as an official NVIDIA SDK contract.
- No Runtime, screenshots, user images, private paths, local grants, logs, benchmark data, or internal Git history belong in this preview.
- No screenshot, download link, or supported-hardware promise is fabricated. MIT covers
  only the supplied reviewed source; it is not a blanket license for an internal product.

This source preview is experimental engineering material by Chips Studio / Ka1y0. See
[AGENTS.md](AGENTS.md) before extending it. Do not treat it as an end-user DLDR distribution.
