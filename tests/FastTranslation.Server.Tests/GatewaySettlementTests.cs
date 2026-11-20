using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Gateway;
using FastTranslation.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FastTranslation.Server.Tests;

public sealed class GatewaySettlementTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    [Fact]
    public async Task A_repeated_operation_is_sent_and_charged_once()
    {
        using var client = await SignInAsync();
        var operationId = Guid.NewGuid();
        var callsBefore = factory.Upstream.Calls.Count;

        using var first = await TranslateAsync(client, operationId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var retry = await TranslateAsync(client, operationId);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(callsBefore + 1, factory.Upstream.Calls.Count);

        var profile = await client.GetFromJsonAsync<UsageDto>("/api/v1/account/usage");
        Assert.Equal("Hello".Length, profile?.UsedMonthlyChars);
    }

    [Fact]
    public async Task An_uncertain_upstream_outcome_shows_up_separately_from_settled_cost()
    {
        using var client = await SignInAsync();
        using var admin = AdminClient();
        var before = await FreeBucketAsync(admin);
        var operationId = Guid.NewGuid();
        factory.Upstream.Responder = (_, _) => StubUpstreamHandler.Json(
            """{"request_id":"mock-provider-timeout"}""", HttpStatusCode.GatewayTimeout);
        try
        {
            using var response = await TranslateAsync(client, operationId);
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal("pending", Assert.Single(response.Headers.GetValues("X-FT-Request-Outcome")));
        }
        finally
        {
            factory.Upstream.Responder = null;
        }

        var bucket = await FreeBucketAsync(admin);
        Assert.Equal(before.CostMicros, bucket.CostMicros);
        Assert.True(bucket.PendingCostMicros > before.PendingCostMicros);
        using var retry = await TranslateAsync(client, operationId);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);

        var pending = await admin.GetFromJsonAsync<PendingGatewayCharge[]>("/api/v1/admin/gateway/reconciliation");
        Assert.NotNull(pending);
        var charge = Assert.Single(pending, item => item.UpstreamRequestId == "mock-provider-timeout");
        Assert.Equal(bucket.PendingCostMicros - before.PendingCostMicros, charge.EstimatedCostMicros);
        var resolve = $"/api/v1/admin/gateway/reconciliation/{charge.Id}/resolve";
        const int actualCost = 173;
        using var denied = await client.PostAsJsonAsync(resolve, new { costMicros = actualCost });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var original = await admin.PostAsJsonAsync(resolve, new { costMicros = actualCost });
        original.EnsureSuccessStatusCode();
        using var repeat = await admin.PostAsJsonAsync(resolve, new { costMicros = actualCost });
        repeat.EnsureSuccessStatusCode();
        using var different = await admin.PostAsJsonAsync(resolve, new { costMicros = actualCost + 1 });
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        var resolved = await FreeBucketAsync(admin);
        Assert.Equal(before.CostMicros + actualCost, resolved.CostMicros);
        Assert.Equal(before.PendingCostMicros, resolved.PendingCostMicros);

        var profile = await client.GetFromJsonAsync<UsageDto>("/api/v1/account/usage");
        Assert.Equal(0, profile?.UsedMonthlyChars);
    }

    [Fact]
    public async Task A_definitive_refusal_can_be_retried_with_the_same_operation()
    {
        using var client = await SignInAsync();
        var operationId = Guid.NewGuid();
        factory.Upstream.Responder = (_, _) => StubUpstreamHandler.Json(
            """{"code":"Throttling"}""", HttpStatusCode.TooManyRequests);
        try
        {
            using var refused = await TranslateAsync(client, operationId);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.False(refused.Headers.Contains("X-FT-Request-Outcome"));
        }
        finally
        {
            factory.Upstream.Responder = null;
        }

        using var retry = await TranslateAsync(client, operationId);
        retry.EnsureSuccessStatusCode();
        var profile = await client.GetFromJsonAsync<UsageDto>("/api/v1/account/usage");
        Assert.Equal("Hello".Length, profile?.UsedMonthlyChars);
    }

    [Fact]
    public async Task An_upstream_connection_that_never_opens_leaves_no_pending_charge()
    {
        using var client = await SignInAsync();
        using var admin = AdminClient();
        var initial = await FreeBucketAsync(admin);
        var operationId = Guid.NewGuid();
        factory.Upstream.Responder = (_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "Fixture provider could not be resolved.");
        try
        {
            using var offline = await TranslateAsync(client, operationId);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
            Assert.False(offline.Headers.Contains("X-FT-Request-Outcome"));
        }
        finally
        {
            factory.Upstream.Responder = null;
        }

        Assert.Equal(initial, await FreeBucketAsync(admin));
        using var retry = await TranslateAsync(client, operationId);
        retry.EnsureSuccessStatusCode();
    }

    [Fact]
    public void Restart_recovery_preserves_unfinished_operation_for_reconciliation()
    {
        var database = factory.Services.GetRequiredService<ServerDatabase>();
        var options = factory.Services.GetRequiredService<GatewayOptions>();
        var ledger = new GatewaySettlementStore(database, options);
        var id = Guid.NewGuid();
        var key = GatewaySettlementStore.GetOperationKey(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(ledger.TryBegin(id, key, "gateway_paid", 218));

        database.Initialize();
        database.RecoverQuotaReservations();
        database.RecoverQuotaReservations();
        var recovered = new GatewaySettlementStore(database, options);

        Assert.False(recovered.TryBegin(Guid.NewGuid(), key, "gateway_paid", 218));
        var pending = Assert.Single(recovered.ReadPending(200, 0), item => item.Id == id);
        Assert.Equal(218, pending.EstimatedCostMicros);
        Assert.True(recovered.Resolve(id, 0));
    }

    private static async Task<AdminBudgetBucket> FreeBucketAsync(HttpClient admin)
    {
        var usage = await admin.GetFromJsonAsync<AdminUsageResponse>("/api/v1/admin/usage");
        Assert.NotNull(usage);
        return Assert.Single(usage.Buckets, item => item.Source == "gateway");
    }

    private async Task<HttpClient> SignInAsync()
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"settlement-{Guid.NewGuid():N}@example.com", "password123", null, "qa-device", "pc"));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private HttpClient AdminClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Token", ServerFactory.AdminToken);
        return client;
    }

    private static async Task<HttpResponseMessage> TranslateAsync(HttpClient client, Guid operationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Add("X-FT-Request-Id", operationId.ToString("D"));
        request.Content = JsonContent.Create(new
        {
            model = "qwen-mt-flash",
            messages = new[] { new { role = "user", content = "Hello" } },
            translation_options = new { source_lang = "en", target_lang = "zh" },
        });
        return await client.SendAsync(request);
    }
}

