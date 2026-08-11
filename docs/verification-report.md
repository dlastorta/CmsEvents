# Verification Cycle Report — ADRs vs Code

**Date**: August 2026
**Method**: Systematic ADR-by-ADR comparison against the codebase in `src/` and `tests/`. Each drift found in the initial pass was resolved in a follow-up commit, and this document was rewritten to reflect current state — not left describing already-fixed problems.

## Executive summary

| Status | Count |
|--------|-------|
| **Conforms** | 16 ADRs implemented as documented |
| **Drift** | 0 open (see § Cycle log for drifts found and resolved) |
| **Observation** | 2 non-blocking notes worth tracking |

The verification is a **cycle**, not a single pass. When code and ADR disagree, one of them is wrong — the fix is to reconcile them and update this report, not to leave it describing an old state. Below, every ADR is verified against the current code; the § Cycle log at the end lists what changed since the initial audit and where the fix landed.

## Verification per ADR

### ADR-001 — Clean Architecture with 5-Project Layering — CONFORMS

- Five projects present under `src/`: `Api`, `Application`, `Contracts`, `Domain`, `Infrastructure`.
- Dependency direction matches ADR: `Api → Application, Infrastructure, Contracts`; `Application → Domain, Contracts`; `Infrastructure → Application, Domain`; `Domain → nothing beyond System.*`.
- `Infrastructure → Application` is legitimate per ADR-010 revision (dependency inversion for repository ports).

### ADR-002 — Architectural Boundary Tests with NetArchTest — CONFORMS

- `tests/CmsEvents.Architecture.Tests/BoundaryTests.cs` encodes 7 `[Fact]` tests, one per rule.
- Rule 7 (Application no-EF-Core) verified — no `Microsoft.EntityFrameworkCore` reference in the Application csproj or in any Application `.cs` file.

### ADR-003 — Vertical Slice + MediatR + Strategy — CONFORMS

- `src/CmsEvents.Application/Features/` contains 5 slices: `ProcessEventBatch`, `ListEntities`, `GetEntity`, `DisableEntity`, `EnableEntity`.
- `src/CmsEvents.Application/EventProcessing/` is a sibling folder as documented (per ADR-003 folder Option A).
- Each feature has its Command/Query + Handler + Validator where applicable.

### ADR-004 — Strategy Pattern for Event Dispatch — CONFORMS

- `IEventHandler` interface in `EventProcessing/`.
- `EventDispatcher` with dictionary lookup by type string.
- Three concrete handlers: `PublishEventHandler`, `UnPublishEventHandler`, `DeleteEventHandler`.
- `UnknownEventTypeException` for unmapped types.
- Explicit DI registration (not assembly scan) per ADR rationale.

### ADR-005 — Version + Timestamp Idempotency — CONFORMS

- Idempotency rule implemented in `Entity.EvaluateForApply` — version/timestamp comparison matches the ADR exactly.
- Delete events: hard-delete + orphan skipped + stale-delete guard, all with Warning logs, implemented in `DeleteEventHandler` and `Entity.EvaluateForDelete`.
- Clock-skew observability warning implemented in `ProcessEventBatchHandler.WarnIfClockSkew` (previously flagged as missing in the initial audit — resolved).

### ADR-006 — Handling Events for Unknown Entities — CONFORMS

- `PublishEventHandler`: new id → `Entity.CreateFromPublish` + insert.
- `UnPublishEventHandler`: new id → `Entity.CreateOrphanFromUnpublish` + insert with `Status=Unpublished` (spec corner case).
- `DeleteEventHandler`: unknown id → `EventOutcome.Skipped("orphan_delete")` + Warning log — not itemized in response `errors[]`, matches ADR-006.
- Stale delete (id exists but incoming timestamp <= stored) → `EventOutcome.Skipped("stale_delete")` + Warning log — matches ADR-005 revised.

### ADR-007 — Local Disabled Flag — CONFORMS

