# Nera source-preview agent guide

## Scope and authority

This v0.5.0-alpha.2 tree is `SOURCE_PREVIEW`: real managed components and fake-backend contract tests, not
the working Nera GUI/DLDR distribution. Preserve that distinction in code, diagnostics,
README text and test reports. Do not advertise fake Processing/ON fixtures as graphics proof.
No `Nera.exe` or end-user binary is supplied. Do not invent a repository/release URL.

Keep the reviewed build closure in `SOURCE_PREVIEW_BUILD.json`: Localization, Control,
AgentBridge, Control.Tests, AgentBridge.Tests. Do not reintroduce private Feature18 ABI,
caller-name/IAT compatibility, Runtime payloads, SDK libraries, capture, Presenter, or product
GUI as an incidental dependency fix. No arbitrary shell, process-kill, file-write, game-launch,
screen-pixel or network-listener tools may be added to the AgentBridge command catalog.
Exclude `src/Nera.Localization/ExperimentalFeatureRegistry.cs`; its private key metadata
has no role in this preview's build or tests.

The source manifest is a build selection, not a legal or privacy clearance. Public product
binaries remain blocked pending private compatibility/identity rights and other release gates.
Use the repository owner's MIT license/notices for this reviewed subset only; do not infer blanket MIT
from a dependency, a source header, an experimental label or user Runtime import.

## Public GitHub language policy

PUBLIC_GITHUB_LANGUAGE_POLICY:

Until explicitly changed by the project owner, all repository-facing documentation,
release notes, research documentation, issue templates, and GitHub metadata must be
published in English only. Application localization is separate and must remain intact.

Do not add translated READMEs or documentation language-switch links. Preserve all
en-US, zh-CN and zh-TW application resources, language preferences, native language
names, localized tutorials, OSD and errors. Keep JSON keys, MCP tool names, CLI
commands, reason codes and protocol fields stable and untranslated.
When removing a document translation, first merge any unique technical facts into
the English document. Rewriting public history or replacing release assets requires
explicit project-owner authorization and a verified private backup. Validate documentation separately from
application resources and localization tests.

## Architecture

- `src/Nera.Control` owns canonical state and serialized commands. Preserve request IDs,
  idempotency, revision conflict checks, typed errors, permissions and emergency priority.
- `src/Nera.AgentBridge` adapts CLI and stdio MCP to that command contract. It must not become
  an independent state machine or load a Runtime. Strict wire models must stay in parity.
- `src/Nera.Localization` owns human-readable messages; stable machine IDs are not translated.
- Tests use fake backends and isolated endpoints. Do not replace them with a connection to
  an already-running private product or broaden their authority to obtain a passing result.

No backend is supplied here for real display processing. Runtime import does not enable it.
An AgentBridge built from the source is still a client and must obtain normal local control
permission before it can talk to any separately supplied compatible application.

## Build and verify

Prerequisites: Windows 11 x64, 64-bit PowerShell 7, .NET 10 SDK (tested with 10.0.400).

```powershell
pwsh -File .\build-source-preview.ps1
```

The script validates explicit project-reference closure, copies only reviewed source paths
and extensions to an isolated build tree, builds outside the original source tree,
then runs both existing executable test suites. It is not an MSBuild sandbox for untrusted
repositories. Review source and inherited build configuration before executing it. SDK
reference-pack restore may use configured NuGet sources; no NVIDIA Runtime or SDK is fetched.

The tests create temporary files and uniquely named local current-user pipes. One existing
Windows smoke test temporarily registers/unregisters a Ctrl+Alt+Shift+F22–F24 chord. They
never send input or launch games, GUI, native Host, or a neural renderer. Report a registration
conflict honestly; never terminate the owning application. Clean only a test's own exact paths.

Use `-BuildOnly` when execution is unavailable, and report tests as `NOT_RUN`, not PASS.
OutputRoot must be new and outside the source tree. Never commit artifacts, local test output,
Data, generated binaries, PDBs, user content, private paths, credentials or internal Git history.

## Contract invariants

- ON requires current-session, fresh, coherent evidence for Feature18, DLDR and Presenter;
  counters or success booleans from a stale Host generation do not satisfy the gate.
- Missing telemetry is unavailable, not zero or guessed performance.
- Recovery retains ownership until exit/restore is proven. Do not fabricate an OFF gate.
- Unknown parameters and unsupported commands fail closed; no heuristic UI Protection or
  media viewer commands reappear in the stable product surface.
- Keep framed, bounded JSON; MCP stays stdio; product IPC is current-user local pipe, not TCP.
- Keep tests for malformed/oversized messages, permission denial, revision conflict,
  duplicate requests, session changes, recovery and shared Control/Bridge snapshots.

This preview supplies no screen-based acceptance or performance evidence. Human graphics QA,
Runtime qualification, public binary rights and a finished product release are separate work.

## Native policies and release

`native-safety` contains standard-library-only C++20 policies and tests. Do not connect it to
the internal Core or include private runtime structs to fix a build. Run its standalone
CMake/CTest suite. The source scanner, localization audit and source-only packaging must pass
before a commit is tagged. Source SPDX is not a binary SBOM.

Use an explicit reviewed path list for Git staging. Never stage the internal repository or
its history, use an unauthorized or blind force-push, overwrite an unrelated remote, or include generated binaries.
Build, package and logs go outside this source tree. Product release authorization and
credentials must not be inferred from an existing connector or a tests-only PASS.
