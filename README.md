# Copilot Beacon v2

A lightweight local HTTP server that tracks AI agent activity (VS Code Copilot and Claude Code) using their official hook systems, then exposes state via SSE so downstream clients (LED strips, desktop widgets, etc.) can react in real time.

## States

| Mode | Meaning |
|---|---|
| **Idle** | No active signal — agent is not doing anything noteworthy |
| **Waiting** | Agent needs your attention (permission prompt, idle prompt) |
| **Done** | Agent finished responding |

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## Quick Start

```bash
cd src/beacon-core
dotnet run
```

The server starts on `http://127.0.0.1:17321`. Verify with:

```bash
curl http://127.0.0.1:17321/health
```

## Setup: Agent Hooks

The beacon relies on agent hooks — each agent runs a `curl` command at lifecycle moments, which POSTs JSON to the beacon server. You just need to drop a config file into your project (or globally).

### VS Code Copilot

Copy the hook config into your project's `.github/hooks/` directory:

```bash
mkdir -p /path/to/your/project/.github/hooks
cp hook-configs/copilot/CopilotHookSettings.json /path/to/your/project/.github/hooks/CopilotHookSettings.json
```

Copilot automatically discovers JSON files in `.github/hooks/`.

### Claude Code

Merge the hook entries from `hook-configs/claude-code/settings.json` into your project's `.claude/settings.json`:

```bash
# If you don't have a settings file yet, just copy it:
mkdir -p /path/to/your/project/.claude
cp hook-configs/claude-code/settings.json /path/to/your/project/.claude/settings.json
```

If you already have a `.claude/settings.json`, manually merge the `"hooks"` section into your existing file.

### Automated Install Script

Use the provided scripts to copy hook configs into any target project:

**Bash** (macOS / Linux / Git Bash on Windows):

```bash
./scripts/install-hooks.sh /path/to/your/project
```

**PowerShell** (Windows / pwsh):

```powershell
.\scripts\install-hooks.ps1 -TargetDir C:\path\to\your\project
```

Both scripts copy the Copilot config into `.github/hooks/` and the Claude Code config into `.claude/`. If a `.claude/settings.json` already exists, the script warns you to merge manually instead of overwriting.

### Per-Project vs Global

The steps above are **per-project** — hooks only fire in repos that have the config. If you want hooks in every project:

- **Copilot**: Place `beacon.json` in your global `.github/hooks/` directory
- **Claude Code**: Add the hooks section to `~/.claude/settings.json` (user-level settings)

## API

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Returns `{ "ok": true, "version": "2.0.0" }` |
| `/state` | GET | Returns current `{ "active", "mode", "source" }` |
| `/events` | GET | SSE stream of `BeaconEvent` objects |
| `/hook` | POST | Receives hook payloads from agent scripts |

### SSE Event Format

```json
{
  "eventType": "Done",
  "source": "Copilot",
  "hookEvent": "Stop",
  "reason": "Agent finished responding",
  "timestamp": "2026-02-21T12:00:00Z"
}
```

## Configuration

Edit `src/beacon-core/appsettings.json` or override via environment variables using the ASP.NET Core convention (`Beacon__PropertyName`):

| Setting | Default | Description |
|---|---|---|
| `Port` | 17321 | HTTP listen port |
| `VscodeProcessName` | "Code" | Process name for focus detection |
| `AfkThresholdSeconds` | 30 | Seconds idle before AFK return triggers Clear |
| `PollIntervalMs` | 250 | Focus/idle polling interval |
| `IdleTimeoutSeconds` | 300 | Seconds with no events before auto-clear |
| `FakeMode` | false | Cycle through states for testing |
| `CopilotEventMappings` | *(see below)* | Copilot hook event → beacon state mapping |
| `ClaudeEventMappings` | *(see below)* | Claude Code hook event → beacon state mapping |

### Default Event Mappings

Mappings are split per agent since each has different hook events:

**Copilot:**
```json
{
  "Stop": "Done",
  "PermissionRequest": "Waiting",
  "UserPromptSubmit": "Clear",
  "SessionStart": "Clear"
}
```

**Claude Code:**
```json
{
  "Stop": "Done",
  "SubagentStop": "Done",
  "Notification:permission_prompt": "Waiting",
  "Notification:idle_prompt": "Waiting",
  "UserPromptSubmit": "Clear",
  "SessionStart": "Clear"
}
```

To add a new mapping (e.g., treat Copilot's `PreToolUse` as `Waiting`), just add it to the appropriate section — no code changes required.

### Fake Mode

For testing without a real agent, enable fake mode to cycle through states every 10 seconds:

```bash
# Via environment variable
Beacon__FakeMode=true dotnet run

# Or in appsettings.json
{ "Beacon": { "FakeMode": true } }
```

## Architecture

```
Agent (Copilot/Claude) ──hook──► curl POST /hook ──► Beacon Server ──SSE──► Clients
                                                            │
                                                 IPlatformMonitor
                                                 (focus + idle)
                                                            │
                                                  PresenceClearService
                                                  (auto-clear on AFK return)
```

- **Hooks** use inline `curl` commands — agents pipe JSON to stdin, curl POSTs it to the server. No script files needed.
- **HookNormalizer** translates agent-specific events into unified `BeaconEvent` types using configurable per-agent mappings
- **EventBus** manages state and pub/sub to SSE clients
- **IPlatformMonitor** (Windows: `SetWinEventHook` + `GetLastInputInfo`; other platforms: no-op stub) detects focus changes and idle duration
- **PresenceClearService** watches for AFK-return to auto-clear stale signals

## Platform Support

| Platform | Hook Pipeline | Focus/Idle Detection |
|---|---|---|
| Windows | Full | Full (Win32 P/Invoke) |
| macOS/Linux | Full | Stub (no-op) — PRs welcome |

The hook-based detection (the core feature) is fully cross-platform. Only the presence clearing (focus + AFK) is Windows-specific, and it degrades gracefully — on other platforms, clearing happens via `UserPromptSubmit` and `SessionStart` hooks instead.
