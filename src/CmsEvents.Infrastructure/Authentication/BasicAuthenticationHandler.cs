namespace CmsEvents.Infrastructure.Authentication;

using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CmsEvents.Application.Common.Repositories;
using CmsEvents.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Basic Authentication handler per ADR-011. Looks up the user via <see cref="IUserQueries"/>,
/// verifies the password with BCrypt (timing-safe), and emits a <see cref="ClaimsIdentity"/> with
/// a Role claim on success.
/// </summary>
public sealed class BasicAuthenticationHandler : AuthenticationHandler<BasicAuthenticationSchemeOptions>
{
    private const string SchemePrefix = "Basic ";

    private readonly IUserQueries _userQueries;

    public BasicAuthenticationHandler(
        IOptionsMonitor<BasicAuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IUserQueries userQueries)
        : base(options, logger, encoder)
    {
        _userQueries = userQueries;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var parseResult = TryParseBasicHeader();
        if (parseResult.Status == BasicHeaderStatus.Missing)
        {
            return AuthenticateResult.NoResult();
        }

        if (parseResult.Status == BasicHeaderStatus.Malformed)
        {
            return AuthenticateResult.Fail("Invalid credentials");
        }

        var user = await _userQueries.FindByUsernameAsync(parseResult.Username, Context.RequestAborted);
        if (user is null || !BCrypt.Net.BCrypt.Verify(parseResult.Password, user.PasswordHash))
        {
            // Generic message avoids leaking whether the user exists (per ADR-011).
            return AuthenticateResult.Fail("Invalid credentials");
        }

        return AuthenticateResult.Success(BuildTicket(user));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{Options.Realm}\", charset=\"UTF-8\"";
        return base.HandleChallengeAsync(properties);
    }

    /// <summary>
    /// Extracts and decodes the Basic Authorization header into username + password. Returns a
    /// discriminated result indicating whether the header was Missing (no auth attempted),
    /// Malformed (present but invalid), or Ok.
    /// </summary>
    private BasicHeaderParseResult TryParseBasicHeader()
    {
        if (!Request.Headers.TryGetValue(BasicAuthenticationDefaults.AuthorizationHeaderName, out var headerValues))
        {
            return BasicHeaderParseResult.Missing();
        }

        var authHeader = headerValues.ToString();
        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return BasicHeaderParseResult.Missing();
        }

        try
        {
            var encoded = authHeader[SchemePrefix.Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separatorIndex = decoded.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                return BasicHeaderParseResult.Malformed();
            }

            var username = decoded[..separatorIndex];
            var password = decoded[(separatorIndex + 1)..];

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                return BasicHeaderParseResult.Malformed();
            }

            return BasicHeaderParseResult.Ok(username, password);
        }
        catch (FormatException)
        {
            return BasicHeaderParseResult.Malformed();
        }
    }

    private AuthenticationTicket BuildTicket(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D", CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, Scheme.Name);
    }

    private readonly record struct BasicHeaderParseResult(BasicHeaderStatus Status, string Username, string Password)
    {
        public static BasicHeaderParseResult Missing() => new(BasicHeaderStatus.Missing, string.Empty, string.Empty);
        public static BasicHeaderParseResult Malformed() => new(BasicHeaderStatus.Malformed, string.Empty, string.Empty);
        public static BasicHeaderParseResult Ok(string username, string password) => new(BasicHeaderStatus.Ok, username, password);
    }

    private enum BasicHeaderStatus
    {
        Missing = 0,
        Malformed = 1,
        Ok = 2,
    }
}
