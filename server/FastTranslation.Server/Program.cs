using System.Security.Cryptography;
using FastTranslation.Server;
using FastTranslation.Server.Api;
using FastTranslation.Server.Auth;
using FastTranslation.Server.Gateway;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Options are resolved lazily from IConfiguration (not from builder.Configuration) so that
// configuration sources added by the host — appsettings per environment and, in tests,
// WebApplicationFactory overrides — are all visible.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions());
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServerOptions>().Smtp);

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<ServerOptions>();
    var environment = sp.GetRequiredService<IHostEnvironment>();
    return new TokenService(ResolveSigningKey(options, environment), TimeSpan.FromMinutes(options.AccessTokenMinutes));
});

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<ServerOptions>();
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var path = Path.IsPathRooted(options.DatabasePath)
        ? options.DatabasePath
        : Path.Combine(environment.ContentRootPath, options.DatabasePath);
    return new ServerDatabase(path);
});

builder.Services.AddSingleton(sp => sp.GetRequiredService<ServerOptions>().Gateway);
builder.Services.AddSingleton<GatewayRateLimiter>();
builder.Services.AddSingleton(sp => new DashScopeGateway(sp.GetRequiredService<GatewayOptions>()));

builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<NoteStore>();
builder.Services.AddSingleton<VerificationCodeService>();
builder.Services.AddSingleton<IVerificationMailSender, VerificationMailSender>();

builder.Services.AddAuthentication(BearerScheme.Name)
    .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(BearerScheme.Name, null);
builder.Services.AddAuthorization();
builder.Services.AddAuthRateLimiting();

var app = builder.Build();

app.Services.GetRequiredService<ServerDatabase>().Initialize();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapNotesEndpoints();
app.MapGatewayEndpoints();
app.MapAdminEndpoints();

app.Run();

static byte[] ResolveSigningKey(ServerOptions options, IHostEnvironment environment)
{
    if (!string.IsNullOrWhiteSpace(options.SigningKey))
    {
        try
        {
            var decoded = Convert.FromBase64String(options.SigningKey);
            if (decoded.Length >= 32)
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
            // Not base64 — fall through to the raw-bytes path.
        }

        var raw = System.Text.Encoding.UTF8.GetBytes(options.SigningKey);
        if (raw.Length >= 32)
        {
            return raw;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Server:SigningKey must be at least 32 bytes (32 ASCII chars or 44 base64 chars).");
        }
    }

    if (!environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Server:SigningKey is required outside Development. Set it in appsettings.Production.json or the environment.");
    }

    // Development convenience only: a random key invalidates tokens on every restart, which is exactly
    // what we want for a throwaway local box.
    return RandomNumberGenerator.GetBytes(32);
}

/// <summary>Exposed so integration tests can boot the same app with an isolated database.</summary>
public partial class Program;
