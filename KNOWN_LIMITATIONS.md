# Known limitations

This is **SOURCE_PREVIEW**, not an end-user Portable Alpha. It cannot turn on DLDR.

- No `Nera.exe`, GUI, first-run tutorial, icon, HDR capture/Presenter, RuntimeBroker or
  operational Feature18 adapter is included. No rendered screenshots or face comparisons
  are published. Runtime import cannot unlock this subset.
- CLI/MCP are real protocol implementations; commands requiring an application fail when
  there is no compatible, authorized control service. Tests use isolated fake backends.
- Package/source version is 0.5.0-alpha.2. Inherited protocol 7 / 0.4.1 handshake values
  describe the separately versioned protocol baseline, not shipped product binaries.
- Managed tests require Windows 11 x64. One bounded hotkey test can fail if all its spare
  candidates are already registered; it never closes the owning program.
- Native policy tests validate values/branches, not process termination, HDR restoration,
  input latency, secure desktop, protected media, or graphics performance.
- SDK/reference-pack restore may need official package access. No promise of offline
  first-build or Visual Studio-free native compilation is made.
- Operational private adapter permissions remain unresolved. There is no public Portable
  download in this preview. No NVIDIA endorsement or production-readiness claim is made.
- GitHub CI and publication must be confirmed on the actual repository after authenticated
  creation; a local workflow file is not a completed CI run.
