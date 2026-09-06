# AI Native source contract

AgentBridge is a locally buildable CLI and stdio MCP adapter. It has no rendering backend
and does not launch a private Host. Control serializes canonical commands; snapshot revision,
request identity, permissions, bounded framing and first-frame evidence are shared contracts.

Transport uses the current user's `Nera.Control.*` pipe namespace, explicit protocol/version
handshake and framed JSON. MCP uses stdio. No TCP or HTTP listener is intended. Authorization
is still required; a connected local client does not gain implicit display-control consent.

Read [CLI.md](CLI.md), [MCP.md](MCP.md) and [../SECURITY.md](../SECURITY.md). In this source
preview, use the isolated contract tests rather than contacting a separately running product.
The tests' fake Processing states never demonstrate real DLSS5/HDR functionality.
