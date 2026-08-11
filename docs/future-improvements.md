# Future Improvements — CmsEvents Service

**Project**: CmsEvents Service
**Last updated**: August 2026

## About this document

This document collects ideas evaluated during the initial design phase but deferred from the first iteration. Each entry captures:

- **Context** — the current state or limitation the improvement would address.
- **Why not now** — the reason for deferral.
- **Trigger** — the evidence or requirement that would justify implementing it.
- **Design sketch** — a brief note on how it would be implemented, when useful.
- **Related ADRs** — cross-references.

This is a living backlog, not a wishlist. Every entry represents a trade-off the team explicitly weighed and chose to defer. Items may be promoted to ADRs when triggered.

Companion documents:

- `decisions.md` — accepted design decisions.
- `architecture.md` — structural reference.
- `responses.md` — API contract.

---

## 1. Batch-level idempotency key

**Context**: Producers may retry entire batches after a timeout or network failure. Per-event idempotency (ADR-005) handles correctness — already-processed events skip naturally — but the retried batch pays the full processing cost again.

**Why not now**: Per-event idempotency covers correctness. Optimization is speculative without evidence of frequent batch retries.

**Trigger**: Producer telemetry shows a meaningful fraction of batches retried in short windows (e.g., > 5% of batches within 60 seconds).

**Design sketch**: Support `X-Idempotency-Key` request header. Cache recent batch keys (with a small TTL) in a distributed store. If a repeat key arrives, return the cached response without re-processing.

**Related ADRs**: ADR-005, ADR-008, ADR-009.

---

## 2. Async batch processing

**Context**: ADR-008 chose synchronous processing. Large or slow batches may hit the 60-second timeout from ADR-009, forcing CMS retries.

**Why not now**: No evidence of sustained load or batch sizes exceeding sync capacity. Sync gives producers immediate per-event feedback with no infrastructure cost.

**Trigger**: See ADR-009 § When to Migrate to Async — sustained throughput > 10 batches/second, batch size consistently > 1000 events, P95 latency > 30 seconds under normal load, or producer changes to tolerate async delivery.

**Design sketch**: `POST /cms/events` accepts batch and returns `202 Accepted` with a status endpoint URL. A `BackgroundService` consumes an internal queue (or Azure Service Bus) and invokes the same `EventDispatcher`. Handlers unchanged (transport-agnostic per ADR-002 rule 3). Status endpoint returns batch outcome when processing completes.

**Related ADRs**: ADR-004, ADR-008, ADR-009.

---

## 3. Streaming ingestion (Kafka / Event Hubs / Service Bus)

**Context**: Current ingestion is HTTP webhook. High-scale or multi-tenant deployments may benefit from a streaming pipeline with backpressure and native replay semantics.

**Why not now**: The spec describes a webhook receiver — HTTP is the correct default. Streaming infrastructure adds significant operational overhead without evidence of need.

**Trigger**: Sustained ingestion beyond sync capacity even after async migration, OR requirement for producer-side replay across arbitrary time windows.

**Design sketch**: A Kafka / Event Hubs consumer replaces (or complements) the HTTP endpoint, invoking the same `EventDispatcher` with parsed events. Handlers unchanged. HTTP endpoint may remain for occasional producers.

**Related ADRs**: ADR-004, ADR-009.

---

## 4. Full role-based access control (RBAC)

**Context**: ADR-011 uses a single `Role` column per user, backed by a string enum (`UserRole`). Supports one role per user.

**Why not now**: The spec identifies three actors (organization, user, admin) and no user has multiple roles. Single-column model is sufficient and simpler.

**Trigger**: A user needs to hold multiple roles simultaneously (e.g., an admin who is also a regular user in a different context), OR the number of roles grows beyond ~5 and dynamic role management via API is desired.

**Design sketch**: Migrate to `Users` + `Roles` + `UserRoles` (many-to-many). Preserve string-based role checks in policies (`user.HasRole("Admin")`) — existing policy code does not change. Migration is a schema change + one lookup update in the auth middleware. Estimated effort: 2-4 hours per ADR-011 discussion.

**Related ADRs**: ADR-007, ADR-011.

---

