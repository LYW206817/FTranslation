using System.Text;
using System.Text.Json;
using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Providers.QwenMt.Tests;

public class QwenMtImageTranslatorTests
{
    private static readonly byte[] PngBytes = { 137, 80, 78, 71, 13, 10, 26, 10, 7, 42, 99 };

    private static string SuccessBody(string authority) =>
        "{\"output\":{\"image_url\":\"" + authority + "/out.jpg?Expires=123&OSSAccessKeyId=k&Signature=s\"},"
        + "\"usage\":{\"image_count\":1},\"request_id\":\"req-1\"}";

    private static ImageTranslationRequest Request(string model = "qwen-mt-image-2.0") =>
        new(PngBytes, "auto", "Chinese", model);

    /// <summary>POST 返回译后图 URL，GET 返回假 JPG 字节。</summary>
    private static FakeQwenServer ImageServer(string jpeg = "JPEGDATA") => new()
    {
        Handler = ctx => ctx.HttpMethod == "GET"
            ? (200, jpeg)
            : (200, SuccessBody(ctx.Url!.GetLeftPart(UriPartial.Authority))),
    };

    [Fact]
    public void BuildPayload_SendsPngBase64DataUri()
    {
        using var doc = JsonDocument.Parse(QwenMtImageTranslator.BuildPayload(Request()));
        var input = doc.RootElement.GetProperty("input");
        var uri = input.GetProperty("image_url").GetString()!;

        const string prefix = "data:image/png;base64,";
        Assert.True(uri.StartsWith(prefix, StringComparison.Ordinal), "image_url 必须以 PNG base64 data URI 开头");
        Assert.Equal(PngBytes, Convert.FromBase64String(uri[prefix.Length..]));
    }

    [Fact]
    public void BuildPayload_CarriesModelAndBothLanguages()
    {
        using var doc = JsonDocument.Parse(
            QwenMtImageTranslator.BuildPayload(new ImageTranslationRequest(PngBytes, "Japanese", "Chinese", "qwen-mt-image-2.0")));
        var root = doc.RootElement;
        var input = root.GetProperty("input");

        Assert.Equal("qwen-mt-image-2.0", root.GetProperty("model").GetString());
        Assert.Equal("Japanese", input.GetProperty("source_lang").GetString());
        Assert.Equal("Chinese", input.GetProperty("target_lang").GetString());
    }

