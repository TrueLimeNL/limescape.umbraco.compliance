# Contributing

Thanks for helping improve the Limescape Umbraco Compliance Pack.

## Ground rules

- Protective rules (deletion block, publication gate, retention) are enforced server-side in Umbraco notification handlers, never only in the backoffice UI. Every protective rule ships with a negative test that bypasses the UI (for example a direct service or Management API call) and asserts the operation is cancelled.
- Keep the package generic: no site-, customer- or organisation-specific code, names or defaults. Behaviour that differs per site is configuration.
- Audit and deletion logs are append-only. Do not add code paths that update or delete log rows.
- User-facing text is localized in English and Dutch (`Client/src/localization`, and server-side resources for API messages).

## Workflow

1. Open an issue describing the change before large pull requests.
2. Build and test locally: `dotnet build` and `dotnet test --project tests/Limescape.Umbraco.Compliance.Tests` from the repository root.
3. Keep pull requests focused; include tests and update `README.md` when behaviour or configuration changes.

## Releases

Releases are cut by tagging `compliance-v<version>` (for example `compliance-v1.0.0`). CI builds, tests, packs and publishes to NuGet.
