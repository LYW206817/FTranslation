using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FastTranslation.Core.Documents;

/// <summary>
/// 把文档里的段翻译成目标语言：负责单元划分、并发、短段合并、占位符保护与失败降级。
/// <para>
/// 不做整份文档一次请求：qwen-mt 没有批量接口也没有「按编号对齐」的承诺，
/// 一份 200 页文档塞进一个请求既会超 <c>MaxInputLength</c>，也没有任何机制保证
/// 返回的是同样段数——那样一旦对不齐就没有可用的降级粒度。
/// </para>
/// </summary>
public static class SegmentTranslator
{
    /// <summary>同时在飞的请求数。取 4 是因为上游 RPM 60 与网关 RPM 40，再多只会在限流上排队。</summary>
    private const int MaxConcurrency = 4;

    /// <summary>单段/单单元的重试次数（含首次共 3 次）。</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// v75：合并上限从「5 段 / 单段 80 字符 / 整包 400 字符」放到「20 段 / 单段 2000 / 整包 4000」。
    /// <para>
    /// 旧值让一份 100 页、658 段的文档发出约 658 个请求（实测 180 RPM），而 qwen-mt-flash 的
    /// 上限是 60 RPM，于是大批段落撞 429、3 次重试在毫秒内耗光，最终 417 段没译成。
    /// 合并到接近 <see cref="TranslationRequest.MaxInputLength"/>（5000）后请求数降一个数量级。
    /// 留 1000 字符余量是因为占位符保护会把受保护片段换成哨兵，长度会有小幅膨胀。
    /// 但合并只压请求数，压不了上游按字符计的配额——100 页 16 万字符的物理下限仍是 5–7 分钟，
    /// 所以原验收线「100 页 ≤3 分钟」在 v75 已作废。
    /// </para>
    /// </summary>
    private const int MergeMaxSegments = 20;

    private const int MergeMaxSegmentChars = 2_000;

    private const int MergeMaxTotalChars = 4_000;

    /// <summary>
    /// 每分钟发出的请求数上限。取 30 是两侧限流的公共安全区：qwen-mt-flash 上游 60 RPM、
    /// 自建网关 40 RPM 且按用户 id 分桶，压到 30 才留得出重试的余量。
    /// </summary>
    private const int DefaultRequestsPerMinute = 30;

    /// <summary>
    /// v75：每分钟送上去的字符数上限，24,000。
    /// <para>
    /// 只按请求数节流挡不住限流——这是 v75 实测出来的。同样 30 RPM，一段 4,000 字符的请求
    /// 等于 120,000 字符/分钟，而 qwen-mt-flash 的配额是每分钟 35,000 token（中文约 1 字符
    /// 1 token）。实测：4,000 字符每 4 秒一次连发 12 次只有 3 次成功，改成每 6 秒一次则 10 次
    /// 全成；200 字符每 1 秒一次 20 次也全成。可见约束是字符量而不是请求次数。
    /// 留 30% 余量取 24,000，剩下的留给重试与同账号的划词翻译。
    /// </para>
    /// </summary>
    private const int DefaultCharsPerMinute = 24_000;

    /// <summary>
    /// 真实限速值，只在测试里调高。放开它是因为按 30 RPM 算一个请求要等 2 秒，
    /// 而单测里一个 5 次请求的用例就会实等 8 秒——测试要验的是「有没有按速率放行」，
    /// 不是「等够没有」。生产路径一律走 <see cref="DefaultRequestsPerMinute"/>。
    /// </summary>
    internal static int RequestsPerMinute { get; set; } = DefaultRequestsPerMinute;

    /// <summary>
    /// 一次失败之后让路多久（指数退避的基数）。测试里置零，否则重试用例要真等上几秒。
    /// </summary>
    internal static TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>字符量限速，与 <see cref="RequestsPerMinute"/> 同理只在测试里调高。</summary>
    internal static int CharsPerMinute { get; set; } = DefaultCharsPerMinute;

