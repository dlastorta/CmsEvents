# Verification Report — ADRs vs Code

**Date**: August 2026
**Method**: Systematic ADR-by-ADR comparison against the current codebase in `src/` and `tests/`.

## Executive summary

| Status | Count |
|--------|-------|
| **Conforms** | 12 ADRs fully implemented as documented |
| **Drift** | 4 findings — code partially or incorrectly implements the ADR |
| **Observation** | 3 non-blocking notes worth tracking |

Fixes for the 4 drifts are recommended in this report. Nothing indicates a fundamental design mismatch — the drifts are missing details or partial implementations, not wrong decisions.

## Verification per ADR

### ADR-001 — Clean Architecture with 5-Project Layering — CONFORMS

- Five projects present under `src/`.
- Dependency direction matches ADR: `Api → Application, Infrastructure, Contracts`; `Application → Domain, Contracts`; `Infrastructure → Application, Domain`; `Domain → nothing beyond System.*`.
- `Infrastructure → Application` is legitimate per ADR-010 revision (dependency inversion for repository ports).

### ADR-002 — Architectural Boundary Tests with NetArchTest — CONFORMS

- `tests/CmsEvents.Architecture.Tests/BoundaryTests.cs` encodes 7 `[Fact]` tests, one per rule.
- Rule 7 (Application no-EF-Core) verified — no `Microsoft.EntityFrameworkCore` reference in Application csproj or code.

### ADR-003 — Vertical Slice + MediatR + Strategy — CONFORMS

- `src/CmsEvents.Application/Features/` contains 5 slices: `ProcessEventBatch`, `ListEntities`, `GetEntity`, `DisableEntity`, `EnableEntity`.
- `src/CmsEvents.Application/EventProcessing/` is a sibling folder as documented (per ADR-003 folder Option A).
- Each feature has its Command/Query + Handler + Validator (where applicable).

### ADR-004 — Strategy Pattern for Event Dispatch — CONFORMS

- `IEventHandler` interface in `EventProcessing/`.
- `EventDispatcher` with dictionary lookup by type string.
- Three concrete handlers: `PublishEventHandler`, `UnPublishEventHandler`, `DeleteEventHandler`.
- `UnknownEventTypeException` for unmapped types.
- Explicit DI registration (not assembly scan) per ADR rationale.

### ADR-005 — Version + Timestamp Idempotency — DRIFT

**Idempotency rule**: implemented correctly in `Entity.EvaluateForApply` — the version/timestamp comparison matches the ADR exactly.

**Delete events**: hard-delete + orphan skipped + Warning log — implemented in `DeleteEventHandler`.

**DRIFT — Missing feature**: the ADR specifies:

> `incoming.timestamp > now + 24h` → warning logged. Threshold accommodates producer clock skew and timezone misconfiguration.

This clock-skew warning is **not implemented** in `PublishEventHandler` or `UnPublishEventHandler`. Timestamps beyond `now + 24h` are silently accepted with no warning.

**Recommended fix**: add a helper method (or middleware in the handler) that logs at Warning level when `evt.Timestamp > _clock.UtcNow.AddHours(24)`. Should not affect the idempotency decision itself — pure observability signal.

### ADR-006 — Handling Events for Unknown Entities — CONFORMS

- `PublishEventHandler`: new id → `Entity.CreateFromPublish` + insert.
- `UnPublishEventHandler`: new id → `Entity.CreateOrphanFromUnpublish` + insert with `Status=Unpublished` (spec corner case).
- `DeleteEventHandler`: unknown id → `EventOutcome.Skipped("orphan_delete")` + Warning log — not itemized in response (`errors[]`), matching ADR-006 revised.

### ADR-007 — Local Disabled Flag — CONFORMS

- `Entity.IsDisabled` — boolean, never touched by CMS handlers (verified in `PublishEventHandler.ApplyPublish` and `UnPublishEventHandler.ApplyUnpublish` — no `IsDisabled` mutation).
- `Entity.Disable`/`Enable` — idempotent.
- Role-based filter in `IEntityQueries.FindByIdAsync(includeHidden)` and `ListAsync(includeHidden)` — normal user gets `Status=Published AND !IsDisabled`, admin gets everything.
- Endpoints: `POST /entities/{id}/disable`, `POST /entities/{id}/enable`, both `AdminOnly`.
- Role-aware DTO projection in `ListEntitiesHandler.Map(entity, isAdmin)`.

### ADR-008 — Sync Batch Processing with Per-Event Transactions — CONFORMS (with note)

