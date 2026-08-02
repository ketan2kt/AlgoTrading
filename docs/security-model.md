# Security Model

## 1. Objectives

Protect capital, broker authority, personal data, configuration integrity, and audit evidence. A compromised browser, leaked log, malformed provider response, or ordinary configuration mistake must not silently grant live-trading authority.

## 2. Trust boundaries

- Browser ↔ ASP.NET Core over HTTPS
- API/Worker ↔ PostgreSQL
- Backend ↔ Groww and approved external providers
- Service process ↔ operating-system secret store/environment
- Administrator/operator ↔ protected control endpoints
- Build/test environment ↔ production environment

Angular and SignalR clients are untrusted presentation endpoints. Provider and broker payloads are untrusted inputs. Database content is not a substitute for live broker truth.

## 3. Identity and access

- ASP.NET Core Identity or an equivalent supported implementation with Argon2id or PBKDF2 password hashing via vetted libraries/configuration.
- Unique accounts; no shared administrator credential.
- Roles: Administrator, Operator, Auditor/Viewer; policies protect live activation, emergency control, secret status, and configuration.
- Secure, HttpOnly, SameSite cookies for the local web UI; antiforgery protection on state-changing cookie-authenticated requests.
- Rate limiting and lockout on login; session rotation after authentication; recent re-authentication for critical actions.
- Bootstrap administrator creation is one-time, local, audited, and disabled after completion.

## 4. Broker secrets

The backend accepts a manually generated daily Groww access token through an administrator-only, antiforgery-protected endpoint. `IGrowwTokenVault` encrypts it with ASP.NET Core Data Protection before PostgreSQL persistence; the protected value is never returned by an API. Persistent Data Protection keys must remain outside the database and be restricted to the App Service identity. `IGrowwAccessTokenProvider` uses a valid protected token first and the configured process environment variable only as a backend fallback. The token is added directly to the `Authorization` header and is never included in exception messages, Angular assets, health responses, audit details, or normal logs.

Automated API-key/secret or TOTP flows are not implemented until Groww confirms unattended approval and acceptable secret custody. The user's normal Groww username and password are never requested.

- Never collect or store the normal Groww username/password.
- Never send API key, API secret, access token, or TOTP material to Angular.
- Development: .NET user-secrets or environment variables; `.env` is ignored and only `.env.example` placeholders are committed.
- Windows deployment: prefer Windows Credential Manager/DPAPI or an approved local vault under the service identity.
- PostgreSQL may store the daily access token only as Data Protection ciphertext, together with expiry and non-sensitive update metadata—never as plaintext.
- Tokens live in memory for the minimum necessary duration, are never included in exception detail, telemetry, health payloads, or audit before/after values.
- Rotation/revocation is documented and independently testable. Process memory dumps and diagnostic endpoints are restricted.
- Do not store a TOTP seed unless Groww explicitly permits the flow and the user approves the residual risk; prefer an official non-seed unattended mechanism.

## 5. Live activation controls

Live functionality is compiled/registered only in the live deployment profile and remains disabled. Activation requires:

1. Administrator policy and recent authentication.
2. Explicit mode and account/environment confirmation.
3. Expiring, server-side activation lease.
4. Healthy broker, live feed, database, clock, calendar, reconciliation, and risk state.
5. No kill switch, unresolved order, position mismatch, or stale required data.
6. Audited acknowledgement of quantity/exposure/daily-loss limits.

Configuration changes cannot silently activate live trading. Activation expires on restart, configured timeout, account change, serious incident, or kill-switch event.

## 6. Application and API controls

- Validate all DTOs and reject unknown/unsafe enum values.
- Authorise at endpoint and application-command level.
- Use anti-CSRF tokens for cookie-authenticated mutations; strict CORS allowlist.
- Angular uses default contextual escaping; avoid bypass APIs and raw HTML.
- Apply Content Security Policy, HSTS in production, secure headers, request-size limits, and OpenAPI exposure policy.
- SignalR groups are user/role authorised; sensitive events are projected to safe DTOs.
- Configuration changes use optimistic concurrency, validation, reason, actor, before/after redaction, and audit event.
- Emergency controls are idempotent and resistant to double-click/replay.

## 7. Network and supply chain

- TLS certificate validation is never disabled.
- Broker/provider base URLs are fixed by trusted configuration; prevent SSRF and arbitrary callback URLs.
- Pin NuGet/npm dependencies and lockfiles; use vulnerability scanning and dependency review.
- CI uses no live credentials and cannot reach a live order path.
- Production service identity has least privilege: no interactive login, scoped filesystem/database access, and no source-tree write access.
- PostgreSQL is not exposed publicly; unique least-privilege application and migration identities.

## 8. Logging, audit, and privacy

Serilog structured logging uses an allowlist of fields. Redact authorization headers, cookies, secrets, tokens, TOTP, account identifiers, full provider payloads containing personal data, and exception request bodies.

Security audit events include login/lockout, role changes, secret reference changes, mode/activation changes, risk/configuration changes, kill switch, manual exit, reconciliation override, and log/export access. Audit records are append-only with UTC time, actor, source, correlation, reason, and redacted before/after hashes where appropriate.

The one-time Azure administrator bootstrap may create an administrator or reset the password of the existing administrator only when both the configured username and email match. It runs from the protected `azure-paper` GitHub environment, records a redacted audit event, deliberately stops startup, and requires the workflow to remove all temporary bootstrap settings before normal restart.

Retention is documented by data class. Export endpoints are authorised, bounded, watermarked/audited, and CSV-injection safe.

## 9. Threats and mitigations

| Threat | Primary controls |
|---|---|
| Browser/UI toggles live label | Server mode record, gateway registration boundary, activation lease |
| Stolen token/secret | OS secret store, backend-only use, redaction, rotation, short lifetime |
| Replay/duplicate command | Antiforgery, idempotency key, unique DB constraints, command ledger |
| Unknown broker submission | Query/reconcile by reference; never blind retry |
| Malicious/stale feed | Strict schema, source/freshness/sequence validation, trading suspension |
| Privilege escalation | Policy authorization, recent auth, audited role changes |
| SQL/XSS/CSRF | EF parameters, validation, Angular escaping/CSP, antiforgery/SameSite |
| Log leakage | Allowlist logging, destructuring policies, automated secret scans |
| Tampered configuration/audit | Concurrency, append-only events, hashes/backups, restricted DB roles |
| Worker duplication | Database singleton lease/account ownership fence |
| Dependency compromise | Lockfiles, trusted registries, scanning, staged upgrades |

## 10. Security gate checklist

- Threat model reviewed and updated
- Secret storage and rotation demonstrated without revealing values
- Authentication/RBAC/antiforgery tests pass
- No secrets in Git history, logs, Angular bundles, fixtures, or error responses
- Dependency vulnerability scan reviewed
- TLS/headers/CORS/CSP verified
- Live activation and kill-switch authorization tested
- Backup restore and audit retention verified
- Open findings assigned severity, owner, and due gate
