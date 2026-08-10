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

    private static CmsEventEnvelope NewPublish(string id, int version) => new()
    {
        Type = CmsEventType.Publish,
        Id = id,
        Version = version,
        Timestamp = NowIso,
        Payload = JsonDocument.Parse("{\"title\":\"integration-test\"}").RootElement,
    };
}
