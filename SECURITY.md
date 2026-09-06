# Security policy — Nera source preview

## System and scope

This policy covers this repository's managed Control, AgentBridge, Localization, test
projects, pure native policies and source-build tooling. There is no rendering application,
Runtime adapter or working display backend in this preview. The owner authorized this
source-only release and the privacy/input/runtime boundaries below; no broader risk waiver
or finding suppression is implied.

## Threat model and trust boundaries

Treat CLI arguments, MCP JSON, pipe messages, profiles and persisted settings as untrusted.
The intended transport is local stdio or a current-user-only Windows Named Pipe. There is
no intended TCP/HTTP listener or cloud credential requirement. Same-user access alone is
not consent to operate a display: command permissions and connection authorization still
matter. A version/name string supplied by a client is not cryptographic publisher identity.

Assets include command authority, private paths/settings, audit integrity, bounded memory,
state consistency and safe display-release evidence. Tests supply synthetic backends and
cannot establish live graphics/input safety. Root/administrator compromise and arbitrary
untrusted build scripts are not security boundaries this project can enforce; this does
not exclude reachable vulnerabilities with realistic lower-privilege impact.

## Required invariants

- Only Control owns mutable canonical state. UI/CLI/MCP/hotkey adapters may not invent ON.
- Mutations require authorization and revision validation; request IDs prevent duplicate
  application. Emergency requests must not be starved by ordinary work.
- A current-session first-frame gate requires coherent Feature/DLDR/Presenter evidence.
  Stale generation counters and fake fixtures are never real graphics proof.
- OFF/recovery requires display restoration and cleanup evidence. Confirmed owned-process
  exit can reclaim a crashed child without pretending it acknowledged a clean release.
- Pipe ACLs restrict access to the current user; remote clients are rejected. Enforce
  framing/size/depth limits before allocation and reject malformed/unsupported requests.
- Never add arbitrary shell, file-write/delete, process-kill, game-launch, screen-pixel,
  credential, driver or system-settings capabilities to the public tool surface.
- Logs/state must not contain images, secrets, private titles, command payloads or raw
  exception paths. Local retention and bounded queues must not block the control lane.
- Respect user input and explicit permission. No injection, driver/system modification,
  security/DRM bypass, or implicit consent based on being the same Windows user.
- Public assets exclude proprietary Runtime, private adapters, local grants, user media,
  private paths, credentials and internal history. Build outputs stay outside source.

## Reportable findings and severity

Report authorization bypass, cross-user/remote pipe exposure, secret/path disclosure,
unsafe path handling, exploitable unbounded parsing, forged lifecycle evidence, mutation
races, denial of emergency recovery, and release-boundary escape with concrete reachability.
Calibrate severity to privileges, exposure and actual impact. Passing tests, local-only
intent or an Experimental label do not dismiss a demonstrated vulnerability.

## Limits and reporting

There are no owner-approved blanket exclusions for same-user attacks or preview code.
Missing product components are absent attack surfaces, not a safety claim about the private
product. The source scanner is a heuristic release guard plus explicit allowlist, not a
complete vulnerability scan or proof of rights. Accepted risks beyond the listed scope
remain unresolved, not silently approved.

Do not post secrets, private files, screenshots or exploit payloads in a public issue.
A private GitHub vulnerability-reporting channel must be confirmed before submitting
sensitive findings; no private email or reporting endpoint is invented in this preview.
