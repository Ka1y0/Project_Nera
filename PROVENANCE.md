# Source provenance and exclusions

This is a new source-preview history, not a mirror of an internal development repository.
The initial release selected reviewed text files. No original Git objects, local configuration,
Runtime, logs, benchmark data, game settings or user files were imported.
The later README showcase adds one explicitly authorized real product screenshot as described below.

| Public area | Origin and publication scope |
|---|---|
| `src/Nera.Control` | Nera-owned canonical control, evidence, IPC, hotkey and lifecycle implementation |
| `src/Nera.AgentBridge` | Nera-owned CLI and stdio MCP implementation, no runtime loader |
| `src/Nera.Localization` | Nera-owned localizer, language preference store and three language resources; private feature-key registry omitted |
| `src/Nera.Control.Tests`, `src/Nera.AgentBridge.Tests` | Nera-owned deterministic fake-backend and transport/dispatch contracts |
| `native-safety` | Reviewed pure policies and new standalone fixtures; see its [source audit](native-safety/SOURCE_AUDIT.md) |
| Root/docs/tools/CI | Source-preview-specific build, release boundary, notices and verification material |

Managed package metadata is 0.5.0-alpha.2. Wire protocol 7 and the inherited 0.4.1 product
handshake constants are intentionally retained as compatibility-contract data; they do not
assert that an application/adapter binary is included. Tests exercise that contract.

Some test fixtures use obviously synthetic drive-root paths to exercise redaction and
validation. They refer to no actual user's files. The boundary scanner reports reviewed
synthetic literals separately from real private-path matches; it does not hide actual paths
by encoding them. No real Runtime path is in the source.

The source SPDX manifest inventories included files and hashes. It is a source SBOM, not a
full product/publish-output SBOM. Its own file and generated release bookkeeping are excluded
from self-referential checksums and declared as such. The ZIP checksum identifies the final
archive as a whole.

## Not included

WinUI GUI, monitor capture, HDR Presenter, RuntimeBroker, operational Host, private ABI,
IAT/caller compatibility, third-party SDKs and derived adapters, InputLab/InputTrace, user images,
fonts and product binaries. Resources naming a feature do not ship that feature.

## Documentation screenshot

`docs/media/nera-main-ui-en.png` was captured from the real current internal Nera Home UI
in English, with DLDR OFF. The owner authorized a temporary OFF-only diagnostic to lift
main-window capture exclusion. It changed no controls, visual parameters or layout code;
normal Windows window sizing was used. Original application files were restored after capture.

The image was reviewed for private content and re-encoded as PNG without source metadata.
It contains no user files, account details, private paths or personal photographs. It is not
generated, a mockup, a before/after comparison or evidence that this source preview includes
the pictured application. Screenshot publication is owner-authorized. No font binaries are
distributed, and no license to third-party marks or Windows UI assets is implied. SPDX records
the image separately as IMAGE / NOASSERTION rather than asserting a source-code license for it.

The boundary scanner pins its exact path and SHA-256. A replacement image requires fresh
privacy/publication review and a deliberate hash update. Other images remain disallowed.

## Required review for future additions

Identify author/source, exact upstream revision and terms, retained notices, dependencies,
privacy risks and whether a real initialization/processing/cleanup path is introduced.
Never infer permission from a successful experiment, a disclaimer, or user Runtime import.