    /// <summary>
    /// 翻译全部待译段，返回新的段列表（已译的段带 <see cref="SegmentStatus.Done"/> 与译文）。
    /// 单段失败不会抛异常，只会被标成 <see cref="SegmentStatus.Failed"/>，
    /// 让整份文档能落地并在界面上列出「哪几段没译成」——把 5000 段里的一段失败升级成整份失败，
    /// 等于几乎每份大文档都失败。
    /// </summary>
    public static async Task<IReadOnlyList<DocumentSegment>> TranslateAsync(
        IReadOnlyList<DocumentSegment> segments,
        Func<string, CancellationToken, Task<string>> translate,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<DocumentSegment>, Task>? onUnitCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(translate);

        var pending = segments.Where(segment => segment.NeedsTranslation).ToList();
        if (pending.Count == 0) return segments;

        var units = BuildUnits(pending);
        var pacer = new RequestPacer(RequestsPerMinute, CharsPerMinute, RetryBackoff);
        using var gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var completed = 0;

        // v75: 额度耗尽是整份作业的死刑，不是某一段的问题。记下第一个原因并用一个
        // 联动 token 把还在排队的单元叫停——否则每个单元还要先按 30 RPM 排队 2 秒才
        // 轮到它去撞同一堵墙，658 段的文档要白等一分多钟才看到错误。
        QuotaExhaustedException? quotaFailure = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = units.Select(async unit =>
        {
            await gate.WaitAsync(stop.Token).ConfigureAwait(false);
            IReadOnlyList<DocumentSegment> done;
            try
            {
                done = await TranslateUnitAsync(unit, translate, pacer, stop.Token).ConfigureAwait(false);
            }
            catch (QuotaExhaustedException ex)
            {
                Interlocked.CompareExchange(ref quotaFailure, ex, null);
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }

            // 进度按「已完成的段数」累计而不是按单元：一个合并单元译完可能一次带来 20 段，
            // 按单元计的话进度条最多只能走到 段数/单元数（短段合并时可能永远到不了 100%）。
            progress?.Report(Interlocked.Add(ref completed, done.Count));
            if (onUnitCompleted is not null)
            {
                // 断点回调：让调用方在取消/额度中断之前把已译好的单元落盘。
                // 回调自己负责吞掉写盘失败，这里不把它升级成整份作业的失败。
                await onUnitCompleted(done).ConfigureAwait(false);
            }
            return done;
        }).ToList();

        IReadOnlyList<DocumentSegment>[] perUnit;
        try
        {
            perUnit = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception) when (quotaFailure is not null)
        {
            // 其余单元是被我们自己取消的，用户只需要看到真正的原因。
            throw quotaFailure;
        }

        var translated = perUnit
            .SelectMany(unit => unit)
            .ToDictionary(segment => segment.Index);
        return [.. segments.Select(segment => translated.TryGetValue(segment.Index, out var done) ? done : segment)];
    }

