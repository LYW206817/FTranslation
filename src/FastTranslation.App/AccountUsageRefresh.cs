using System.Windows.Threading;
using FastTranslation.Core.Account;

namespace FastTranslation.App;

/// <summary>在 UI 线程合并账号页和套餐页的用量刷新。</summary>
/// <remarks>会话和用量的边界见 docs/superpowers/specs/2026-11-26-account-plan-interaction.md。</remarks>
internal sealed class AccountUsageRefresh(AccountCoordinator account)
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private long _revision;

    public event Action? Changed;

    public AccountApiResult<AccountUsage>? Result { get; private set; }
    public bool IsLoading { get; private set; }
    public string ButtonText => IsLoading ? "正在刷新…" : Result?.Error is not null ? "重试用量" : "刷新用量";

    public void Reset()
    {
        // 网关也会在后台刷新登录会话，WPF 的状态通知始终在创建页面的线程发出。
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(Reset);
            return;
        }
        _revision++;
        Result = null;
        IsLoading = false;
        Changed?.Invoke();
    }

    public void EnsureLoaded()
    {
        if (Result is null) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!account.IsSignedIn || IsLoading) return;
        var revision = _revision;
        var scope = (account.SessionGeneration, account.Session?.Profile.Plan);
        Result = null;
        IsLoading = true;
        Changed?.Invoke();

        AccountApiResult<AccountUsage> result;
        try
        {
            result = await account.ReadUsageAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 刷新从导航和按钮触发，意外的解析错误也应结束等待，并留下可诊断的异常类型。
            App.Trace($"account usage: {ex.GetType().Name}");
            result = AccountApiResult<AccountUsage>.Fail(AccountErrorCode.InvalidResponse);
        }

        // 换账号或套餐后，旧请求即使更晚返回也没有资格更新当前页面。
        if (_revision != revision || scope != (account.SessionGeneration, account.Session?.Profile.Plan)) return;
        Result = result;
        IsLoading = false;
        Changed?.Invoke();
    }
}