## 5. Unit-of-Work extraction

**Context**: ADR-010 (revised) puts `SaveChangesAsync` and `ExecuteInTransactionAsync` on `IEntityRepository` for simplicity. This works for single-aggregate transactions but starts to leak Unit-of-Work concerns into the repository.

**Why not now**: Only one aggregate root (`Entity`) exists; cross-aggregate transactions are not needed. Adding `IUnitOfWork` upfront is ceremony without payoff.

**Trigger**: A second aggregate emerges that must participate in the same transaction as `Entity`, OR the `SaveChangesAsync`/`ExecuteInTransactionAsync` on `IEntityRepository` starts to feel out of place for the "single-aggregate abstraction" concept.

**Design sketch**: Introduce `IUnitOfWork` in Application with `SaveChangesAsync` and `ExecuteInTransactionAsync`. Remove those methods from `IEntityRepository`. Command handlers inject both `IEntityRepository` and `IUnitOfWork`. Infrastructure implementation of `IUnitOfWork` uses `WriterDbContext`. Handlers preserve their business logic; only the "how to commit" call site changes.

**Related ADRs**: ADR-008, ADR-010.

---

## 5b. Rate-limit partition key by route pattern (not literal path)

**Context**: `RateLimitingSetup.BuildPartitionKey` uses `user:{username}:{path}` where `path` is the literal request URI. This means `/entities/entity-a/disable` and `/entities/entity-b/disable` count against different buckets, so an admin can disable many entities in a short window without triggering the limit.

**Why not now**: The permissive-only nature means users can do more than expected, not less — no correctness bug. Reworking to route-pattern grouping (`user:admin:disable-endpoint`) requires either route metadata lookup or a policy naming convention.

**Trigger**: An admin abuse scenario emerges (single admin flapping many entity states in rapid succession) that a per-endpoint bucket would prevent.

**Design sketch**: Extract the route template (e.g., `/entities/{id}/disable`) instead of the resolved path when building the partition key. `context.GetEndpoint()?.Metadata.GetMetadata<RouteNameMetadata>()` or `HttpContext.GetRouteData()` provide the template.

**Related ADRs**: ADR-013.

---

## 6. Audit trail for admin actions

**Context**: ADR-007 uses a single `IsDisabled` boolean without tracking who set it or when. Support workflows requiring "who disabled entity X on 2026-08-01?" cannot be answered from the current schema alone (though Application Insights logs contain the information).

**Why not now**: The spec does not require audit. Compliance-driven requirements have not emerged.

**Trigger**: Compliance framework (SOC 2, ISO 27001) requires audit trails for admin actions, OR support requests for "who disabled/enabled X" become frequent.

**Design sketch**: Add `DisabledAt` (nullable DateTime UTC), `DisabledBy` (string, username), and optionally an `AdminActionsAudit` table with (`Id`, `EntityId`, `Action`, `PerformedBy`, `PerformedAt`). Middleware writes an audit row on every disable/enable call. Retention policy TBD.

**Related ADRs**: ADR-007.

---

## 7. Batch response compression (gzip)

**Context**: `POST /cms/events` response includes counts + `errors[]` array. Large batches with many validation failures could produce responses in the tens of KB range.

**Why not now**: Response size is not observed as a problem. ASP.NET Core supports compression via `ResponseCompressionMiddleware` — trivial to enable when needed.

**Trigger**: Response sizes exceed ~50 KB regularly, OR producer-side telemetry shows meaningful bandwidth overhead.

**Design sketch**: Enable `ResponseCompressionMiddleware` with gzip and brotli providers. No code changes to handlers or contracts.

**Related ADRs**: ADR-008.

---

## 8. Payload typed deserialization

**Context**: The CMS event `payload` is opaque per spec — we store it as JSON (`nvarchar(max)`) without deserializing into typed entities. Query endpoints return the payload as an unstructured object.

**Why not now**: The spec does not describe payload structure. Assuming a schema would be speculative and locks us into it.

**Trigger**: The spec (or CMS producer team) provides a payload schema, OR consumer requirements emerge for structured filtering (e.g., "find entities where payload.category = 'X'").

