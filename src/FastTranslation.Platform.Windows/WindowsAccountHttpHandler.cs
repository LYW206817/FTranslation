using System.ComponentModel;
using System.Net.Http;

namespace FastTranslation.Platform.Windows;

/// <summary>账号请求在 Windows 当前代理配置变化后使用新的 WinHTTP 会话。</summary>
public sealed class WindowsAccountHttpHandler : HttpMessageHandler
{
    private const int WinHttpTimeout = 12002;
    private const int WinHttpNameNotResolved = 12007;
    private const int WinHttpCannotConnect = 12029;
    private const int WinHttpSecureFailure = 12175;

    private readonly Lock _gate = new();
    private readonly Func<WindowsProxySettings> _readProxy;
    private readonly Func<WindowsProxySettings, HttpMessageHandler> _createTransport;
    private Session? _current;
    private bool _disposed;

    /// <summary>使用 Windows 代理，包括自动检测、PAC 和绕过列表。</summary>
    public WindowsAccountHttpHandler()
        : this(WindowsProxySettings.ReadCurrent, _ => new WinHttpHandler
        {
            WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
        })
    {
    }

    internal WindowsAccountHttpHandler(Func<WindowsProxySettings> readProxy,
        Func<WindowsProxySettings, HttpMessageHandler> createTransport)
    {
        _readProxy = readProxy;
        _createTransport = createTransport;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = AcquireSession();
        HttpResponseMessage? response = null;
        try
        {
            response = await session.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // 账号响应是完整读取的 JSON。读完再放行旧会话回收，代理切换才不会掐断在途响应体。
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            var completed = response;
            response = null;
            return completed;
        }
        catch (HttpRequestException ex) when (ex.InnerException is Win32Exception error)
        {
            if (error.NativeErrorCode == WinHttpTimeout) throw new TimeoutException("WinHTTP timed out.", ex);
            var kind = error.NativeErrorCode switch
            {
                WinHttpNameNotResolved => HttpRequestError.NameResolutionError,
                WinHttpCannotConnect => HttpRequestError.ConnectionError,
                WinHttpSecureFailure => HttpRequestError.SecureConnectionError,
                _ => ex.HttpRequestError,
            };
            throw new HttpRequestException(kind, ex.Message, ex, ex.StatusCode);
        }
        finally
        {
            response?.Dispose();
            lock (_gate)
            {
                session.InFlight--;
                if (session.InFlight == 0 && session != _current) session.Http.Dispose();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                var previous = _current;
                _current = null;
                if (previous is { InFlight: 0 }) previous.Http.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private Session AcquireSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var settings = _readProxy();
            // v209：默认代理可能固定在进程启动时的环境配置；复用的是当前 Windows 配置下的会话。
            if (_current is not { } session || session.Settings != settings)
            {
                var previous = _current;
                session = new Session(settings, new HttpMessageInvoker(_createTransport(settings)));
                _current = session;
                if (previous is { InFlight: 0 }) previous.Http.Dispose();
            }

            session.InFlight++;
            return session;
        }
    }

    private sealed class Session(WindowsProxySettings settings, HttpMessageInvoker http)
    {
        public WindowsProxySettings Settings { get; } = settings;
        public HttpMessageInvoker Http { get; } = http;
        public int InFlight { get; set; }
    }
}