    private static async Task<IReadOnlyList<DocumentSegment>> TranslateUnitAsync(
        IReadOnlyList<DocumentSegment> unit,
        Func<string, CancellationToken, Task<string>> translate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        if (unit.Count == 1) return [await TranslateSingleAsync(unit[0], translate, pacer, cancellationToken).ConfigureAwait(false)];
        return await TranslateMergedAsync(unit, translate, pacer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DocumentSegment> TranslateSingleAsync(
        DocumentSegment segment,
        Func<string, CancellationToken, Task<string>> translate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        var target = await TryTranslateAsync(segment.Source, translate, pacer, cancellationToken).ConfigureAwait(false);
        return target is null
            ? segment with { Target = null, Status = SegmentStatus.Failed }
            : segment with { Target = target, Status = SegmentStatus.Done };
    }

    /// <summary>
    /// 短段合并：连续几句一两行的段落，一段一请求会把固定开销（每请求 45 token 的包装）
    /// 烧到比正文还贵，而且用户看到的是「一堆一句话的请求」把配额吃掉。
    /// </summary>
    private static async Task<IReadOnlyList<DocumentSegment>> TranslateMergedAsync(
        IReadOnlyList<DocumentSegment> unit,
        Func<string, CancellationToken, Task<string>> translate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        var target = await TryTranslateMergedAsync(unit, translate, pacer, cancellationToken).ConfigureAwait(false);
        if (target is not null) return target;

        // 降级：整包 3 次都没成，把单元对半切开各自再来一轮。
        // 不直接退到「一段一请求」：20 段的单元一次退化成 20 个请求，等于把刚压下去的
        // 请求量又还给限流；二分能让「只有一小段有毒」的单元只多花几次请求就绕过去。
        // 单段单元落到 TranslateSingleAsync，它自己还有 3 次机会。
        if (unit.Count == 1) return [await TranslateSingleAsync(unit[0], translate, pacer, cancellationToken).ConfigureAwait(false)];

        var half = unit.Count / 2;
        var left = await TranslateMergedAsync([.. unit.Take(half)], translate, pacer, cancellationToken).ConfigureAwait(false);
        var right = await TranslateMergedAsync([.. unit.Skip(half)], translate, pacer, cancellationToken).ConfigureAwait(false);
        // 进度与断点都按整个单元报一次（调用方拿到的是合并后的 done），
        // 二分的两半不再各自上报。
        return [.. left, .. right];
    }

    /// <summary>试一次合并请求（含 3 次重试），成功返回整单元的译文，失败返回 null 交给调用方降级。</summary>
    private static async Task<IReadOnlyList<DocumentSegment>?> TryTranslateMergedAsync(
        IReadOnlyList<DocumentSegment> unit,
        Func<string, CancellationToken, Task<string>> translate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        if (unit.Count == 1) return null;

        // 每段各自 Protect：哨兵编号在段内从 1 起，回切后按段还原就不会串号。
        var protectedTexts = new string[unit.Count];
        var originals = new IReadOnlyList<string>[unit.Count];
        for (var i = 0; i < unit.Count; i++) protectedTexts[i] = PlaceholderProtector.Protect(unit[i].Source, out originals[i]);
        var joined = string.Join("\n\n", protectedTexts);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string translated;
            try
            {
                await pacer.WaitAsync(joined.Length, cancellationToken).ConfigureAwait(false);
                translated = await translate(joined, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (QuotaExhaustedException)
            {
                // 额度耗尽重试一次也是同样的结果，直接放弃整份作业交给上层报错。
                throw;
            }
            catch (Exception)
            {
                // 上游任何失败都只影响这个单元：重试，用尽后交给调用方降级。
                // v75: 重试之间必须退避——100 页实测里 63% 的段落失败是连发撞上同一个限流窗口，
                // 不退避的 3 次重试在几十毫秒内就烧完了，等于只有 1 次机会。
                await pacer.WaitAfterFailureAsync(attempt, MaxAttempts, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 回切靠段落分隔符，段数不符说明模型把两段并成了一段或多切了——整包重试。
            var pieces = translated.Split("\n\n");
            if (pieces.Length != unit.Count) continue;
            var filled = Fill(unit, pieces, originals);
            if (filled is not null) return filled;
        }

        return null;
    }

    /// <summary>
    /// 把回切出来的译文逐段还原占位符；任何一段的哨兵对不上就整包作废（返回 null）。
    /// 为什么不接受「部分成功」：一处哨兵没回来，说明模型整体在改写受保护片段，
    /// 其它段的还原也就不可信了。
    /// </summary>
    private static List<DocumentSegment>? Fill(
        IReadOnlyList<DocumentSegment> unit,
        IReadOnlyList<string> pieces,
        IReadOnlyList<string>[] originals)
    {
        var filled = new List<DocumentSegment>(unit.Count);
        for (var i = 0; i < unit.Count; i++)
        {
            var restored = PlaceholderProtector.Restore(pieces[i], originals[i], out var ok);
            if (!ok) return null;
            filled.Add(unit[i] with { Target = restored, Status = SegmentStatus.Done });
        }

        return filled;
    }

    private static async Task<string?> TryTranslateAsync(
        string source,
        Func<string, CancellationToken, Task<string>> translate,
        RequestPacer pacer,
        CancellationToken cancellationToken)
    {
        var protectedText = PlaceholderProtector.Protect(source, out var originals);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string translated;
            try
            {
                await pacer.WaitAsync(protectedText.Length, cancellationToken).ConfigureAwait(false);
                translated = await translate(protectedText, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (QuotaExhaustedException)
            {
                // 同上：额度耗尽是「换个 Key」而不是「稍后再试」，逐段重试只会把用户的等待拉长。
                throw;
            }
            catch (Exception)
            {
                // Core 层不引日志框架：上游异常在这里一律等价于「这次没译成」，靠重试与 Failed 标记兜住。
                // 退避理由同 TranslateMergedAsync：连发重试只会把 3 次机会撞进同一个限流窗口。
                await pacer.WaitAfterFailureAsync(attempt, MaxAttempts, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var restored = PlaceholderProtector.Restore(translated, originals, out var ok);
            if (ok) return restored;
        }

        return null;
    }

    private static IReadOnlyList<IReadOnlyList<DocumentSegment>> BuildUnits(List<DocumentSegment> pending)
    {
        var units = new List<IReadOnlyList<DocumentSegment>>();
        var current = new List<DocumentSegment>();
        foreach (var segment in pending)
        {
            if (current.Count > 0 && CanMerge(current, segment))
            {
                current.Add(segment);
                continue;
            }

            if (current.Count > 0) units.Add(current);
            current = [segment];
        }

        if (current.Count > 0) units.Add(current);
        return units;
    }

    private static bool CanMerge(List<DocumentSegment> current, DocumentSegment next)
    {
        var last = current[^1];
        if (current.Count >= MergeMaxSegments) return false;
        if (!string.Equals(PartOf(last.Path), PartOf(next.Path), StringComparison.Ordinal)) return false;
        // 中间夹着被跳过的段（Index 不连续）就不合并：回切只能按顺序对齐，缺一段就会整体错位。
        if (next.Index != last.Index + 1) return false;
        if (last.Source.Length >= MergeMaxSegmentChars || next.Source.Length >= MergeMaxSegmentChars) return false;
        return current.Sum(segment => segment.Source.Length) + next.Source.Length <= MergeMaxTotalChars;
    }

    private static string PartOf(string path)
    {
        var hash = path.LastIndexOf('#');
        return hash < 0 ? string.Empty : path[..hash];
    }
}

/// <summary>
/// 按固定间隔放行请求的节流器（v75）。
/// <para>
/// 加它的原因：v75 的 100 页实测里 658 段发出约 658 个请求、峰值 180 RPM，
/// 而 qwen-mt-flash 上游只有 60 RPM、自建网关 40 RPM。撞限流之后重试之间又没有退避，
/// 3 次在毫秒内打完，结果 417 段没译成——限流不是靠重试能穿的墙，只能从源头压住速率。
/// </para>
/// <para>
/// 实现是「上一个请求发出的时刻 + 最小间隔」而不是令牌桶：文档翻译是有限批量的活，
/// 均匀放行比允许突发更省心，也省掉一个会持续跑动的补充计时器。
/// </para>
/// </summary>
internal sealed class RequestPacer
{
    private readonly long _requestIntervalTicks;
    private readonly int _charsPerMinute;
    private readonly TimeSpan _backoffUnit;
    private readonly SemaphoreSlim _slots;
    private long _nextTicks;

    public RequestPacer(int requestsPerMinute, int charsPerMinute)
        : this(requestsPerMinute, charsPerMinute, TimeSpan.FromSeconds(1))
    {
    }

    internal RequestPacer(int requestsPerMinute, int charsPerMinute, TimeSpan backoffUnit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(charsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(backoffUnit, TimeSpan.Zero);
        _requestIntervalTicks = TimeSpan.TicksPerMinute / requestsPerMinute;
        _charsPerMinute = charsPerMinute;
        _backoffUnit = backoffUnit;
        // 初始时刻取「现在」：第一批请求不必先等一个间隔。
        _nextTicks = DateTime.UtcNow.Ticks;
        // 每一批并发（MaxConcurrency = 4）同时进来，靠这把锁排队，谁先到谁先占下一个时间片。
        _slots = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// 等到本笔请求可以发出为止再返回，<paramref name="chars"/> 是这次要送上去的字符数；
    /// 取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task WaitAsync(int chars, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chars);

        long waitTicks;
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow.Ticks;
            var start = Math.Max(now, _nextTicks);
            // 两个约束都要满足，取更长的那个时间片：请求数约束是固定间隔，
            // 字符量约束按这笔的字数折算（4,000 字符在 24,000/分钟下占 10 秒）。
            var charTicks = chars * TimeSpan.TicksPerMinute / _charsPerMinute;
            _nextTicks = start + Math.Max(_requestIntervalTicks, charTicks);
            waitTicks = start - now;
        }
        finally
        {
            _slots.Release();
        }

        if (waitTicks > 0)
        {
            await Task.Delay(TimeSpan.FromTicks(waitTicks), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 第 <paramref name="attempt"/> 次失败之后让路：1 秒、2 秒、4 秒……逐次翻倍，
    /// 最后一次不再等（等完就直接放弃了，没必要）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="WaitAsync"/> 分开：那个管「按规定速率发」，这个管「撞了墙先退一步」。
    /// 限流窗口里的额度是随时间回补的，退避让回补有机会发生。
    /// </remarks>
    public Task WaitAfterFailureAsync(int attempt, int maxAttempts, CancellationToken cancellationToken)
    {
        if (attempt + 1 >= maxAttempts) return Task.CompletedTask;
        var delay = TimeSpan.FromTicks(_backoffUnit.Ticks << Math.Min(attempt, 4));
        return delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
    }
}

/// <summary>
/// 把不该被翻译的东西（代码、URL、变量占位、HTML 标签）换成哨兵 <c>⟦n⟧</c>，译完再换回来。
/// <para>
/// 用 <c>⟦n⟧</c> 而不是 <c>[[n]]</c>：后者由 ASCII 字母数字与常见标点组成，模型很容易
/// 顺手把 <c>[[1]]</c> 改写成 <c>[1]</c> 或按目标语言习惯调整；U+27E6/U+27E7 不在任何
/// 自然语言的正文标点里，被原样保留的概率高得多。即便这样也没有官方保证，
/// 所以回填时必须数哨兵——少一个就整段作废，绝不让丢掉代码块的译文写回文档。
/// </para>
/// </summary>
internal static class PlaceholderProtector
{
    private const char SentinelOpen = '\u27E6';

    private const char SentinelClose = '\u27E7';

    private static readonly Regex PlaceholderPattern = new(
        // 顺序即优先级：围栏代码块里可以含有 URL 与行内 code，必须先整块吞掉。
        @"(?<fence>```[\s\S]*?```)"
        + @"|(?<code>`[^`\r\n]+`)"
        + @"|(?<url>https?://[^\s<>()\[\]]+)"
        + @"|(?<mail>[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})"
        + @"|(?<var>\{\d+\}|%[sd])"
        + @"|(?<tag></?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]{0,79})?/?>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SentinelPattern = new(
        "\u27E6(\\d+)\u27E7",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>把源串里的受保护片段换成哨兵，<paramref name="originals"/> 按编号顺序给出原文。</summary>
    internal static string Protect(string source, out IReadOnlyList<string> originals)
    {
        var captured = new List<string>();
        var protectedText = PlaceholderPattern.Replace(source, match =>
        {
            captured.Add(match.Value);
            return SentinelOpen + captured.Count.ToString(CultureInfo.InvariantCulture) + SentinelClose;
        });
        originals = captured;
        return protectedText;
    }

    /// <summary>
    /// 按编号换回原文。任何一个编号缺失、重复或越界都返回 <paramref name="ok"/> = false，
    /// 调用方必须回退原文（宁可这段显示「未翻译」，也不要把缺了代码块的译文写进用户的文档）。
    /// </summary>
    internal static string Restore(string translated, IReadOnlyList<string> originals, out bool ok)
    {
        ArgumentNullException.ThrowIfNull(translated);
        ArgumentNullException.ThrowIfNull(originals);
        // 本来就没有受保护片段：译文原样收下。这里若返回 false，几乎所有普通段落都会
        // 被判定成「哨兵对不上」而整段作废——那会把占位符机制变成一份什么都译不出来的文档。
        if (originals.Count == 0)
        {
            ok = true;
            return translated;
        }

        ok = false;

        var seen = new HashSet<int>();
        var builder = new StringBuilder(translated.Length);
        var position = 0;
        foreach (Match match in SentinelPattern.Matches(translated))
        {
            var number = int.Parse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (number < 1 || number > originals.Count || !seen.Add(number)) return translated;
            builder.Append(translated, position, match.Index - position);
            builder.Append(originals[number - 1]);
            position = match.Index + match.Length;
        }

        // 编号集合必须与原文完全一致：模型可能漏掉一个，也可能凭空多写一个哨兵。
        if (seen.Count != originals.Count) return translated;
        builder.Append(translated, position, translated.Length - position);
        ok = true;
        return builder.ToString();
    }
}