**Design sketch**: Introduce typed entity classes in `CmsEvents.Domain` matching the schema. Add EF Core mappings for structured columns (either as owned entities or via JSON columns with query support). Handlers deserialize the payload into the typed entity for business logic. Backwards-compatible if versioned properly (see ADR-015).

**Related ADRs**: ADR-006, ADR-015.

---

## 9. Distributed rate limiting

**Context**: ADR-013 uses ASP.NET Core built-in rate limiting with per-instance state. Behind N instances behind a load balancer, effective aggregate limit per user is `perInstance × N`.

**Why not now**: Per-instance precision is adequate for the expected traffic profile. Distributed state adds infrastructure (Redis) with operational cost.

**Trigger**: Precise per-user limits across the cluster become a requirement (e.g., multi-tenant SLA, abuse prevention that requires strict caps).

**Design sketch**: Replace the in-memory rate limiter store with a Redis-backed store. `Microsoft.AspNetCore.RateLimiting` supports pluggable stores. Handlers unchanged.

**Related ADRs**: ADR-013.

---

## 10. Orphan-delete rate alerting

**Context**: Orphan deletes (per ADR-006) are logged at Warning level but not alerted. A sustained pattern may indicate a producer bug or delivery-order issue that deserves proactive investigation.

**Why not now**: No baseline for what constitutes a "concerning rate" until real production data is available. Manual review of Warning logs is sufficient initially.

**Trigger**: More than N orphan deletes within a rolling time window (initial suggestion: > 10 in 1 hour, tune with data), OR support requests emerge tied to orphan-delete patterns.

**Design sketch**: Application Insights Kusto query aggregating `orphan_delete` skip logs by hour. Alert rule firing when count exceeds threshold. Routes to on-call channel.

**Related ADRs**: ADR-005, ADR-006, ADR-014.

---

## 11. Cursor-based pagination

**Context**: `GET /entities` currently returns up to `limit` (default 100, max 500) entities in a single response. Beyond that, clients cannot iterate.

**Why not now**: The spec does not specify pagination. Data volume in initial deployment is expected to be within a single response.

**Trigger**: Data volume grows beyond ~1000 entities and consumers need to list all, OR consumer requirements emerge for stable iteration under concurrent writes.

**Design sketch**: Query parameters `?after=<opaque-cursor>&limit=100`. Response includes `nextCursor` field (null when no more). Cursor encodes the last returned entity's sort key (e.g., `Id` with a stable order). Handlers use `WHERE Id > cursor ORDER BY Id LIMIT n`.

**Related ADRs**: ADR-007.

---

## 12. Authenticated principal cache

**Context**: `BCrypt.Verify` runs on every request per ADR-011. Work factor 11 costs ~200ms per call. Under sustained load, this is measurable CPU pressure.

**Why not now**: Expected load does not stress the auth path. Caching adds cache-invalidation concerns (password change, user deletion).

**Trigger**: Auth path becomes a measured bottleneck (P95 latency dominated by BCrypt), OR request rate exceeds ~50 rps per user.

**Design sketch**: `IMemoryCache` keyed by `(username, passwordHashHash)` with short TTL (e.g., 5 minutes). Cache stores the resolved `ClaimsPrincipal`. Invalidate on user update (`Users` table trigger or event). Trade-off: password rotation delay bounded by TTL.

**Related ADRs**: ADR-011.

---

## 13. Batch payload audit table

**Context**: Inbound batches are not persisted separately. Once processed, we retain only the applied entities. Support workflows requiring the original payload cannot recover it after the fact.

**Why not now**: Payloads are opaque and potentially sensitive; persisting them raises privacy and cost concerns. Structured logs (per ADR-014) capture enough for most debugging.

**Trigger**: Support requests requiring original payload retrieval become frequent, OR compliance requires immutable audit of all inbound data.

**Design sketch**: `BatchAudit` table storing (`BatchId`, `CorrelationId`, `ReceivedAt`, `RawBody`, `Outcome`). Retention policy: 30-90 days (align with log retention per ADR-014). Access restricted to admin API endpoint or database-only via runbook.

**Related ADRs**: ADR-008, ADR-014.

---

## 14. SignalR / WebSocket push to consumers

