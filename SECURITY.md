# Security Policy

Spectra collects and displays security-sensitive data (Microsoft 365/Azure AD configuration, mailbox permissions, Conditional Access policies, etc.) for the tenants it's connected to. Reports of security issues are taken seriously.

## Reporting a vulnerability

Please report suspected vulnerabilities privately rather than opening a public GitHub issue — open issues are visible to everyone before a fix ships.

Use [GitHub's private vulnerability reporting](https://github.com/TitanGameDev/Spectra/security/advisories/new) for this repository. Include:

- A description of the issue and its potential impact
- Steps to reproduce (a minimal example if possible)
- The affected version/commit

You should expect an initial response within a few days. This is an alpha-stage project maintained on a best-effort basis, so please be patient — but security reports get priority over other work.

## Scope

In scope: the Spectra application itself (`backend/`, `frontend/`, `deploy/`) — authentication/authorization logic, data handling, the deploy scripts, and how it talks to Microsoft Graph/Exchange Online/Azure Resource Manager.

Out of scope: vulnerabilities in third-party dependencies (report those upstream), and issues that require an attacker to already have admin access to a deployed instance or to a customer's own Microsoft 365/Azure tenant.

## Known limitations

A few things are already documented as known, accepted tradeoffs rather than bugs — see the README's "Production hardening" and "Mechanics worth knowing" sections throughout for specifics (e.g. `TrustServerCertificate=true` for the optional SQL Server connection, the scope of the `deploy/install.sh` automation). If you're reporting one of those, a quick search of the README first is worth it, but when in doubt, report it anyway.
