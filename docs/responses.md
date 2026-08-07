# API Response Schemas — CmsEvents Service

**Project**: CmsEvents Service
**Last updated**: August 2026

This document is the canonical contract for HTTP responses. All response shapes, status codes, and error semantics are defined here. Producers and consumers should treat this as the source of truth. Cross-referenced from ADR-008 (batch processing) and ADR-013 (rate limiting).

## Conventions

### Common fields

- **`correlationId`** — GUID present in every response body and in the `X-Correlation-ID` response header. Sourced from the `X-Correlation-ID` request header if provided; otherwise generated server-side at ingress. Used for distributed tracing (per ADR-014).
- **`batchId`** — GUID generated per `POST /cms/events` request. Present in the request response body and in the `X-Batch-Id` response header. Not present on other endpoints.

### Error envelope

Non-2xx responses share a common shape:

```json
{
  "correlationId": "<guid>",
  "error": "<machine_readable_code>",
  "detail": "<optional_human_readable_description>"
}
```

Some 5xx and 500 responses additionally include `batchId` when the failure occurred inside batch processing.

### Content type

All request and response bodies use `application/json; charset=utf-8`.

### Headers

| Header | Direction | Notes |
|--------|-----------|-------|
| `Authorization: Basic <base64>` | Request | Required on all endpoints per ADR-011 |
| `X-Correlation-ID: <guid>` | Request (optional) | Reused in response if provided; else generated |
| `X-Correlation-ID: <guid>` | Response | Always present |
| `X-Batch-Id: <guid>` | Response | Present on `POST /cms/events` responses only |
| `Retry-After: <seconds>` | Response | Present on `429` responses per ADR-013 |
| `traceparent: <w3c-value>` | Request / Response | W3C Trace Context propagation per ADR-014 |
| `Content-Type: application/json; charset=utf-8` | Both | Required |

---

## POST /cms/events

Webhook endpoint consumed by the CMS producer. Accepts a JSON array of events per spec item 1.

### Request

Authorization: Basic Auth as `Organization` role user (per ADR-011).

Body:

```json
[
  { "type": "publish", "id": "X", "payload": { }, "version": 2, "timestamp": "2026-08-01T10:00:00Z" },
  { "type": "delete", "id": "Y", "timestamp": "2026-08-01T10:00:00Z" },
  { "type": "unPublish", "id": "Z", "payload": { }, "version": 4, "timestamp": "2026-08-01T10:00:00Z" }
]
```

Field constraints per ADR-008:

- `type` — enum `{publish, unPublish, delete}`, case-sensitive.
- `id` — non-null, non-empty string.
- `version` — integer >= 1; required for `publish`/`unPublish`, absent for `delete`.
- `timestamp` — valid ISO 8601 UTC.
- `payload` — non-null JSON object for `publish`/`unPublish`; absent for `delete`. Opaque per spec.

### 200 OK — batch processed

Returned regardless of individual event outcomes. The batch was received and every event was evaluated. Producer must inspect the counts and `errors` array to determine per-event outcome.

**Response body**:

```json
{
  "batchId": "550e8400-e29b-41d4-a716-446655440000",
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "totalEvents": 100,
  "processed": 87,
  "skipped": 11,
  "failed": 2,
  "errors": [
    {
      "eventIndex": 42,
      "id": "entity-X",
      "type": "publish",
      "reason": "validation_error",
      "detail": "Field 'version' is required for type 'publish'"
    },
    {
      "eventIndex": 88,
      "id": "entity-Z",
      "type": "unPublish",
      "reason": "processing_timeout",
      "detail": "Processing failed, please retry this event"
    }
  ]
}
```

**Field reference**:

| Field | Type | Description |
|-------|------|-------------|
| `batchId` | GUID string | Server-generated per request |
| `correlationId` | GUID string | See conventions |
| `totalEvents` | integer | Count of events in the request; equals `processed + skipped + failed` |
| `processed` | integer | Events applied to persisted state |
| `skipped` | integer | Events not applied due to idempotency (superseded, duplicate) or orphan-delete no-ops (per ADR-006). Not itemized in `errors` — see ADR-008. Skip details are in structured logs |
| `failed` | integer | Events not applied due to failure. Every failed event appears in `errors` |
| `errors` | array | Per-event details for failed events only. Empty array when `failed == 0` |
| `errors[].eventIndex` | integer | 0-based index of the event in the request array |
| `errors[].id` | string | Event `id` from request |
| `errors[].type` | string | Event `type` from request |
| `errors[].reason` | string | Machine-readable enum, see below |
| `errors[].detail` | string | Human-readable context (optional) |

**Failure `reason` enum** (present in response `errors[]`):

