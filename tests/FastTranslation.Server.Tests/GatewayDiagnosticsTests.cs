using FastTranslation.Server.Gateway;
using FastTranslation.Server.Storage;
using Xunit;

namespace FastTranslation.Server.Tests;

public class GatewayDiagnosticsTests
{
    [Fact]
    public void Counts_requests_and_failures_by_reason()
    {
        var path = Path.Combine(Path.GetTempPath(), "ft-diagnostics-tests", Guid.NewGuid().ToString("N") + ".db");
        var database = new ServerDatabase(path);
        try
        {
            var diagnostics = new GatewayDiagnostics(database);

            diagnostics.RecordGatewayRequest();
            diagnostics.RecordGatewayRequest();
            diagnostics.RecordGatewayFailure("rate_limited");
            diagnostics.RecordSpeechRequest();
            diagnostics.RecordSpeechFailure("speech_busy");

            var gateway = diagnostics.SnapshotGateway();
            Assert.Equal(2, gateway.Requests);
            Assert.Equal(1, gateway.Failures);
            Assert.Equal(1, gateway.ByReason["rate_limited"]);
            Assert.Equal("rate_limited", gateway.LastFailureReason);
            Assert.True(gateway.LastFailureAtUnix > 0);

            var speech = diagnostics.SnapshotSpeech();
            Assert.Equal(1, speech.Requests);
            Assert.Equal(1, speech.Failures);
            Assert.Equal(1, speech.ByReason["speech_busy"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                    // 尽力而为地清理。
                }
            }
        }
    }
}