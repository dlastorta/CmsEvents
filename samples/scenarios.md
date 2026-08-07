# Sample scenarios — walkthrough

Each scenario below combines an HTTP request, the expected response, and a SQL query to verify the DB state. Run them in order — later scenarios depend on state produced by earlier ones.

The requests match one-to-one with entries in [`CmsEvents.http`](CmsEvents.http). Open that file in Visual Studio, Rider, or VS Code (with the REST Client extension) and click "Send Request" — or copy the equivalent `curl` shown here.

## Prerequisites

- Service running on `http://localhost:5000` per README Quick Start.
- SQL Server 2022 container from `docker-compose up -d`.
- Seed users active (`cms-webhook-user`, `readonly-user`, `admin-user`) with the local dev password (`LocalDevPassword-1!` in examples below — replace with yours if different).

## Verifying DB state with sqlcmd

All verification queries below use `sqlcmd` inside the SQL Server container, so they work from any shell without installing extra tools:

```bash
docker exec cmsevents-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Local_Dev_Password_123!" -C -d CmsEventsDb -Q "SELECT Id, Status, IsDisabled, LastProcessedVersion FROM Entities"
```

Save yourself typing with a shell function or the included batch file:

```bash
# bash / zsh
sql() { docker exec cmsevents-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Local_Dev_Password_123!" -C -d CmsEventsDb -Q "$1"; }

# PowerShell
function sql { param([string]$q) docker exec cmsevents-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Local_Dev_Password_123!" -C -d CmsEventsDb -Q $q }

# Windows CMD — use the included samples/sql.bat wrapper
samples\sql.bat "SELECT * FROM Entities"
```

Then just `sql "SELECT * FROM Entities"` (or `samples\sql.bat "..."` in CMD).

---

## Scenario 1 — Publish a new entity

Request:

```bash
curl -u "cms-webhook-user:LocalDevPassword-1!" -H "Content-Type: application/json" -X POST http://localhost:5000/cms/events -d "[{\"type\":\"publish\",\"id\":\"article-1\",\"version\":1,\"timestamp\":\"2026-08-01T10:00:00Z\",\"payload\":{\"title\":\"Hello world\",\"body\":\"First version\"}}]"
```

Expected response (status `200`):

```json
{
  "batchId": "<guid>",
  "correlationId": "<guid>",
  "totalEvents": 1,
  "processed": 1,
  "skipped": 0,
  "failed": 0,
  "errors": []
}
```

Verify DB:

```bash
sql "SELECT Id, Status, IsDisabled, LastProcessedVersion FROM Entities"
```

Expected: one row.

```
Id           Status     IsDisabled  LastProcessedVersion
------------ ---------- ----------- --------------------
article-1    Published  0           1
```

---

## Scenario 2 — Idempotency: same version twice

Send Scenario 1's request again exactly. Expected response:

```json
{
  ...
  "processed": 0,
  "skipped": 1,
  "failed": 0,
  "errors": []
}
```

`skipped=1` — the idempotency rule (ADR-005) detected `incoming.version == stored.LastProcessedVersion` and same timestamp, marked as `duplicate` in logs. The response does not itemize skips.

Verify DB is unchanged:

```bash
sql "SELECT Id, LastProcessedVersion FROM Entities WHERE Id = 'article-1'"
```

Expected: `LastProcessedVersion=1` (unchanged).

Check the service console — you should see a Warning log entry like:

```
[WRN] UnPublish/Publish skipped: id=article-1, incomingVersion=1, storedVersion=1, reason=duplicate
```

---

## Scenario 3 — Higher version applies the update

Publish `article-1` with `version: 2`. Expected: `processed=1`.

Verify:

```bash
sql "SELECT Id, LastProcessedVersion, CAST(Payload AS NVARCHAR(MAX)) AS Payload FROM Entities WHERE Id = 'article-1'"
```

Expected: `LastProcessedVersion=2` and payload updated to the new body.

---

## Scenario 4 — Lower version is skipped (superseded)

Publish `article-1` with `version: 1` again (after the entity is already at v2). Expected: `skipped=1`, log reason `superseded_by_version`.

DB unchanged — the "supersede" protection works.

---

## Scenario 5 — Unpublish existing entity

Send `unPublish` with `version: 3`. Expected: `processed=1`.

Verify:

```bash
sql "SELECT Id, Status, LastProcessedVersion FROM Entities WHERE Id = 'article-1'"
```

Expected: `Status=Unpublished`, `LastProcessedVersion=3`.

---

## Scenario 6 — Orphan unpublish (spec corner case)

Send `unPublish` for `article-never-seen` (an id we have never published). Expected: `processed=1`.

Verify:

```bash
sql "SELECT Id, Status, LastProcessedVersion FROM Entities WHERE Id = 'article-never-seen'"
```

Expected: **new row exists** with `Status=Unpublished`. This is the ADR-006 upsert-on-orphan-unpublish behavior — the CMS's view is preserved even under out-of-order delivery.

---

## Scenario 7 — Hard-delete an existing entity

Send `delete` for `article-1`. Expected: `processed=1`.

Verify:

```bash
sql "SELECT Id FROM Entities WHERE Id = 'article-1'"
```

Expected: **zero rows**. Per ADR-005 hard-delete semantics, no tombstone is retained.

---

## Scenario 8 — Orphan delete (per ADR-006 revision)

Send `delete` for `does-not-exist`. Expected:

```json
{
  ...
  "processed": 0,
  "skipped": 1,
  "failed": 0,
  "errors": []
}
```

