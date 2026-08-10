namespace CmsEvents.Integration.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using CmsEvents.Contracts.Responses;
using CmsEvents.Integration.Tests.Fixtures;
using FluentAssertions;
using Xunit;

/// <summary>
/// End-to-end verification of rate limiting per ADR-013 — 429 response body per <c>responses.md</c>,
/// Retry-After header, and per-user-per-endpoint partition bucket.
///
/// The default admin actions limit (30/min) is the cheapest to exhaust for a smoke test —
/// we fire &gt; 30 disable calls in quick succession against the same admin user.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class RateLimitingTests
{
    private const int AdminActionsPerMinute = 30;
    private readonly CmsEventsWebAppFactory _factory;

    public RateLimitingTests(CmsEventsWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_Disable_ExceedingPermitLimit_Returns429_WithJsonBody_AndRetryAfterHeader()
    {
        using var adminClient = _factory.CreateClientAsAdmin();

        // Use ONE URL repeatedly so all requests hit the same rate-limit partition (per current
        // BuildPartitionKey design of user:{path}). Firing burst against different entity IDs would
        // spread across different partitions and never hit the limit. See ADR-013 § Trade-offs.
        const string SameUrl = "/entities/rate-limit-target/disable";

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < AdminActionsPerMinute + 5; i++)
        {
            var response = await adminClient.PostAsync(SameUrl, content: null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
                break;
            }
        }

        rejected.Should().NotBeNull("the burst against a single URL must exceed the per-minute limit of " + AdminActionsPerMinute);
        rejected!.Headers.Should().ContainKey("Retry-After");

        var body = await rejected.Content.ReadFromJsonAsync<ErrorEnvelope>();
        body.Should().NotBeNull();
        body!.Error.Should().Be("rate_limit_exceeded");
        body.CorrelationId.Should().NotBe(Guid.Empty);
        body.RetryAfterSeconds.Should().BeGreaterThan(0);
    }
}
