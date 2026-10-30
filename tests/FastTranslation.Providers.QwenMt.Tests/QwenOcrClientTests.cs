using System.Text.Json;
using FastTranslation.Core;
using Xunit;

namespace FastTranslation.Providers.QwenMt.Tests;

public class QwenOcrClientTests
{
    private static readonly byte[] PngBytes = { 137, 80, 78, 71, 13, 10, 26, 10, 7, 42, 99 };

    private static ImageOcrRequest Request() => new(PngBytes);

    private static string SuccessBody(string text) =>
        "{\"output\":{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\","
        + "\"content\":[{\"text\":\"" + text + "\"}]}}]},\"usage\":{\"input_tokens\":1}}";

    /// <summary>qwen-vl-ocr 的真实响应形态：分片里是 ocr_result.processed_text，不是 text。</summary>
    private static string RealOcrBody(string text) =>
        "{\"output\":{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\","
        + "\"content\":[{\"ocr_result\":{\"processed_text\":\"" + text + "\"}}]}}]},"
        + "\"usage\":{\"input_tokens\":1}}";

    [Fact]
    public void BuildPayload_PutsPngBase64DataUriInMessages()
    {
        using var doc = JsonDocument.Parse(QwenOcrClient.BuildPayload(Request(), "qwen-vl-ocr"));
        var root = doc.RootElement;
        var content = root.GetProperty("input").GetProperty("messages")[0].GetProperty("content");
        // 字段名必须是 image：DashScope 不认 OpenAI 兼容的 image_url，会回
        // 400 "The item of `content` should be a message of a certain modal"(2026-10-30 实测)。
        Assert.False(content[0].TryGetProperty("image_url", out _), "image_url 会被 DashScope 拒绝");
        var uri = content[0].GetProperty("image").GetString()!;

        Assert.Equal("qwen-vl-ocr", root.GetProperty("model").GetString());
        Assert.Equal("user", root.GetProperty("input").GetProperty("messages")[0].GetProperty("role").GetString());
        const string prefix = "data:image/png;base64,";
        Assert.True(uri.StartsWith(prefix, StringComparison.Ordinal), "image 必须以 PNG base64 data URI 开头");
        Assert.Equal(PngBytes, Convert.FromBase64String(uri[prefix.Length..]));
    }

