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
/// End-to-end tests for the /entities endpoints per ADR-007 (role-based visibility) and ADR-011
/// (Basic Auth with policies). Every test uses per-run unique IDs to avoid state collision across
/// runs of the shared TestContainers SQL Server (see <see cref="CmsEventsWebAppFactory"/>).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class EntitiesEndpointsTests
{
    private const string NowIso = "2026-08-01T10:00:00Z";
    private readonly CmsEventsWebAppFactory _factory;

    public EntitiesEndpointsTests(CmsEventsWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Entities_WithoutAuth_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/entities");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Entities_AsCmsWebhookRole_ReturnsForbidden()
    {
        using var client = _factory.CreateClientAsCmsWebhook();

        var response = await client.GetAsync("/entities");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_Entities_AsReadonlyUser_ReturnsOnlyPublishedNonDisabled()
    {
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);

        using var readonlyClient = _factory.CreateClientAsReadonlyUser();
        var response = await readonlyClient.GetAsync("/entities?limit=500");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ListEntitiesResponse>();
        body.Should().NotBeNull();
        body!.Entities.Should().Contain(e => e.Id == $"published-{run}");
        body.Entities.Should().NotContain(e => e.Id == $"unpublished-{run}",
            "normal users must not see Unpublished entities per ADR-007");
        body.Entities.Should().AllSatisfy(e =>
        {
            e.Status.Should().BeNull("Status field is omitted for normal users");
            e.IsDisabled.Should().BeNull("IsDisabled field is omitted for normal users");
        });
    }

    [Fact]
    public async Task Get_Entities_AsAdmin_ReturnsAllEntitiesWithAdminFields()
    {
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);

        using var adminClient = _factory.CreateClientAsAdmin();
        var response = await adminClient.GetAsync("/entities?limit=500");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ListEntitiesResponse>();
        body.Should().NotBeNull();
        body!.Entities.Should().Contain(e => e.Id == $"published-{run}");
        body.Entities.Should().Contain(e => e.Id == $"unpublished-{run}");
        body.Entities
            .Where(e => e.Id.EndsWith(run, StringComparison.Ordinal))
            .Should().AllSatisfy(e =>
            {
                e.Status.Should().NotBeNullOrEmpty("Admin response includes Status");
                e.IsDisabled.Should().NotBeNull("Admin response includes IsDisabled");
            });
    }

    [Fact]
    public async Task Get_EntityById_Existing_ReturnsEntity()
    {
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);

        using var adminClient = _factory.CreateClientAsAdmin();
        var response = await adminClient.GetAsync($"/entities/published-{run}");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EntityResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().Be($"published-{run}");
        body.Status.Should().Be("Published");
    }

    [Fact]
    public async Task Get_EntityById_NonExistent_Returns404()
    {
        using var adminClient = _factory.CreateClientAsAdmin();

        var response = await adminClient.GetAsync($"/entities/never-was-{UniqueRunId()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_EntityById_AsReadonlyUser_FilteredOut_Returns404_AntiEnumeration()
    {
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);

        using var readonlyClient = _factory.CreateClientAsReadonlyUser();
        var response = await readonlyClient.GetAsync($"/entities/unpublished-{run}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "Unpublished entity exists but is filtered out for normal user — API returns 404 to avoid enumeration");
    }

    [Fact]
    public async Task Disable_Enable_RoundTrip_AffectsVisibilityForReadonlyUser()
    {
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);

        using var adminClient = _factory.CreateClientAsAdmin();
        using var readonlyClient = _factory.CreateClientAsReadonlyUser();

        // Baseline: readonly user can see the Published entity
        (await readonlyClient.GetAsync($"/entities/published-{run}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Admin disables it
        var disableResponse = await adminClient.PostAsync($"/entities/published-{run}/disable", content: null);
        disableResponse.EnsureSuccessStatusCode();
        var disableBody = await disableResponse.Content.ReadFromJsonAsync<DisableEnableResponse>();
        disableBody!.IsDisabled.Should().BeTrue();

        // Now readonly user can no longer see it
        (await readonlyClient.GetAsync($"/entities/published-{run}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "Disabled entity is filtered out for normal users");

        // Admin re-enables it
        var enableResponse = await adminClient.PostAsync($"/entities/published-{run}/enable", content: null);
        enableResponse.EnsureSuccessStatusCode();
        var enableBody = await enableResponse.Content.ReadFromJsonAsync<DisableEnableResponse>();
        enableBody!.IsDisabled.Should().BeFalse();

        // Readonly user sees it again
        (await readonlyClient.GetAsync($"/entities/published-{run}")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disable_AsCmsWebhookRole_ReturnsForbidden()
    {
        // Admin endpoints are AdminOnly per ADR-011 — even the CMS-webhook role cannot
        // toggle IsDisabled. Locks in the AdminOnly policy on admin actions.
        using var webhookClient = _factory.CreateClientAsCmsWebhook();
        var response = await webhookClient.PostAsync($"/entities/any-id/disable", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enable_AsReadonlyUser_ReturnsForbidden()
    {
        using var readonlyClient = _factory.CreateClientAsReadonlyUser();
        var response = await readonlyClient.PostAsync($"/entities/any-id/enable", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enable_AsCmsWebhookRole_ReturnsForbidden()
    {
        using var webhookClient = _factory.CreateClientAsCmsWebhook();
        var response = await webhookClient.PostAsync($"/entities/any-id/enable", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminDisable_ThenCmsPublish_ForSameId_PreservesIsDisabled_EndToEnd()
    {
        // AC6 sticky end-to-end: admin flips IsDisabled=true, then the CMS republishes the entity
        // via the real event pipeline. IsDisabled must survive — the CMS handler must not touch it.
        // Unit test EntityIdempotencyTests.ApplyPublish_DoesNotTouch_IsDisabled locks this at the
        // Domain level; this test proves the same invariant through HTTP + real SQL.
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);
        var id = $"published-{run}";

        using var adminClient = _factory.CreateClientAsAdmin();
        using var webhookClient = _factory.CreateClientAsCmsWebhook();
        using var readonlyClient = _factory.CreateClientAsReadonlyUser();

        // Admin disables.
        (await adminClient.PostAsync($"/entities/{id}/disable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Readonly user cannot see it anymore.
        (await readonlyClient.GetAsync($"/entities/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "readonly view honors IsDisabled");

        // CMS republishes (higher version to force the apply branch).
        var republish = new[]
        {
            new CmsEventEnvelope
            {
                Type = CmsEventType.Publish,
                Id = id,
                Version = 2,
                Timestamp = "2026-08-01T11:00:00Z",
                Payload = JsonDocument.Parse("{\"title\":\"republished\"}").RootElement,
            },
        };
        var republishResp = await webhookClient.PostAsJsonAsync("/cms/events", republish);
        republishResp.EnsureSuccessStatusCode();
        var republishBody = await republishResp.Content.ReadFromJsonAsync<BatchResponse>();
        republishBody!.Processed.Should().Be(1);

        // IsDisabled must STILL be true — the CMS publish did not override the admin decision.
        var adminGet = await adminClient.GetAsync($"/entities/{id}");
        adminGet.StatusCode.Should().Be(HttpStatusCode.OK);
        var entity = await adminGet.Content.ReadFromJsonAsync<EntityResponse>();
        entity!.IsDisabled.Should().BeTrue("admin disable must survive a subsequent CMS publish (AC6)");
        entity.Version.Should().Be(2, "the publish did apply — version advanced — but IsDisabled stayed");

        // Readonly still cannot see it — the disable overrides published status per ADR-007.
        (await readonlyClient.GetAsync($"/entities/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "post-publish, IsDisabled is still true, so readonly view still filters it out");
    }

    [Fact]
    public async Task Disable_AsReadonlyUser_ReturnsForbidden()
    {
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);

        using var readonlyClient = _factory.CreateClientAsReadonlyUser();

        var response = await readonlyClient.PostAsync($"/entities/published-{run}/disable", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Disable_NonExistent_Returns404()
    {
        using var adminClient = _factory.CreateClientAsAdmin();

        var response = await adminClient.PostAsync($"/entities/never-was-{UniqueRunId()}/disable", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disable_IsIdempotent_SecondCallStillReturns200()
    {
        var run = UniqueRunId();
        await SeedEntitiesAsync(run);

        using var adminClient = _factory.CreateClientAsAdmin();

        (await adminClient.PostAsync($"/entities/published-{run}/disable", content: null)).EnsureSuccessStatusCode();
        var second = await adminClient.PostAsync($"/entities/published-{run}/disable", content: null);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await second.Content.ReadFromJsonAsync<DisableEnableResponse>();
        body!.IsDisabled.Should().BeTrue();
    }

    /// <summary>
    /// Seeds two entities: a Published one (visible to all) and an Unpublished one
    /// (visible only to admin). Uses the CMS webhook to insert them via the real event pipeline.
    /// </summary>
    private async Task SeedEntitiesAsync(string run)
    {
        using var webhookClient = _factory.CreateClientAsCmsWebhook();
        var events = new[]
        {
            new CmsEventEnvelope
            {
                Type = CmsEventType.Publish,
                Id = $"published-{run}",
                Version = 1,
                Timestamp = NowIso,
                Payload = JsonDocument.Parse("{\"title\":\"published\"}").RootElement,
            },
            new CmsEventEnvelope
            {
                Type = CmsEventType.UnPublish,
                Id = $"unpublished-{run}",
                Version = 1,
                Timestamp = NowIso,
                Payload = JsonDocument.Parse("{\"title\":\"orphan-unpub\"}").RootElement,
            },
        };

        var response = await webhookClient.PostAsJsonAsync("/cms/events", events);
        response.EnsureSuccessStatusCode();
    }

    private static string UniqueRunId() => Guid.NewGuid().ToString("N")[..8];
}