- `Entity.IsDisabled` — boolean, never touched by CMS handlers (verified in `PublishEventHandler.ApplyPublish` and `UnPublishEventHandler.ApplyUnpublish` — no `IsDisabled` mutation).
- `Entity.Disable`/`Enable` — idempotent.
- Role-based filter in `IEntityQueries.FindByIdAsync(includeHidden)` and `ListAsync(includeHidden)`: normal user sees `Status=Published AND !IsDisabled`, admin sees everything.
- Endpoints: `POST /entities/{id}/disable`, `POST /entities/{id}/enable`, both `AdminOnly`.
- Role-aware DTO projection in `ListEntitiesHandler.Map(entity, isAdmin)`.

### ADR-008 — Sync Batch Processing with Per-Event Transactions — CONFORMS

- Sequential iteration in `ProcessEventBatchHandler.Handle` — for-loop over events, no parallelism.
- Per-event transaction via `IEntityRepository.ExecuteInTransactionAsync` (uses `CreateExecutionStrategy` pattern for compatibility with retrying strategies).
- Polly retry: 3 attempts, exponential backoff (100/200/400 ms), scoped to `TransientPersistenceException` only.
- Non-transient `DbUpdateException` is wrapped as `PermanentPersistenceException` by `SqlExceptionClassifier` and does NOT retry — mapped to `persistence_error` outcome.
- Validation via `CmsEventValidator` called explicitly per event, does not throw (in-handler placement per ADR-008 principle).
- Failure reason enum in response matches ADR: `validation_error`, `processing_timeout`, `persistence_error`, `unknown_event_type`.

### ADR-009 — Failure Handling and Extension Paths — CONFORMS

- No batch size limit — CONFORMS.
- Catastrophic-failure handling: `GlobalExceptionHandler` (`Api/Middleware/`) returns HTTP 500 with `batchId` + `correlationId` + `internal_error` per contract in `responses.md`. Integration-tested indirectly via unhandled-exception paths in `ProcessEventBatchHandler`.
- Processing timeout enforced via `AddRequestTimeouts` + `WithRequestTimeout(RequestTimeoutPolicies.EventBatch)` on the `POST /cms/events` endpoint — 60-second policy read from `ProcessingOptions` (previously flagged as unenforced in the initial audit — resolved).

### ADR-010 — Reader/Writer DbContext + Domain-Facing Repositories — CONFORMS

- `WriterDbContext` + `ReaderDbContext` separated; reader uses `NoTrackingWithIdentityResolution` globally.
- `EnableRetryOnFailure` intentionally NOT enabled (documented via inline comment); `EntityRepository.ExecuteInTransactionAsync` uses `CreateExecutionStrategy` so the code is robust if a retrying strategy is later added.
- Repository interfaces in Application (`IEntityRepository`, `IEntityQueries`, `IUserQueries`); implementations in `Infrastructure/Persistence/Repositories/`.
- Application csproj does NOT reference `Microsoft.EntityFrameworkCore` — verified by `BoundaryTests.Rule7`.
- `WriterDbContext` owns migrations; `Migrations/` folder under Infrastructure has the initial schema.
- Reader-side projections claim reconciled with actual implementation in ADR-010's revised "Current state of projections" subsection (queries materialize full rows because 6 of 8 columns are consumed by the response DTO — projection deferred per `future-improvements.md` #15).

### ADR-011 — Basic Authentication with Users Table — CONFORMS

- `Users` table with `Id`, `Username`, `PasswordHash`, `Role`, `CreatedAt`.
- `UserRole` enum `{Organization, User, Admin}` stored as string via `HasConversion<string>()`.
- BCrypt work factor 11 (default in `BCrypt.Net-Next`).
- `BasicAuthenticationHandler` uses `IUserQueries.FindByUsernameAsync` + `BCrypt.Verify`.
- `UserSeeder.SeedAsync` runs at every startup and is idempotent — inserts missing users, updates password hashes when configuration drifts.
- Three authorization policies: `OrganizationOnly`, `UserOrAdmin`, `AdminOnly` — string-based `RequireRole`.
- Failure responses generic ("Invalid credentials") — no user-existence leak.
- Negative-auth coverage: missing header, wrong password, unknown username, and malformed Base64 header all tested (`CmsEventsEndpointTests`).