    [Fact]
    public void BuildPayload_SendsNoPrompt()
    {
        // 不带 prompt 才是「只输出图中文字」；带一句「请识别」反而会让模型开始闲聊。
        using var doc = JsonDocument.Parse(QwenOcrClient.BuildPayload(Request(), "qwen-vl-ocr"));
        var message = doc.RootElement.GetProperty("input").GetProperty("messages")[0];

        Assert.False(message.TryGetProperty("prompt", out _));
        Assert.Equal(1, message.GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void TryReadText_ReadsFirstTextPart()
    {
        Assert.True(QwenOcrClient.TryReadText(SuccessBody("Hello world"), out var text), "合法响应应解析出 text");
        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void TryReadText_ReadsOcrResultProcessedText()
    {
        // qwen-vl-ocr 实际返回的是 ocr_result.processed_text；只认 text 会让每次截图都被判成「没识别到文字」。
        Assert.True(QwenOcrClient.TryReadText(RealOcrBody("识别到的原文"), out var text), "ocr_result.processed_text 必须认");
        Assert.Equal("识别到的原文", text);
    }

    [Fact]
    public void TryReadText_SkipsBlankOcrResultAndFallsBackToText()
    {
        const string body = "{\"output\":{\"choices\":[{\"message\":{\"content\":["
            + "{\"ocr_result\":{\"processed_text\":\"   \"}},{\"text\":\"fallback\"}]}}]}}";

        Assert.True(QwenOcrClient.TryReadText(body, out var text), "空白 ocr_result 应跳过并继续读 text");
        Assert.Equal("fallback", text);
    }

    [Fact]
    public void TryReadText_JoinsMultiplePartsWithNewlines()
    {
        const string body = "{\"output\":{\"choices\":[{\"message\":{\"content\":["
            + "{\"text\":\"first line\"},{\"text\":\"second line\"}]}}]}}";

        Assert.True(QwenOcrClient.TryReadText(body, out var text), "多个 text 分片应全部保留");
        Assert.Equal("first line\nsecond line", text);
    }

    [Fact]
    public void TryReadText_AcceptsPlainStringContent()
    {
        const string body = "{\"output\":{\"choices\":[{\"message\":{\"content\":\"plain\"}}]}}";

        Assert.True(QwenOcrClient.TryReadText(body, out var text), "OpenAI 兼容形态的纯字符串 content 也要认");
        Assert.Equal("plain", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("{\"output\":{}}")]
    [InlineData("{\"output\":{\"choices\":[]}}")]
    [InlineData("{\"output\":{\"choices\":[{}]}}")]
    [InlineData("{\"output\":{\"choices\":[{\"message\":{\"content\":[]}}]}}")]
    [InlineData("{\"output\":{\"choices\":[{\"message\":{\"content\":[{\"text\":\"   \"}]}}]}}")]
    public void TryReadText_RejectsBrokenBodies(string json)
    {
        Assert.False(QwenOcrClient.TryReadText(json, out var text), "残缺响应不能当成识别成功");
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task Success_ReturnsRecognizedText()
    {
        using var server = new FakeQwenServer { Handler = _ => (200, SuccessBody("Hello world")) };
        using var client = new QwenOcrClient(() => "sk-test", new QwenOcrOptions { BaseUrl = server.Url });

        var result = await client.RecognizeAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Hello world", result.Text);
        Assert.Null(result.Error);
        Assert.Equal(string.Empty, result.RemoteUrl); // OCR 不下载任何东西
    }

    [Fact]
    public async Task Success_SendsBearerToken()
    {
        using var server = new FakeQwenServer { Handler = _ => (200, SuccessBody("x")) };
        using var client = new QwenOcrClient(
            () => "sk-test",
            new QwenOcrOptions
            {
                BaseUrl = server.Url,
                ExtraHeaders = new Dictionary<string, string> { ["X-FT-Device"] = "device-1" },
            });

        await client.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Contains("Bearer sk-test", server.AuthHeaders);
    }

    [Fact]
    public async Task MissingKey_FailsWithAuthentication()
    {
        using var client = new QwenOcrClient(() => null);

        var result = await client.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Authentication, result.Error!.Code);
    }

    [Fact]
    public async Task EmptyImage_FailsWithoutCallingTheServer()
    {
        using var server = new FakeQwenServer { Handler = _ => (200, SuccessBody("never")) };
        using var client = new QwenOcrClient(() => "sk-test", new QwenOcrOptions { BaseUrl = server.Url });

        var result = await client.RecognizeAsync(new ImageOcrRequest(Array.Empty<byte>()), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.InvalidResponse, result.Error!.Code);
        Assert.Empty(server.Bodies);
    }

    [Fact]
    public async Task SuccessWithoutText_IsInvalidResponseNotSuccess()
    {
        // 图里本来就没字，是「没认出来」而不是故障——但也不能当成成功（空笔记没有意义）。
        using var server = new FakeQwenServer { Handler = _ => (200, "{\"output\":{\"choices\":[{\"message\":{\"content\":[]}}]}}") };
        using var client = new QwenOcrClient(() => "sk-test", new QwenOcrOptions { BaseUrl = server.Url });

        var result = await client.RecognizeAsync(Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TranslationErrorCode.InvalidResponse, result.Error!.Code);
        Assert.Contains("没有识别到文字", result.Error.Message);
    }

    [Fact]
    public async Task Unauthorized_IsAuthentication()
    {
        using var server = new FakeQwenServer
        {
            Handler = _ => (401, "{\"code\":\"InvalidApiKey\",\"message\":\"Invalid API-key provided.\"}"),
        };
        using var client = new QwenOcrClient(() => "sk-bad", new QwenOcrOptions { BaseUrl = server.Url });

        var result = await client.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.Authentication, result.Error!.Code);
    }

    [Fact]
    public async Task GatewayQuotaRefusal_IsQuotaExhausted()
    {
        // 开发者网关用 429/503 + {"error":"<code>"} 表达「额度花完了」，与 DashScope 的限流同码不同因，
        // 所以只有 body 能分辨：这里必须是 QuotaExhausted 而不是 RateLimited。
        using var server = new FakeQwenServer
        {
            Handler = _ => (429, "{\"error\":\"monthly_quota_exhausted\"}"),
        };
        using var client = new QwenOcrClient(() => "sk-test", new QwenOcrOptions { BaseUrl = server.Url });

        var result = await client.RecognizeAsync(Request(), CancellationToken.None);

        Assert.Equal(TranslationErrorCode.QuotaExhausted, result.Error!.Code);
        Assert.Contains("本月额度已用完", result.Error.Message);
    }
}
