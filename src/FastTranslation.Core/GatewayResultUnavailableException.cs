namespace FastTranslation.Core;

/// <summary>网关已接收请求，文档自动重试或拆段不能重新提交这份内容。</summary>
public sealed class GatewayResultUnavailableException(string message) : Exception(message);
