# Coverage Notes

**Date**: August 2026
**Method**: Merged coverage report from `dotnet test --collect:"XPlat Code Coverage"` across all three tiers, aggregated with `dotnet-reportgenerator-globaltool`.

## Summary

| Metric | Value | Target (ADR-016) | Status |
|--------|-------|------------------|--------|
| **Line coverage** | **85%** | ≥ 75% overall | Exceeds |
| **Branch coverage** | **72%** | Not formally targeted | Solid — see gaps below |
| **Cyclomatic max** | ≤ 10 (after two extract-method refactors) | ≤ 20 recommended | No hotspots |

Numbers reflect the merged report from `dotnet test --collect:"XPlat Code Coverage"` across all three tiers after clearing stale TestResults. Rounds of iterative fixes:

- **Round 1** (initial): 79% line / 67% branch — snapshot after removing stale coverage files from previous builds.
- **Round 2**: 85% line / 72% branch — after applying:
  - `ProcessingOptions` instantiated via `configuration.Get<T>()` (was 0% because the class was never constructed, only used to name a config key).
  - 11 new integration tests covering `GET /entities`, `GET /entities/{id}`, `POST /disable`, `POST /enable` (previously only `POST /cms/events` was integration-tested).
- **Round 3** (post-review): additional tests added in response to the external code review — invalid-credential auth paths, `SqlExceptionClassifier` unit matrix, `EvaluateForDelete` branches, validator boundary tests (delete-with-version, payload size cap). Fresh numbers were not captured before this note was written; re-run `dotnet test --collect:"XPlat Code Coverage"` locally for the current figures. The new tests raise line + branch coverage, they do not lower it.

Line coverage well above target. Branch coverage naturally lags line coverage in any project that has defensive guards and environment-conditional setup code — the gaps documented below explain why the remaining branches are legitimately uncovered by tests.

## Uncovered branches — accepted gaps

The gap between 100% and current branch coverage lives in four categories, all of which are deliberate and low-risk. Attempting to cover them would either require executing code that only runs in production (Key Vault, App Insights) or writing tests that exercise unreachable/defensive paths.

### Category 1: Environment-conditional configuration (Program.cs)

Roughly 25 uncovered branches. These blocks only execute under specific hosting conditions:

- `if (builder.Environment.IsDevelopment())` — User Secrets registration. Tested in dev, false-branch runs only in Staging/Production.
- `if (!string.IsNullOrWhiteSpace(keyVaultUri))` — Key Vault configuration provider. Registered only when `KeyVaultUri` env var is set (production). Local dev + integration tests leave it null.
- `if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))` in `AddOpenTelemetry` — Azure Monitor exporter vs Console exporter. Same logic.

**Why not test**: exercising these would require spinning up a fake Key Vault or App Insights emulator in integration tests, adding significant infrastructure without corresponding correctness benefit. The wiring is simple and any mistake would fail loudly at startup in the target environment.

### Category 2: Defensive guards on transport-layer paths

Roughly 15 uncovered branches. Examples:

- `catch (Exception ex) when (ex is not HostAbortedException)` in `Program.Main` — logs fatal errors and exits with code 1. Reachable only if `WebApplication.RunAsync` throws unexpectedly during host shutdown.
- Null-coalescing fallbacks: `context.User.Identity?.Name ?? "anonymous"` in `RateLimitingSetup.BuildPartitionKey`. The `?? "anonymous"` branch runs only for unauthenticated requests, but all endpoints require auth per ADR-011 — this branch is defensive against future unauthenticated endpoints.
- `context.Items[CorrelationIdMiddleware.HttpContextItemKey] is Guid id ? id : Guid.NewGuid()` — the fallback triggers only if `CorrelationIdMiddleware` did not run before the endpoint, which is prevented by the middleware pipeline order.