### ADR-012 — Secret Management — CONFORMS

- Configuration precedence: `appsettings.json` → `appsettings.{Env}.json` → User Secrets (dev) → env vars → Azure Key Vault (prod).
- Key Vault provider wired via `KeyVaultUri` env var + Managed Identity (`DefaultAzureCredential`).
- User Secrets registered in Api csproj (`UserSecretsId`).
- `.gitignore` excludes `appsettings.Local.json` and `*.secrets.*`.
- Runbook (`docs/runbook-secret-rotation.md`) covers 90-day rotation + rollback.

### ADR-013 — Rate Limiting — CONFORMS

- Built-in `Microsoft.AspNetCore.RateLimiting` (no third-party).
- Sliding-window algorithm via `RateLimitPartition.GetSlidingWindowLimiter`.
- Partition key: `user:{username}:{path}` for authenticated requests — per-user-per-endpoint bucket (documented in ADR-013 § Trade-offs).
- Per-endpoint permits configurable via `RateLimiting:*` in appsettings.
- 429 response includes `Retry-After` header (with a segment-duration fallback when the limiter does not emit `MetadataName.RetryAfter`) and an `ErrorEnvelope` JSON body with `correlationId`, `error: "rate_limit_exceeded"`, and `retryAfterSeconds`. Integration-tested (`RateLimitingTests`).
- Pipeline order: auth → rate limiter → endpoints.

### ADR-014 — Observability — CONFORMS

- Serilog configured with Console + Application Insights sinks; JSON output template.
- Log levels match ADR: Info for applied events, Warning for skipped events (`superseded_by_version`, `duplicate`, `orphan_delete`, `stale_delete`, clock-skew), Error for permanent failures.
- OpenTelemetry: ASP.NET Core + HTTP + EF Core instrumentation, plus a custom `EventProcessingActivitySource` for per-handler spans. Azure Monitor exporter enabled when `ApplicationInsightsConnectionString` is set.
- `CorrelationIdMiddleware` extracts/generates `X-Correlation-ID`, pushes to Serilog `LogContext`, echoes to response headers.
- `X-Batch-Id` set on `POST /cms/events` responses.

**Observation**: W3C `traceparent` propagation is provided by OpenTelemetry's ASP.NET Core auto-instrumentation. Not explicitly wired in code, which is correct — the library handles it.

### ADR-015 — API Versioning — CONFORMS

- Endpoints unversioned: `/cms/events`, `/entities`, `/entities/{id}/{disable|enable}`.
- No `AddApiVersioning` package or route prefixes.
- Versioning triggers and migration plan documented in ADR.

### ADR-016 — Testing Strategy — CONFORMS

- Three test projects: `Unit.Tests`, `Integration.Tests`, `Architecture.Tests`.
- Correct tooling per tier: SQLite in Unit; Testcontainers.MsSql in Integration; NetArchTest in Architecture.
- CI (`.github/workflows/ci.yml`) runs all three tiers.
- xUnit + Moq + FluentAssertions.
- Coverage: 85% line / 72% branch as of Round 2 (see `coverage-notes.md`); Round 3 tests (invalid-auth matrix, `SqlExceptionClassifier`, `EvaluateForDelete`, validator boundary cases) were added after that capture — re-run coverage locally for post-fix figures.

## Additional observations (non-blocking)

### Sample HTTP auth headers hardcoded for one specific password

`samples/CmsEvents.http` has base64-encoded credentials for a specific dev password. If the operator picks a different password, they must regenerate the base64. Documented in the file header. Could be improved by templating the password into a `.http` variable, but `.http` files do not support runtime base64 encoding portably across editors.

