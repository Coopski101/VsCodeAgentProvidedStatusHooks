# Security Policy

## Reporting a vulnerability

**Do not file public GitHub issues for security vulnerabilities.**

Please report security issues through GitHub's private vulnerability reporting:

1. Go to the [Security tab](https://github.com/Coopski101/AgenticUnattended-Service/security) of the repository.
2. Click **Report a vulnerability**.
3. Fill out the form describing the issue, impact, and reproduction steps.

You can expect an initial acknowledgment within 7 days. If the issue is confirmed, a fix will be developed privately and disclosed coordinated with the release.

## Scope

This project runs as a local HTTP service on `127.0.0.1` and is not intended to be exposed to remote networks. Reports about the following are in scope:

- Authentication / authorization gaps when the service is bound to a non-loopback interface
- Hook payload parsing leading to crash, memory corruption, or arbitrary code execution
- SSE handler or endpoint vulnerabilities (DoS via slow clients, resource exhaustion)
- Privilege escalation via the auto-start (registry) mechanism on Windows
- Dependency vulnerabilities not already tracked by Dependabot / GitHub advisories

Out of scope:

- Issues that require the user to already have local code execution as the same user the service runs under
- Social-engineering attacks against the user to install malicious hook configs
- Reports against `127.0.0.1`-only behavior when the user has explicitly rebound the listen address to a public interface

## Supported versions

Only the latest released version receives security fixes.

| Version | Supported |
|---|---|
| latest `main` | ✅ |
| older tags | ❌ |
