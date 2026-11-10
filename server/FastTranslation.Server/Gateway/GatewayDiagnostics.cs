using System.Collections.Concurrent;
using FastTranslation.Server.Storage;

namespace FastTranslation.Server.Gateway;

/// <summary>
/// 网关/语音的进程内健康计数器。重启清零——它回答的是「最近有没有在失败」，
/// 不是持久账本；持久用量仍在 usage_events。请求与失败分开计数，失败原因
/// 使用受限的 error token（rate_limited / budget_exhausted / upstream_xxx）。
/// </summary>
public sealed class GatewayDiagnostics(ServerDatabase database)
{
    private readonly Counters _gateway = new();
    private readonly Counters _speech = new();

    public long StartedAtUnix { get; } = database.NowUnix;

    public void RecordGatewayRequest() => Interlocked.Increment(ref _gateway.Requests);

    public void RecordGatewayFailure(string reason) => RecordFailure(_gateway, reason);

    public void RecordSpeechRequest() => Interlocked.Increment(ref _speech.Requests);

    public void RecordSpeechFailure(string reason) => RecordFailure(_speech, reason);

    public GatewayDiagnosticsSnapshot SnapshotGateway() => _gateway.Snapshot();

    public GatewayDiagnosticsSnapshot SnapshotSpeech() => _speech.Snapshot();

    private void RecordFailure(Counters counters, string reason)
    {
        Interlocked.Increment(ref counters.Failures);
        counters.ByReason.AddOrUpdate(reason, 1, static (_, value) => value + 1);
        Volatile.Write(ref counters.LastFailureAt, database.NowUnix);
        Volatile.Write(ref counters.LastFailureReason, reason);
    }

    private sealed class Counters
    {
        public long Requests;
        public long Failures;
        public long LastFailureAt;
        public string? LastFailureReason;
        public readonly ConcurrentDictionary<string, long> ByReason = new(StringComparer.Ordinal);

        public GatewayDiagnosticsSnapshot Snapshot() => new(
            Interlocked.Read(ref Requests),
            Interlocked.Read(ref Failures),
            new Dictionary<string, long>(ByReason, StringComparer.Ordinal),
            Interlocked.Read(ref LastFailureAt),
            Volatile.Read(ref LastFailureReason));
    }
}

/// <summary>一次快照；<c>LastFailureAtUnix</c> 为 0 表示还没失败过。</summary>
public sealed record GatewayDiagnosticsSnapshot(
    long Requests,
    long Failures,
    IReadOnlyDictionary<string, long> ByReason,
    long LastFailureAtUnix,
    string? LastFailureReason);