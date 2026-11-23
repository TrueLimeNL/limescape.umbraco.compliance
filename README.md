# Limescape Umbraco Compliance Pack

Repository-grade governance for Umbraco 17 LTS, enforced server-side for every route (backoffice, Management API, scheduled jobs, bulk operations):

- **Retention classes** with computed retention periods (duration, start event, end action), assigned per document type, category or item.
- **Deletion block** while a retention period or a legal hold is active, including emptying the recycle bin.
- **Publication gate**: configured document types publish only with an approval record for exactly that version.
- **Audit log and deletion log**, append-only, recording the acting person (also when changes arrive through a trusted integration), with field-level diffs and a query API.
- **Purge** of the recycle bin after a restoration period, skipping everything under retention or legal hold, taking the elements (media) of a purged document along.
- **Submission guard**: a document cannot be submitted for review while required fields are empty.

> Status: version 1.0.0. Included: the package tables, acting-user tokens, retention (classes, calculation, daily job, content app), the deletion block with legal hold, the publication gate, the audit handlers with the audit and deletion-log query API, purge of the recycle bin and the submission guard.

## Requirements

- Umbraco CMS 17 LTS on .NET 10
- Any database Umbraco supports (SQL Server, Azure SQL, SQLite)

## Install

```bash
dotnet add package Limescape.Umbraco.Compliance
```

The package registers itself through an Umbraco composer; no startup code is needed. The backoffice extension is served from `/App_Plugins/Limescape.Umbraco.Compliance`.

On startup Umbraco runs the package migration that creates the tables `lsAudit`, `lsDeletionLog`, `lsApproval`, `lsRetentionClass`, `lsRetentionAssignment` and `lsItemRetention` (unattended, unless `Umbraco:CMS:Unattended:PackageMigrationsUnattended` is `false`). Since Umbraco 17.7 this happens in a background service just after the site starts; the runtime level is `Upgrading` until it finishes.

## Acting user

Changes are attributed to the person who acted, not to the technical account. Without anything configured that is the signed-in Umbraco user (channel `backoffice`, or `api` for an API user). A trusted server-side integration that calls the Management API with an API user can name the person it acts for by sending a signed acting-user token.

### Header format

```text
Authorization: Bearer <access token of the Umbraco API user>
X-Limescape-Acting-User: <compact JWS>
X-Correlation-Id: <optional, used when the token has no cid>
```

The token is a JWT in compact JWS form, signed with the integration's private key. Header: `alg` one of `ES256`, `ES384`, `ES512`, `RS256`, `RS384`, `RS512`, `PS256`, `PS384`, `PS512` (asymmetric only; `none` and `HS*` are rejected). Payload:

| Claim | Required | Meaning |
|---|---|---|
| `iss` | yes | Issuer; must match a configured trusted issuer exactly |
| `sub` | yes | Person id in the issuing system (max 255 characters); stored as the actor |
| `name` | no | Display name (max 255) |
| `channel` | yes | Channel the person acted through, lowercase `[a-z][a-z0-9_-]{0,31}`, for example `app`, `assistant`, `api`, `job` |
| `cid` | no | Correlation id (max 128) |
| `iat` | yes | Issued at (seconds since epoch) |
| `exp` | yes | Expiry; `exp - iat` at most 60 seconds |
| `nbf` | no | Not before |
| `aud` | if configured | Must contain the configured audience |

Example payload:

```json
{ "iss": "https://integration.example", "sub": "4f1c…", "name": "A. Person", "channel": "assistant", "cid": "7d0e…", "iat": 1791460800, "exp": 1791460830 }
```

Validation allows a clock skew of 10 seconds (configurable). Send a fresh token per request.

### Behaviour

- **No header**: the request passes; the actor is the signed-in Umbraco user, or `system` when there is none (background jobs, anonymous requests).
- **Valid token**: the actor is `sub`/`name`/`channel` from the token; the authenticated Umbraco user is recorded as the technical identity, together with the issuer.
- **Header present but invalid** (bad signature, unknown issuer, expired, lifetime over 60 seconds, missing claim, empty or repeated header): the request is rejected with `401` before any endpoint runs, so nothing is written. The package never falls back to the Umbraco user when a token was presented. Response body (`application/problem+json`):

  ```json
  { "status": 401, "title": "Invalid acting-user token", "detail": "...", "code": "acting_user_invalid" }
  ```

The reason is logged as a warning (never the token itself).

### Configuration

```json
{
  "Compliance": {
    "ActingUser": {
      "HeaderName": "X-Limescape-Acting-User",
      "ClockSkew": "00:00:10",
      "TrustedIssuers": [
        {
          "Issuer": "https://integration.example",
          "Audience": "https://content.example",
          "PublicKeys": [ "-----BEGIN PUBLIC KEY-----
...
-----END PUBLIC KEY-----" ]
        }
      ]
    }
  }
}
```

`PublicKeys` takes PEM-encoded EC (P-256/384/521) or RSA (2048 bits or more) public keys; list two keys while rotating. `Audience` is optional. With no trusted issuers, every request that carries the header is rejected.

