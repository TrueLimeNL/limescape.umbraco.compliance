# Changelog

All notable changes to this package are recorded here. The format follows Keep a Changelog and the versions follow Semantic Versioning.

## 1.0.0 - 2026-11-23

First public version of the Compliance Pack for Umbraco 17 LTS:

- Retention classes with computed retention periods, a daily job and a content app.
- Deletion block while a retention period or a legal hold is active, on every route including the recycle bin.
- Publication gate that requires an approval record for exactly the version that is published.
- Append-only audit log and deletion log with field-level diffs and a query API.
- Purge of the recycle bin after a restoration period.
- Submission guard for required fields, and archived fields and deprecated values.
- Lifecycle statuses with a protected archive and rejection route.
