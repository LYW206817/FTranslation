using System.Security.Cryptography;
using FastTranslation.Server;
using FastTranslation.Server.Api;
using FastTranslation.Server.Auth;
using FastTranslation.Server.Gateway;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// 选项从 IConfiguration 惰性解析（而不是从 builder.Configuration 直接取），这样
// 宿主添加的配置源——按环境区分的 appsettings，以及测试中的
// WebApplicationFactory 覆盖——都可见。
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
builder.Services.AddSingleton<RedeemStore>();
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

// 语音中继要升级成 WebSocket;UseWebSockets 只开通道,鉴权仍由 /v1/asr 端点在升级前完成。
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapNotesEndpoints();
app.MapRedeemEndpoints();
app.MapGatewayEndpoints();
app.MapSpeechEndpoints();
app.MapAdminEndpoints();
app.MapRedeemAdminEndpoints();

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
            // 不是 base64——继续走原始字节路径。
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

    // 仅用于开发便利：随机密钥会让 token 在每次重启后失效，这正是
    // 我们希望对一次性本地环境达到的效果。
    return RandomNumberGenerator.GetBytes(32);
}

/// <summary>公开出来，便于集成测试用隔离数据库启动同一个应用。</summary>
public partial class Program;
