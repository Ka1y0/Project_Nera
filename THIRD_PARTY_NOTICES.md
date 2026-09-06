# Third-party notices — source preview

The files supplied in this source-only repository are Nera-owned code and documentation,
licensed under the accompanying MIT License by the project owner. Existing MIT SPDX
identifiers are retained. This decision covers only this reviewed preview, not an internal
repository, omitted components, third-party trademarks, SDKs or a future product binary.

## Build and platform dependencies (not bundled)

| Dependency | Use | Source / terms |
|---|---|---|
| .NET SDK 10.0.400 and .NET/Windows reference packs | Build the five managed projects | [.NET repository](https://github.com/dotnet/runtime); [.NET license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) and separately supplied SDK notices |
| Windows system APIs | User-local pipe ACLs and bounded hotkey registration | Part of the user's Windows installation; no Microsoft system binary or sample implementation copied |
| CMake 3.20 or newer; C++20 compiler and standard library | Optional native policy tests | Separately installed tools; no compiler/runtime payload supplied |
| GitHub Actions checkout/setup-dotnet/upload-artifact | Optional hosted CI | Actions referenced at fixed commits; not vendored into this source package |

No third-party NuGet `PackageReference`, MCP library, Hook library or JSON library is
vendored. JSON uses .NET's `System.Text.Json`; CLI and MCP code are Nera implementations.
Protocol conformance does not imply endorsement by the protocol authors.

## Explicitly excluded

No NVIDIA Runtime, NGX/Streamline library or header, third-party Feature18 adapter,
SAOG-derived private adapter, Magpie/RenoDX/Feeder source, fonts, icon images,
or user media is shipped here. References to unavailable capabilities in types or translated
messages are not executable implementations of those capabilities.

The sole documentation PNG shows the actual internal Nera UI. Publication is owner-authorized;
it includes rendered system UI but no font binaries. It does not grant rights to third-party
marks. Its SPDX record uses IMAGE / NOASSERTION, distinct from MIT source-code records.
See the capture and privacy boundary in [PROVENANCE.md](PROVENANCE.md).

The excluded operational adapter has separate, unresolved authorization and distribution
questions. Neither this MIT license nor user-imported Runtime files resolve those questions.
See [LICENSE_AUDIT.md](LICENSE_AUDIT.md) and [PROVENANCE.md](PROVENANCE.md).
