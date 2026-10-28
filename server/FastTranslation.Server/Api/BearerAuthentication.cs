using System.Security.Claims;
using System.Text.Encodings.Web;
using FastTranslation.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FastTranslation.Server.Api;

/// <summary>
/// Bearer authentication backed by <see cref="TokenService"/>. Validating an HS256 token is a
/// signature check, so a full JWT package would buy nothing.
/// </summary>
public static class BearerScheme
{
    public const string Name = "Bearer";
}

public sealed class BearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string UserIdClaim = "sub";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header["Bearer ".Length..].Trim();
        if (!tokens.TryValidateAccessToken(token, out var principal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired access token."));
        }

        var claims = new[]
        {
            new Claim(UserIdClaim, principal.UserId.ToString("D")),
            new Claim("email", principal.Email),
            new Claim("plan", principal.Plan),
        };

        var identity = new ClaimsIdentity(claims, BearerScheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), BearerScheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirstValue(BearerAuthenticationHandler.UserIdClaim)!);
}
