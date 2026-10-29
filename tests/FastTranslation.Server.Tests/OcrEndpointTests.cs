using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;
using FastTranslation.Server.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>
/// <c>POST /v1/ocr</c> exists because the chat endpoint cannot carry a picture: it only counts
/// <c>content</c> strings, so an image-only body reads as zero characters and is refused. These tests
/// pin the three things that make the extra endpoint worth having — it forwards to the vision path,
/// it bills at the vision price, and a refusal never costs money.
/// </summary>
public class OcrEndpointTests : IClassFixture<GatewayFactory>, IClassFixture<ServerFactory>
{
    private readonly GatewayFactory _gateway;
    private readonly ServerFactory _plain;

    public OcrEndpointTests(GatewayFactory gateway, ServerFactory plain)
    {
        _gateway = gateway;
        _plain = plain;
    }

    /// <summary>An OCR body shaped like the one the desktop client sends: an image, no text.</summary>
    private static Task<HttpResponseMessage> OcrAsync(HttpClient client, string? device = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/ocr")
        {
            Content = JsonContent.Create(new
            {
                model = "qwen-vl-ocr",
                input = new
                {
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[] { new { image_url = "data:image/png;base64,AAAA" } },
                        },
                    },
                },
            }),
        };

        if (device is not null)
        {
            request.Headers.Add(GatewayEndpointConstants.DeviceHeader, device);
        }

        return client.SendAsync(request);
    }

    private static async Task<HttpClient> SignedInClientAsync(
        WebApplicationFactory<Program> factory,
        PlanTier plan,
        string? device = null)
    {
        var client = factory.CreateClient();
        var email = $"ocr-{Guid.NewGuid():N}@example.com";
        var fingerprint = device ?? "fp-" + Guid.NewGuid().ToString("N");

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, fingerprint, "test-pc"));
        register.EnsureSuccessStatusCode();
        var auth = (await register.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        if (plan != PlanTier.Free)
        {
            using var upgrade = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
            {
                Content = JsonContent.Create(new PlanChangeRequest(Plans.ToWire(plan), null)),
            };
            upgrade.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
            (await client.SendAsync(upgrade)).EnsureSuccessStatusCode();
        }

        return client;
    }

    private static async Task<string> EmailAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/account/me");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!.Email;
    }

    private static Guid UserId(GatewayFactory factory, string email)
        => factory.Services.GetRequiredService<AccountStore>().FindUserByEmail(email)!.Id;

    private static (string Model, long Chars, long CostMicros) LastUsageEvent(GatewayFactory factory, Guid userId)
    {
        var database = factory.Services.GetRequiredService<ServerDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model, chars, cost_micros FROM usage_events
            WHERE user_id = $userId ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static int UsageEventCount(GatewayFactory factory, Guid userId)
    {
        var database = factory.Services.GetRequiredService<ServerDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_events WHERE user_id = $userId";
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public async Task Ocr_RequiresAuthentication()
    {
        var anonymous = _gateway.CreateClient();

        var response = await OcrAsync(anonymous);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ocr_DisabledGatewayIs503()
    {
        // The plain factory has no API key, which is the default deployment: it must refuse, not guess.
        var client = await SignedInClientAsync(_plain, PlanTier.Light);

        var response = await OcrAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("gateway_disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ocr_EmptyBodyIs400()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);

        var response = await client.PostAsync("/v1/ocr", new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("body_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ocr_ForwardsToTheVisionEndpointWithDeveloperKey()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var before = _gateway.Upstream.Calls.Count;

        var response = await OcrAsync(client);

        response.EnsureSuccessStatusCode();
        var call = _gateway.Upstream.Calls[before];
        Assert.Equal("http://upstream.test/ocr", call.Url);
        // The client's Bearer token must never leak upstream; the developer key replaces it.
        Assert.Equal(GatewayFactory.UpstreamKey, call.Authorization);
        Assert.Contains("image_url", call.Body);
    }

    [Fact]
    public async Task Ocr_SuccessIsBilledAtVisionPrice()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var email = await EmailAsync(client);

        (await OcrAsync(client)).EnsureSuccessStatusCode();

        var (model, chars, costMicros) = LastUsageEvent(_gateway, UserId(_gateway, email));
        Assert.Equal("qwen-vl-ocr", model);
        // Quota is charged like one screenshot, so an OCR cannot be cheaper than the image it came from.
        Assert.Equal(1_000, chars);
        Assert.Equal(Pricing.TextCostMicros("qwen-vl-ocr", 1_000, 0), costMicros);
        Assert.True(costMicros > 0);
    }

    [Fact]
    public async Task Ocr_UsesReportedUsageWhenUpstreamSuppliesIt()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var email = await EmailAsync(client);
        _gateway.Upstream.Responder = (_, _) => StubUpstreamHandler.Json("""
            {"output":{"choices":[{"message":{"content":[{"text":"hello"}]}}]},"usage":{"input_tokens":1500,"output_tokens":40}}
            """);

        try
        {
            (await OcrAsync(client)).EnsureSuccessStatusCode();
        }
        finally
        {
            _gateway.Upstream.Reset();
        }

        var (_, _, costMicros) = LastUsageEvent(_gateway, UserId(_gateway, email));
        // Vision tokens are what the model actually bills, so they must beat the 1000-char fallback.
        Assert.Equal(Pricing.TextCostMicros("qwen-vl-ocr", 1_500, 40), costMicros);
    }

    [Fact]
    public async Task Ocr_ForwardsUpstreamFailureWithoutBilling()
    {
        var client = await SignedInClientAsync(_gateway, PlanTier.Light);
        var email = await EmailAsync(client);
        var userId = UserId(_gateway, email);
        var before = UsageEventCount(_gateway, userId);
        _gateway.Upstream.Responder = (_, _) => StubUpstreamHandler.Json("""{"error":{"message":"nope"}}""", HttpStatusCode.BadRequest);

        try
        {
            var response = await OcrAsync(client);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, UsageEventCount(_gateway, userId));
        }
        finally
        {
            _gateway.Upstream.Reset();
        }
    }

    [Fact]
    public async Task Ocr_RefusesWhenFreeDailyQuotaIsSpent()
    {
        // Free allows 2000 characters a day, so two screenshots (1000 a piece) exhaust it.
        var client = await SignedInClientAsync(_gateway, PlanTier.Free);

        (await OcrAsync(client)).EnsureSuccessStatusCode();
        (await OcrAsync(client)).EnsureSuccessStatusCode();

        var refused = await OcrAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("daily_quota_exhausted", refused.Headers.GetValues("X-FT-Quota-Reason").Single());
    }

    [Fact]
    public void Pricing_KnowsTheVisionModel()
    {
        // A missing entry silently bills at flash prices, which under-reports vision cost badly.
        var price = Pricing.For("qwen-vl-ocr");

        Assert.Equal("qwen-vl-ocr", price.Model);
        Assert.True(price.InputPerMillionTokens > 0m);
    }
}
