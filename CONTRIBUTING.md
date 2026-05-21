# Contributing

Thanks for your interest in contributing! This project is a small, focused utility — bug reports, hook configs for other agents, and platform support PRs are all welcome.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- Windows for full feature work (focus/idle detection is Win32-specific). macOS/Linux contributors can still work on the hook pipeline, normalizer, state machine, and tests.

## Build & test

```bash
git clone https://github.com/Coopski101/AgenticUnattended-Service.git
cd AgenticUnattended-Service

# Restore + build
dotnet build AgenticUnattended-Service.sln --configuration Release

# Run tests (87+ xUnit tests)
dotnet test AgenticUnattended-Service.tests/AgenticUnattended-Service.tests.csproj --configuration Release

# Run the service locally
cd AgenticUnattended-Service
dotnet run
```

The service listens on `http://127.0.0.1:17321`. Verify with `curl http://127.0.0.1:17321/health`.

## Project layout

| Folder | Purpose |
|---|---|
| `AgenticUnattended-Service/` | Main service (ASP.NET Core minimal API + Avalonia tray) |
| `AgenticUnattended-Service.tests/` | xUnit test project |
| `hook-configs/` | Reference hook configs for Copilot and Claude Code |
| `scripts/` | Hook install helpers (bash + PowerShell) |
| `docs/` | Architecture diagrams and notes |

See [docs/architecture.md](docs/architecture.md) for a component overview.

## Pull request guidelines

1. **One change per PR.** Smaller PRs get reviewed faster.
2. **Add tests** for new behavior in `AgenticUnattended-Service.tests/`. Existing tests use xUnit + fakes (see `Fakes/`).
3. **Keep platform code isolated.** Windows-specific code lives under `Platform/Windows/`, macOS under `Platform/macOS/`, with `NullPlatformMonitor` as the cross-platform fallback. Don't sprinkle `#if WINDOWS` throughout the codebase.
4. **No new top-level dependencies** without discussion. Stick to BCL + ASP.NET Core + Avalonia where possible.
5. **Run `dotnet test` before submitting.** CI will run it too, but local iteration is faster.
6. **Don't commit `bin/`, `obj/`, or any `*.user` files.** The `.gitignore` already excludes them.

## Adding a new agent

To add support for another agent (e.g., a custom CLI tool):

1. Add a sample hook config under `hook-configs/<agent-name>/`. The hook should `curl` JSON to `POST /hook`.
2. Add an entry in `appsettings.json` under `Beacon:<Agent>EventMappings` mapping the agent's lifecycle event names to beacon states (`Done`, `Waiting`, `Clear`).
3. Update `HookNormalizer` only if the agent's payload shape differs from the existing ones.
4. Update the README with the new agent and any caveats.

## Adding a new platform

To add focus/idle detection on a new platform:

1. Create `Platform/<Platform>/<Platform>PlatformMonitor.cs` implementing `IPlatformMonitor`.
2. Wire it up in `Program.cs` behind the appropriate OS check.
3. The `NullPlatformMonitor` stub already provides graceful degradation, so partial implementations are fine.

## Reporting bugs

Open an issue using the bug report template. Include:

- OS and version
- .NET SDK version (`dotnet --version`)
- Agent (Copilot / Claude Code / other) and version
- Service logs (right-click tray icon → Open Log Window)
- Steps to reproduce

## Security issues

Don't open public issues for security vulnerabilities. See [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