public sealed class GatewaySettlementClockTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ft-charge-tests", Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public void A_dispatch_past_its_deadline_can_be_reconciled_while_running()
    {
        var clock = new ManualTimeProvider(1_800_000_000);
        var database = new ServerDatabase(_path, clock);
        database.Initialize();
        var ledger = new GatewaySettlementStore(database, new GatewayOptions { TimeoutSeconds = 3 });
        var accounts = new AccountStore(database);
        var budget = new GatewayBudgetGuard(accounts);
        var id = Guid.NewGuid();
        Assert.True(ledger.TryBegin(id, null, "gateway", 500));
        Assert.Equal((0, 0, 500), accounts.ReadTodayCosts("gateway"));
        Assert.Empty(ledger.ReadPending(50, 0));
        Assert.False(ledger.Resolve(id, 170));
        Assert.False(budget.TryReserve("gateway", 600, 101));

        clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal((0, 500, 0), accounts.ReadTodayCosts("gateway"));
        Assert.False(budget.TryReserve("gateway", 600, 101));
        Assert.Equal(id, Assert.Single(ledger.ReadPending(50, 0)).Id);
        Assert.True(ledger.Resolve(id, 170));
        Assert.Equal((170, 0, 0), accounts.ReadTodayCosts("gateway"));
    }

    [Fact]
    public void Cleanup_expires_completed_keys_but_preserves_unresolved_requests()
    {
        var clock = new ManualTimeProvider(1_800_000_000);
        var database = new ServerDatabase(_path, clock);
        database.Initialize();
        var ledger = new GatewaySettlementStore(database, new GatewayOptions());
        var completed = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var completedKey = GatewaySettlementStore.GetOperationKey(Guid.NewGuid(), Guid.NewGuid());
        var pendingKey = GatewaySettlementStore.GetOperationKey(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(ledger.TryBegin(completed, completedKey, "gateway", 145));
        Assert.True(ledger.TryBegin(pending, pendingKey, "gateway", 145));
        ledger.MarkPending(completed, null);
        ledger.MarkPending(pending, null);
        Assert.True(ledger.Resolve(completed, 130));
        clock.Advance(TimeSpan.FromDays(31));

        Assert.False(ledger.HasOperation(completedKey));
        Assert.True(ledger.HasOperation(pendingKey));
        Assert.Equal(pending, Assert.Single(ledger.ReadPending(50, 0)).Id);
    }

    public void Dispose()
    {
        using var connection = new ServerDatabase(_path).Open();
        SqliteConnection.ClearPool(connection);
        connection.Close();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
}
