namespace FastTranslation.Core;

/// <summary>
/// 额度或余额已用尽（v75）。与其它翻译失败不同，它意味着「再试一次也一样」。
/// <para>
/// 为什么要单独一个异常类型：文档翻译里一段失败会被重试 3 次再标「未翻译」，
/// 而额度耗尽时这么做的结果是 658 段 × 3 次请求全打在同一个已经枯竭的额度上——
/// 用户多等几分钟，看到的仍是「有 400 多段没译成」，配额窗口还被继续占着。
/// </para>
/// </summary>
public sealed class QuotaExhaustedException(string message) : Exception(message);
