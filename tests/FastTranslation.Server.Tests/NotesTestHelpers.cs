using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using FastTranslation.Server.Domain;

namespace FastTranslation.Server.Tests;

/// <summary>笔记测试的共享 fixture。每次调用都注册一个新账号,这样两个测试
/// 永远不会看到彼此的笔记;默认会把账号升级,因为保存本身就是付费墙。</summary>
internal static class NotesTestHelpers
{
    public static async Task<HttpClient> SignedInNotesClientAsync(
        this ServerFactory factory, PlanTier plan = PlanTier.Standard)
    {
        var client = factory.CreateClient();
        var email = $"notes-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, "fp-" + Guid.NewGuid().ToString("N"), "test-pc"));
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        if (plan != PlanTier.Free)
        {
            using var upgrade = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/accounts/{email}/plan")
            {
                Content = JsonContent.Create(new PlanChangeRequest(Plans.ToWire(plan))),
            };
            upgrade.Headers.Add(AdminEndpoints.HeaderName, ServerFactory.AdminToken);
            (await client.SendAsync(upgrade)).EnsureSuccessStatusCode();
        }

        return client;
    }

    public static Task<HttpResponseMessage> SaveNoteAsync(this HttpClient client, SaveNoteRequest request) =>
        client.PostAsJsonAsync("/api/v1/notes/", request);

    public static SaveNoteRequest Pair(string source, string target, string? kind = null) =>
        new(kind, source, target, "en", "zh", "qwen-mt-flash", "chrome");
}