| Reason | Meaning |
|--------|---------|
| `validation_error` | Event failed input validation (missing/invalid field per ADR-008) |
| `processing_timeout` | Event could not be processed after retries. Producer should retry the event. Internal cause (e.g., DB deadlock) is logged at Error level per ADR-014 but never exposed in the response |
| `unknown_event_type` | `type` value not in the enum (validation should catch, but defensive) |

Producer-facing `detail` messages are non-technical and actionable. Internal implementation details (DB errors, stack traces, etc.) never appear in responses — they are logged for the dev team.

**Skip `reason` enum** (present in logs only, not in response — per ADR-008 and ADR-014):

- `superseded_by_version` — `incoming.version < stored.LastProcessedVersion` (per ADR-005)
- `duplicate` — `incoming.version == stored.LastProcessedVersion` and `incoming.timestamp <= stored.LastProcessedTimestamp` (per ADR-005)
- `orphan_delete` — `delete` event received for an `id` that does not exist locally (per ADR-006). Logged at Warning level for anomaly detection

### 200 OK — all events processed

```json
{
  "batchId": "550e8400-e29b-41d4-a716-446655440000",
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "totalEvents": 3,
  "processed": 3,
  "skipped": 0,
  "failed": 0,
  "errors": []
}
```

### 200 OK — all events failed validation

```json
{
  "batchId": "550e8400-e29b-41d4-a716-446655440000",
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "totalEvents": 3,
  "processed": 0,
  "skipped": 0,
  "failed": 3,
  "errors": [
    { "eventIndex": 0, "id": "A", "type": "publish", "reason": "validation_error", "detail": "Field 'version' must be >= 1" },
    { "eventIndex": 1, "id": "B", "type": "unPublish", "reason": "validation_error", "detail": "Field 'timestamp' is not a valid ISO 8601 UTC" },
    { "eventIndex": 2, "id": "", "type": "delete", "reason": "validation_error", "detail": "Field 'id' must be non-empty" }
  ]
}
```

### 200 OK — batch with an orphan delete

Illustrates that orphan deletes are counted as `skipped` (per ADR-006) and do NOT appear in `errors[]`. The desired end state (entity absent) is achieved; the anomaly is logged at Warning level for the dev team.

Request:

```json
[
  { "type": "publish", "id": "X", "payload": {}, "version": 1, "timestamp": "2026-08-01T10:00:00Z" },
  { "type": "delete", "id": "does-not-exist", "timestamp": "2026-08-01T10:00:00Z" }
]
```

Response:

```json
{
  "batchId": "550e8400-e29b-41d4-a716-446655440000",
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "totalEvents": 2,
  "processed": 1,
  "skipped": 1,
  "failed": 0,
  "errors": []
}
```

The orphan delete for `does-not-exist` is counted in `skipped` and appears in structured logs with `reason: "orphan_delete"` at Warning level.

### 400 Bad Request — malformed batch envelope

Returned when the request body cannot be parsed as a JSON array. Nothing was processed; no `batchId` generated.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "malformed_batch",
  "detail": "Request body is not a valid JSON array"
}
```

### 401 Unauthorized

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "unauthorized",
  "detail": "Invalid credentials"
}
```

Generic message per ADR-011 — does not distinguish missing header from wrong credentials.

### 403 Forbidden

Authenticated but not `Organization` role.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "forbidden",
  "detail": "Insufficient role"
}
```

### 429 Too Many Requests

Per ADR-013. Includes `Retry-After` header.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "rate_limit_exceeded",
  "retryAfterSeconds": 30
}
```

### 500 Internal Server Error — catastrophic failure

Per ADR-009. State may be partially committed (per-event transactions per ADR-008).

```json
{
  "batchId": "550e8400-e29b-41d4-a716-446655440000",
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "internal_error",
  "detail": "Contact support with batchId and correlationId"
}
```

Note: timeouts mid-processing return no response (client sees connection drop). See ADR-009 for the timeout log entry produced server-side.

---

## GET /entities

List entities. Role-based filtering per ADR-007.

### Request

Authorization: Basic Auth as `User` or `Admin` role.

Query parameters (optional):

- `limit` — max entities to return in a single response. Default 100; max 500. Beyond `limit`, clients should paginate (pagination is deferred to future improvements — see `future-improvements.md`).

### 200 OK — normal user response