### Integration test isolation risk

Tests share one Testcontainers SQL Server via collection fixture (amortizes ~10s startup). State accumulates across tests. Mitigated by per-test unique-Guid IDs in every test that could otherwise collide. New tests should follow the same pattern.

## Cycle log — drifts found and resolved

The initial audit surfaced four drifts. All were resolved in follow-up commits before this report was rewritten. This section keeps the history visible; the § Verification per ADR section above describes current state.

| # | Original drift | Resolution | Where the fix lives |
|---|----------------|------------|---------------------|
| 1 | ADR-005: clock-skew warning (`incoming.timestamp > now + 24h`) not implemented | Added `WarnIfClockSkew` in `ProcessEventBatchHandler`; unit-tested | `Application/Features/ProcessEventBatch/ProcessEventBatchHandler.cs`; `ProcessEventBatchHandlerTests.Handle_FarFutureTimestamp_*` |
| 2 | ADR-009: 60-second processing timeout not enforced (config key existed but was never read) | Added `AddRequestTimeouts` in `Program.cs`; endpoint uses `.WithRequestTimeout(EventBatch)` policy that reads `ProcessingOptions.TimeoutSeconds` | `Api/Program.cs`; `Api/Configuration/RequestTimeoutPolicies.cs`; `Api/Configuration/ProcessingOptions.cs` |
| 3 | ADR-007: `ISJSON` check constraint on `Payload` column marked in a comment but not applied by any migration | Added `migrationBuilder.Sql("ALTER TABLE [Entities] ADD CONSTRAINT [CK_Entities_Payload_IsJson] CHECK (ISJSON([Payload]) = 1)")` to the initial schema migration | `Infrastructure/Migrations/20260807163605_InitialSchema.cs` |
| 4 | ADR-016: coverage below 75% target on first run | Added integration tests for `Entities` endpoints; fixed `ProcessingOptions` instantiation (was 0% because the DTO was declared but never constructed via `Get<T>()`); expanded validator, dispatcher, and handler unit tests | `tests/CmsEvents.Integration.Tests/Endpoints/EntitiesEndpointsTests.cs`; `Api/Program.cs` (Get<ProcessingOptions>); `tests/CmsEvents.Unit.Tests/**/*` |

Subsequent review cycles surfaced additional findings, each resolved and reflected in the current-state section above:

- **DbUpdateException classified as transient vs permanent** (was all-transient) → `SqlExceptionClassifier` + `PermanentPersistenceException`; taxonomy update in ADR-008.
- **Delete order-blind** (late delete could erase newer state) → `Entity.EvaluateForDelete` + `stale_delete` skip reason; ADR-005 § Delete events rewritten.
- **Validator missing rules** (stray version on delete, no payload size cap) → `Null().When(delete)` + `MaxPayloadBytes` rule; ADR-008 § Input validation clarified.
- **ADR-005/006 wording drift** (non-monotonic version, orphan-delete described as failure in Consequences) → wording aligned with code behavior.
- **ADR-010 projections claim** (documented but not implemented) → ADR reconciled with actual reader materialization; deferred as `future-improvements.md` #15.
- **Negative-auth matrix incomplete** (only missing-header covered) → wrong-password, unknown-user, malformed-header integration tests added.
- **Integration coverage thin on ordering** (delete-then-publish, out-of-order versions, late-delete) → four integration tests added against real SQL.
- **Mock-call assertions** in Publish/UnPublish handler unit tests → replaced with `Callback` capture + explicit state assertions on the mutated aggregate.

## How to keep this document honest

- When code changes ADR behavior, update the ADR and rerun the corresponding row above.
- When an ADR is superseded, mark it "Superseded" here (not deleted) and link the successor.
- A stale audit is worse than no audit. If this document is ever unclear, delete it rather than leave a lie in the repo.
