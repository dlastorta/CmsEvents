# Architecture Decision Records — CmsEvents Service

**Project**: CmsEvents Service
**Last updated**: August 2026

## About this document

This document collects the architectural decisions made for the CmsEvents Service. Each entry follows a hybrid MADR-style template inspired by Martin Fowler's guidance on ADRs — brief (typically under 500 words), decision-focused, with explicit alternatives and consequences.

Cross-cutting design principles and operational details referenced by these ADRs live in companion documents:

- `architecture.md` — deployment, scaling, boundary rules, transport-agnostic handler design, logging schema, batch response format.
- `responses.md` — full API response schemas with examples.
- `future-improvements.md` — deferred ideas and evolution paths.
- `runbook-secret-rotation.md` — 90-day rotation runbook.

## Table of Contents

- [ADR-001: Clean Architecture with 5-Project Layering](#adr-001-clean-architecture-with-5-project-layering)
- [ADR-002: Architectural Boundary Tests with NetArchTest](#adr-002-architectural-boundary-tests-with-netarchtest)
- [ADR-003: Vertical Slice Organization with MediatR + Internal Strategy](#adr-003-vertical-slice-organization-with-mediatr--internal-strategy)
- [ADR-004: Strategy Pattern for Internal CMS Event Dispatch](#adr-004-strategy-pattern-for-internal-cms-event-dispatch)
- [ADR-005: Version + Timestamp Idempotency](#adr-005-version--timestamp-idempotency)
- [ADR-006: Handling Events for Unknown Entities](#adr-006-handling-events-for-unknown-entities)
- [ADR-007: Local Disabled Flag Separate from CMS Publication State](#adr-007-local-disabled-flag-separate-from-cms-publication-state)
- [ADR-008: Synchronous Batch Processing with Per-Event Transactions](#adr-008-synchronous-batch-processing-with-per-event-transactions)
- [ADR-009: Failure Handling and Extension Paths for Batch Processing](#adr-009-failure-handling-and-extension-paths-for-batch-processing)
- [ADR-010: Reader/Writer DbContext Separation](#adr-010-readerwriter-dbcontext-separation)
- [ADR-011: Basic Authentication with Users Table](#adr-011-basic-authentication-with-users-table)
- [ADR-012: Secret Management](#adr-012-secret-management)
- [ADR-013: Rate Limiting](#adr-013-rate-limiting)
- [ADR-014: Observability](#adr-014-observability)
- [ADR-015: API Versioning](#adr-015-api-versioning)
- [ADR-016: Testing Strategy](#adr-016-testing-strategy)

---

## ADR-001: Clean Architecture with 5-Project Layering

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

The service ingests CMS webhook events, persists state in SQL Server, and exposes a REST API. It must accommodate future evolution: additional event types, different transport mechanisms (see ADR-009), and potentially additional consumers of the persisted state. Structural boundaries chosen now must support that evolution without inviting boundary erosion.

### Decision

Clean Architecture with five projects, each mapping to a well-defined layer:

- **CmsEvents.Api** — HTTP surface (controllers/endpoints), authentication middleware, DI composition root, serialization concerns.
- **CmsEvents.Application** — Use case orchestration via MediatR (see ADR-003), input validation, mapping between contracts and domain, transport-agnostic (see architecture.md § Transport-Agnostic Handlers).
- **CmsEvents.Domain** — Enterprise business rules, entities, value objects, domain interfaces (ports). No framework references.
- **CmsEvents.Infrastructure** — Concrete adapters: EF Core DbContexts, repositories, clock, secret store, external HTTP clients.
- **CmsEvents.Contracts** — Public API surface (request/response DTOs). Consumed by Api and potentially by external clients as a shared package.

Dependency direction: `Api → Application → Domain`; `Infrastructure → Domain`; `Api → Infrastructure` (composition root only); `Api → Contracts`. Domain depends on nothing.

### Alternatives Considered

- **Single-project monolith**: rejected — no enforceable boundaries; concerns collapse under sustained work.
- **N-tier (Presentation / Business / Data)**: rejected — data layer as dependency of business inverts Clean Architecture; harder to test business rules in isolation.
- **Onion Architecture**: rejected — near-equivalent to Clean; chose Clean for stronger community familiarity in .NET ecosystem and clearer layer naming.
- **Layer names "Presentation / Business / Data"**: rejected — Api/Application/Domain/Infrastructure aligns with prevailing .NET convention and communicates intent more precisely.
- **Merged Contracts into Api**: rejected — separating Contracts allows external consumers to reference DTOs without pulling the entire Api project.

### Consequences

**Positive**:

- Domain is testable without HTTP, DB, or filesystem.
- Adapters can be swapped (e.g., SQL Server → PostgreSQL, or add message-queue ingestion per ADR-009) without touching business rules.
- Boundaries are enforceable in code (see ADR-002).

**Trade-offs**:

- Five projects vs. one is more ceremony for a small codebase — accepted for the boundary guarantees.
- Mapping between Contracts DTOs and Domain entities requires explicit translation (see ADR-003 for pattern).
- Discipline required to keep concerns in their assigned layer; enforcement via boundary tests mitigates.

### Related ADRs

- ADR-002 (Boundary Tests) — enforces the dependency rules stated here.
- ADR-003 (Vertical Slice + MediatR) — organization within Application layer.
- ADR-004, ADR-009 — depend on the transport-agnostic property of Application handlers.

---

## ADR-002: Architectural Boundary Tests with NetArchTest

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

ADR-001 defines layer boundaries and dependency rules. Documentation alone does not prevent erosion — developers under time pressure, or unaware of the rules, will introduce boundary breaches that pass code review. The rules must be enforceable in build/CI.

### Decision

A dedicated test project, **CmsEvents.Architecture.Tests**, uses **NetArchTest.Rules** to encode the rules from ADR-001 as executable assertions. Failures block CI.

Rules enforced (see architecture.md § Architectural Boundaries for complete list and rationale):

1. Domain must not depend on Api, Application, Infrastructure, or any framework namespace beyond `System.*`.
2. Application must not depend on Api or Infrastructure.
3. Application must not depend on `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Mvc`, or `Microsoft.AspNetCore.Routing` — preserves the transport-agnostic property required by ADR-004 and ADR-009 (see architecture.md § Transport-Agnostic Handlers).
4. Infrastructure must not depend on Api. Infrastructure **may** depend on Application (dependency inversion — adapters implement ports defined in Application, per ADR-010 repository pattern).
5. MediatR request/query handlers must reside only in the Application assembly.
6. Types in Contracts must be public and sealed unless justified.
7. Application must not depend on `Microsoft.EntityFrameworkCore` — persistence details are hidden behind the repository interfaces in Application (see ADR-010).

### Alternatives Considered

- **Code review only**: rejected — human-only enforcement fails under load; boundary violations accumulate silently.
- **Roslyn analyzers with custom rules**: rejected — significantly higher authoring cost for the same guarantees; better fit when rules cannot be expressed as dependency checks.
- **ArchUnitNET**: viable alternative; chose NetArchTest for lower learning curve and adequate feature coverage for this project's rule set.
- **Reflection-based tests hand-written**: rejected — reinvents what NetArchTest already provides with less readable assertions.

### Consequences

**Positive**:

- Boundary rules are executable, not aspirational.
- New developers surface boundary breaches at build time rather than in code review.
- Refactoring safely — moving a type across projects fails the test if it introduces a forbidden dependency.

**Trade-offs**:

- Occasional false positives from transitive NuGet dependencies. Mitigation: narrow rules to specific namespaces (as in rule 3) rather than broad wildcards.
- Requires the test project to reference every assembly under test, adding a small build-time cost.
- Rules must be maintained alongside architectural evolution; drift between documented and enforced rules is possible if the ADR is updated without updating the test.

### Related ADRs

- ADR-001 (Clean Architecture) — source of truth for the rules encoded here.
- ADR-004 (Strategy Pattern) — depends on rule 3 for transport-agnostic dispatch.
- ADR-009 (Failure Handling and Extension Paths) — depends on rules 2 and 3 for its extension claims.

---

## ADR-003: Vertical Slice Organization with MediatR + Internal Strategy

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

The Application layer contains use cases (event batch processing, entity queries, admin operations). Organization within this layer influences discoverability, testability, and change cost. Two axes must be decided: how to group code (by feature vs. by technical concern), and what pattern mediates between the API surface and use case execution.

The design is not for a static feature set — it is built with the possibility of growth in mind (additional use cases, additional event types).

### Decision

**Vertical Slice** organization by feature, with **MediatR** as the request pipeline. Internal event dispatch inside the batch processing handler uses the **Strategy** pattern (see ADR-004).

Folder structure inside `Application/`:

```
Features/
  ProcessEventBatch/
  ListEntities/
  GetEntity/
  DisableEntity/
  EnableEntity/
EventProcessing/
  IEventHandler.cs
  EventDispatcher.cs
  Handlers/
Common/
```

Each feature folder contains its command/query, handler, and — **when the input requires validation beyond route-parameter and auth-role checks** — a FluentValidation validator. In this codebase only `ProcessEventBatch` currently has a validator (`CmsEventValidator`) because it accepts a JSON body per event that requires cross-field rules. `ListEntities`, `GetEntity`, `DisableEntity`, and `EnableEntity` take only route parameters and headers (validated by the framework and by authorization policies per ADR-011), so no explicit validator is needed. Adding one when a feature grows a body is a per-feature call.

Shared code lives in `Common/` only after the **Rule of Three** applies — code is duplicated across at least three features before being extracted. Premature abstraction is worse than duplication for a small codebase.

### Alternatives Considered

- **Horizontal (technical) layering inside Application** (`Handlers/`, `Validators/`, `Mappers/`): rejected — changing one feature touches multiple folders; discovery requires jumping across the tree.
- **Vertical slice without MediatR** (direct handler classes injected into controllers): rejected — as use cases grow, cross-cutting concerns (logging, validation, transactions) require pipeline behaviors that MediatR provides for free.
- **Full CQRS with separate command and query stacks**: rejected — over-engineered for the current read model, which serves the same data shape as the write model. Documented as an evolution path if read patterns diverge.
- **MediatR notification (`INotification`) for event dispatch**: rejected — semantic mismatch (see ADR-004 for detail).

### Consequences

**Positive**:

- Adding a use case is a single-folder change — new feature, new tests, minimal ripple.
- MediatR pipeline provides one insertion point for cross-cutting behaviors (logging, validation, transactions) applied uniformly.
- Vertical slices reduce merge conflicts when multiple developers touch different features.

**Trade-offs**:

- Slight boilerplate per use case (command class + handler class + validator class).
- Vertical slice can encourage duplication if the Rule of Three is not respected — mitigation via periodic review.
- MediatR adds an indirection layer between controller and handler; standard trade-off, well-documented, low friction for teams familiar with the pattern.

### Related ADRs

- ADR-001 (Clean Architecture) — Application layer boundaries respected by this organization.
- ADR-002 (Boundary Tests) — MediatR handler placement enforced.
- ADR-004 (Strategy Pattern) — internal event dispatch inside batch processing handler.

---

## ADR-004: Strategy Pattern for Internal CMS Event Dispatch

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

ADR-003 established MediatR for use case orchestration. The `ProcessEventBatchCommand` handler receives a batch of CMS events, each with a `type` field (`publish`, `unPublish`, `delete`). Within the handler, individual events must be routed to type-specific processing logic. Each type has distinct rules (version comparison, edge cases, hard-delete semantics) that cannot be collapsed into shared logic.

Event type count is 3 today but plausible additions exist: batch operations, entity-specific event types, CMS-specific dialects.

The dispatch mechanism chosen here operates **inside** a MediatR handler — complementing MediatR at a different layer, not competing with it.

### Decision

**Strategy pattern** with an `IEventHandler` interface, one handler class per event type, registered as **scoped** services. An `EventDispatcher` service holds a dictionary mapping event type strings to handler instances, resolved from DI at construction. Unknown event types raise `UnknownEventTypeException`, surfaced as permanent failure per ADR-008.

Files under `Application/EventProcessing/`, sibling to `Features/` (per ADR-003 folder structure).

### Alternatives Considered

- **Switch statement in handler**: violates Open/Closed; branches accumulate; dependencies conflated in single class.
- **MediatR `INotification`**: semantic mismatch — designed for pub/sub (one publish → N subscribers), not point-to-point routing. Parallel execution by default conflicts with sequential batch processing.
- **MediatR `IRequest` per event type**: still requires string-to-type mapping (Strategy implicit); adds pipeline overhead per event; muddles user use cases with internal type routing.
- **Chain of Responsibility**: wrong semantic — each event has exactly one correct handler, not fallback semantics.
- **Reflection-based resolver**: loses compile-time safety; harder to debug; unnecessary for 3 known types.

### Consequences

**Positive**:

- Open/Closed: new event type = new handler + DI registration, no changes to dispatcher or existing handlers.
- Single Responsibility per handler; testable in isolation.
- DI-clean: each handler injects only its specific dependencies.
- Complements MediatR without conflict at a distinct layer of abstraction.

**Trade-offs**:

- More files (1 interface + 1 dispatcher + N handlers).
- Slight indirection when tracing execution.
- DI registration discipline required; missing registration fails at dispatch time (mitigated via assembly scanning + integration tests).
- Two dispatch mechanisms coexist (MediatR + Strategy); requires clear documentation of applicability (this ADR + ADR-003).

### Related ADRs

- ADR-003 (Vertical Slice + MediatR + Strategy hybrid) — MediatR at use case boundary; this ADR details internal dispatch.
- ADR-005 (Version-based idempotency) — each handler enforces per-type idempotency rules.
- ADR-006 (Handling events for unknown entities) — handled inside each type-specific handler.
- ADR-008 (Sync batch processing) — uses this dispatcher inside per-event transaction loop.

---

## ADR-005: Version + Timestamp Idempotency

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

CMS events arrive via webhook and may be duplicated, delivered out of order (v4 before v3 due to network variability), retried after partial batch failure (see ADR-008), or delivered with identical version but different timestamps (rare CMS republish behavior).

Without an idempotency rule, retries could revert state to older versions or double-count. The service must apply each event's outcome exactly once regardless of delivery order or duplication.

### Decision

Each incoming `publish` or `unPublish` event carries a `version` (monotonic integer per entity) and a `timestamp` (ISO 8601 UTC, sourced from the CMS event emission time — not the receive time). Persisted entities store `LastProcessedVersion` and `LastProcessedTimestamp`.

**Idempotency rule** for `publish` and `unPublish`, applied per entity inside each event handler:

1. `incoming.version > stored.LastProcessedVersion` → apply; update both fields.
2. `incoming.version < stored.LastProcessedVersion` → skip (superseded).
3. `incoming.version == stored.LastProcessedVersion`:
   - `incoming.timestamp > stored.LastProcessedTimestamp` → apply; update timestamp only.
   - Otherwise → skip (duplicate or superseded).

**Delete events**: hard-delete per spec item 2. Delete carries no `version`, so ordering is derived from `timestamp` alone — the rule is:

- `incoming.timestamp > stored.LastProcessedTimestamp` → apply (hard-delete).
- `incoming.timestamp <= stored.LastProcessedTimestamp` → skip as `stale_delete` (Warning log). A later `publish` or `unPublish` has already advanced the entity; applying the delete would erase valid state. Guards against network reordering, replayed batches, and at-least-once producers.
- Entity not found locally → skip as `orphan_delete` (Warning log). See ADR-006 for handling policy, ADR-014 for logging levels.

Equal-timestamp is fail-safe skipped: a delete "at the same instant" as the last observed state has no defensible interpretation, so we protect against replays.

If a `publish` event arrives after a `delete` for the same id due to out-of-order delivery, it is processed as a new entity — hard-delete semantics forbid retaining a tombstone. This is a documented limitation of the spec-required delete behavior; the timestamp guard above only protects entities that still exist locally at the moment the delete is evaluated.

**Validation errors** (event rejected as permanent failure per ADR-008; batch processing continues with remaining events):

- Missing `version` on `publish` or `unPublish`, or `version < 1`. A **non-monotonic** version (lower than what's stored, or lower than a version already applied in the same batch) is NOT a validation error — it is handled by the idempotency rule above and skipped as `superseded_by_version`.
- Missing or malformed `timestamp` on any event.
- Stray `version` field on a `delete` event, or `payload` exceeding the size cap (see ADR-008 § Input validation).

**Observability warnings** (do not alter the idempotency decision — the event is still evaluated by the rule above):

- `incoming.timestamp > now + 24h` → warning logged. Threshold accommodates producer clock skew and timezone misconfiguration (e.g., local time labeled as UTC). A future-timestamped event with a lower version is still skipped by rule 2. A future-timestamped event with equal or higher version is still applied by rules 1 or 3. The warning is a signal for the operations team, not a gate.

Rule applies uniformly across `publish` and `unPublish` (see ADR-006 for orphan-entity handling).

### Alternatives Considered

- **Version only**: rejected — cannot resolve same-version reprocessing.
- **Timestamp only**: rejected — timestamps can tie or drift; version is the primary ordering signal.
- **Event ID deduplication (store processed IDs)**: rejected — unbounded storage; detects duplicates but not out-of-order.
- **Optimistic concurrency (EF Core `RowVersion`)**: rejected — solves concurrent-write conflicts, not delivery ordering.
- **Sort batch by version**: rejected — requires full batch in memory; does not help across-batch retries.
- **Reject batch on malformed event or future timestamp**: rejected — per-event handling preserves valid events; producer clock skew and timezone bugs are common enough that hard rejection creates operational friction.
- **Receive-time as timestamp instead of CMS event time**: rejected — introduces non-determinism (same-version events could produce different winners depending on our processing order).
- **Soft-delete tombstones for delete events**: rejected — spec explicitly requires hard-delete.

### Consequences

**Positive**:

- Retries are safe — already-processed `publish`/`unPublish` events skip naturally.
- Out-of-order delivery handled without buffering or sorting.
- Two-field comparison — cheap, trivially testable.
- Same rule across all publish/unPublish event types.

**Trade-offs**:

- CMS must send a `version` field on `publish`/`unPublish`; missing → permanent `validation_error` per event. Non-monotonic version (lower than what's stored) is NOT rejected — it is skipped as `superseded_by_version` by the idempotency rule, so retries and reorderings are safe by design.
- Millisecond precision assumed for tie-breaking; risk documented if CMS emits second-precision under rapid republish.
- Skips and warnings are logged, not silent — observability (ADR-014) surfaces both as anomaly signals.
- Sustained producer clock skew > 24h relative to UTC generates warning noise; if observed, iterate to hard rejection.
- Hard-delete precludes idempotent retry of delete-then-publish sequences. Retries of a delete after processing are safe (target already gone); retries of a publish after processing pass this rule normally; but a publish arriving after a delete for the same ID is indistinguishable from a legitimate new entity. Acceptable per spec requirement.
- **Concurrent same-id writers are not serialized at the domain layer.** Two producers submitting `publish` for the same new id in the same instant may both read `FindByIdAsync -> null`, both attempt an insert, and the second one fails with a `persistence_error` when SQL Server surfaces the primary-key violation. This is the correct outcome under the current design — the batch continues, no data corruption, no HTTP 500 — but it produces a `persistence_error` for the loser instead of a cleaner `duplicate` skip. The alternative (adding a `RowVersion` column + a re-read retry loop) was considered and deferred: the spec does not describe concurrent producers, we have no evidence of the pattern under real load, and the producer already has to handle retry on any non-processed outcome. Documented as `future-improvements.md` #16 with the trigger conditions. The current behavior is captured end-to-end by `CmsEventsEndpointTests.Post_ConcurrentPublishesForSameNewId_ResolveGracefully_ExactlyOneEntityPersists`.
- **A higher-version event with an earlier timestamp overwrites `LastProcessedTimestamp`.** Consequence of "version is CMS truth; timestamp is only a tie-breaker within the same version": when a higher-version event applies, its timestamp becomes the new `LastProcessedTimestamp` even if it is earlier than the stored value. This can then make a subsequent `delete` with a "mid-range" timestamp appear strictly newer than the stored state, and it will apply. This is intentional — the alternative (persisting `MAX(stored, incoming)` on the timestamp field) would decouple the field from its "when did this event happen" semantics and complicate reasoning without solving a spec-defined problem. If future evidence shows CMS sending a mix of high-version-old-timestamp and mid-timestamp deletes for the same id, revisit with either (a) an explicit `LastKnownActivityTimestamp` field separate from event-time, or (b) requiring producer to guarantee monotonic timestamp within an id. Behavior locked in by `EntityIdempotencyTests.EvaluateForApply_HigherVersion_EarlierTimestamp_AppliesAndRewindsTimestamp`.

### Related ADRs

- ADR-004 (Strategy Pattern) — each handler applies this rule.
- ADR-006 (Handling events for unknown entities) — rule interacts with entity absence.
- ADR-008 (Sync Batch Processing) — skips and permanent failures reported in batch response.
- ADR-009 (Failure Handling) — safe retry semantics depend on this rule.
- ADR-014 (Observability) — skip counts and clock-skew warnings as metrics.

---

## ADR-006: Handling Events for Unknown Entities

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

CMS events (`publish`, `unPublish`, `delete`) may arrive for an entity the service has never observed:

- **Out-of-order delivery** — a terminal event (unPublish, delete) arrives before publish.
- **Historical replay** — first-time consumer receives events for entities whose original publish predates ingestion.
- **Producer bug** — CMS emits a terminal event for an ID that never had a publish.

Each event type carries different plausibility for arriving orphaned. Handling policy differs accordingly, but all cases require traceability per the service's observability commitment (see spec item 6).

The spec explicitly calls out one variation of this scenario: *"an entity with version X can be modified → resulting in version X+1. Then, this gets unpublished. However, since there was no published version before that, you do no longer have the latest version in your database. Please also treat this corner case appropriately!"*

### Decision

**Publish for unknown entity**: standard insert (not an edge case).

**Unpublish for unknown entity**: upsert with `Status = Unpublished`, `LastProcessedVersion` and `LastProcessedTimestamp` from the event, other fields from the payload (nulls allowed). Subsequent publish events are evaluated by ADR-005 idempotency: higher version → applied; lower → skipped; equal → timestamp tie-break. This preserves the CMS's authoritative view under out-of-order delivery.

**Delete for unknown entity**: hard-delete is a no-op (nothing to remove). **Counted as skipped**. Logged at Warning level with `batchId`, `correlationId`, `eventId`, `entityId`, and reason `orphan_delete` — surfaces producer state drift or out-of-order delivery patterns to the dev team. **Not itemized in the batch response** — skipped events are logs-only per ADR-008; the desired end state (entity absent) is achieved so no producer action is required. Cross-batch delete-then-publish remains a documented limitation of hard-delete (see ADR-005 Consequences). Alerting on orphan-delete rate is documented in `future-improvements.md`.

**Payload validation**: incoming payload must not be null on `publish`/`unPublish`. Individual fields inside the payload may be null and are persisted as null; missing fields are completed when a later event carries full data.

### Alternatives Considered

- **Reject unpublish as permanent failure**: rejected — orphan unpublish is plausible under out-of-order delivery.
- **Symmetric upsert for delete**: rejected — delete-on-nonexistent is not plausible under normal operation; upserting a tombstone would mask producer bugs rather than surface them, and contradicts the spec-required hard-delete.
- **Report orphan delete as failed in response body**: rejected on reconsideration — the desired end state (entity absent) is achieved; producer requires no action. Warning-level log surfaces the anomaly to the dev team without adding noise to producer-facing response. Reported outcomes should be actionable, not merely informational.
- **Buffer events in-memory**: rejected — bounded memory, does not survive restart or span batches.
- **`WasEverPublished` flag**: rejected — no query use case identified; ADR-014 covers pattern detection without schema changes.
- **Minimum field requirements on unpublish payload**: rejected — spec does not define required fields; enforcing them would exceed the contract.

### Consequences

**Positive**:

- Unpublish out-of-order handled deterministically without buffering or coordination.
- Orphan deletes surfaced as **anomalies in structured logs** (Warning-level with full context) — producer bugs and delivery-order issues are visible to the dev team without adding noise to producer-facing responses.
- Consistent with idempotency contract (ADR-005) and traceability policy (ADR-014).

**Trade-offs**:

- Local store may contain entities that were only ever unpublished (never published). Query endpoints must respect `Status` filter (see ADR-007).
- Unpublish payloads with sparse data yield records with null fields until a publish arrives.
- Asymmetric handling of unpublish vs. delete requires documentation so future contributors do not "fix" the inconsistency by unifying behavior.

### Related ADRs

- ADR-005 (Version + Timestamp Idempotency)
- ADR-007 (Local Disabled Flag Separate from CMS Publication State) — query filtering interacts with unpublished-status entities.
- ADR-008 (Sync Batch Processing) — orphan delete counted as skipped in the batch response (not itemized in `errors[]`); Warning-level log carries full context.
- ADR-014 (Observability) — logged with full context.

---

## ADR-007: Local Disabled Flag Separate from CMS Publication State

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

Per spec item 4: entities have CMS-driven publication state; admins can disable via API as local override that does not affect CMS state; normal users see published-and-not-disabled entities; admins see everything. Spec allows "no separate endpoints".

Two orthogonal state axes exist: CMS publication and local disabled. Design must not conflate — CMS re-publish of a disabled entity must not re-enable it for normal users.

### Decision

Two separate fields on the entity, each governed by a distinct actor:

- **`Status`** — enum `{Published, Unpublished}`, driven by CMS webhook events. Persisted per ADR-005 idempotency. Never touched by API endpoints.
- **`IsDisabled`** — boolean, default `false`. Set exclusively by admin API endpoints. Never touched by CMS handlers.

Normal user visibility requires: `Status = Published AND IsDisabled = false`. Admin sees all.

Endpoints (single endpoint per operation, role-based filtering server-side):

- `GET /entities`, `GET /entities/{id}` — filtered by role. Normal users see `Status = Published AND IsDisabled = false`. Admins see all.
- `POST /entities/{id}/disable` — admin only. Sets `IsDisabled = true`. Idempotent.
- `POST /entities/{id}/enable` — admin only. Sets `IsDisabled = false`. Idempotent.

Authorization enforced via ADR-011 policies. Role identification from Users table (ADR-011); no hardcoded users.

### Alternatives Considered

- **Single `IsVisible` boolean**: rejected — collapses orthogonal concerns; CMS re-publish would incorrectly re-enable.
- **Combined enum** (`Published`, `Unpublished`, `Disabled`): rejected — cannot represent "published AND disabled" simultaneously.
- **Separate endpoints per role**: rejected — spec says "No need to implement separate endpoints".
- **Query parameter `includeDisabled=true`**: rejected — security risk; normal user could bypass.
- **PATCH `/entities/{id}`**: rejected — implies field-level update which spec prohibits.
- **Single endpoint `POST /entities/{id}/visibility`** with action body: rejected — two dedicated endpoints have trivial cost and clearer semantics for logs, audit, and independent evolution.
- **Audit fields** (`DisabledAt`, `DisabledBy`): deferred to future improvements — spec does not require audit trail.

### Consequences

**Positive**:

- Clear separation — CMS handlers touch only `Status`; admin endpoints touch only `IsDisabled`.
- Local disable is sticky: CMS re-publish does not undo it.
- Single endpoint per operation matches spec intent.
- Filter predicate applied uniformly at the repository level.

**Trade-offs**:

- Two fields to persist and index; composite index on `(Status, IsDisabled)` covers common query path.
- Query handlers must remember to apply role-based filter — mitigation: centralize filter in a shared repository method.
- Field names do not encode the actor; documented in architecture.md and covered by tests.

### Testing Considerations

Test coverage for the four combinations of (`Status`, `IsDisabled`) is central to correctness:

- Normal user: only `(Published, IsDisabled=false)` visible; other three combinations hidden.
- Admin: all four combinations visible.
- CMS re-publish of a disabled entity: `Status` updates, `IsDisabled` unchanged, entity remains hidden from normal users.
- Idempotent disable/enable: repeated calls succeed without error.

Detailed test strategy in ADR-016 (Testing Strategy).

### Related ADRs

- ADR-005 (Version + Timestamp Idempotency) — governs `Status` updates.
- ADR-011 (Basic Authentication) — provides role identification via Users table.
- ADR-016 (Testing Strategy) — detailed test approach.

---

## ADR-008: Synchronous Batch Processing with Per-Event Transactions

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

The `/cms/events` endpoint receives event batches per spec item 1. The service processes each event, persists state, and returns a response with the outcome. Two decisions: sync vs. async processing (spec item 5 asks for justification), and transactional scope for bounding partial failures.

### Decision

**Synchronous processing** with **per-event transactions**.

#### Sync vs. async

Sync chosen for the first iteration:

- Spec priority is correctness ("Correct event processing is the most important part"), not throughput.
- Narrative describes a CMS webhook receiver — HTTP POST batches at editor-triggered pace match sync semantics naturally. Sample shows batches of 3 events, not thousands.
- Producer receives immediate feedback per event; no polling infrastructure required.
- No async infrastructure overhead (queue, worker, status endpoint) — simpler operational and testing surface.

Extension path to async preserved by design. Revisit triggers documented in ADR-009 § When to Migrate to Async.

#### Per-event transactional scope

Each event processed inside its own DB transaction. **Events are processed sequentially within a batch, in the order received** — per-event transactions do not imply parallelism. Failure on one event does not roll back others in the batch.

Failure classification:

- **Transient** (DB deadlock, connection reset, network glitch): identified by SQL Server error number via `SqlExceptionClassifier`. Polly retry 3 attempts, exponential backoff (100/200/400ms). If exhausted, marked `processing_timeout` in the response (internal cause logged separately at Error level per ADR-014; not exposed to producer).
- **Permanent DB failure** (PK/FK/CHECK violation, optimistic-concurrency conflict, unclassified SQL error): NOT retried — retrying just delays the same error. Wrapped as `PermanentPersistenceException`, marked `persistence_error` in the response (SQL error number logged at Error level; not exposed).
- **Permanent domain failure** (validation error, unknown type): marked with the specific reason (`validation_error`, `unknown_event_type`); processing continues on the next event.

#### Input validation (per spec item 2)

- `type` — enum `{publish, unPublish, delete}`, case-sensitive per sample.
- `id` — non-null, non-empty string.
- `version` — integer >= 1; required for `publish`/`unPublish`; **must be absent for `delete`** (a stray version field signals a malformed producer contract and is rejected rather than silently ignored).
- `timestamp` — valid ISO 8601 UTC.
- `payload` — non-null JSON object for `publish`/`unPublish`; absent for `delete`. Internal structure opaque per spec.

**Payload sanitization** (per spec item 2 — "validated and sanitized"):

- Structural sanitization is enforced by the JSON deserializer at the transport boundary: malformed JSON never reaches the validator; the payload is exposed as a `JsonElement` and cannot carry non-JSON content.
- **Size cap**: payload UTF-8 byte length must be `<= 64 KiB` (`CmsEventValidator.MaxPayloadBytes`). Chosen an order of magnitude above any realistic CMS entity body while capping DoS-adjacent scenarios where a producer submits multi-MB payloads that would inflate log lines, DB rows, and memory pressure. Configurable in a future revision if the CMS emits larger legitimate bodies.
- Schema-level sanitization (e.g., HTML stripping, allow-listed fields) is deliberately NOT applied — the spec declares the payload opaque and the service does not render it.

Validation failure → permanent failure with `validation_error` reason.

**Validation placement principle**: FluentValidation validators are used in **two different modes** in this codebase, and the distinction is deliberate:

- **Fail-fast validators** (MediatR `ValidationBehavior` pipeline): applied to commands/queries where an invalid input represents a bug or malformed request the caller must fix before retrying. The pipeline throws `ValidationException` and the framework returns 400. Use for command/query envelope validation where the request cannot be partially satisfied.
- **In-handler validators** (called explicitly, do not throw): applied per element inside a batch or collection where an invalid element must be recorded and reported without blocking valid elements. The `CmsEventValidator` used by `ProcessEventBatchHandler` is this kind — a single malformed event produces a permanent-failure outcome for that event, and the rest of the batch continues.

Rule of thumb: **if a failure should halt the whole request, put the validator in the pipeline; if a failure should be reported alongside successful outcomes, call the validator inside the handler**. In this codebase, `CmsEventValidator` is deliberately NOT registered in the MediatR pipeline for this reason.

#### Response

- Status codes: `200` (batch processed), `400` (malformed envelope), `401` (auth per ADR-011), `500` (catastrophic per ADR-009).
- Body includes: `batchId`, `correlationId`, counts (`totalEvents`, `processed`, `skipped`, `failed`) as flat top-level fields, and `errors[]` array with per-event details for **failed events only**.
- **Skipped events are counted but not itemized in the response** — they are outcomes under idempotency (retry-safe behavior) or orphan-delete no-ops (per ADR-006). Skipped events appear in structured logs per ADR-014.
- Failure `reason` enum (present in response `errors[]`): `validation_error`, `processing_timeout`, `persistence_error`, `unknown_event_type`. Producer-facing details use non-technical wording (no internal implementation leaks).
- Skip `reason` enum (logs only, not response): `superseded_by_version`, `duplicate`, `orphan_delete`, `stale_delete`.
- Full schema and per-scenario examples in `responses.md`.

### Alternatives Considered

- **Async 202 + status endpoint**: rejected — infrastructure cost without evidence of need; documented extension in ADR-009.
- **All-or-nothing single transaction**: rejected — one bad event would revert valid ones, undermining partial-failure progress.
- **Fire-and-forget (no response detail)**: rejected — producer cannot detect failures; violates spec item 6.
- **Include skipped events in response body**: rejected — skipped are expected outcomes under idempotency, not actionable for producers. Adding them to the response inflates size without value. Logs preserve traceability.
- **No retry on transient failures**: rejected — SQL Server deadlocks are common and transient; in-process retry cheaper than bouncing batch back to producer.

### Consequences

**Positive**:

- Producer receives compact, actionable response — only failures need attention.
- Partial failures do not block valid events in the same batch.
- Retry semantics safe due to ADR-005 idempotency.
- Response schema stable and machine-parseable; canonical contract in `responses.md`.

**Trade-offs**:

- Large batches increase response time; extremes addressed by ADR-009.
- Per-event transaction overhead exceeds single batch transaction; acceptable trade for partial-failure safety.
- Skipped events not itemized in response — producers needing per-event skip traceability must consult logs.

### Related ADRs

- ADR-005 (Idempotency) — enables safe retries.
- ADR-006 (Handling events for unknown entities) — orphan handling contributes to failure counts.
- ADR-009 (Failure Handling and Extension Paths) — catastrophic failure and async extension.
- ADR-014 (Observability) — skipped events and diagnostics logged.

---

## ADR-009: Failure Handling and Extension Paths for Batch Processing

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

ADR-008 handles happy path per-event processing. Two scenarios need explicit handling that ADR-008 does not cover: batches exceeding reasonable sync processing bounds, and timeouts or catastrophic failures mid-processing. The design must also document extension paths for future migration to async, streaming, or higher-scale infrastructure without rewrite.

### Decision

#### Failure handling

1. **No enforced maximum batch size**. Spec does not specify a limit; imposing one is a decision without evidence.

2. **Processing timeout**: 60 seconds (below Kestrel default and typical load balancer defaults). Enforced via `CancellationToken` linked to request abort.

3. **Timeout mid-processing**: per-event transactions (ADR-008) ensure partial state is durably committed. Client receives no response — connection dropped. Server logs full context: `batchId`, `correlationId`, `totalEvents`, `processedCount`, `remainingCount`, `lastProcessedEventId`, `elapsedMs`, and payload summary (event types + counts, not full bodies). CMS retries entire batch → ADR-005 idempotency skips already-processed events.

4. **Catastrophic failure** (DB down, exception exceeding retries): HTTP 500 with `batchId` + `correlationId` for support workflow. Full exception logged (ADR-014).

#### Extension paths preserved by design

- **Async background processing**: `EventDispatcher` is transport-agnostic (ADR-004). A `BackgroundService` consuming from an internal queue invokes the same dispatcher without handler changes. HTTP layer would return 202 + status endpoint.
- **Stream ingestion (Kafka / Event Hubs / Service Bus)**: same dispatcher invoked from a message consumer; handlers and domain logic unaffected.
- **Horizontal scaling**: stateless service; idempotency (ADR-005) enables safe execution behind load balancer with N instances. See architecture.md § Deployment.

#### When to migrate to async

Concrete triggers that would flip the sync decision (ADR-008) to async with 202 + status endpoint:

- Sustained throughput > 10 batches/second.
- Typical batch size consistently > 1000 events.
- P95 request latency > 30 seconds under normal load.
- HTTP timeout errors in producer telemetry as a meaningful fraction of requests.
- Producer changes to tolerate async delivery (polling, callbacks, or status pushes).
- Multi-tenant deployment where per-tenant processing time isolation becomes a concern.

Absent these triggers, sync + per-event transactions (ADR-008) remain correct.

### Alternatives Considered

- **Enforce max batch size**: rejected — spec silent; artificial constraint without justification. Traceability under stress preferred.
- **Async 202 by default**: rejected — infrastructure cost without evidence of need for first iteration.
- **Streaming ingestion (Kafka)**: rejected — over-engineered for a REST webhook receiver.

### Consequences

**Positive**:

- Bounded resource usage via CancellationToken and predictable timeout.
- Traceable failures — every outlier logged with full context; extension justified only by evidence.
- Design does not lock the service out of async or streaming migration.

**Trade-offs**:

- Very large batches may time out — CMS must retry; ADR-005 idempotency covers safety.
- Extension paths are claims until exercised — must be verified by boundary tests (ADR-002) preventing HTTP or infrastructure concerns from leaking into the dispatcher.
- Revisit triggers are heuristics; iteration required as real usage patterns emerge.

### Related ADRs

- ADR-002 (Boundary Tests) — enforces transport-agnostic property required by extension paths.
- ADR-004 (Strategy Pattern) — dispatcher is transport-agnostic.
- ADR-005 (Idempotency) — safe retry semantics after timeout.
- ADR-008 (Sync Batch Processing) — happy path this ADR extends.
- ADR-014 (Observability) — timeout and catastrophic failure logging.

---

## ADR-010: Reader/Writer DbContext Split with Domain-Facing Repositories

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

Two decisions must be made about persistence:

1. **Reader/Writer split** — spec item 5 requires: *"Use a read-only/writer configuration for your application context and optimize your EF read queries."*
2. **Persistence abstraction level** — should command/query handlers depend on the `DbContext` directly, or on domain-facing repository abstractions?

Two distinct data access patterns exist: write path (event batch processing with per-event transactions per ADR-008) and read path (GET endpoints with role-based filtering per ADR-007). Beyond the reader/writer split, the coupling between handlers and the ORM shapes the maintainability of the Application layer as the project grows.

### Decision

**Two distinct EF Core DbContexts** mapping the same entities with different configurations:

- **`WriterDbContext`** — full change tracking. Primary DB connection string. **Owns all EF Core migrations**; the reader does not modify schema.
- **`ReaderDbContext`** — `NoTrackingWithIdentityResolution` set globally via `UseQueryTrackingBehavior`. Read-only connection string.

**Domain-facing repositories in Application layer** — handlers depend on these interfaces, not on `DbContext`:

- **`IEntityRepository`** — write side. `FindByIdAsync`, `AddAsync`, `Remove`, `SaveChangesAsync`, `ExecuteInTransactionAsync` (per-event transactional scope per ADR-008). Backed by `WriterDbContext` in Infrastructure.
- **`IEntityQueries`** — read side. `FindByIdAsync(includeHidden)`, `ListAsync(includeHidden, limit)`. The `includeHidden` parameter implements the role-based visibility filter per ADR-007. Backed by `ReaderDbContext`.
- **`IUserQueries`** — auth lookups. `FindByUsernameAsync`. Backed by `ReaderDbContext`. Consumed by the Basic Authentication middleware per ADR-011.

**Application layer has no dependency on `Microsoft.EntityFrameworkCore`** — repository interfaces expose only Domain types and primitives. Handlers are ORM-agnostic; the ORM lives entirely in Infrastructure.

**Schema declarations shared**: both `DbContext` types declare the same `DbSet<Entity>` and reuse the same `IEntityTypeConfiguration<T>` files. The design reserves per-query optimization to two techniques applied on the reader side — `AsNoTracking` (or `NoTrackingWithIdentityResolution`, currently set globally on `ReaderDbContext`) and, if needed, LINQ `.Select(...)` projections — rather than to parallel read models that require duplicate schema maintenance.

**Current state of projections**: `EntityQueries.ListAsync` and `EntityQueries.FindByIdAsync` materialize the full `Entity` row rather than projecting to a lean read model. Rationale: the response DTO (`EntityResponse`) consumes 6 of the 8 entity columns; the only columns projection would skip today are `CreatedAt` and `UpdatedAt` (~16 bytes per row), while the largest column (`Payload`, opaque JSON) is required by the response and would remain on the query. The ceremony of maintaining a separate read model would exceed the bandwidth savings at current scale. Projections become worthwhile when the API-visible field count diverges further from the persisted schema (e.g., admin-only audit fields, denormalized aggregates, or a payload column that is often not needed by list endpoints). See `future-improvements.md` § Reader-side projections.

**Connection string strategy**:

- Production: reader points to a read replica; writer points to primary. Different connection strings entirely.
- Local dev: both point to the same DB, but reader appends `ApplicationIntent=ReadOnly` (SQL Server specific) to catch accidental writes.

### Alternatives Considered

- **DbContext direct in handlers** (Jason Taylor's Clean Architecture template style, via `IApplicationDbContext` exposing `DbSet<T>` to Application): rejected — mixes data-layer concerns into the business layer, couples handlers to EF Core, and accumulates coupling smells as the project grows. Templates using this pattern are optimized for small demos; small projects that grow into large ones tend to regret it. The Repository pattern's fundamental value — expressing business intent in the persistence contract — is lost when handlers manipulate `DbSet<T>` directly. This project is designed with the possibility of growth in mind (per ADR-003 phrasing), and reducing coupling early is cheaper than refactoring later.
- **Generic `IRepository<T>`**: rejected — "Repository over Repository" anti-pattern. `DbContext` already provides generic CRUD; a generic wrapper adds ceremony without domain intent. Repositories chosen here are domain-specific (`IEntityRepository`, not `IRepository<Entity>`).
- **Single `IRepository` per aggregate with both reads and writes**: rejected in favor of CQRS-style split (Repository = writes, Queries = reads). The split aligns with the reader/writer `DbContext` decision and MediatR's command/query separation. It also enables the reader-side to skip change tracking without ceremony.
- **Separate `IUnitOfWork` abstraction** (`SaveChangesAsync` and transactions on a dedicated interface): rejected for first iteration — `IEntityRepository` owns `SaveChangesAsync` and `ExecuteInTransactionAsync` for simplicity. Deferred: extract `IUnitOfWork` if cross-aggregate transactions emerge.
- **ReaderDbContext declares only view models, not entities**: rejected — requires parallel class maintenance for compile-time enforcement that projections already handle at query time.
- **Dapper for reads, EF for writes**: rejected — adds dependency for marginal gain at current query complexity. EF with `NoTracking` + projections is fast enough. If reads become bottlenecked, replacing the `IEntityQueries` implementation is a contained change.
- **Repository as valid solution for a SQL → NoSQL migration**: not the reason for this decision. Even with Repository, a fundamental storage swap (SQL → Cosmos or MongoDB) requires rewriting the queries themselves; the Repository preserves the interface but the implementation is essentially new. The real value of Repository here is **decoupling the business layer from EF Core**, not "magical portability" across storage engines.

### Consequences

**Positive**:

- Application layer is truly ORM-agnostic — no `Microsoft.EntityFrameworkCore` reference.
- Handlers express business intent via repository method names (`FindByIdAsync`, `ListAsync(includeHidden)`) instead of raw LINQ.
- Reader path optimized by default — no per-query `AsNoTracking()` discipline required.
- Reads route to a read replica via connection string change alone.
- Boundary between command and query handlers reinforced at the interface level.
- ORM swap (EF → Dapper) requires only replacing repository implementations; handlers are unchanged.

**Trade-offs**:

- More interfaces to maintain (three abstractions + three implementations vs. two DbContext registrations).
- Slight ceremony overhead per feature — each command handler injects `IEntityRepository`, each query handler injects `IEntityQueries`.
- Two `DbContext` types still exist in Infrastructure; convention required to prevent internal Infrastructure adapters from using the wrong one — mitigated by naming and code review.
- `ApplicationIntent=ReadOnly` is SQL Server specific; if we migrate DB engine, this behavior may need adjustment.

### Related ADRs

- ADR-002 (Boundary tests) — the repository abstraction enables strict Application boundary enforcement; NetArchTest verifies Application does not reference EF Core.
- ADR-003 (Vertical Slice + MediatR) — command handlers inject `IEntityRepository`; query handlers inject `IEntityQueries`.
- ADR-007 (Local Disabled Flag) — role-based filter implemented via `IEntityQueries.ListAsync(includeHidden)`.
- ADR-008 (Sync Batch Processing) — per-event transactional scope via `IEntityRepository.ExecuteInTransactionAsync`.
- ADR-011 (Basic Authentication) — middleware depends on `IUserQueries`.

---

## ADR-011: Basic Authentication with Users Table

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

Spec item 1 requires Basic Authentication with 10-20 char username and GUID password. Spec item 4 requires distinct users for CMS webhooks (organization) vs consumer queries, plus admin/user distinction. Three actors need identification: CMS webhook (organization), regular users, admins. Endpoint authorization reflects role.

### Decision

**Basic Authentication** with a **Users table in the database** (no hardcoded credentials).

#### Users table

Fields: `Id`, `Username` (unique, indexed), `PasswordHash` (BCrypt), `Role` (string mapped from `UserRole` enum via `HasConversion`), `CreatedAt`.

#### UserRole enum

```
Organization  → CMS webhook caller
User          → read-only queries
Admin         → read + disable/enable
```

Stored as `string` via `HasConversion<string>()` — readable in DB queries and manual inspections.

#### Seed data

Three users seeded at startup from configuration (BCrypt hashes; from Azure Key Vault in production per ADR-012, `appsettings.Development.json` for local dev):

- `cms-webhook-user` → Organization
- `readonly-user` → User
- `admin-user` → Admin

Usernames 10-20 chars per spec. `UserSeeder.SeedAsync` runs at every startup and is idempotent — it inserts any missing seed user and updates the stored `PasswordHash` when the configured hash has drifted (so rotating a hash in User Secrets or Key Vault propagates on the next restart without a manual DB step).

#### Authentication middleware

Custom `BasicAuthenticationHandler`: decodes `Authorization: Basic` header, looks up user in Users table via `ReaderDbContext` (per ADR-010), verifies password with `BCrypt.Verify` (timing-safe internally), creates `ClaimsIdentity` with `Role` claim from `user.Role`.

#### Authorization policies

- **`OrganizationOnly`** — `Role == "Organization"`. Applied to `POST /cms/events`.
- **`UserOrAdmin`** — `Role in { "User", "Admin" }`. Applied to `GET /entities`, `GET /entities/{id}`.
- **`AdminOnly`** — `Role == "Admin"`. Applied to `POST /entities/{id}/disable`, `POST /entities/{id}/enable`.

Policies use string-based role checks (`user.HasRole("Admin")`), preserving migration path to full RBAC without changing business logic.

#### Failure responses

- `401 Unauthorized` — missing/invalid credentials.
- `403 Forbidden` — authenticated but insufficient role.
- Generic error messages to avoid leaking authentication signal ("Invalid credentials", not "User does not exist").

Scheme name: `"Basic"`. BCrypt work factor: 11 (default of `BCrypt.Net-Next`).

### Alternatives Considered

- **Hardcoded users in code**: rejected — code smell; secrets in source; not extensible.
- **Config-only users (no DB table)**: rejected — DB adds minimal ceremony and enables audit trail, user management endpoints, password rotation.
- **JWT / OAuth2**: rejected — spec explicitly requires Basic Auth.
- **Full RBAC (Users + Roles + UserRoles many-to-many)**: rejected for first iteration — no user has multiple roles; migration path preserved via string-based policies. Deferred to `future-improvements.md`.
- **ASP.NET Core Identity**: rejected — full user management (registration, 2FA, password reset) far exceeds spec; adds complexity and dependencies.
- **Plain-text or SHA-hashed passwords**: rejected — BCrypt is standard, resistant to rainbow tables, tunable work factor.

### Consequences

**Positive**:

- Meets spec requirements exactly.
- Passwords hashed at rest with BCrypt.
- No hardcoded credentials in code.
- Role-based authorization prepared for future RBAC.
- Extensible: adding users is a DB insert (or future admin endpoint).

**Trade-offs**:

- Basic Auth transmits credentials on every request — mitigation: HTTPS required in production (documented in architecture.md § Deployment).
- Password rotation requires config change + restart (or a future admin endpoint).
- Single Role column limits users to one role; RBAC migration required if multi-role emerges.
- BCrypt.Verify per request has non-trivial CPU cost — future mitigation: cache authenticated principals if throughput becomes concern.

### Related ADRs

- ADR-007 (Local Disabled Flag) — consumes role identification for filtering and endpoint authorization.
- ADR-010 (Reader/Writer DbContext) — auth middleware reads Users via ReaderDbContext.
- ADR-012 (Secret Management) — details password/config storage in Key Vault.
- ADR-016 (Testing Strategy) — auth tests with valid/invalid credentials per spec item 7.

---

## ADR-012: Secret Management

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

The service handles several sensitive values:

- BCrypt password hashes for seeded users (ADR-011).
- Database connection strings (with credentials).
- Telemetry endpoints and API keys (ADR-014).

Committing secrets to source, logging them, or storing them in application config files creates a permanent leak in git history. The design must ensure secrets never appear in the repository and are loaded from appropriate stores per environment.

### Decision

Layered configuration per .NET convention with environment-specific secret stores.

**Local development**:

- **User Secrets** (`Microsoft.Extensions.Configuration.UserSecrets`) for sensitive values (BCrypt hashes, connection strings). Per-developer, outside the repo.
- `appsettings.Development.json` for non-sensitive dev config only (logging levels, feature flags).

**Production**:

- **Azure Key Vault** for all secrets, accessed via `Azure.Extensions.AspNetCore.Configuration.Secrets`.
- **Managed Identity** for Key Vault authentication — no service credentials in config.
- Application code reads secrets via standard `IConfiguration` — no direct Key Vault SDK calls in handlers.

**CI/CD (GitHub Actions)**:

- **GitHub Actions Secrets** for build/test values, passed as env vars, masked in logs.

**Configuration precedence** (lowest → highest, later overrides earlier):

1. `appsettings.json` (base, non-secret, committed).
2. `appsettings.{Environment}.json` (env-specific, non-secret, committed).
3. User Secrets (local dev only, per-developer, not committed).
4. Environment variables (CI, container).
5. Azure Key Vault (production, via configuration provider).

**Repository hygiene**:

- `.gitignore` excludes `appsettings.Local.json` and files matching `*.secrets.*`.
- README documents required secrets per environment with setup instructions.
- No secret values in code comments, tests, or commit messages.

**Rotation policy**: 90-day rotation for all secrets (BCrypt hashes, DB connection strings, telemetry keys). Operational procedure documented in `runbook-secret-rotation.md`.

### Alternatives Considered

- **Everything in appsettings.json**: rejected — commits secrets to git; permanent leak.
- **`.env` file**: rejected — same risk if committed; no native .NET tooling for scoping.
- **HashiCorp Vault**: rejected — over-engineered for a small service; adds infrastructure and operational overhead.
- **AWS Secrets Manager / Google Secret Manager**: viable equivalents; Azure Key Vault chosen because .NET tooling and Managed Identity integration is most mature. Migration to another cloud provider is a provider swap.
- **Hardcoded secrets in Dockerfile ENV**: rejected — image layer contains the secret; visible to anyone with image access.
- **Local Key Vault emulator** (e.g., Azurite): rejected for local dev — User Secrets is the standard .NET approach; emulator adds setup cost without behavioral gain.

### Consequences

**Positive**:

- Secrets never appear in the repository or build artifacts.
- Rotation is a Key Vault update in production; no redeploy required if configuration is refreshed on schedule.
- Least privilege via Managed Identity — service can read specific secrets, nothing else.
- Standard `IConfiguration` interface in application code; no Key Vault SDK leaks into business logic.

**Trade-offs**:

- Local dev requires User Secrets initialization step (documented in README).
- Key Vault read latency at startup adds ~100-500ms to cold start; mitigation: refresh interval + cache in `IConfiguration`.
- Managed Identity requires Azure hosting; if we move to non-Azure hosting, need equivalent identity mechanism (service principal + secret as fallback).
- Adding a new secret requires update in three places (appsettings key, User Secrets/local, Key Vault/prod) — documented process in README.

### Related ADRs

- ADR-011 (Basic Authentication) — password hashes stored via this mechanism.
- ADR-014 (Observability) — telemetry endpoints/keys stored via this mechanism.

---

## ADR-013: Rate Limiting

**Status**: Accepted (beyond spec — see below)

**Date**: August 2026

**Proposed by**: Diego Lastorta

**Beyond spec**: The assignment does not require rate limiting. This ADR is included as a defensive-design decision aligned with treating the deliverable as the first sprint of a real product rather than a time-boxed PoC. If the project were rescoped to strict spec compliance, this feature would be removed as a whole (delete `RateLimitingSetup.cs`, `RateLimitingTests.cs`, the `services.AddRateLimitingPolicies(...)` call in `Program.cs`, and mark this ADR as `Superseded`). Kept because the code is self-contained (one file + one config section) and demonstrates production-oriented thinking; explicitly documented so it is not confused with a spec requirement.

### Context

Public HTTP endpoints face potential misuse: accidental floods (misconfigured CMS, retry storms after downtime), and intentional abuse (DoS attempts). Without rate limits, a single misbehaving client can consume DB resources, saturate CPU, or block legitimate traffic.

The service exposes:

- `POST /cms/events` — webhook receiver called by the CMS (Organization role).
- `GET /entities`, `GET /entities/{id}` — queries by User or Admin.
- `POST /entities/{id}/{disable|enable}` — Admin actions.

Different endpoints have different expected traffic profiles and require distinct limits. The CMS is a trusted source but not immune to bugs or retry storms — even trusted producers should be bounded.

### Decision

**Built-in ASP.NET Core rate limiting** (`Microsoft.AspNetCore.RateLimiting`, .NET 7+ native, no third-party dependency).

**Algorithm**: Sliding window per limiter. Sliding window smooths bursts better than fixed window while remaining simple to reason about.

**Partition key**: `user:{username}:{path}` for authenticated requests — each authenticated user has an independent bucket **per request path**, so a burst on `/cms/events` does not exhaust the user's ability to query `/entities`. Falls back to `ip:{remote}` for unauthenticated requests (edge case — auth middleware rejects unauthenticated before rate limit sees them). The literal-path granularity is more permissive than a route-pattern grouping would be (see § Trade-offs); refining is deferred to `future-improvements.md`.

**Per-endpoint limits** (starting values, expected to tune with real usage):

- `POST /cms/events` — 100 req/min per Organization user. Webhook may burst; batches carry multiple events per request.
- `GET /entities`, `GET /entities/{id}` — 60 req/min per user. Consumer read rate.
- `POST /entities/{id}/{disable|enable}` — 30 req/min per Admin. Admin actions are low-frequency.

**Rejection response**: HTTP `429 Too Many Requests` with:

- `Retry-After` header (seconds until next allowed request).
- JSON body: `{ "correlationId": "...", "error": "rate_limit_exceeded", "retryAfterSeconds": N }`.

**Middleware pipeline order**:

1. Auth middleware (identifies user).
2. Rate limiter (partitions per authenticated user).
3. Endpoint routing.

Ordering ensures rate limits apply per-user, not per-IP. Unauthenticated requests are blocked by auth before hitting rate limit.

### Alternatives Considered

- **No rate limiting**: rejected — stability risk under misbehaving producer or attacker.
- **Third-party library** (AspNetCoreRateLimit): rejected — built-in provides sufficient features and is officially maintained; avoids external dependency.
- **Global rate limit only** (single bucket): rejected — a webhook burst would block admin operations. Per-endpoint isolation preferred.
- **Fixed window**: rejected — allows burst up to 2× nominal rate at window boundaries.
- **Token bucket**: viable alternative; sliding window chosen for simpler mental model and adequate behavior for expected traffic.
- **IP-based partition**: rejected — IP can be shared (NAT, corporate proxies); user-based is precise.
- **Infrastructure-only rate limiting** (Azure APIM, load balancer): valid complement; application-level enables per-user precision and integrates with auth.
- **Bypass rate limiting for Organization role**: rejected — even trusted producers can misbehave (bugs, retry storms). Higher limit preferred over no limit.

### Consequences

**Positive**:

- Bounded resource consumption per user; misbehaving client cannot starve others.
- Per-endpoint limits accommodate different traffic profiles.
- Rejection responses include actionable info (`Retry-After`, `correlationId`) for producers.
- No external dependencies; built into .NET framework.

**Trade-offs**:

- Limits are heuristics; production tuning expected as traffic patterns emerge.
- Sliding window state held per instance; behind a load balancer, each instance tracks its own bucket — effective aggregate limit is `perInstance × instanceCount`. Multi-instance precision requires distributed store (Redis) — deferred to `future-improvements.md` if evidence emerges.
- **Partition key includes literal request path** (e.g., `user:admin:/entities/abc/disable`). This means `/entities/abc/disable` and `/entities/xyz/disable` count against different buckets even for the same admin user. Intended reading of the ADR is "per user, per endpoint kind" — the current implementation is "per user, per URL" which is more granular than the ADR text suggests. Refining to route-pattern grouping (`user:admin:disable-endpoint`) is deferred to `future-improvements.md`; not blocking because the current behavior is permissive-only (users can do more), not restrictive.
- Retry-After hint is application-layer; upstream infrastructure may not honor it, requiring producer-side retry logic.

### Related ADRs

- ADR-011 (Basic Authentication) — provides user identity used as partition key.
- ADR-008 (Sync Batch Processing) — batch size (uncapped per ADR-009) is orthogonal to request rate.
- ADR-014 (Observability) — 429 responses logged for anomaly detection.

---

## ADR-014: Observability

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

Spec item 6 requires "Log processed events, including failing ones." The service needs structured logging, distributed tracing, and correlation identifiers that link logs, traces, and response bodies together for support workflows. Per ADR-008, skipped events appear only in logs (not response); per ADR-005, clock-skew warnings log; per ADR-013, rate-limit rejections log.

### Decision

**Serilog** for structured logging + **OpenTelemetry** for distributed tracing.

#### Serilog configuration

- Sinks: Console (all environments), Application Insights (production).
- JSON format; standard fields: `Timestamp`, `Level`, `Message`, `CorrelationId`, `BatchId`, `EventId`, `Exception`.
- Levels: `Info` (event applied, batch completed), `Warning` (skipped events including orphan deletes, clock skew, 4xx, 429), `Error` (permanent failures, 5xx, retry exhaustion with internal cause), `Debug` (idempotency comparisons, disabled in prod).
- Global enrichment: `Environment`, `MachineName`, `AssemblyVersion` via `Enrich.FromLogContext`.

#### OpenTelemetry configuration

- Automatic instrumentation for ASP.NET Core (incoming requests), EF Core (SQL queries), HttpClient (outbound).
- Custom spans per event handler (`ProcessEvent.publish`, `.unPublish`, `.delete`) with `eventId` and `version` as attributes.
- W3C Trace Context propagation via `traceparent` header.
- Exporter: Application Insights (production), Console (local dev).

#### Correlation strategy

- **`CorrelationId`** — from `X-Correlation-ID` header if present, else generated GUID at ingress.
- **`BatchId`** — generated per `POST /cms/events` request.
- **`EventId`** — from event payload if present, else generated per event during processing.
- Both `CorrelationId` and `BatchId` propagated to Serilog `LogContext` via middleware; all subsequent logs in request scope inherit them.
- Present in response headers (`X-Correlation-ID`, `X-Batch-Id`) and response body (per ADR-008).

#### Per-event log entry (spec item 6)

Every event (applied, skipped, or failed) generates a log entry with: `BatchId`, `CorrelationId`, `EventIndex`, `EventId`, `EntityId`, `Type`, `Version`, `Outcome`, `Reason` (skip/fail), `DurationMs`. Full field schema in architecture.md § Logging Schema.

#### Sampling and retention

- Application Insights adaptive sampling in production (default Azure behavior — retains errors, samples warnings/info under high volume).
- Retention: 90 days (Application Insights default; aligned with secret rotation cadence per ADR-012).
- No full payload logging; payload retrieved from DB by `BatchId` if needed (documented in runbook).

### Alternatives Considered

- **`Microsoft.Extensions.Logging` alone**: rejected — less mature structured logging; Serilog is .NET standard.
- **NLog**: viable alternative; Serilog chosen for wider community support and better OpenTelemetry integration.
- **No distributed tracing**: rejected — logs alone lose parent-child relationships across operations.
- **Elasticsearch + Kibana**: viable non-Azure alternative; Application Insights chosen for Managed Identity integration and tighter Azure ecosystem fit.
- **Custom correlation scheme**: rejected — W3C Trace Context is industry standard, ensures interop with any tracing backend.
- **Log full event payloads**: rejected — opaque per spec, may contain sensitive fields, high ingest cost. Log summary; retrieve full payload from DB by batch ID if needed.
- **Fixed sampling rate**: rejected — adaptive is more resilient to traffic patterns.

### Consequences

**Positive**:

- Spec item 6 met — all events logged with structured metadata.
- Correlation IDs link producer support reports to server-side context.
- Distributed tracing exposes DB slow queries and per-event latency without extra code.
- Log level control per environment balances verbosity and ingest cost.

**Trade-offs**:

- Application Insights ingest cost grows with volume — mitigation: sampling in production, log level per env.
- Serilog + OpenTelemetry adds startup config (documented in architecture.md).
- Structured logging discipline required — property templates (`_logger.LogInformation("Event {EventId} processed", eventId)`), not string interpolation.
- Payload not in logs — support workflows requiring payload inspection go through DB lookup (documented in runbook).

### Related ADRs

- ADR-005 (Idempotency) — clock skew warnings logged here.
- ADR-008 (Sync Batch Processing) — event outcomes logged per spec item 6; skipped events in logs only.
- ADR-012 (Secret Management) — Application Insights connection string via Key Vault.
- ADR-013 (Rate Limiting) — 429 responses logged as warnings.

---

## ADR-015: API Versioning

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

The service exposes `POST /cms/events` (webhook) and `GET/POST /entities/*` (REST API). Introducing API versioning up front (e.g., `/v1/*`) adds path segments and producer configuration surface without immediate benefit. The spec does not specify versioning; the CMS integration contract is fixed at project start; REST API consumers are not yet known.

### Decision

**No API versioning in the first iteration.** Endpoints use unversioned paths (`/cms/events`, `/entities`, etc.).

**When versioning is introduced**, strategy is **URI path versioning** with **per-endpoint-group scope** (`/cms/v1/events`, `/entities/v1`), not a single global `/v1/*`. Rationale: `/cms/events` evolves with the CMS contract (external factor), while `/entities/*` evolves with our internal design. Independent lifecycles justify independent versioning.

- Existing endpoints move to `/v1/*` at the moment `/v2/*` is introduced. Both paths route to the same handlers during transition.
- Versions coexist until deprecation.
- Deprecation policy: **6 months** notice via `Deprecation: true` and `Sunset: <date>` response headers (RFC 8594 / RFC 9745 draft), plus README communication.

**Triggers to introduce versioning**:

- Breaking change to CMS event schema (field renamed, type changed, required field added).
- Breaking change to REST endpoint response format.
- Second external consumer appears with contract requirements incompatible with the current one.

Non-breaking changes (adding optional fields, new endpoints, new response fields) do NOT trigger a version bump.

### Alternatives Considered

- **Version from day one** (`/v1/cms/events`): rejected — path segment cost without benefit; no evidence of second consumer or schema evolution planned.
- **Header versioning** (`API-Version: 1`): rejected — less discoverable in logs, harder to test with `curl`, non-idiomatic in .NET tooling.
- **Query parameter versioning** (`?apiVersion=1`): rejected — mixes routing with query concern; poor cacheability.
- **Media type versioning** (`Accept: application/vnd.cms-events.v1+json`): rejected — REST-purist but adds complexity for producers without evidence of need.
- **Global versioning** (`/v1/*` for entire API surface): rejected in favor of per-endpoint-group — global couples independent lifecycles.
- **Custom deprecation headers** (`X-API-Deprecated`): rejected — RFC standards ensure interop with API management tooling.

### Consequences

**Positive**:

- Simpler paths and fewer surprises during initial adoption.
- No premature commitment to a versioning scheme.
- Trigger-based approach adds versioning only when it solves a real problem, not speculatively.
- Per-endpoint-group scope allows independent evolution of `/cms/*` and `/entities/*`.

**Trade-offs**:

- If versioning is added later, existing producers must be notified; mitigated by keeping unversioned paths working during the deprecation period.
- Absence of versioning may lead producers to couple to implicit assumptions about the response format; mitigated by explicit contract documentation in `responses.md`.
- Retrofitting versioning if a second consumer emerges quickly is more work than having it from day one — accepted risk given uncertainty at project start.

### Related ADRs

- ADR-008 (Sync Batch Processing) — response format subject to versioning if it changes.
- ADR-007 (Local Disabled Flag) — REST endpoint shape subject to versioning if it changes.

---

## ADR-016: Testing Strategy

**Status**: Accepted

**Date**: August 2026

**Proposed by**: Diego Lastorta

### Context

Spec item 7 requires testing how events are processed, ingestion constraints are followed, and basic authentication with valid/invalid credentials. Multiple test layers are needed to cover unit-level logic, integration-level behavior (HTTP → DB), and architectural invariants (boundary rules per ADR-002). The strategy must specify tooling, project layout, coverage expectations, and CI integration.

### Decision

**Testing pyramid** with three tiers.

**Unit tests** (xUnit + Moq + FluentAssertions, SQLite in-memory DbContext): handlers (MediatR commands/queries), domain logic (idempotency rule per ADR-005, status transitions), validators (FluentValidation per ADR-008), Strategy handlers per event type (ADR-004).

**Integration tests** (xUnit + `WebApplicationFactory` + TestContainers with SQL Server): full HTTP flow for `/cms/events` and `/entities/*`; Basic Auth end-to-end with valid/invalid credentials (spec item 7); rate limiting behavior (ADR-013); batch outcomes (processed/skipped/failed counts, response schema per ADR-008); admin flag interactions (four `Status × IsDisabled` combinations, CMS re-publish of disabled entity, idempotent disable/enable per ADR-007).

**Architecture tests** (NetArchTest.Rules per ADR-002): layer dependency rules, transport-agnostic handler enforcement, MediatR handler placement.

**Test project layout** mirrors production structure per Vertical Slice (ADR-003):

```
tests/
  CmsEvents.Unit.Tests/
  CmsEvents.Integration.Tests/
  CmsEvents.Architecture.Tests/
```

**Coverage** as heuristic, not gate: domain logic and idempotency ≥ 90%; handlers ≥ 80%; infrastructure adapters ≥ 60%; overall ~ 75%. Tooling: `coverlet` for collection + `ReportGenerator` for HTML.

**CI** via GitHub Actions on every push/PR: restore, build, run all three tiers; publish coverage report as artifact; fail on test failure only (coverage % is soft signal, not gate — % gates encourage gaming rather than testing for behavior).

### Alternatives Considered

- **EF Core InMemory provider instead of SQLite**: rejected — LINQ translation differs from real provider; passes tests that fail against SQL Server (per ADR-010 rationale).
- **Manual QA only**: rejected — spec requires automated tests.
- **End-to-end tests with staged environment**: rejected — infrastructure overhead; TestContainers covers integration adequately.
- **NUnit or MSTest**: viable alternatives; xUnit chosen for modern .NET convention and better parallelism model.
- **Coverage gate at fixed %**: rejected — encourages testing for coverage rather than for behavior; soft signal preferred.
- **Full BDD framework (SpecFlow)**: rejected — over-engineered; spec item 7 does not require BDD format.
- **LocalDB for integration tests**: rejected — Windows-only; violates spec requirement for platform-agnostic build.

### Consequences

**Positive**:

- Spec item 7 requirements explicitly met (event processing tests, auth tests).
- Three-tier pyramid catches bugs at the appropriate layer (fast unit, thorough integration).
- Architecture tests prevent boundary erosion across future PRs.
- CI enforces test pass; coverage report visible without becoming a gaming metric.

**Trade-offs**:

- TestContainers adds ~10s per integration test class startup (Docker container spin-up). Mitigation: xUnit collection fixtures for container reuse.
- SQLite has SQL Server dialect gaps (some LOB types, `MERGE` statement); mitigation: reserve those tests for integration tier where TestContainers uses real SQL Server.
- Coverage as heuristic requires code review discipline to catch under-tested paths.

### Related ADRs

- ADR-002 (Boundary Tests) — architecture test tier.
- ADR-003 (Vertical Slice) — test project layout mirrors production.
- ADR-005, ADR-006, ADR-007, ADR-008 — specific test scenarios per ADR.
- ADR-010 (Reader/Writer DbContext) — SQLite for unit, TestContainers for integration.
- ADR-011 (Basic Authentication) — auth coverage per spec item 7.
