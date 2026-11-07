namespace FastTranslation.Server.Auth;

/// <summary>
/// 认证接口共用的校验。集中在一处，避免注册/登录/重置各写一套而逐渐走样。
/// </summary>
public static class CredentialRules
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxEmailLength = 254;
    private const int MaxLocalLength = 64;
    private const int MaxDomainLabelLength = 63;

    // RFC 5322 的 atext 里除字母数字外允许的符号；点单独检查位置(不能首尾/连续)。
    private const string LocalSymbols = "!#$%&'*+/=?^_`{|}~-";

    /// <summary>
    /// 计划中的生产配置不发邮箱验证码,格式校验是注册邮箱的唯一闸门,所以收得比「有个 @ 有个点」紧:
    /// 首尾空白忽略、地址内部禁空白/控制字符；本地部分限常见字符、域名是合法的点分标签、顶级域至少两个字母。
    /// 刻意不做完整 RFC 校验——带引号的本地部分和国际化域名极罕见,误杀成本高于收益;
    /// 客户端 Core 的 EmailFormat 镜像同一规则,两边用同一组用例钉住。
    /// </summary>
    public static string? ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "email_required";
        }

        var trimmed = email.Trim();
        if (trimmed.Length > MaxEmailLength)
        {
            return "email_too_long";
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.IndexOf('@', at + 1) >= 0)
        {
            return "email_invalid";
        }

        return IsLocalPart(trimmed[..at]) && IsDomain(trimmed[(at + 1)..])
            ? null
            : "email_invalid";
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

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "password_required";
        }

        if (password.Length < MinPasswordLength)
        {
            return "password_too_short";
        }

        if (password.Length > MaxPasswordLength)
        {
            return "password_too_long";
        }

        return null;
    }
}
