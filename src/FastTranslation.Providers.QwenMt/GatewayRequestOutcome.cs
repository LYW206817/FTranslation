using System.Net.Sockets;
using FastTranslation.Core;

namespace FastTranslation.Providers.QwenMt;

internal static class GatewayRequestOutcome
{
    private const string Unconfirmed = "翻译结果暂未确认，已停止自动重试";

    public static TranslationError? Read(
        HttpResponseMessage response, IReadOnlyDictionary<string, string>? headers, bool previousAttemptUnconfirmed)
    {
        if (!IsGateway(headers)) return null;
        var outcome = response.Headers.TryGetValues("X-FT-Request-Outcome", out var values)
            ? values.FirstOrDefault()
            : null;
        var message = outcome switch
        {
            "pending" => Unconfirmed,
            "already_processed" => "这次翻译已提交，当前无法取回结果",
            // 反代或服务端在发送后断开时，错误响应可能没有网关写入的结果头。
            _ when previousAttemptUnconfirmed && !response.IsSuccessStatusCode => Unconfirmed,
            _ when (int)response.StatusCode is 408 or 500 or 502 or 504 => Unconfirmed,
            _ => null,
        };
        return message is null ? null : new TranslationError(TranslationErrorCode.GatewayResultUnavailable, message);
    }

    public static TranslationError TransportFailure(
        IReadOnlyDictionary<string, string>? headers, string details, Exception? failure = null)
    {
        var socket = failure as SocketException ?? failure?.InnerException as SocketException;
        // DNS 失败和连接被拒发生在请求发送前，仍能按原策略走自带 Key。
        if (!IsGateway(headers) || failure is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
            || socket?.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.NoData)
        {
            return TranslationError.Network(details);
        }
        return new TranslationError(TranslationErrorCode.GatewayResultUnavailable, Unconfirmed);
    }

    private static bool IsGateway(IReadOnlyDictionary<string, string>? headers) =>
        headers?.ContainsKey("X-FT-Request-Id") is true;
}