In code, `IActingUserAccessor.Current` returns the actor for the current operation.

## Audit and deletion log

`IAuditWriter` and `IDeletionLogWriter` append rows to `lsAudit` and `lsDeletionLog`. Both take the actor, channel, technical identity, correlation id and UTC timestamp from `IActingUserAccessor` and have no update or delete path.

- `lsAudit`: one row per change: action (`create`, `update`, `publish`, `unpublish`, `move`, `trash`, `restore`, `delete`, `relate`, `unrelate`, `permissionChange`, `approval`, `publishBlocked`, `deleteBlocked`, `retentionChange`), entity type and key, and for field-level changes the field with old and new value (each truncated to 2,000 characters, ending in `…`).
- `lsDeletionLog`: one row per event (`trashed`, `restored`, `deleted`, `purged`, `purgeSkipped`) with a title snapshot and the retention class that applied, and for purges the reason. The history of an item is the sequence of its rows.

### What is audited

The package's notification handlers are the one source of audit entries for content. Every route (backoffice, Management API, integrations such as the Agent API, jobs, code) goes through `IContentService` and `IMediaService`, so the same handlers see every change. Integrations should not write their own entries for content changes, or the change is logged twice.

| Operation | Notification | Audit entries | Deletion log |
|---|---|---|---|
| Create document or media | `ContentSaving`/`ContentSaved`, `MediaSaving`/`MediaSaved` | `create` without field (type and parent as JSON), plus `create` per filled field | |
| Update | same | `update` per changed field with old and new value; a save without changes writes nothing | |
| Publish, unpublish | `ContentPublished`, `ContentUnpublished` | `publish` (with the published cultures for variant types), `unpublish` | |
| Move | `ContentMoved`, `MediaMoved` | `move`, field `@parent`, old and new parent key (`root` for the root) | |
| Move to recycle bin | `ContentMovedToRecycleBin`, `MediaMovedToRecycleBin` | `trash` for the trashed item | `trashed` |
| Restore from the recycle bin | `ContentMoved`, `MediaMoved` (out of the bin) | `restore` | `restored` |
| Delete (also from the bin, emptying the bin, deleting a type) | `ContentDeleted`, `MediaDeleted` | `delete` per deleted item, descendants included | `deleted`, or `purged` with the reason when the purge deleted it |
| Typed link between two documents created or removed | `RelationSaved`, `RelationDeleted` (through `IRelationService`) | `relate` or `unrelate` on **both** documents, field `@relation.<alias>`, value `{"parent":"<key>","child":"<key>"}` in new (relate) or old (unrelate). Umbraco's own relation types (`umbDocument`, `umbMedia`, `umbMember`, on copy, on delete) and non-document relations are not audited | |
| User group changed | `UserGroupSaving`/`UserGroupSaved` | `permissionChange` per changed field: `@name`, `alias`, `sections`, `permissions`, `granularPermissions`, `startContentId`, `startMediaId`, `hasAccessToAllLanguages`, `languages` | |
| User group deleted | `UserGroupDeleted` | `permissionChange`, field `@deleted` | |
| Users added to or removed from a group | `UserGroupWithUsersSaved`, `UserSaving`/`UserSaved` | `permissionChange` on the user, field `groups` | |
| Node permissions assigned | `AssignedUserGroupPermissions` | `permissionChange` on the group, field `permissions[{nodeId}]` | |
| Approval, retention change, refused publish or delete | written by the publication gate, retention and deletion block | `approval`, `retentionChange`, `publishBlocked`, `deleteBlocked` | |

Field names are the property alias, with culture and segment when the property varies (`title[nl-NL]`, `title[nl-NL|mobile]`), and `@name` for the item name. Values are stored as text: strings as they are, numbers and dates invariant (ISO 8601), booleans `1`/`0`, other values as JSON. The "saving" notification takes a snapshot of the stored item; the "saved" notification compares it with the saved item. Saved notifications fire after the operation's transaction, so a failing audit write is logged as an error and does not undo the change.

The deletion log reads the retention class before the retention handler removes the item's retention row on delete. The composer registers the audit handlers first for that reason: Umbraco runs the handlers of one notification in registration order.

### Query API

Under `/umbraco/management/api/v1/compliance`:

| Method and path | Purpose |
|---|---|
| `GET audit` | Audit entries, newest first |
| `GET deletion-log` | Deletion-log events, newest first |

Filters (all optional, combined): `person` (acting person id), `item` (entity key), `action` (for the deletion log: the event), `entityType`, `channel`, `from` and `to` (UTC, ISO 8601 date or date-time; `to` is exclusive). Paging with `skip` and `take` (1 to 500, default 100). The response is `{ "total", "skip", "take", "items": [...] }`.

The history of one item (`item` set) needs access to the content or media section. The whole log names every person who changed anything, so without `item` the caller must be an administrator or in `Compliance:Retention:ManagerGroups`; anyone else gets `403 audit_requires_manager`. An invalid date or paging value gives `400 audit_invalid_filter`.

### Purge