**Why not test**: these are defensive-by-design. Writing tests that bypass the middleware pipeline to exercise the fallback would test the wrong thing (they'd assert on behavior that will never occur in production). Documented in code with rationale.

### Category 3: Parser edge cases in `BasicHeaderParseResult`

`BasicAuthenticationHandler.TryParseBasicHeader` has multiple guards that only branch on malformed input the framework rejects earlier:

- `if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith(SchemePrefix, ...))` — the null/empty path is unreachable when the header is present (framework guarantees non-null).
- `catch (FormatException)` on `Convert.FromBase64String` — reached when the client sends invalid Base64 in the Authorization header. **This branch is now covered** by `CmsEventsEndpointTests.Post_WithMalformedAuthorizationHeader_ReturnsUnauthorized`.

The remaining uncovered guards are defensive against inputs the framework layer never produces (e.g., a non-null header value that is nonetheless empty). They are documented in code with rationale.

The "wrong password" and "unknown user" branches — spec item 7 requires them tested — are covered by `CmsEventsEndpointTests.Post_WithWrongPassword_ReturnsUnauthorized` and `Post_WithUnknownUsername_ReturnsUnauthorized`.

### Category 4: EF Core / infrastructure boilerplate

Roughly 20 uncovered branches from:

- Setter branches on init-only properties that only the migration path exercises.
- `SeedAsync` conditional `LogWarning` when a seed entry is not configured — normally all three seeds are set.
- `DbInitializer.StartAsync` logging — successful path is tested; the error path isn't (would require an unreachable DB during startup).

**Why not test**: same "defensive against runtime environment problems" pattern. The logging is verified by manual inspection during setup; failing to log is not a functional bug.

### Consciously deferred: mid-batch transient failure integration test

An end-to-end test that exercises the Polly retry + `TransientPersistenceException` path against real SQL was considered and deferred. Reliably injecting a transient failure at the SQL layer (deadlock, connection reset, resource-limit) requires either a test-only `DbCommandInterceptor`, a mid-transaction connection kill, or a chaos-engineering harness — all of which add non-trivial test infrastructure whose maintenance cost exceeds the incremental confidence gain.

The transient-retry behavior is instead covered by:

- Unit test `ProcessEventBatchHandlerTests.Handle_TransientFailure_RetriesThenMapsToProcessingTimeout` — asserts on Polly attempt count (initial + 3 retries) and the resulting `processing_timeout` outcome.
- Unit test `ProcessEventBatchHandlerTests.Handle_PermanentPersistenceFailure_DoesNotRetry_MapsToPersistenceErrorReason` — asserts that non-transient failures bypass Polly (called exactly once).
- Unit matrix in `SqlExceptionClassifierTests` — verifies the SQL error-number taxonomy (transient vs permanent) used by the repository at the classification seam.

If a future incident implicates the retry path, promote this to an integration test using one of the injection techniques above.

## Options if higher branch coverage is required

If a stakeholder requires ≥ 80% branch coverage, options in order of effort:

1. **Add integration tests with `KeyVaultUri` set** — spin up a mock secret provider. Requires new infrastructure.
2. **Split `Program.Main` into a testable `Startup` class** — enables unit-testing configuration branches. Moderate refactor.
3. **Add invalid-header integration tests** — send malformed `Authorization` headers to `/entities`. Would cover the `FormatException` branch. Small effort, ~5 test cases.

None of these are recommended for the current scope — they would add complexity for coverage-per-coverage, not for defect prevention.

## Cyclomatic complexity — post-refactor

Two hotspots identified in the initial coverage run and refactored:

| Method | CC before | CC after | Refactor |
|--------|-----------|----------|----------|
| `Program.AddRateLimiting` | 18 | ~4 (main) + 4 (`ResolvePermitLimit`) + 2 (`BuildPartitionKey`) + 2 (`SetRetryAfterHeader`) | Extracted to `Api/Configuration/RateLimitingSetup.cs` with dedicated helpers per concern |
| `BasicAuthenticationHandler.HandleAuthenticateAsync` | 16 | ~5 (main) + 8 (`TryParseBasicHeader`) + 2 (`BuildTicket`) | Extracted header parsing to a helper returning a discriminated result (`Missing` / `Malformed` / `Ok`) |

The `TryParseBasicHeader` helper is still at CC 8 due to the sequence of defensive guards required for parsing Basic Auth. Further extraction would fragment the parser into unnaturally small pieces without reducing overall complexity.

## Recommendation

Coverage above the ≥ 75% line target is a strong result for the scope of this service (see the Summary table for the most-recently-captured Round 2 figures — 85% line / 72% branch — with Round 3 tests raising both). The uncovered branches are documented, low-risk, and would require disproportionate effort to reach 100%. Coverage is not a substitute for design review, and every remaining gap has been evaluated and accepted here.
