# CLI source preview

Build with the root source-preview script. `Nera.AgentBridge` is a real client, but no Nera
application/service is shipped. `cli --help` describes commands without enabling a display.
An installed compatible application and explicit local permission would be required for
`status`, `dldr on`, `dldr off`, `displays`, `hud` and other product operations.

The preview contract tests exercise argument parsing, JSON output, permissions, revision
conflicts and return codes with a fake transport. Do not mistake these tests for a usable
Portable application.

| Exit code | Meaning |
|---|---|
| 0 | Successful command |
| 2 | Invalid arguments |
| 3 | Nera not running |
| 4 | Permission denied |
| 5 | State conflict |
| 6 | Runtime unavailable |
| 7 | Operation failed |
| 8 | Timeout |

Machine output uses `--json`. Do not parse localized human prose for automation. No generic
shell, process-kill, arbitrary file mutation or game-settings command is provided.