**Note**: `skipped=1`, NOT `failed=1`. Orphan delete is a no-op — the desired end state (entity absent) is already achieved. The response has no `errors[]` entry; the anomaly is logged at Warning level for the dev team.

Service console:

```
[WRN] Delete skipped as orphan no-op: id=does-not-exist, timestamp=...
```

---

## Scenario 9 — Mixed batch: processed + skipped + failed

Send a batch containing one valid publish, one invalid publish (missing version), and one orphan delete. Expected:

```json
{
  "totalEvents": 3,
  "processed": 1,
  "skipped": 1,
  "failed": 1,
  "errors": [
    {
      "eventIndex": 1,
      "id": "article-mixed-b",
      "type": "publish",
      "reason": "validation_error",
      "detail": "..."
    }
  ]
}
```

Only the failed event appears in `errors[]`. The skipped orphan delete is counted but not itemized.

Verify DB has `article-mixed-a` (the valid publish) but not `article-mixed-b`:

```bash
sql "SELECT Id, Status FROM Entities WHERE Id LIKE 'article-mixed%'"
```

Expected: one row for `article-mixed-a` with `Status=Published`.

---

## Scenario 10 — Reject unauthenticated calls

POST without an `Authorization` header. Expected: `401 Unauthorized`.

## Scenario 11 — Reject wrong-role calls

POST as `readonly-user` (User role, not Organization). Expected: `403 Forbidden`.

---

## Scenario 12 — List entities as normal user

```bash
curl -u "readonly-user:LocalDevPassword-1!" http://localhost:5000/entities
```

Expected: only entities matching `Status=Published AND IsDisabled=false`. `article-mixed-a` should be present. `article-never-seen` (Unpublished) should NOT be present.

The response omits `status` and `isDisabled` fields for normal users (role-aware DTO per ADR-007).

## Scenario 13 — List entities as admin

Same URL, `admin-user` credentials. Expected: **all** entities, including Unpublished ones. Response includes `status` and `isDisabled` fields.

---

## Scenario 14-15 — Fetch by id

- GET `/entities/article-mixed-a` as admin → 200 with the entity.
- GET `/entities/does-not-exist` as admin → 404. Note: the 404 does not distinguish "does not exist" from "filtered out by role" — anti-enumeration per ADR-007.

---

## Scenario 16-18 — Admin disable (sticky flag per ADR-007)

Disable `article-mixed-a`:

```bash
curl -u "admin-user:LocalDevPassword-1!" -X POST http://localhost:5000/entities/article-mixed-a/disable
```

Expected: `200 OK` with `{ "id": "article-mixed-a", "isDisabled": true }`.

Verify:

```bash
sql "SELECT Id, Status, IsDisabled FROM Entities WHERE Id = 'article-mixed-a'"
```

Expected: `IsDisabled=1`.

Now:
- GET as `readonly-user` → 404 (filtered out).
- GET as `admin-user` → 200 (admin sees everything).

---

## Scenario 19 — Sticky disable across CMS re-publish

With `article-mixed-a` currently `IsDisabled=1`, publish a higher version:

```json
[
  {
    "type": "publish",
    "id": "article-mixed-a",
    "version": 2,
    "timestamp": "2026-08-01T17:00:00Z",
    "payload": { "title": "Republished — but admin disable is sticky" }
  }
]
```

Then GET as admin. Expected: `LastProcessedVersion=2`, **`IsDisabled` still `true`**. Per ADR-007, admin disable is not affected by CMS events.

```bash
sql "SELECT Id, Status, IsDisabled, LastProcessedVersion FROM Entities WHERE Id = 'article-mixed-a'"
```

Expected: `Published`, `IsDisabled=1`, `LastProcessedVersion=2`. Normal user still cannot see it.

---

## Scenario 20 — Admin re-enables

```bash
curl -u "admin-user:LocalDevPassword-1!" -X POST http://localhost:5000/entities/article-mixed-a/enable
```

Expected: `200` with `{ "isDisabled": false }`.

Now normal user can see it again.

## Scenario 21 — Non-admin cannot disable

POST disable as `readonly-user`. Expected: `403 Forbidden`.

---

## Full DB snapshot after all scenarios

```bash
sql "SELECT Id, Status, IsDisabled, LastProcessedVersion, LastProcessedTimestamp, CAST(Payload AS NVARCHAR(200)) AS PayloadPreview FROM Entities ORDER BY Id"
```

Expected rows (with `Id`, `Status`, `IsDisabled`, `LastProcessedVersion`):

| Id | Status | IsDisabled | LastProcessedVersion |
|----|--------|-----------|---------------------|
| article-mixed-a | Published | 0 | 2 |
| article-never-seen | Unpublished | 0 | 5 |

(`article-1` was deleted in Scenario 7; `article-mixed-b` failed validation and was never persisted; `does-not-exist` was an orphan and no-op'd.)

## Extra: inspect a specific batch in logs

The service prints structured logs with `BatchId` and `CorrelationId` (per ADR-014). To trace a specific request, note the correlation ID from the response and grep the service console:

```
CorrelationId=11111111-1111-1111-1111-111111111111
```

In production, this would be an Application Insights Kusto query per the runbook.

## Reset the DB and start over

```bash
docker compose down -v
docker compose up -d
dotnet run --project src/CmsEvents.Api
```

`-v` removes the data volume, giving you a fresh empty DB. The `DbInitializer` re-applies migrations and re-seeds users on startup.
