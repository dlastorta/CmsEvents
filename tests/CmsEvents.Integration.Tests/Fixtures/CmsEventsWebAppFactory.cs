namespace CmsEvents.Integration.Tests.Fixtures;

using System.Net.Http.Headers;
using System.Text;
using CmsEvents.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;
using Xunit;

/// <summary>
/// Boots the API in-memory with a real SQL Server 2022 backing store via Testcontainers.
/// Seeds three test users with known passwords so tests can exercise the Basic Auth flow
/// end-to-end per spec item 7.
///
/// Fixture lifecycle: container starts once per collection (see <c>IntegrationTestCollection</c>),
/// keeping suite runtime bounded despite the ~10s container startup cost.
/// </summary>
public sealed class CmsEventsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Low cost factor for test speed. BCrypt.Verify auto-detects the cost embedded in the hash,
    // so this does not affect the production configuration.
    private const int TestBcryptWorkFactor = 4;

    public const string CmsWebhookUsername = "cms-webhook-user";
    public const string ReadonlyUsername = "readonly-user";
    public const string AdminUsername = "admin-user";
    public const string TestPassword = "TestPasswordForIntegrationOnly-1!";

    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public async Task InitializeAsync()
    {
        await _sqlContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _sqlContainer.DisposeAsync();
        await base.DisposeAsync();
    }

    public HttpClient CreateClientAs(string username) => AddBasicAuth(CreateClient(), username, TestPassword);

    public HttpClient CreateClientAsCmsWebhook() => CreateClientAs(CmsWebhookUsername);

    public HttpClient CreateClientAsReadonlyUser() => CreateClientAs(ReadonlyUsername);

    public HttpClient CreateClientAsAdmin() => CreateClientAs(AdminUsername);

    /// <summary>
    /// Creates a client whose <c>Authorization</c> header carries arbitrary credentials — used by
    /// negative-auth tests (wrong password, unknown user) per spec item 7.
    /// </summary>
    public HttpClient CreateClientWithBasicAuth(string username, string password) =>
        AddBasicAuth(CreateClient(), username, password);

    /// <summary>
    /// Creates a client whose <c>Authorization</c> header is present but malformed (not valid
    /// Base64 or missing the <c>username:password</c> separator). Exercises the "Malformed" branch
    /// of <c>BasicAuthenticationHandler</c>.
    /// </summary>
    public HttpClient CreateClientWithMalformedAuthHeader(string rawHeaderValue)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", rawHeaderValue);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(TestPassword, TestBcryptWorkFactor);
        var writerConnection = _sqlContainer.GetConnectionString();
        var readerConnection = writerConnection; // Local dev-style — same DB (production points to replica).

        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Writer"] = writerConnection,
                ["ConnectionStrings:Reader"] = readerConnection,
                ["Users:CmsWebhookUser:Username"] = CmsWebhookUsername,
                ["Users:CmsWebhookUser:PasswordHash"] = hash,
                ["Users:ReadonlyUser:Username"] = ReadonlyUsername,
                ["Users:ReadonlyUser:PasswordHash"] = hash,
                ["Users:AdminUser:Username"] = AdminUsername,
                ["Users:AdminUser:PasswordHash"] = hash,
            });
        });
    }

    private static HttpClient AddBasicAuth(HttpClient client, string username, string password)
    {
        var credentials = Encoding.UTF8.GetBytes($"{username}:{password}");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));
        return client;
    }
}

/// <summary>
/// xUnit collection that shares one <see cref="CmsEventsWebAppFactory"/> across all
/// integration tests, amortizing the ~10s container startup.
/// </summary>
[CollectionDefinition(nameof(IntegrationTestCollection))]
public sealed class IntegrationTestCollection : ICollectionFixture<CmsEventsWebAppFactory>;
