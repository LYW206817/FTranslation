using System.Collections.Concurrent;
using FastTranslation.Core.Lexicon;

namespace FastTranslation.Core;

/// <summary>
/// 协调 provider 调用：LRU 缓存、单飞取消（新请求会取消上一个在途请求）、
/// 空输入直接短路。
/// </summary>
public sealed class TranslationService
{
    private const int CacheCapacity = 500;

    private readonly ITranslationProvider _provider;
    private readonly ILexicon? _lexicon;
    private readonly ConcurrentDictionary<string, TranslationResult> _cache = new();
    private readonly object _cacheEvictionLock = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private CancellationTokenSource? _inFlight;

    /// <param name="lexicon">本地词库；缺失时所有输入都走在线翻译。</param>
    public TranslationService(ITranslationProvider provider, ILexicon? lexicon = null)
    {
        _provider = provider;
        _lexicon = lexicon;
    }

    public Task<TranslationResult> TranslateAsync(string text, AppSettings settings, CancellationToken external = default)
    {
        var normalized = TextNormalization.Normalize(text);
        if (!TextNormalization.IsMeaningful(normalized))
        {
            return Task.FromResult(new TranslationResult(text, string.Empty, settings.SourceLanguage,
                settings.TargetLanguage, settings.Model, 0, false)
            { Error = new TranslationError(TranslationErrorCode.InvalidResponse, "没有可翻译的文本") });
        }
        if (normalized.Length > TranslationRequest.MaxInputLength)
        {
            return Task.FromResult(new TranslationResult(text, string.Empty, settings.SourceLanguage,
                settings.TargetLanguage, settings.Model, 0, false)
            { Error = TranslationError.ContentTooLong(normalized.Length, TranslationRequest.MaxInputLength) });
        }

        var (source, target) = LanguagePolicy.Resolve(settings.SourceLanguage, settings.TargetLanguage, normalized);
        var request = new TranslationRequest(normalized, LanguageCatalog.ToApiName(source), LanguageCatalog.ToApiName(target), settings.Model);

        var cacheKey = $"{request.Model}|{request.SourceLanguage}>{request.TargetLanguage}|{request.Text}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            TouchCacheKey(cacheKey);
            return Task.FromResult(cached with { FromCache = true });
        }

        if (external.IsCancellationRequested)
        {
            return Task.FromResult(new TranslationResult(text, string.Empty, source, target, settings.Model, 0, false)
            { Error = TranslationError.Cancelled() });
        }

        // 单个英文词先查本地词库：词性与多义项是 MT 模型给不出的，本地命中还不需要
        // 网络、不消耗额度。只有「英→中」才成立——ECDICT 是英汉词典，反向查不到。
        if (_lexicon is not null
            && target == LanguageCatalog.ChineseCode
            && source == LanguageCatalog.EnglishCode
            && WordShape.TryNormalize(normalized) is { } headword)
        {
            var lookupStarted = Environment.TickCount64;
            if (_lexicon.TryLookup(headword, out var dictionary))
            {
                var local = new TranslationResult(
                    normalized, dictionary.Summary, source, target,
                    Lexicon.DictionaryEntry.SourceName, Environment.TickCount64 - lookupStarted, FromCache: false)
                {
                    Dictionary = dictionary,
                };
                // 词库命中不建立新的 in-flight，但上一笔在线请求仍要按「最后一次输入赢」
                // 取消；否则它晚到的译文会盖掉这张词典卡。
                var superseded = Interlocked.Exchange(ref _inFlight, null);
                try { superseded?.Cancel(); } catch { }
                _cache[cacheKey] = local;
                EvictIfNeeded(cacheKey);
                return Task.FromResult(local);
            }
        }

        var previous = Interlocked.Exchange(ref _inFlight, new CancellationTokenSource());
        // 只取消、不 Dispose——在这里释放会和取消回调竞争；由所属请求在完成时
        // 于自己的 finally 块里释放它自己的 CTS。
        try { previous?.Cancel(); } catch { }
        var cts = _inFlight!;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, external);
        var token = linked.Token;

        return RunAsync(request, cacheKey, token, linked, cts);
    }

    private async Task<TranslationResult> RunAsync(
        TranslationRequest request, string cacheKey, CancellationToken token,
        CancellationTokenSource linked, CancellationTokenSource inFlight)
    {
        try
        {
            var result = await _provider.TranslateAsync(request, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                // 已被更新的请求取代：绝不要把过期结果抛给上层
                return result with { Error = TranslationError.Cancelled() };
            }
            if (result.Succeeded)
            {
                _cache[cacheKey] = result;
                EvictIfNeeded(cacheKey);
            }
            return result;
        }
        finally
        {
            linked.Dispose();
            if (ReferenceEquals(_inFlight, inFlight))
            {
                Interlocked.CompareExchange(ref _inFlight, null, inFlight);
                inFlight.Dispose();
            }
            else
            {
                inFlight.Dispose();
            }
        }
    }

    private void TouchCacheKey(string key)
    {
        lock (_cacheEvictionLock)
        {
            _cacheOrder.Remove(key);
            _cacheOrder.AddLast(key);
        }
    }

    private void EvictIfNeeded(string newestKey)
    {
        lock (_cacheEvictionLock)
        {
            _cacheOrder.AddLast(newestKey);
            while (_cacheOrder.Count > CacheCapacity)
            {
                var oldest = _cacheOrder.First!.Value;
                _cacheOrder.RemoveFirst();
                _cache.TryRemove(oldest, out _);
            }
        }
    }
}