**Context**: Consumers currently poll `GET /entities` to see updated data. Real-time propagation of entity changes requires client-driven polling.

**Why not now**: Spec does not require real-time push. Adding push infrastructure (SignalR hub, connection state, reconnection logic) is significant complexity.

**Trigger**: Consumer requirement for near-real-time updates, OR polling volume becomes wasteful (measured by GET request rate vs actual change rate).

**Design sketch**: SignalR hub in `CmsEvents.Api`. Event handlers (per ADR-004) publish domain events after applying state changes. A hub subscriber projects events into consumer-facing messages, filtered by consumer's role. Requires new authorization model for hub connections.

**Related ADRs**: ADR-004, ADR-007.

---

## 15. Reader-side projections

**Context**: `EntityQueries.ListAsync` and `EntityQueries.FindByIdAsync` materialize the full `Entity` row via `NoTrackingWithIdentityResolution`. The reader technique of projecting with `.Select(e => new EntityReadModel {...})` is documented as an available optimization in ADR-010 but is not currently applied.

**Why not now**: The response DTO (`EntityResponse`) consumes 6 of the 8 entity columns; the only columns projection would skip today are `CreatedAt` and `UpdatedAt` (~16 bytes per row). The largest column by far — `Payload` (opaque JSON, potentially KBs) — is required by the response and would remain on the query regardless. The ceremony of maintaining a separate read model exceeds the bandwidth savings at current scale.

**Trigger**: One or more of the following becomes true — (a) the entity schema gains columns that are NOT surfaced in the response (e.g., admin-only audit fields, denormalized aggregates, soft-delete markers); (b) a list endpoint variant is introduced that does NOT need `Payload` (e.g., a lightweight index endpoint); (c) profiling shows that the reader path is bottlenecked on column materialization rather than payload deserialization.

**Design sketch**: Add a lean `EntityReadModel` (or per-endpoint read models) in Infrastructure. `EntityQueries.ListAsync` and `FindByIdAsync` project via `.Select(e => new EntityReadModel { ... })`. Handler `Map` methods rebind to the read model shape. Two implementations of `IEntityQueries` may coexist during migration behind a feature flag if the reader is critical-path.

**Related ADRs**: ADR-010.

---

## 16. Optimistic concurrency for same-id writers

**Context**: `Entity` has no `RowVersion` / `Timestamp` concurrency token. Two producers submitting `publish` for the same new id in the same instant may both read `FindByIdAsync -> null` and both attempt an insert; the second attempt gets a primary-key violation and surfaces as `persistence_error` (correct outcome, but the loser sees a persistence-layer reason for what is logically a race). Same shape applies to concurrent updates against an existing id — the last committed write wins with no detection of the overwrite.

**Why not now**: The spec does not describe concurrent producers, and there is no measured evidence of the pattern in real load. Producers already have to handle non-`processed` outcomes (retry on `processing_timeout`, `persistence_error`, or apply idempotency skip semantics), so the current failure surface is not silently wrong — it is a slightly-noisier presentation of a race that the producer can recover from. The alternative implementation cost (migration + repository + handler retry loop + tests) is real and would be gold-plating today. Documented as an accepted limitation in ADR-005 § Consequences.

**Trigger**: One or more of the following becomes true — (a) telemetry shows a non-trivial rate of `persistence_error` outcomes correlated with concurrent same-id activity; (b) a producer requirement emerges for a distinct outcome that says "another writer beat you"; (c) a use case appears that requires read-modify-write with detection of intervening writes.

**Design sketch**: Add a `RowVersion byte[]` (SQL `rowversion`) column on `Entity`; EF Core `IsRowVersion()` in `EntityConfiguration`. In `EntityRepository.SaveChangesAsync`, catch `DbUpdateConcurrencyException`, re-read the entity, re-evaluate the incoming event against the fresh state via `Entity.EvaluateForApply`/`EvaluateForDelete`, and either re-apply (bounded retry) or fall through to the correct skip/failure outcome. Add an integration test that fires two concurrent `Task.WhenAll` updates against the same id and asserts the winner + the re-read outcome for the loser.

**Related ADRs**: ADR-005, ADR-008, ADR-010.