Only entities with `Status = Published AND IsDisabled = false` are returned. `Status` and `IsDisabled` fields are omitted from the response (always `Published` and `false` for a normal user's view).

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "count": 2,
  "entities": [
    {
      "id": "entity-X",
      "version": 5,
      "timestamp": "2026-08-01T10:00:00Z",
      "payload": { }
    },
    {
      "id": "entity-Y",
      "version": 2,
      "timestamp": "2026-08-02T15:30:00Z",
      "payload": { }
    }
  ]
}
```

### 200 OK — admin response

All entities regardless of `Status` or `IsDisabled`. Admin response includes `status` and `isDisabled` fields.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "count": 4,
  "entities": [
    {
      "id": "entity-X",
      "version": 5,
      "status": "Published",
      "isDisabled": false,
      "timestamp": "2026-08-01T10:00:00Z",
      "payload": { }
    },
    {
      "id": "entity-Y",
      "version": 2,
      "status": "Published",
      "isDisabled": true,
      "timestamp": "2026-08-02T15:30:00Z",
      "payload": { }
    },
    {
      "id": "entity-Z",
      "version": 3,
      "status": "Unpublished",
      "isDisabled": false,
      "timestamp": "2026-08-03T09:15:00Z",
      "payload": { }
    },
    {
      "id": "entity-W",
      "version": 1,
      "status": "Unpublished",
      "isDisabled": true,
      "timestamp": "2026-08-04T12:00:00Z",
      "payload": { }
    }
  ]
}
```

### 401 / 403 / 429

Same envelopes as `POST /cms/events`.

---

## GET /entities/{id}

Fetch a single entity by id. Role-based filtering per ADR-007.

### Request

Authorization: Basic Auth as `User` or `Admin` role.

Path parameter: `id` — entity identifier as provided in CMS events.

### 200 OK — normal user response

Returned only if the entity is `Status = Published AND IsDisabled = false`.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "id": "entity-X",
  "version": 5,
  "timestamp": "2026-08-01T10:00:00Z",
  "payload": { }
}
```

### 200 OK — admin response

Returned for any existing entity regardless of `Status` or `IsDisabled`.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "id": "entity-X",
  "version": 5,
  "status": "Published",
  "isDisabled": false,
  "timestamp": "2026-08-01T10:00:00Z",
  "payload": { }
}
```

### 404 Not Found

Returned when:
- The entity does not exist.
- The entity exists but is filtered out by role (e.g., normal user requesting an `Unpublished` or `Disabled` entity).

The response does not distinguish these cases — see ADR-007 rationale (prevents enumeration attacks).

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "not_found",
  "detail": "Entity not found"
}
```

### 401 / 403 / 429

Same envelopes as `POST /cms/events`.

---

## POST /entities/{id}/disable

Set `IsDisabled = true` on the entity. Admin only. Idempotent — repeated calls with the same state succeed.

### Request

Authorization: Basic Auth as `Admin` role.

Path parameter: `id` — entity identifier.

Body: none (empty).

### 200 OK

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "id": "entity-X",
  "isDisabled": true
}
```

Same response whether the entity was already disabled or newly disabled (idempotent).

### 404 Not Found

Entity does not exist.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "not_found",
  "detail": "Entity not found"
}
```

### 401 / 403 / 429

Same envelopes as `POST /cms/events`.

---

## POST /entities/{id}/enable

Set `IsDisabled = false` on the entity. Admin only. Idempotent.

### Request

Authorization: Basic Auth as `Admin` role.

Path parameter: `id` — entity identifier.

Body: none (empty).

### 200 OK

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "id": "entity-X",
  "isDisabled": false
}
```

### 404 Not Found

Entity does not exist.

```json
{
  "correlationId": "3f1a9b7c-2d4e-4f8a-b6c1-1e2f3d4a5b6c",
  "error": "not_found",
  "detail": "Entity not found"
}
```

### 401 / 403 / 429

Same envelopes as `POST /cms/events`.

---

## Error code reference

All values of the `error` field in non-2xx responses, and their contexts.

| Code | HTTP status | Endpoint(s) | Meaning |
|------|-------------|-------------|---------|
| `malformed_batch` | 400 | `POST /cms/events` | Request body is not a valid JSON array |
| `unauthorized` | 401 | All | Missing/invalid credentials |
| `forbidden` | 403 | All | Authenticated but insufficient role |
| `not_found` | 404 | `GET /entities/{id}`, `POST /entities/{id}/{disable\|enable}` | Entity not found (or filtered out by role, for GET) |
| `rate_limit_exceeded` | 429 | All | Rate limit exceeded per ADR-013 |
| `internal_error` | 500 | All | Server-side failure; contact support with `batchId` (if applicable) and `correlationId` |

## HTTP status code summary

| Status | Meaning in this API |
|--------|---------------------|
| 200 | Success. For `POST /cms/events`, this includes batches with per-event failures — inspect the body |
| 400 | Client request is malformed at the envelope level (bad JSON, missing required top-level fields) |
| 401 | Authentication required or credentials invalid |
| 403 | Authenticated but role does not permit the operation |
| 404 | Entity not found (or filtered out by role) |
| 429 | Rate limit exceeded; retry after `Retry-After` seconds |
| 500 | Server-side failure; support may require `batchId` and `correlationId` from the response |

Note: `POST /cms/events` returns `200` even when every event in the batch fails. This is intentional — per-event outcomes belong in the response body, not the HTTP status. The status conveys "batch received and processed", not "all events successful".
