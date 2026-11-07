namespace FastTranslation.Core.Account;

/// <summary>
/// 提交前本地拦截明显填错的邮箱,省一次无效请求,也避免旧服务端把格式错误说成「服务器出错」。
/// 规则必须与服务端 CredentialRules.ValidateEmail 完全一致:服务端不引用 Core,
/// 代码没法共享,两边用同一组测试用例钉住(EmailFormatTests / CredentialRulesTests)。
/// 线上不发邮箱验证码,格式校验是注册邮箱的唯一闸门,所以不做「有个 @ 就行」的宽松版。
/// </summary>
public static class EmailFormat
{
    private const int MaxEmailLength = 254;
    private const int MaxLocalLength = 64;
    private const int MaxDomainLabelLength = 63;

    // RFC 5322 的 atext 里除字母数字外允许的符号;点单独检查位置(不能首尾/连续)。
    private const string LocalSymbols = "!#$%&'*+/=?^_`{|}~-";

    /// <summary>格式能否提交给服务端；接受/拒绝结果与服务端 <c>CredentialRules.ValidateEmail</c> 完全一致。</summary>
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        var trimmed = email.Trim();
        if (trimmed.Length > MaxEmailLength) return false;

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.IndexOf('@', at + 1) >= 0) return false;

        return IsLocalPart(trimmed[..at]) && IsDomain(trimmed[(at + 1)..]);
    }

    private static bool IsLocalPart(string local)
    {
        if (local.Length is < 1 or > MaxLocalLength) return false;
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var c in local)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && !LocalSymbols.Contains(c)) return false;
        }
        return true;
    }

    private static bool IsDomain(string domain)
    {
        // Split 出的空标签同时挡住首尾点与连续点,不需要再单独查。
        var labels = domain.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > MaxDomainLabelLength) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            foreach (var c in label)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
            }
        }
        var tld = labels[^1];
        if (tld.Length < 2) return false;
        foreach (var c in tld)
        {
            if (!char.IsAsciiLetter(c)) return false;
        }
        return true;
    }
}