    [Fact]
    public void TryReadImageUrl_ReadsOssUrl()
    {
        var url = "http://dashscope-result.oss-cn-beijing.aliyuncs.com/a.jpg?Expires=1&Signature=s";
        Assert.True(QwenMtImageTranslator.TryReadImageUrl(
            "{\"output\":{\"image_url\":\"" + url + "\"},\"usage\":{\"image_count\":1}}", out var imageUrl), "合法响应应解析出 image_url");
        Assert.Equal(url, imageUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("{\"output\":{}}")]
    [InlineData("{\"output\":{\"image_url\":\"\"}}")]
    [InlineData("{\"output\":{\"image_url\":123}}")]
    public void TryReadImageUrl_RejectsBrokenBodies(string json)
    {
        Assert.False(QwenMtImageTranslator.TryReadImageUrl(json, out var imageUrl), "非法响应不应解析出 image_url");
        Assert.Empty(imageUrl);
    }

    [Theory]
    [InlineData(401, TranslationErrorCode.Authentication)]
    [InlineData(403, TranslationErrorCode.Authentication)]
    [InlineData(429, TranslationErrorCode.RateLimited)]
    [InlineData(400, TranslationErrorCode.InvalidResponse)]
    [InlineData(500, TranslationErrorCode.ServerError)]
    public void ClassifyStatus_MapsStatusCode(int statusCode, TranslationErrorCode expected)
    {
        var body = "{\"request_id\":\"r1\",\"code\":\"InvalidParameter.DataInspection\","
                   + "\"message\":\"<400> InternalError.Algo.InvalidParameter.DataInspection: blocked\"}";
        var error = QwenMtImageTranslator.ClassifyStatus(statusCode, body);
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public void ClassifyStatus_PutsUpstreamCodeIntoMessage()
    {
        var body = "{\"request_id\":\"r1\",\"code\":\"InvalidParameter.DataInspection\","
                   + "\"message\":\"<400> InternalError.Algo.InvalidParameter.DataInspection: blocked\"}";
        var error = QwenMtImageTranslator.ClassifyStatus(400, body);

        Assert.Contains("InvalidParameter.DataInspection", error.Message);
        Assert.Contains("blocked", error.Message);
    }

    [Fact]
    public void ClassifyStatus_FallsBackToBodyWhenNotJson()
    {
        var error = QwenMtImageTranslator.ClassifyStatus(502, "  <html>bad gateway</html>  ");
        Assert.Equal(TranslationErrorCode.ServerError, error.Code);
        Assert.Contains("bad gateway", error.Message);
    }

    [Theory]
    [InlineData(null, null, "auto", "Chinese")]
    [InlineData("auto", "auto", "auto", "Chinese")]
    [InlineData("", "", "auto", "Chinese")]
    [InlineData("en", "zh", "English", "Chinese")]
    [InlineData("zh", "zh", "auto", "Chinese")]
    [InlineData("ja", "en", "Japanese", "English")]
    public void Resolve_AppliesLanguagePolicy(string? settingsSource, string? settingsTarget, string expectedSource, string expectedTarget)
    {
        var (source, target) = ImageTranslationPolicy.Resolve(settingsSource, settingsTarget);
        Assert.Equal(expectedSource, source);
        Assert.Equal(expectedTarget, target);
    }

    // v126: 「自动(中英互译)」的截图方向必须按 OCR 原文判——中文译英文、外文译中文。
    // 方向错了发给图片模型的是「中→中」，它会原样返回，用户看到截图没变化。
    [Theory]
    [InlineData("你的测试计划非常严谨，特别是将整百年作为独立的等价类进行验证。", "auto", "English")]
    [InlineData("The quick brown fox jumps over the lazy dog.", "auto", "Chinese")]
    [InlineData("中英 mixed text", "auto", "Chinese")] // 英文占比过半，和文字链路同为「非中文→中文」
    [InlineData("2026-11-11", "auto", "Chinese")]       // 没有字母：判不出方向时回落 Chinese
    [InlineData("", "auto", "Chinese")]                 // OCR 拿到空串
    public void Resolve_AutoTarget_UsesOcrText(string sampleText, string settingsTarget, string expectedTarget)
    {
        var (source, target) = ImageTranslationPolicy.Resolve("auto", settingsTarget, sampleText);
        Assert.Equal("auto", source);
        Assert.Equal(expectedTarget, target);
    }

    // 显式目标语言是用户决定，不能被 OCR 文本推翻。
    [Fact]
    public void Resolve_ExplicitTarget_IgnoresOcrText()
    {
        var (source, target) = ImageTranslationPolicy.Resolve("auto", "zh", "The quick brown fox jumps over the lazy dog.");
        Assert.Equal(("auto", "Chinese"), (source, target));
    }

    [Fact]
    public async Task Success_DownloadsJpegFromRemoteUrl()
    {
        using var server = ImageServer();
        using var translator = new QwenMtImageTranslator(() => "sk-test", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message ?? "");
        Assert.Equal(Encoding.UTF8.GetBytes("JPEGDATA"), result.JpegBytes);
        Assert.Contains("/out.jpg?Expires=123", result.RemoteUrl);
        Assert.Equal("Chinese", result.TargetLanguage);
        Assert.True(result.ElapsedMs >= 0);
        Assert.Contains("data:image/png;base64,", server.Bodies[0]);
        Assert.Equal("Bearer sk-test", server.AuthHeaders[0]);
    }

    [Fact]
    public async Task Success_FallsBackToOptionsModelWhenRequestHasNone()
    {
        using var server = ImageServer();
        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(model: ""), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message ?? "");
        Assert.Contains("qwen-mt-image-2.0", server.Bodies[0]);
    }

    [Fact]
    public async Task MissingKey_FailsImmediately()
    {
        using var server = ImageServer();
        using var translator = new QwenMtImageTranslator(() => null, new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Authentication, result.Error?.Code);
        Assert.Equal(TranslationError.Authentication().Message, result.Error?.Message);
        Assert.Empty(server.Bodies);
    }

    [Fact]
    public async Task EmptyImage_FailsWithoutCallingNetwork()
    {
        using var server = ImageServer();
        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(
            new ImageTranslationRequest(Array.Empty<byte>(), "auto", "Chinese", "qwen-mt-image-2.0"), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.InvalidResponse, result.Error?.Code);
        Assert.Contains("空图片", result.Error!.Message);
        Assert.Empty(server.Bodies);
    }

    [Fact]
    public async Task Unauthorized_MapsToAuthenticationWithoutRetry()
    {
        using var server = new FakeQwenServer { Handler = _ => (401, "{\"code\":\"InvalidApiKey\",\"message\":\"bad key\"}") };
        using var translator = new QwenMtImageTranslator(() => "sk-bad", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Authentication, result.Error?.Code);
        Assert.Single(server.Bodies);
    }

    [Fact]
    public async Task RateLimited_RetriesOnceThenSucceeds()
    {
        var posts = 0;
        using var server = ImageServer();
        server.Handler = ctx => ctx.HttpMethod == "GET"
            ? (200, "JPEGDATA")
            : Interlocked.Increment(ref posts) == 1
                ? (429, "{\"code\":\"Throttling\",\"message\":\"rpm limit\"}")
                : (200, SuccessBody(ctx.Url!.GetLeftPart(UriPartial.Authority)));

        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message ?? "");
        Assert.Equal(2, posts);
    }

