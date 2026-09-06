# License audit — reviewed source subset

Date: 2026-09-04. Scope: the files in this new, source-only public candidate tree.
This is an engineering provenance review, not a legal opinion or NVIDIA authorization.

## Decision

**MIT for the Nera-owned source preview only. Product binary: BLOCKED.**

The project owner expressly authorized publication and use of MIT where the included
source and dependencies permit it. Review of the managed dependency closure and the
standalone native policy subset found no copied third-party implementation or linked
proprietary library within this selection. The owner publishes these selected files under
[LICENSE](LICENSE). The internal repository's license status is not changed.

## Reviewed components

- Control, AgentBridge, Localization, and two managed contract-test projects. Their only
  project references are within this five-project closure; no PackageReference is present.
- Pure C++20 policies and deterministic tests, with standard-library dependencies only.
- New source-only build, scan, packaging, CI and documentation files.
- No graphical assets, fonts, user content, SDK implementation, private Runtime adapter,
  Runtime payload or full internal history.

## Private compatibility boundary

The omitted operational display Host uses an undocumented Feature18 contract and in-memory
caller-name/IAT compatibility. Applicable permission to publish that operational integration
has not been established. Unchanged on-disk Runtime hashes do not mean the loaded image is
unmodified, and valid Authenticode does not grant redistribution rights.

Public binary distribution remains blocked until the exact Runtime's provenance and terms,
private entry-point use, caller compatibility, linked SDK obligations and product use are
cleared. The [NVIDIA SDK license at a fixed reviewed commit](https://github.com/NVIDIA/DLSS/blob/a291cc7d2cc642a51566f3dfd5376f635cd1b284/LICENSE.txt)
is relevant context, not proof that it grants rights to the separately obtained Runtime.
No conclusion of infringement is asserted here.

There is no borrowed commercial Application ID or third-party Project ID in this preview.
It contains no Runtime initialization implementation. A supported file hash/version in a
validation model is public compatibility data, not a credential or distribution license.

## Limits

Source review cannot prove the entire history of every line. This review is deliberately
limited to the allowlisted files. Introducing more code, generated binaries, SDKs or assets
requires a fresh provenance/terms review. .NET, Windows and build tools keep their own terms;
MIT does not relicense them. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