`POST /umbraco/management/api/v1/compliance/purge` with `{ "restorationPeriodDays": 30, "dryRun": false, "entityTypes": ["document", "media"] }` permanently deletes the top-level recycle-bin items that were trashed at least `restorationPeriodDays` ago (the last `trashed` event in the deletion log; for items trashed before the package was installed, the item's update date). `restorationPeriodDays` is required (0 to 36500; 0 takes every free item). `entityTypes` defaults to both. Needs an administrator or a group in `Compliance:Retention:ManagerGroups`.

An item under retention or legal hold, itself or through a descendant, is never deleted. It is skipped, listed under `skipped` with reason `retention_blocks_deletion` and the blocking items, and an audit entry `deleteBlocked` with field `purge` is written per blocking item. The deletes themselves go through the content and media services, so the deletion block checks each item once more. Each purged item (and each descendant) gets a deletion-log event `purged` with reason `restorationPeriodEnded` and the retention class. The response:

```json
{ "cutoffUtc": "…", "dryRun": false, "purged": [ { "key": "…", "entityType": "document", "name": "…", "trashedUtc": "…" } ], "skipped": [ { "key": "…", "entityType": "document", "name": "…", "trashedUtc": "…", "reason": "retention_blocks_deletion", "blockedItems": [ { "itemKey": "…", "entityType": "document", "name": "…", "reason": "legalHold", "retentionUntil": null, "retentionClass": null, "viaItemKey": null } ], "message": null } ], "notDue": 3, "elements": [] }
```

With `dryRun` nothing is deleted or logged. `IPurgeService` does the same from code (for example a scheduled job); call it outside an open Umbraco scope.

### Elements of a material

A document can carry elements: the media items in a media picker property, `elements` by default (`Compliance:Purge:ElementsProperty`; empty turns the whole section off). The package follows them through the life of the document:

| Moment | What happens to the elements |
|---|---|
| Document moved to the recycle bin | Each element moves to the media recycle bin too (`Compliance:Purge:ElementsFollowMaterial`, default `true`), unless another document in the tree still uses it (tracked reference) or the media block refuses it (retention, legal hold). Each element gets its own `trashed` row |
| Document restored | Elements that are in the media recycle bin come back, to the folder they were trashed from (the root when that folder is gone). Each gets a `restored` row |
| Document purged | The elements are deleted permanently after the document, with deletion-log event `purged` and reason `elementOfPurgedMaterial:{document key}`. This does not depend on `ElementsFollowMaterial`: a purge always takes its elements |

An element is kept, listed in `elements` of the purge response with `purged: false`, and logged as event `purgeSkipped` with reason `{code}:{document key}` when:

- another item still references it (`elementShared`; items in the recycle bin count too, so the last of the documents that purge takes the element along), or
- the element, or a descendant of it, is under retention or legal hold (`retention_blocks_deletion`; also an audit entry `deleteBlocked` with field `purge`).

```json
{ "elements": [ { "key": "…", "materialKey": "…", "name": "…", "purged": true, "reason": null, "blockedItems": [] } ] }
```

With `dryRun` the response lists what would happen and nothing is logged. Documents below a purged document are treated as part of it, so an element they share with it is not "shared". Limits: deleting a document by hand from the recycle bin, or emptying the bin, goes through `IContentService` directly and does not take elements along; only `IPurgeService` does. An element trashed on purpose before its document is trashed is restored with the document.

### Log retention

Audit and deletion-log entries are kept for `Compliance:Audit:LogRetentionYears` years (default 7; 0 keeps them forever). A daily job (`LogRetentionJob`, scheduling server only) removes older entries. It is the only code that removes log rows.

## Retention

A **retention class** has a name, a duration (years, months, days), a start event and an end action:

| Start event | Start date |
|---|---|
| `archived` | The archive date: the date property named in `Compliance:Retention:ArchivedDatePropertyAlias` (default `archivedAt`) |
| `published` | The latest publication date (media: the creation date) |
| `lastModified` | The latest save |
| `dateProperty` | The date property named in the class (`startPropertyAlias`) |

End action `notify` lists the item for review when the period ends; `allowDeletion` lists it and deletion becomes possible. The package never deletes anything itself.

A class is **assigned** to a document or media type (`contentType`), to every item whose property has a value (`propertyValue`, a category; optionally limited to one type; matches inside multi-value properties), or to one item (`item`). Item assignments override type and category assignments for that item.

**Calculation.** Every applicable class gives a period; the **longest wins** and every class is kept with its rule. A class whose start event has not happened yet (for example: not archived yet) makes the period **open-ended** (status `pending`) until it does. The effective period is stored per item in `lsItemRetention` and recalculated when an item is saved (which covers archiving), published, or when a class or assignment changes. A recalculation **never shortens** a stored period (an earlier date, a shorter class, a removed assignment) unless an administrator confirms it with `confirmShortening=true`; until then the longer period stays and the item is marked `heldFromEarlier`. Dates are UTC. Every change to a class, an assignment or an item's period is written to the audit log (`retentionChange`).

**Daily job.** `RetentionJob` runs once a day on the scheduling server. For items whose period ended and that were not reported yet it raises a `RetentionEndedNotification` (handle it to notify someone or start a review), writes an audit entry and marks the item reported. Extending a period resets the report.

**Content app.** Documents and media get a tab "Retention & audit" ("Bewaring & audit") with the status, end date, governing class, end action, legal hold and a table of every class with its rule, start and end. Administrators also get a button "Set legal hold" / "Lift legal hold" next to the legal hold status (confirmation dialog with an optional reason; the reason receives keyboard focus, spaces stay in the text, Enter explicitly confirms and Escape cancels; see [Legal hold](#legal-hold)); everyone else sees the status only. Gated documents with a lifecycle also get a box "Archive" ("Archiveren") with the lifecycle status, the archive date and, for an owner or functional manager of a published document, the button "Archive" (see [Lifecycle](#lifecycle-rejection-and-archive)). Buttons are hidden for people who may not use them; the API stays the real protection.

**Retention dashboard.** The Settings section gets a dashboard "Retention" ("Bewaring") for administrators and users in `ManagerGroups`, with four tabs: *Retention classes* (list, create, change, delete), *Assignments* (a class to a document or media type, a category, or one item, using Umbraco's own pickers), *Running periods* (items whose period started and has not ended, for example after archiving, with start and end date, earliest end first) and *Retention ended* (items whose period ended, linked to the item). Changing a class or removing an assignment shows the items that kept a longer period; an administrator confirms the shortening there (`confirmShortening`), everyone else is told that an administrator has to. The dashboard only decides what is shown: administrators see it at once, other users when `GET ended` answers 200. Every change goes through the Management API below, which enforces the permissions.

### API

Under `/umbraco/management/api/v1/compliance/retention`, authenticated like the rest of the Management API. Reading needs access to the Content or Media section; changing needs an administrator or a user in one of the `ManagerGroups`; `confirmShortening=true` needs an administrator. JSON is camelCase, enum values are camelCase strings. Errors are `application/problem+json` with a stable `code`.

| Method and path | Purpose |
|---|---|
| `GET class`, `GET class/{key}` | List classes, one class |
| `POST class` | Create: `{ "name", "description", "durationYears", "durationMonths", "durationDays", "startEvent", "startPropertyAlias", "endAction" }` |
| `PUT class/{key}[?confirmShortening=true]` | Update and recalculate; the response lists items whose shortening was refused |
| `DELETE class/{key}` | Delete an unassigned class (`409 retention_class_in_use` otherwise) |
| `GET assignment` | List assignments |
| `POST assignment` | Create: `{ "classKey", "target": "contentType" \| "propertyValue" \| "item", "contentTypeKey", "propertyAlias", "propertyValue", "itemKey" }` |
| `DELETE assignment/{id}[?confirmShortening=true]` | Remove and recalculate |
| `GET item/{key}` | Retention of a document or media item: status (`none`, `pending`, `active`, `ended`), `startDate`, `retentionUntil`, rules, legal hold |
| `POST item/{key}/recalculate[?confirmShortening=true]` | Recalculate one item |
| `GET running?skip=&take=` | Items whose period started and has not ended, with `startDate` and `retentionUntil`, earliest end first (managers) |
| `GET ended?skip=&take=` | Items whose period ended, oldest first |

Error codes: `retention_invalid`, `retention_class_not_found`, `retention_class_in_use`, `retention_content_type_not_found`, `retention_item_not_found`, `retention_shortening_requires_administrator`.

### Configuration

```json
{
  "Compliance": {
    "Retention": {
      "ArchivedDatePropertyAlias": "archivedAt",
      "ManagerGroups": [ "functionalManagement" ],
      "JobPeriod": "1.00:00:00",
      "JobDelay": "00:10:00"
    }
  }
}
```

`ManagerGroups` takes user group aliases that may manage retention besides administrators; empty by default.

## Deletion block

An item **blocks deletion** while it has a legal hold, while its retention period runs until a date in the future, or while its period is open-ended (status `pending`: a class applies whose start event has not happened yet). Trashing or deleting an item also removes its descendants, so a retained descendant blocks its ancestors too.

The block is a set of Umbraco notification handlers, so it holds for every caller of the content and media services: the backoffice, the Management API (any API user), integrations inside the site and background jobs. Route matrix (each row is an integration test, for documents and for media):

| Route | Notification | Result |
|---|---|---|
| Move to recycle bin (backoffice, `PUT /document/{id}/move-to-recycle-bin`, `IContentService.MoveToRecycleBin`) | `ContentMovingToRecycleBinNotification`, `MediaMovingToRecycleBinNotification` | Refused |
| Delete (`DELETE /document/{id}`, `IContentService.Delete`) | `ContentDeletingNotification`, `MediaDeletingNotification` | Refused |
| Delete from the recycle bin (`DELETE /recycle-bin/document/{id}`) | same | Refused |
| Empty the recycle bin (`DELETE /recycle-bin/document`, `EmptyRecycleBin`) | `ContentEmptyingRecycleBinNotification`, `MediaEmptyingRecycleBinNotification` | Free items are deleted, blocked items stay; the operation reports as refused |
| Bulk trash or delete (the backoffice calls the endpoint per item) | as above, per item | Free items go, blocked items are refused per item |
| Delete a document or media type (deletes its items, trashes their children) | `ContentTypeDeletingNotification`, `MediaTypeDeletingNotification` | Refused while any item of the type (or of a type inheriting from it), or a descendant of one, blocks |
| Scheduled job or integration calling the services (no HTTP context) | as above | Refused |

A refusal writes an audit entry per blocking item (action `deleteBlocked`, field = `trash`, `delete`, `emptyRecycleBin`, `deleteType` or `purge`) and adds a localized message (Dutch or English) to the operation's event messages, which the backoffice shows as a notification:

> Verwijderen niet toegestaan: bewaartermijn loopt tot 15 januari 2034 (Onderwijsmateriaal).
> Deletion not allowed: the retention period runs until 15 January 2034 (Teaching material).

The language is the backoffice user's language; for an API user the request's `Accept-Language` comes first. Management API callers get the problem details Umbraco returns for a cancelled operation (status kept), extended with a stable code and the blocking items:

```json
{
  "status": 400,
  "title": "Deletion blocked",
  "detail": "Deletion not allowed: the retention period runs until 15 January 2034 (Teaching material).",
  "code": "retention_blocks_deletion",
  "blockedItems": [
    { "itemKey": "…", "entityType": "document", "name": "Lesson 1", "reason": "retention", "retentionUntil": "2034-01-15T00:00:00Z", "retentionClass": "Teaching material", "viaItemKey": null }
  ]
}
```

`reason` is `legalHold`, `retention` or `retentionOpenEnded`; `viaItemKey` is set when the blocking item is a descendant of the item the call was for. `IDeletionBlockService.Check` gives the same answer to code that wants to ask before it deletes; the [purge](#purge) uses it.

### Legal hold

`PUT /umbraco/management/api/v1/compliance/retention/item/{key}/legal-hold` with `{ "legalHold": true, "reason": "Case 2026-17" }` sets the hold (`false` lifts it) and returns the item's retention. Administrators only; anyone else gets `403 retention_legal_hold_requires_administrator`, also users in `ManagerGroups`. The backoffice offers the same through the button in the content app. The change and the optional reason (max 500 characters) are written to the audit log (`retentionChange`, field `legalHold`). A hold blocks deletion regardless of retention, also after the period ended and also for items already in the recycle bin.
## Publication gate

Documents of a **gated** document type publish only when the latest decision for **exactly that version** is an approval. The gate is a handler on Umbraco's `ContentPublishingNotification`, which Umbraco raises for every route: the backoffice, the Management API (any user, also API users), scheduled publishing, publish with descendants and `IContentService` calls from code. Nothing else is gated: types that are not configured publish as before, and unpublishing is never blocked.

**What "this version" means.** Umbraco keeps the same version id while a draft is saved again, so the version id cannot show a change after approval. An approval therefore records a **version hash**: SHA-256 over the document type, template, name(s) and every edited property value of every culture and segment. At publish time the gate computes the hash of what is being published and looks for the latest decision with that content key and hash. Any change after approval (a field, the name, a culture) gives another hash, so it needs a new approval. A rejection recorded after an approval closes the gate again. The version id is stored as well, for traceability.

**Cancelled publish.** The publish is cancelled with an error event message: category `publication_requires_approval`, message "Publishing requires an approved quality check for this version of …" (Dutch for users whose backoffice language is Dutch). Through the Management API that is `400` "Publish cancelled by event" with the message in the `Umb-Notifications` response header:

```text
HTTP/1.1 400 Bad Request
Umb-Notifications: [{"message":"Publishing requires an approved quality check for this version of \"…\".","category":"publication_requires_approval","type":"Error"}]
```

Every cancellation is written to the audit log (`publishBlocked`, with the version hash) and logged as a warning. An **archived** document (status `archived` or an archive date) is never published again, even with an approval of its current version: the publish is cancelled with `publication_archived`. A cancelled scheduled publish is not retried: Umbraco clears the release date when the schedule fires.

**Approval records** (`lsApproval`) are append-only: content key, version id, version hash, decision (`approved`, `rejected`), reviewer (person id, name, channel), source (`backoffice`, `integration`, `workflow`), comment, findings, UTC date, technical user and correlation id. Every decision is also written to the audit log (`approval`).

**Who may decide.** A decision always names a person. A backoffice user in a user group listed in `ReviewerGroups` can decide directly (source `backoffice`). An API user is a technical account: it can only record a decision with a valid acting-user token (see [Acting user](#acting-user)) for the person who decides, and it must itself be in a reviewer group. The integration vouches that this person is a reviewer; the person is recorded as the reviewer (source `integration`). Without a token the API user gets `403 approval_requires_acting_user`. Acting-user tokens may decide only through channel `app` or `api` (`403 approval_channel_not_allowed` for all other channels). With `RequireDifferentReviewer=true` (default), an owner in the configured JSON owner field cannot approve their own material (`403 approval_own_material`); they may reject. Workflow follows these rules too. Direct backoffice reviewers remain permitted. Use a separate API user for approvals if the integration's editing API user must not be able to approve.

**Decisions from a route that checks the reviewer itself.** `IApprovalService.DecideAs(content, decision, reviewer)` is for code in the host that has already established that a person holds the reviewer role, from that person's own validated token: the Agent API's `approve_material` and `reject_material`. It writes the same record (source `integration`, the route's channel such as `app` or `assistant`), the same `approval` audit entry and, for a rejection, the same move to status `rejected` as the approval API. It checks again that the reviewer is a person from a validated token (`ActingUserRequired` otherwise), that a rejection has a comment or findings, and the owner rule for an approval; it does not check reviewer groups or the `app`/`api` channel rule, which belong to the approval API. The caller runs it inside its own scope, so a step after it (the Agent API publishes after an approval) can roll the record back.

### Umbraco Workflow

When Umbraco Workflow is installed, an approval flow that completes for publishing a gated document writes an approval record (source `workflow`) with the reviewer who gave the final approval, so Workflow's own publish passes the gate. A **rejection** ("Afwijzen") writes a `rejected` record (source `workflow`) with the reviewer and the reviewer's comment as `findings`, and moves the lifecycle status to `rejected` (see [Lifecycle](#lifecycle-rejection-and-archive)), so the owner can change and resubmit the material through the backoffice or the Agent API. Workflow's setting "Mandatory comments" (on by default; administrators are exempt) is what makes a rejection carry a comment: the rejection notification comes after Workflow rejected and cannot refuse it, so a rejection without a comment is still recorded, without findings, and logged as a warning. Only an approval by a person counts: a backoffice user, or an API user with an acting-user token. The package does not reference Umbraco Workflow: it registers handlers for `WorkflowInstanceApprovingNotification`, `WorkflowInstanceCompletedNotification`, `WorkflowInstanceUpdatedNotification` and `WorkflowInstanceRejectedNotification` only if those types can be loaded, and reads the instance by property name. If a future Workflow version changes them, no record is written and the gate stays closed (a warning is logged). Approving a step that does not complete the flow writes nothing. A flow with a release date writes the record on completion, so the scheduled publish passes later. Tested against Umbraco Workflow 17.5.2.

Workflow is not needed for the gate. Without it, approvals come from the API below.

### API

Under `/umbraco/management/api/v1/compliance/approval`, authenticated like the rest of the Management API. Reading the status needs access to the content or media section, like reading retention.

| Method and path | Purpose |
|---|---|
| `GET document/{id}` | Approval state of the current draft: `gated`, `versionId`, `versionHash`, `approved`, `currentDecision` and the full `history` |
| `POST document/{id}` | Record a decision about the current draft: `{ "decision": "approved" \| "rejected", "versionHash", "comment", "findings" }`. Send the `versionHash` from `GET` so a draft that changed during review is not approved by accident. A rejection needs a `comment` or `findings` and moves the lifecycle status to `rejected` (not for a published or archived document). Returns `201` with the record |

Errors are `application/problem+json` with a `code`: `403 approval_requires_reviewer`, `403 approval_requires_acting_user`, `409 approval_version_changed`, `400 approval_invalid_decision`, `400 approval_rejection_requires_feedback`, `409 approval_cancelled` (another rule refused the status change; nothing is recorded); `404` when the document does not exist.

### Configuration

```json
{
  "Compliance": {
    "PublicationGate": {
      "GatedDocumentTypes": [ "learningMaterial" ],
      "ReviewerGroups": [ "reviewers" ],
      "ExcludedPropertyAliases": [ "status", "owners", "archivedAt" ],
      "StatusPropertyAlias": "status",
      "OwnersPropertyAlias": "owners",
      "RequireDifferentReviewer": true
    }
  }
}
```

`GatedDocumentTypes` takes document type aliases (case-insensitive). A document is gated when its own type or one of its compositions is listed, so listing a composition gates every type built on it, also types created later. Empty by default, so nothing is gated until configured. `ReviewerGroups` takes user group aliases; empty by default, so nobody can approve through the API until configured.

System properties `status`, `owners` and `archivedAt` are excluded from the content hash by default. `ExcludedPropertyAliases` configures additional system aliases (case-insensitive); include the configured lifecycle and owner aliases when using a custom object model. Standard .NET configuration binding appends configured aliases to the defaults; code-based options configuration can replace the array. Do not exclude substantive content such as elements, licence or sharing level. Changing this configuration invalidates previous hashes and requires re-review.

The gate writes `published` into the configured status field during publication, persisted with both draft and published values. Ordinary saves cannot introduce that label. On unpublish, a `published` label becomes `approved` if the current draft is still approved, otherwise `draft`; other statuses, including `archived`, are preserved. Types without the configured field are unaffected.

## Lifecycle: rejection and archive

The lifecycle status (`StatusPropertyAlias`, values `draft`, `submitted`, `inReview`, `rejected`, `approved`, `published`, `archived`) of a gated document moves only through the package's routes:

| Status | Route |
|---|---|
| `submitted` | The submission endpoint or an integration (Agent API `submit_material`), after the [submission guard](#submission-guard) |
| `rejected` | A recorded rejection with feedback: the approval API or an Umbraco Workflow rejection ([above](#umbraco-workflow)) |
| `published` | The publication gate, on publish |
| `archived` | The archive route below |

**Protection.** `LifecycleGuardHandler` cancels every save of a gated document that sets or changes the archive date (`Compliance:Retention:ArchivedDatePropertyAlias`), moves the status into or out of `archived`, or moves it to `rejected`, unless the save is the package's own route (`LifecycleChangeScope`). Codes: `lifecycle_archive_requires_route`, `lifecycle_rejection_requires_decision`; through the Management API that is `400` with the code in `Umb-Notifications`. **Choice for the Management API:** a `PUT /document/{id}` with values for these fields is refused when the property accepts values (for example a text property). In the generic object model of the content layer host they are label properties, whose incoming values Umbraco drops before saving, so there such a `PUT` answers `200` and changes neither field. Both outcomes are covered by tests.

**Archive.** An owner (a person id in `OwnersPropertyAlias`) or a functional manager (administrator or a user in `Compliance:Retention:ManagerGroups`) confirms that a **published** document is archived. Under the content write lock the package first unpublishes (the document leaves the public scope; the OAI-PMH provider turns its record into a tombstone). Only after successful depublication does it set status `archived` and the archive date (UTC) and save (the audit handlers record both fields, the retention handler starts every class with start event `archived`). If another handler cancels depublication, the route returns `409 archive_cancelled`: publication, lifecycle status, archive date, the full retention period and the audit log remain unchanged. The outer scope alone does not undo the side effects of an earlier save, so this order is deliberate and covered by negative route tests. An `archive` audit entry names the person and the optional reason. An archived document is never reopened, changed back or published again.

| Method and path | Purpose |
|---|---|
| `GET /umbraco/management/api/v1/compliance/archive/document/{id}` | `{ "status", "published", "archivedUtc", "supported", "canArchive", "archived" }` for the signed-in person |
| `POST /umbraco/management/api/v1/compliance/archive/document/{id}` | Archives: body `{ "reason" }` (optional, max 500 characters). Returns `200` with the new state |

Needs access to the content section. The archive names a person, like an approval: a backoffice user, or an API user with an [acting-user token](#acting-user) for the person who confirmed it; the integration vouches for that confirmation, and its API user's groups count for the manager check. Errors: `403 archive_requires_acting_user`, `403 archive_not_owner`, `409 archive_requires_published` (a draft is not archived), `409 archive_already_archived`, `400 archive_not_supported` (no lifecycle: type not gated, or no status or archive date property), `409 archive_cancelled`, `404 archive_document_not_found`. Code calls `IArchiveService`.

## Submission guard

Before a document can be submitted for review, its required fields must be filled. Drafts can always be saved.

Required fields come from every registered `IRequiredFieldProvider` (the union):

- `ConfiguredRequiredFieldProvider`: `Compliance:Submission:RequiredFields`, property aliases per document type alias; the key `*` applies to every type.
- `MandatoryPropertyRequiredFieldProvider`: every property with Umbraco's `mandatory` flag on the document type (what a metadata administrator sets), taken from the working version of the document and, for `RequiredFieldsFor`, from the type including its compositions. The status property (`Compliance:Submission:StatusProperty`) and `Compliance:PublicationGate:ExcludedPropertyAliases` (default status, owners, archivedAt) never count: the host fills them. A concept can still be saved without the field.
- `SchemaSnapshotRequiredFieldProvider`: a metadata schema snapshot (JSON) at `Compliance:Submission:SchemaSnapshotPath`, shaped `{ "fields": [ { "alias", "label": { "nl", "en" }, "status", "levels": [ { "scope", "required" } ] } ] }`. A field is required at submission when a level with `required: true` has scope `all` or `type:{alias}` (the document type alias, with or without the `ls` prefix). Scopes `element`, `category:…` and `step:reviewer` do not apply to submission; archived fields are ignored. The file is re-read when it changes.
- An integration can register its own provider (`services.AddSingleton<IRequiredFieldProvider, MyProvider>()`).
- Labels of a missing field: the labels of the required field itself (schema snapshot), else those of a registered
  `IFieldLabelProvider` (`GetLabels(propertyAlias)`, for example a tenant profile), else the property name, else the alias.
  A property name that is a backoffice localization key (`#...`) never reaches a message or response. `missingFields[].labels`
  holds the label per language (`nl` and `en` always).

A required alias the document type does not have is ignored. A value counts as empty when it is null, whitespace, `[]`, `{}` or `null`. For a property that varies by culture every available culture must be filled.

**Enforcement.** When `Compliance:Submission:StatusProperty` names the status property (text or dropdown), a save that moves a document into a submitted status (`SubmittedValues`, default `submitted`) is cancelled while required fields are empty. That holds for every route: the backoffice, the Management API (`400` with category `submission_missing_required_fields` in `Umb-Notifications`), integrations and code. Saving an item that is already submitted is not checked again. Without a status property the guard only validates through the API.

| Method and path | Purpose |
|---|---|
| `GET /umbraco/management/api/v1/compliance/submission/document/{id}` | `{ "submitted", "ready", "missingFields": [ { "alias", "label", "culture", "labels" } ] }` |
| `POST /umbraco/management/api/v1/compliance/submission/document/{id}` | Submits: `400 submission_missing_required_fields` with `missingFields` while fields are empty, otherwise sets the status (when configured) and returns `200` |

Needs access to the content section. Labels and the message are Dutch or English (`Accept-Language` for API users, otherwise the user's language).

```json
{
  "Compliance": {
    "Submission": {
      "StatusProperty": "status",
      "SubmittedValues": [ "submitted" ],
      "RequiredFields": { "*": [ "title", "license" ] },
      "SchemaSnapshotPath": "config/metadata-schema.json"
    },
    "Audit": { "LogRetentionYears": 7 }
  }
}
```

## Archived fields and deprecated values

Umbraco can only delete a property, and with it every value on every document; it also keeps every code of a dropdown
valid for ever. This module adds the two states a metadata administrator needs instead: a field can be
**archived** and a choice code **deprecated**, and neither touches what documents hold.

- A property is archived when it sits in the property group `Gearchiveerd` of its document type (alias or name,
  case-insensitive; `Compliance:FieldState:ArchivedGroup` sets another name), or when `IFieldStateService.Apply` flagged it.
- A code is deprecated when `IFieldStateService.Apply` lists it for the property alias. The state is stored per tenant in
  Umbraco's key-value table (`IFieldStateStore`; no migration, one row per flag) and each change is audited
  (`fieldStateChange`, entity `metadataField`, old and new state).
- On every save of a gated document (backoffice, Management API, integrations, code) a new or changed non-empty value in an
  archived field is cancelled with `field_archived`, and a deprecated code is cancelled with `value_deprecated` unless the
  document held that code already. Unchanged values and clearing a value are never refused. Texts are Dutch or English
  by the user's language, like the other rules.
- An archived field no longer counts as required by the submission guard (`MandatoryPropertyRequiredFieldProvider`).
- `IFieldStateService.CheckChange` lets a caller that knows the old and the new value apply the same rule earlier, to
  answer with a precise message (the Agent API does).

## Build from source

Requires the .NET 10 SDK and Node.js 24.13 or later (required by `@umbraco-cms/backoffice` 17).

```bash
dotnet build
dotnet pack -c Release
```

The build runs `npm ci` (first time) and `npm run build` in `src/Limescape.Umbraco.Compliance/Client/`, which writes the backoffice extension to `wwwroot/App_Plugins/Limescape.Umbraco.Compliance`. Pass `-p:SkipClientBuild=true` to skip the client build.

For backoffice development, `npm run watch` in `Client/` rebuilds on change; reference this project from a local Umbraco site to see the result.

## Project layout

Folders under `src/Limescape.Umbraco.Compliance`:

```text
Composers/        Umbraco composer (registration entry point)
ActingUser/       Acting-user token validation, middleware and accessor
Audit/            Audit and deletion log writers, audit handlers (field-level diffs), log query, log retention job
Retention/        Retention classes, calculation, recalculation handlers, daily job
DeletionBlock/    Deletion block handlers, legal hold, localized messages, API error mapping
PublicationGate/  Publication gate handler, approval records, version hash, Umbraco Workflow bridge
Lifecycle/        Lifecycle statuses, lifecycle guard, archive route
Purge/            Purge of the recycle bin after a restoration period, elements of a material (trash, restore, purge)
Submission/       Submission guard and required-field providers
Api/              Management API extension controllers and authorization
Migrations/       Package migration plan (package tables)
Persistence/      Table definitions (NPoco DTOs)
Client/           Backoffice extension (TypeScript, Lit, Vite)
  public/         umbraco-package.json, copied into the build output
  src/            manifests, elements, localization (en, nl)
```

## Repository layout

- `src/Limescape.Umbraco.Compliance`: the package (the folders under "Project layout").
- `tests/Limescape.Umbraco.Compliance.Tests`: tests that boot a real Umbraco site on SQLite.
- `Directory.Build.props` and `Directory.Packages.props`: shared build settings, NuGet metadata and central package versions.

Run the tests with `dotnet test --project tests/Limescape.Umbraco.Compliance.Tests`. Changes per version are in [CHANGELOG.md](CHANGELOG.md).

## Licence

Copyright © 2026 TrueLime B.V. Source available under the Limescape Open Platform Licentie (LOPL), versie 1.0, see [LICENSE](LICENSE). The licence text is in Dutch and the Dutch version prevails. The LOPL is a source-available licence, not an OSI-approved open-source licence.

TrueLime B.V. grants the rights under the LOPL to public organisations and other non-profit organisations that register as a user at https://www.limescape.ai and accept the licence. Any other use, such as commercial hosting, managed services, OEM or white-label use, requires a commercial licence from TrueLime B.V.
