# Security policy

## Supported versions

The latest minor version of the current major version receives security fixes.

## Reporting a vulnerability

Do not open a public issue for security problems. Use GitHub's private vulnerability reporting ("Report a vulnerability" on the Security tab of the repository) and include:

- the affected version and Umbraco version,
- steps to reproduce or a proof of concept,
- the impact you expect (for example: deletion block bypassed, approval forged, audit entry missing or altered).

We acknowledge reports within five working days and aim to release a fix within 30 days for confirmed issues, coordinating disclosure with the reporter.

## Scope

In scope: any route that bypasses the deletion block, retention calculation or publication gate; forging or replaying acting-user tokens; header injection; tampering with audit or deletion log entries; privilege escalation through the package's Management API endpoints.
