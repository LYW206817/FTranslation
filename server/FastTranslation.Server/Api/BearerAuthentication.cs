using System.Security.Claims;
using System.Text.Encodings.Web;
using FastTranslation.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FastTranslation.Server.Api;

/// <summary>
/// 由 <see cref="TokenService"/> 支撑的 Bearer 认证。校验 HS256 token 本质上就是
/// 一次验签，所以引入完整的 JWT 包没有收益。
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
