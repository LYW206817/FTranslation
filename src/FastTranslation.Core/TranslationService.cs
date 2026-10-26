using System.Collections.Concurrent;

namespace FastTranslation.Core;

/// <summary>
/// Coordinates provider calls: LRU cache, single-flight cancellation (a new request
/// cancels the previous in-flight one), short-circuits empty input.
/// </summary>
public sealed class TranslationService
{
    private const int CacheCapacity = 500;

    private readonly ITranslationProvider _provider;
    private readonly ConcurrentDictionary<string, TranslationResult> _cache = new();
    private readonly object _cacheEvictionLock = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private CancellationTokenSource? _inFlight;

    public TranslationService(ITranslationProvider provider) => _provider = provider;

    public event Action<TranslationResult>? RequestCompleted;

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

        var previous = Interlocked.Exchange(ref _inFlight, new CancellationTokenSource());
        // Cancel only — disposing here races cancellation callbacks; the owning request's
        // finally block disposes its own CTS when it completes.
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
                // superseded by a newer request: never surface the stale result
                RequestCompleted?.Invoke(result);
                return result with { Error = TranslationError.Cancelled() };
            }
            if (result.Succeeded)
            {
                _cache[cacheKey] = result;
                EvictIfNeeded(cacheKey);
            }
            RequestCompleted?.Invoke(result);
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
