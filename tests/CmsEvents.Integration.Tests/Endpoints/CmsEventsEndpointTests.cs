namespace CmsEvents.Integration.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CmsEvents.Contracts.Events;
using CmsEvents.Contracts.Responses;
using CmsEvents.Integration.Tests.Fixtures;
using FluentAssertions;
using Xunit;

/// <summary>
/// End-to-end tests for POST /cms/events. Covers spec item 7:
/// - Event processing paths and ingestion constraints
/// - Basic Authentication with valid/invalid credentials
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class CmsEventsEndpointTests
{
    private const string NowIso = "2026-08-01T10:00:00Z";
    private static readonly string[] ExpectedConcurrentFailureReasons = { "persistence_error" };
    private readonly CmsEventsWebAppFactory _factory;

    public CmsEventsEndpointTests(CmsEventsWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_WithoutAuthentication_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithWrongPassword_ReturnsUnauthorized()
    {
        // Spec item 7: Basic Authentication with valid/invalid credentials. Known-good user,
        // wrong password — must fail closed (BCrypt.Verify branch in BasicAuthenticationHandler).
        using var client = _factory.CreateClientWithBasicAuth(
            CmsEventsWebAppFactory.CmsWebhookUsername,
            password: "definitely-not-the-right-password");

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithUnknownUsername_ReturnsUnauthorized()
    {
        // Spec item 7: unknown user must produce the SAME response as wrong password (no user-
        // existence leak per ADR-011). Covers the "user is null" branch alongside wrong-password.
        using var client = _factory.CreateClientWithBasicAuth(
            username: "user-that-does-not-exist",
            password: CmsEventsWebAppFactory.TestPassword);

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithMalformedAuthorizationHeader_ReturnsUnauthorized()
    {
        // "Basic" scheme but the token is not valid Base64 → Malformed branch of the handler.
        using var client = _factory.CreateClientWithMalformedAuthHeader("!!!not-base64!!!");

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithReadonlyUserRole_ReturnsForbidden()
    {
        using var client = _factory.CreateClientAsReadonlyUser();

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_WithAdminRole_ReturnsForbidden()
    {
        // The CMS ingest endpoint is CMS-webhook-only per ADR-011 — even an admin cannot
        // impersonate the CMS at this boundary. Locks in the ORganizationOnly policy
        // rather than "any authenticated user is fine".
        using var client = _factory.CreateClientAsAdmin();

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_EmptyBatch_ReturnsOkWithAllZeroCounts()
    {
        // Edge case: the spec accepts "an array of events" — an empty array is a valid array
        // and must produce a well-formed response, not 400 or 500. Locks in the empty-batch
        // contract so accidental "if (events.Count == 0) throw" refactors are caught.
        using var client = _factory.CreateClientAsCmsWebhook();

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.TotalEvents.Should().Be(0);
        body.Processed.Should().Be(0);
        body.Skipped.Should().Be(0);
        body.Failed.Should().Be(0);
        body.Errors.Should().BeEmpty();
        body.BatchId.Should().NotBe(Guid.Empty, "every response must carry a batchId for support workflows");
        body.CorrelationId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Post_MalformedRequestBody_Returns400_NotProcessedNotCrashed()
    {
        // AC1: malformed batch body → 400. Verifies the DTO deserializer rejects a non-JSON /
        // non-array body at the transport boundary before it reaches the handler. Producer
        // must see a client error, not a 500.
        using var client = _factory.CreateClientAsCmsWebhook();
        using var content = new StringContent("this-is-not-json-at-all", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/cms/events", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "malformed batch body must surface as 400 at the transport layer, not 200 or 500");
    }

    [Fact]
    public async Task Post_BatchWithOneInvalidEvent_ProcessesValidAndReportsInvalid_BatchContinues()
    {
        // AC2 end-to-end: "failing events are recorded, the rest of the batch continues".
        // Unit tests cover this at the handler level; this test locks the contract at the
        // HTTP boundary against real SQL.
        using var client = _factory.CreateClientAsCmsWebhook();
        var validId = "valid-a-" + Guid.NewGuid().ToString("N")[..8];
        var invalidId = "valid-b-" + Guid.NewGuid().ToString("N")[..8];

        var events = new[]
        {
            NewPublish(validId, version: 1),
            new CmsEventEnvelope
            {
                Type = CmsEventType.Publish,
                Id = invalidId,
                Version = 0, // invalid — validator requires version >= 1
                Timestamp = NowIso,
                Payload = JsonDocument.Parse("{}").RootElement,
            },
        };

        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "one invalid event must not abort the whole batch");

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.TotalEvents.Should().Be(2);
        body.Processed.Should().Be(1, "the valid event must apply despite a peer failing validation");
        body.Failed.Should().Be(1);
        body.Errors.Should().ContainSingle(e =>
            e.Id == invalidId &&
            e.Reason == "validation_error");

        // The valid entity must be queryable — proves the batch actually committed the good work.
        using var adminClient = _factory.CreateClientAsAdmin();
        var getResp = await adminClient.GetAsync($"/entities/{validId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_EchoesCorrelationIdFromRequestHeader_InResponseHeaderAndBody()
    {
        // AC8: X-Correlation-ID from the client must be preserved end-to-end (echoed in both
        // response header and response body). This is the observable surface of the correlation
        // propagation contract that Serilog LogContext uses to tag all logs in the request scope.
        var expectedCorrelationId = Guid.NewGuid();
        using var client = _factory.CreateClientAsCmsWebhook();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", expectedCorrelationId.ToString());

        var response = await client.PostAsJsonAsync("/cms/events", Array.Empty<CmsEventEnvelope>());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response.Headers.Should().ContainKey("X-Correlation-ID");
        response.Headers.GetValues("X-Correlation-ID").Single().Should().Be(expectedCorrelationId.ToString(),
            "the response must echo the client-supplied correlation id, not generate a new one");

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.CorrelationId.Should().Be(expectedCorrelationId,
            "the response body must carry the same correlation id as the header");
    }

    [Fact]
    public async Task Post_ValidBatch_ReturnsProcessedCounts()
    {
        using var client = _factory.CreateClientAsCmsWebhook();

        // Unique IDs per test run — the collection fixture shares one SQL Server container across
        // all tests, so state accumulates. Using Guids ensures repeated runs test brand-new entities
        // rather than hitting the idempotency skip on the second run.
        var run = Guid.NewGuid().ToString("N")[..8];
        var events = new[]
        {
            NewPublish($"integration-a-{run}", version: 1),
            NewPublish($"integration-b-{run}", version: 1),
        };

        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body.Should().NotBeNull();
        body!.TotalEvents.Should().Be(2);
        body.Processed.Should().Be(2);
        body.Failed.Should().Be(0);
        body.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Post_OrphanUnPublish_ForNeverSeenId_UpsertsAsUnpublished_HiddenFromReadonly_VisibleToAdmin()
    {
        // Spec corner case (item 3): an entity can be modified (v X -> X+1) and then unpublished
        // before we ever saw a publish for it. Explicit contract at HTTP + real SQL: the row is
        // created with Status=Unpublished from the event's own version/timestamp/payload, is
        // hidden from a readonly user, and is visible to an admin. Unit-tested at the handler
        // level; this test locks the same invariant end-to-end.
        using var webhookClient = _factory.CreateClientAsCmsWebhook();
        var id = "orphan-unpublish-" + Guid.NewGuid().ToString("N")[..8];

        var events = new[]
        {
            new CmsEventEnvelope
            {
                Type = CmsEventType.UnPublish,
                Id = id,
                Version = 7, // spec's "X+1" — the CMS is already at v7 when we first hear about it
                Timestamp = NowIso,
                Payload = JsonDocument.Parse("{\"title\":\"orphan-unpub-body\"}").RootElement,
            },
        };

        var response = await webhookClient.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.Processed.Should().Be(1, "orphan unpublish upserts — it is not a failure");
        body.Failed.Should().Be(0);

        // Readonly user must NOT see it — Status=Unpublished is filtered by IEntityQueries.
        using var readonlyClient = _factory.CreateClientAsReadonlyUser();
        (await readonlyClient.GetAsync($"/entities/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "readonly view filters out non-published entities");

        // Admin must see it, and the row must carry the event's own version/payload (not defaults).
        using var adminClient = _factory.CreateClientAsAdmin();
        var adminGet = await adminClient.GetAsync($"/entities/{id}");
        adminGet.StatusCode.Should().Be(HttpStatusCode.OK);
        var entity = await adminGet.Content.ReadFromJsonAsync<EntityResponse>();
        entity!.Id.Should().Be(id);
        entity.Version.Should().Be(7, "the CMS-authoritative version is preserved even on first observation");
        entity.Status.Should().Be("Unpublished", "orphan unpublish upserts with Unpublished status");
        entity.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task Post_DeleteForUnknownEntity_IsCountedAsSkipped_NotAsError()
    {
        using var client = _factory.CreateClientAsCmsWebhook();

        var events = new[]
        {
            new CmsEventEnvelope
            {
                Type = CmsEventType.Delete,
                Id = "definitely-does-not-exist-" + Guid.NewGuid().ToString("N"),
                Timestamp = NowIso,
            },
        };

        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.Skipped.Should().Be(1, "orphan_delete is skipped, not failed, per ADR-006 revision");
        body.Failed.Should().Be(0);
        body.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Post_DeleteThenPublish_ForSameId_TreatsSecondPublishAsNewEntity()
    {
        // Spec-adjacent corner case documented in ADR-005: hard-delete semantics forbid retaining
        // a tombstone, so a publish arriving after a completed delete for the same id is
        // indistinguishable from a legitimate new entity. Assert the whole sequence in one batch.
        using var client = _factory.CreateClientAsCmsWebhook();
        var id = "delete-then-publish-" + Guid.NewGuid().ToString("N")[..8];

        var events = new[]
        {
            NewPublish(id, version: 1, timestampIso: "2026-08-01T10:00:00Z"),
            new CmsEventEnvelope
            {
                Type = CmsEventType.Delete,
                Id = id,
                Timestamp = "2026-08-01T10:00:01Z", // strictly newer than the publish, so delete applies
            },
            NewPublish(id, version: 1, timestampIso: "2026-08-01T10:00:02Z"),
        };

        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.TotalEvents.Should().Be(3);
        body.Processed.Should().Be(3, "publish + delete + re-publish are all applied (no tombstone per hard-delete semantics)");
        body.Skipped.Should().Be(0);
        body.Failed.Should().Be(0);

        // Verify final state: the entity exists again with version 1 (the re-publish), not deleted.
        using var adminClient = _factory.CreateClientAsAdmin();
        var getResponse = await adminClient.GetAsync($"/entities/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the second publish creates the entity anew after the delete removed the original");
    }

    [Fact]
    public async Task Post_LateDeleteAfterNewerPublish_IsSkippedAsStaleDelete_EntityRemains()
    {
        // Validates the stale-delete guard (Domain.Entity.EvaluateForDelete) end-to-end against
        // real SQL. Batch 1 publishes at T. Batch 2 tries to delete with an EARLIER timestamp
        // (simulates network reordering / at-least-once replay). Delete must be skipped and the
        // entity must survive.
        using var client = _factory.CreateClientAsCmsWebhook();
        var id = "late-delete-" + Guid.NewGuid().ToString("N")[..8];

        // Batch 1: publish at T = 10:00:05.
        var publishBatch = new[] { NewPublish(id, version: 1, timestampIso: "2026-08-01T10:00:05Z") };
        var publishResp = await client.PostAsJsonAsync("/cms/events", publishBatch);
        publishResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Batch 2: delete with timestamp T = 10:00:00 (earlier than the publish above).
        var lateDeleteBatch = new[]
        {
            new CmsEventEnvelope
            {
                Type = CmsEventType.Delete,
                Id = id,
                Timestamp = "2026-08-01T10:00:00Z",
            },
        };
        var deleteResp = await client.PostAsJsonAsync("/cms/events", lateDeleteBatch);
        deleteResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteBody = await deleteResp.Content.ReadFromJsonAsync<BatchResponse>();
        deleteBody!.Skipped.Should().Be(1, "delete timestamp <= stored timestamp → stale_delete guard skips it");
        deleteBody.Failed.Should().Be(0);
        deleteBody.Errors.Should().BeEmpty();

        // Verify entity survived the stale delete.
        using var adminClient = _factory.CreateClientAsAdmin();
        var getResponse = await adminClient.GetAsync($"/entities/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, "stale delete must not remove — entity has a newer publish");
    }

    [Fact]
    public async Task Post_OutOfOrderVersionsInSameBatch_HigherVersionWins_LowerIsSkippedAsSuperseded()
    {
        // v3 arrives BEFORE v2 in the same batch. Per ADR-005 idempotency, v3 applies and v2 is
        // then skipped as superseded_by_version — final state has version 3 regardless of arrival
        // order.
        using var client = _factory.CreateClientAsCmsWebhook();
        var id = "out-of-order-" + Guid.NewGuid().ToString("N")[..8];

        var events = new[]
        {
            NewPublish(id, version: 3, timestampIso: "2026-08-01T10:00:03Z"),
            NewPublish(id, version: 2, timestampIso: "2026-08-01T10:00:02Z"),
        };

        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.Processed.Should().Be(1, "only the higher-version publish is applied");
        body.Skipped.Should().Be(1, "the older v2 is skipped as superseded_by_version");
        body.Failed.Should().Be(0);

        // Verify final version via admin GET (payload / version exposed).
        using var adminClient = _factory.CreateClientAsAdmin();
        var getResponse = await adminClient.GetAsync($"/entities/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var entity = await getResponse.Content.ReadFromJsonAsync<EntityResponse>();
        entity!.Version.Should().Be(3, "the higher version must win regardless of batch arrival order");
    }

    [Fact]
    public async Task Post_MixedTypesForSameIdInOneBatch_ProcessedInOrder()
    {
        // Cross-scenario: publish → unPublish → delete for the same id inside a single batch.
        // Per-event SaveChangesAsync + strictly-newer timestamps mean each step commits before
        // the next reads. Expected: 3 processed, 0 skipped, 0 failed; final DB state absent.
        using var client = _factory.CreateClientAsCmsWebhook();
        var id = "mixed-types-" + Guid.NewGuid().ToString("N")[..8];

        var events = new[]
        {
            NewPublish(id, version: 1, timestampIso: "2026-08-01T10:00:00Z"),
            new CmsEventEnvelope
            {
                Type = CmsEventType.UnPublish,
                Id = id,
                Version = 2,
                Timestamp = "2026-08-01T10:00:01Z",
                Payload = JsonDocument.Parse("{\"title\":\"unpublished\"}").RootElement,
            },
            new CmsEventEnvelope
            {
                Type = CmsEventType.Delete,
                Id = id,
                Timestamp = "2026-08-01T10:00:02Z", // strictly newer than the unpublish above
            },
        };

        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.Processed.Should().Be(3, "publish, unpublish, and delete all apply in order within one batch");
        body.Skipped.Should().Be(0);
        body.Failed.Should().Be(0);

        // Final state: entity absent (delete was applied last).
        using var adminClient = _factory.CreateClientAsAdmin();
        var getResp = await adminClient.GetAsync($"/entities/{id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "delete is the last applied event — the entity must not be visible even to admin");
    }

    [Fact]
    public async Task Post_UnicodeId_IsAcceptedAndRoundtrips()
    {
        // Id column is nvarchar(256) so Unicode should work end-to-end. Smoke covers real
        // producer patterns (localized ids, non-ASCII characters, emoji). If this breaks in
        // the future — likely at serialization, DB, or URL routing — the failure surfaces
        // here rather than at a customer.
        using var client = _factory.CreateClientAsCmsWebhook();
        var id = "artículo-año-2026-测试-" + Guid.NewGuid().ToString("N")[..8];

        var events = new[] { NewPublish(id, version: 1) };
        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<BatchResponse>())!.Processed.Should().Be(1);

        // Roundtrip through the reader — URL-encoded path segment.
        using var adminClient = _factory.CreateClientAsAdmin();
        var getResp = await adminClient.GetAsync($"/entities/{Uri.EscapeDataString(id)}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK, "unicode id must be queryable end-to-end");

        var entity = await getResp.Content.ReadFromJsonAsync<EntityResponse>();
        entity!.Id.Should().Be(id, "the id echoed back must match the id we sent, byte-for-byte");
    }

    [Fact]
    public async Task Post_DuplicateEventsInSameBatch_ProcessedOnce_SkippedOnce()
    {
        // Same id/version/timestamp appearing twice in one batch. First event applies; second is
        // skipped as duplicate per ADR-005 equal-version-equal-timestamp rule.
        using var client = _factory.CreateClientAsCmsWebhook();
        var id = "duplicate-in-batch-" + Guid.NewGuid().ToString("N")[..8];

        var events = new[]
        {
            NewPublish(id, version: 1, timestampIso: "2026-08-01T10:00:00Z"),
            NewPublish(id, version: 1, timestampIso: "2026-08-01T10:00:00Z"),
        };

        var response = await client.PostAsJsonAsync("/cms/events", events);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BatchResponse>();
        body!.Processed.Should().Be(1);
        body.Skipped.Should().Be(1, "second occurrence hits the duplicate skip rule");
        body.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Post_ConcurrentPublishesForSameNewId_ResolveGracefully_ExactlyOneEntityPersists()
    {
        // Fires N parallel POSTs, each attempting to create the same id for the first time.
        // Race behavior is inherently non-deterministic — depending on how the reads interleave
        // with the writes, the N-1 "losers" can land in EITHER of two correct outcomes:
        //
        //   (a) Serialized shape (typical when the winner commits before the losers read):
        //       processed=1, skipped=N-1 (duplicate — hit ADR-005 idempotency).
        //   (b) Race shape (when multiple reads return null before any write commits):
        //       processed=1, failed=N-1 with reason=persistence_error (PK collision at SaveChanges,
        //       classified as permanent by SqlExceptionClassifier, mapped to persistence_error).
        //
        // Both shapes are valid outcomes of the current design. The contract this test locks in:
        //
        //   * No request returns HTTP 500 — concurrent same-id is handled gracefully at the
        //     application layer.
        //   * Exactly one event applies; the remaining N-1 land in skipped OR failed (never
        //     silently lost).
        //   * If any failures did occur, they carry reason='persistence_error' — no
        //     misclassification as timeout / validation error / unknown type.
        //   * Final DB state contains exactly one entity for the id.
        //
        // Accepted limitation, documented in ADR-005 § Consequences; future-improvements.md #16
        // covers the rowversion + re-read path if concurrency becomes a measured problem.
        using var client = _factory.CreateClientAsCmsWebhook();
        var id = "concurrent-race-" + Guid.NewGuid().ToString("N")[..8];
        const int Parallelism = 5;

        Task<HttpResponseMessage> SendOne() =>
            client.PostAsJsonAsync("/cms/events", new[] { NewPublish(id, version: 1) });

        var responses = await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(_ => SendOne()));

        foreach (var resp in responses)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "concurrent same-id must not surface as HTTP 500");
        }

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<BatchResponse>()));

        var totalProcessed = bodies.Sum(b => b!.Processed);
        var totalSkipped = bodies.Sum(b => b!.Skipped);
        var totalFailed = bodies.Sum(b => b!.Failed);

        (totalProcessed + totalSkipped + totalFailed).Should().Be(Parallelism,
            "every event must land in exactly one outcome bucket");
        totalProcessed.Should().Be(1, "concurrent inserts must resolve to exactly one winner");

        // Failures MAY be zero (serialized shape) or up to N-1 (race shape). If any occurred,
        // they must all carry reason='persistence_error'. BeSubsetOf passes on empty collections,
        // which is exactly what we want here — "if there were failures, they must be this reason".
        var failureReasons = bodies.SelectMany(b => b!.Errors).Select(e => e.Reason).Distinct().ToList();
        failureReasons.Should().BeSubsetOf(ExpectedConcurrentFailureReasons,
            "any failure that occurred must carry the persistence_error reason — no misclassification");

        // Verify final DB state: exactly one entity survives (queryable as admin).
        using var adminClient = _factory.CreateClientAsAdmin();
        var getResp = await adminClient.GetAsync($"/entities/{id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the single winner's entity must be queryable after the race resolves");
    }

    private static CmsEventEnvelope NewPublish(string id, int version) =>
        NewPublish(id, version, timestampIso: NowIso);

    private static CmsEventEnvelope NewPublish(string id, int version, string timestampIso) => new()
    {
        Type = CmsEventType.Publish,
        Id = id,
        Version = version,
        Timestamp = timestampIso,
        Payload = JsonDocument.Parse("{\"title\":\"integration-test\"}").RootElement,
    };
}
