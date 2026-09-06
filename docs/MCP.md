# Local stdio MCP source

`Nera.AgentBridge mcp` hosts the source implementation on stdio, not a network port. The
tool catalog is defined in `src/Nera.AgentBridge/ToolCatalog.cs`; strict JSON schemas reject
unsupported fields and mutating tools require an expected revision. Operations and errors
are structured. Product operations require an authorized, compatible Nera control service,
which is not part of this preview.

The test suite covers initialize/discovery, tool schemas, dispatch, errors, malformed input,
permissions and protocol compatibility using fake transport. It includes the implementation's
new and legacy negotiated protocol paths. No cloud API key is required. Do not configure an
Agent to treat fake test evidence as rendered output.

No Agent configuration is automatically installed or modified. Review [AI_NATIVE.md](AI_NATIVE.md)
and [../SECURITY.md](../SECURITY.md) before integrating a separately supplied backend.
