namespace CmsEvents.Integration.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
