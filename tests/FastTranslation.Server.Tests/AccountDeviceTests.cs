using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Server.Api;
using Xunit;

namespace FastTranslation.Server.Tests;

/// <summary>
/// 设备管理的闭环：列表标出当前设备、不能移除自己、不能移除别人的设备，
/// 以及“移除一台后新的设备能登录”。
/// </summary>
public class AccountDeviceTests : IClassFixture<ServerFactory>
{
    private const string DeviceHeader = "X-FT-Device";

    private readonly ServerFactory _factory;
    private readonly HttpClient _client;

    public AccountDeviceTests(ServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string UniqueEmail() => $"devices-{Guid.NewGuid():N}@example.com";

    private async Task<AuthResponse> RegisterAsync(HttpClient client, string email, string fingerprint)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, "password123", null, fingerprint, "test-pc"));
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return auth;
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string fingerprint, string name) =>
        _client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, "password123", fingerprint, name));

    private static async Task<DeviceListResponse> GetDevicesAsync(HttpClient client, string fingerprint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/devices");
        request.Headers.Add(DeviceHeader, fingerprint);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceListResponse>())!;
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid deviceId, string fingerprint)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/account/devices/{deviceId:D}");
        request.Headers.Add(DeviceHeader, fingerprint);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Devices_ListShowsEveryDeviceAndMarksTheCaller()
    {
        var email = UniqueEmail();
        var client = _factory.CreateClient();
        await RegisterAsync(client, email, "fp-current");
        (await LoginAsync(email, "fp-laptop", "laptop")).EnsureSuccessStatusCode();

        var list = await GetDevicesAsync(client, "fp-current");

        Assert.Equal(2, list.Devices.Count);
        Assert.Equal(3, list.MaxDevices);
        var current = Assert.Single(list.Devices, device => device.IsCurrent);
        Assert.Equal("test-pc", current.Name);
    }

    [Fact]
    public async Task Devices_CannotRemoveTheCallingDevice()
    {
        var client = _factory.CreateClient();
        await RegisterAsync(client, UniqueEmail(), "fp-current");
        var list = await GetDevicesAsync(client, "fp-current");
        var current = Assert.Single(list.Devices, device => device.IsCurrent);

        var response = await DeleteAsync(client, current.Id, "fp-current");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("device_is_current",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Fact]
    public async Task Devices_CannotRemoveAnotherAccountsDevice()
    {
        var owner = _factory.CreateClient();
        await RegisterAsync(owner, UniqueEmail(), "fp-owner");
        var ownerDevices = await GetDevicesAsync(owner, "fp-owner");
        var owned = ownerDevices.Devices[0];

        var intruder = _factory.CreateClient();
        await RegisterAsync(intruder, UniqueEmail(), "fp-intruder");

        var response = await DeleteAsync(intruder, owned.Id, "fp-intruder");

        // 404 而不是 403：不要向别的账号确认这个设备 id 存在
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("device_missing",
            (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Contains((await GetDevicesAsync(owner, "fp-owner")).Devices, device => device.Id == owned.Id);
    }

    [Fact]
    public async Task Devices_RemovalFreesASlotAndRevokesTheRemovedSession()
    {
        var email = UniqueEmail();
        var client = _factory.CreateClient();
        await RegisterAsync(client, email, "fp-first");

        var secondLogin = await LoginAsync(email, "fp-second", "laptop");
        secondLogin.EnsureSuccessStatusCode();
        var second = (await secondLogin.Content.ReadFromJsonAsync<AuthResponse>())!;
        (await LoginAsync(email, "fp-third", "desktop")).EnsureSuccessStatusCode();

        // 第四台被设备上限挡住
        var blocked = await LoginAsync(email, "fp-fourth", "new-pc");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal("device_limit_reached",
            (await blocked.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        // 从第一台移除第二台
        var list = await GetDevicesAsync(client, "fp-first");
        var laptop = Assert.Single(list.Devices, device => device.Name == "laptop");
        (await DeleteAsync(client, laptop.Id, "fp-first")).EnsureSuccessStatusCode();

        // 被移除设备的 refresh token 随即失效
        var refresh = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // 名额空出来后第四台可以登录
        (await LoginAsync(email, "fp-fourth", "new-pc")).EnsureSuccessStatusCode();
    }
}