    [Fact]
    public async Task RateLimitedTwice_ReturnsRateLimitedWithoutThirdTry()
    {
        using var server = new FakeQwenServer { Handler = _ => (429, "{\"code\":\"Throttling\",\"message\":\"rpm limit\"}") };
        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.RateLimited, result.Error?.Code);
        Assert.Equal(2, server.Bodies.Count);
    }

    [Fact]
    public async Task DataInspection400_MapsToInvalidResponseWithUpstreamCode()
    {
        using var server = new FakeQwenServer
        {
            Handler = _ => (400, "{\"request_id\":\"r1\",\"code\":\"InvalidParameter.DataInspection\","
                                + "\"message\":\"<400> InternalError.Algo.InvalidParameter.DataInspection: blocked\"}"),
        };
        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.InvalidResponse, result.Error?.Code);
        Assert.Contains("InvalidParameter.DataInspection", result.Error!.Message);
        Assert.Single(server.Bodies);
    }

    [Fact]
    public async Task MissingImageUrl_MapsToInvalidResponse()
    {
        using var server = new FakeQwenServer { Handler = _ => (200, "{\"output\":{},\"usage\":{\"image_count\":1}}") };
        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.InvalidResponse, result.Error?.Code);
        Assert.Contains("image_url", result.Error!.Message);
    }

    [Fact]
    public async Task FailedDownload_MapsToInvalidResponse()
    {
        using var server = ImageServer();
        server.Handler = ctx => ctx.HttpMethod == "GET"
            ? (404, "gone")
            : (200, SuccessBody(ctx.Url!.GetLeftPart(UriPartial.Authority)));
        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        var result = await translator.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.InvalidResponse, result.Error?.Code);
        Assert.Contains("图片下载失败", result.Error!.Message);
    }

    [Fact]
    public async Task CallerCancellation_MapsToCancelled()
    {
        using var server = ImageServer();
        using var translator = new QwenMtImageTranslator(() => "sk", new QwenMtImageOptions { BaseUrl = server.Url });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await translator.TranslateImageAsync(Request(), cts.Token);
        Assert.Equal(TranslationErrorCode.Cancelled, result.Error?.Code);
    }
[Fact]
    public async Task GatewayQuotaRefusal_MapsToQuotaExhausted()
    {
        using var server = new FakeQwenServer
        {
            Handler = _ => (429, "{\"error\":\"daily_quota_exhausted\"}"),
        };
        using var provider = new QwenMtImageTranslator(
            () => "token",
            new QwenMtImageOptions { BaseUrl = server.Url + "/image" });

        var result = await provider.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.QuotaExhausted, result.Error?.Code);
        Assert.Equal("今日额度已用完", result.Error?.Message);
        Assert.Single(server.Bodies);
    }

    [Fact]
    public async Task ExtraHeaders_AreAddedToTheImageRequest()
    {
        string? seen = null;
        using var server = ImageServer();
        server.Handler = ctx =>
        {
            // 只有 POST 带设备头；后面下载译图的那次 GET 是普通 GET
            if (ctx.HttpMethod == "POST") seen = ctx.Headers["X-FT-Device"];
            return ctx.HttpMethod == "GET"
                ? (200, "JPEGDATA")
                : (200, SuccessBody(ctx.Url!.GetLeftPart(UriPartial.Authority)));
        };
        using var provider = new QwenMtImageTranslator(
            () => "token",
            new QwenMtImageOptions
            {
                BaseUrl = server.Url + "/image",
                ExtraHeaders = new Dictionary<string, string> { ["X-FT-Device"] = "fp-1" },
            });

        var result = await provider.TranslateImageAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("fp-1", seen);
    }
}