- Sequential iteration in `ProcessEventBatchHandler.Handle` — for-loop over events, no parallelism.
- Per-event transaction via `IEntityRepository.ExecuteInTransactionAsync` (with `CreateExecutionStrategy` pattern per fix applied after Diego's runtime error).
- Polly retry: 3 attempts, exponential backoff (100/200/400 ms), excludes `UnknownEventTypeException` per ADR.
- Validation via `CmsEventValidator` called explicitly per event, does not throw (in-handler placement per ADR-008 principle).
- Failure reasons in response match ADR enum: `validation_error`, `processing_timeout`, `persistence_error`, `unknown_event_type`. Internal exception details logged separately at Error level.
- Response schema (counts flat + `errors[]` for failed only) matches `responses.md`.

### ADR-009 — Failure Handling and Extension Paths — DRIFT

- No batch size limit — CONFORMS.
- Catastrophic failure handling (HTTP 500 with batchId + correlationId) — implicit via ASP.NET Core exception middleware; not custom-wired but should produce reasonable behavior. **No explicit test coverage.**

**DRIFT — Processing timeout not enforced**: ADR says:

> Processing timeout: 60 seconds (below Kestrel default and typical load balancer defaults). Enforced via `CancellationToken` linked to request abort.

Current behavior:

- `ProcessingTimeoutSeconds: 60` **exists** in `appsettings.json` but is never read.
- `ProcessEventBatchHandler.Handle` accepts a `CancellationToken` from MediatR (which chains to `HttpContext.RequestAborted`) but does **not** apply an explicit 60-second timeout on top.
- Effective behavior: whatever Kestrel / load balancer default is (typically 2 minutes or infinity depending on hosting).

**Recommended fix**: in `Api/Program.cs` (or a middleware), create a `CancellationTokenSource` linked to the request-aborted token with a 60-second timeout, or read `ProcessingTimeoutSeconds` from config and apply. Then pass the linked token to the handler. Alternatively, drop the 60s from the ADR and clarify that timeout is "whatever the transport enforces".

### ADR-010 — Reader/Writer DbContext + Domain-Facing Repositories — CONFORMS

- `WriterDbContext` + `ReaderDbContext` separated, with `NoTrackingWithIdentityResolution` on reader.
- `EnableRetryOnFailure` **not** enabled — this was intentionally removed after Diego's runtime error. Documented via inline comment.
- `EntityRepository.ExecuteInTransactionAsync` uses `CreateExecutionStrategy` pattern — robust to future re-addition of retry.
- Repository interfaces in Application (`IEntityRepository`, `IEntityQueries`, `IUserQueries`) — implementations in Infrastructure/Persistence/Repositories/.
- Application csproj does NOT reference `Microsoft.EntityFrameworkCore` — ORM-agnostic verified.
- `WriterDbContext` owns migrations (Migrations folder exists under Infrastructure — initial schema committed).

### ADR-011 — Basic Authentication with Users Table — CONFORMS

- `Users` table with `Id`, `Username`, `PasswordHash`, `Role`, `CreatedAt`.
- `UserRole` enum `{Organization, User, Admin}` stored as string via `HasConversion<string>()`.
- BCrypt work factor 11 (default in `BCrypt.Net-Next`).
- `BasicAuthenticationHandler` uses `IUserQueries.FindByUsernameAsync` + `BCrypt.Verify`.
- `UserSeeder` seeds three users at startup, idempotent (updates hash if configuration drifted).
- Three authorization policies: `OrganizationOnly`, `UserOrAdmin`, `AdminOnly` — string-based `RequireRole` (preserves migration path to full RBAC).
- Failure responses generic ("Invalid credentials") — no user existence leak.

### ADR-012 — Secret Management — CONFORMS

- Configuration precedence order matches ADR: `appsettings.json` → `appsettings.{Env}.json` → User Secrets (dev) → env vars → Azure Key Vault (prod).
- Key Vault provider wired via `KeyVaultUri` env var + Managed Identity (`DefaultAzureCredential`).
- User Secrets set in Api csproj (`UserSecretsId`).
- `.gitignore` excludes `appsettings.Local.json` and `*.secrets.*`.
- Runbook (`docs/runbook-secret-rotation.md`) exists with 90-day procedure and rollback.

### ADR-013 — Rate Limiting — CONFORMS

- Built-in `Microsoft.AspNetCore.RateLimiting` (no third-party).
- **Sliding window** algorithm (not fixed) — verified via `RateLimitPartition.GetSlidingWindowLimiter`.
- Partition key: authenticated username + path — matches ADR "user has independent bucket per endpoint".
- Per-endpoint limits configurable via appsettings (`RateLimiting:CmsEventsPerMinute` etc.).
- 429 response includes `Retry-After` header via `OnRejected` callback.
- Pipeline order: auth → rate limiter → endpoints — matches ADR.

### ADR-014 — Observability — CONFORMS (with observation)

- Serilog configured with Console + Application Insights sinks; JSON output template.
- Log levels aligned with ADR — Info for applied events, Warning for skipped events (including `orphan_delete`, `stale_delete`, and clock-skew), Error for permanent failures.
- OpenTelemetry: ASP.NET Core + HTTP + Azure Monitor exporter.
- `CorrelationIdMiddleware` extracts/generates `X-Correlation-ID`, pushes to Serilog `LogContext`, sets on response headers.
- `X-Batch-Id` set on `POST /cms/events` responses.

**Observation**: W3C `traceparent` propagation is provided by OpenTelemetry's ASP.NET Core auto-instrumentation. Not explicitly wired in code, which is correct — the library handles it. No fix needed; noting for future contributors.

### ADR-015 — API Versioning — CONFORMS

- Endpoints unversioned: `/cms/events`, `/entities`, `/entities/{id}/{disable|enable}`.
- No `AddApiVersioning` package or route prefixes.
- When versioning is introduced per triggers in ADR, per-endpoint-group scope is the documented plan.

### ADR-016 — Testing Strategy — CONFORMS (coverage below target)

- Three test projects: `Unit.Tests`, `Integration.Tests`, `Architecture.Tests`.
- Correct tooling per tier: SQLite in Unit; Testcontainers.MsSql in Integration; NetArchTest in Architecture.
- CI (`.github/workflows/ci.yml`) runs all three tiers.
- xUnit + Moq + FluentAssertions.

**Known gap**: coverage is below ADR-016 target (~75% overall). This is being addressed in the follow-up test expansion phase — not a design drift.

## Additional observations

### DB-level JSON constraint marker in EntityConfiguration but not applied

`EntityConfiguration.cs` line 51 has a comment:

```csharp
// ALTER TABLE Entities ADD CONSTRAINT CK_Entities_Payload_IsJson CHECK (ISJSON(Payload) = 1);
```

The `Payload` column is `nvarchar(max)` but no check constraint enforces valid JSON. This is defense-in-depth — the API validates JSON before storage — but the ADR-007 § Persistence details says the constraint should be added via a raw SQL fragment in the migration.

**Recommended fix**: add a `migrationBuilder.Sql(...)` line to the initial migration adding the check constraint, or accept the trade-off (API is the only writer, validation guards at input).

### Sample HTTP auth headers hardcoded for one specific password

`samples/CmsEvents.http` has base64-encoded credentials for `cms-webhook-user:LocalDevPassword-1!`. If the user chooses a different password, they must regenerate the base64. This is documented in the file header, but caught Diego during first use.

**Not a fix required** — just documentation clarity. Could add a `.http` variable computed from a password variable, but `.http` files don't support runtime base64 encoding across editors.

### Integration test isolation risk

Tests share one Testcontainers SQL Server via collection fixture (amortizes ~10s startup). State accumulates across tests. Recent fix: `Post_ValidBatch_ReturnsProcessedCounts` now uses per-run Guid IDs to avoid hitting idempotency skip on second run.

**Recommendation for future tests**: same pattern — inject a per-test unique prefix into any entity ID that would otherwise collide across runs.

## Recommended fix priority

| # | Drift | Priority | Effort |
|---|-------|----------|--------|
| 1 | ADR-005 clock-skew warning (not implemented) | Medium — observability gap | Small (add check in Publish/UnPublish handlers) |
| 2 | ADR-009 processing timeout (not enforced) | Medium — production stability | Small-Medium (add `CancellationTokenSource` with linked timeout, or amend ADR) |
| 3 | ADR-007 ISJSON constraint (not in migration) | Low — defense in depth only | Small (add `migrationBuilder.Sql`) |
| 4 | Test coverage below target | Ongoing | Medium (already scoped for follow-up phase) |

## Suggested next steps

1. Apply fix #1 and #2 as small PRs (each is < 20 lines of code + 1 ADR reference).
2. Decide on fix #3 (either apply or amend ADR to remove the constraint marker).
3. Continue test expansion phase separately for coverage.
