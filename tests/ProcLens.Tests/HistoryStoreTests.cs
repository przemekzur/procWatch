using System.Text.Json;

namespace ProcLens.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "proclens-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesAndReadsRecordsByTime()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var now = DateTime.UtcNow;
        using (var store = new HistoryStore(_directory, options, 14))
        {
            store.Write(new { type = "system_sample", timeUtc = now, runId = "test", cpuPercent = 12.5 });
            store.Flush();
        }

        var lines = HistoryStore.ReadSince(_directory, now.AddMinutes(-1)).ToList();
        Assert.Single(lines);
        Assert.Contains("system_sample", lines[0]);
    }

    [Fact]
    public void CleanupRemovesExpiredRecords()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using (var store = new HistoryStore(_directory, options, 1))
        {
            store.Write(new { type = "system_sample", timeUtc = DateTime.UtcNow.AddDays(-5), runId = "old" });
            store.Cleanup();
        }

        Assert.Empty(HistoryStore.ReadSince(_directory, DateTime.MinValue));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
