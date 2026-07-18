using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ProcLens.Tests;

public sealed class RecommendationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "proclens-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void UpsertRefreshesEvidenceButPreservesUserState()
    {
        var created = DateTimeOffset.UtcNow;
        using var store = new RecommendationStore(_directory, _jsonOptions);
        store.Upsert(CreateRecommendation("rec-1", created));
        Assert.True(store.MarkNeeded(new RecommendationDecision { RecommendationId = "rec-1", Decision = RecommendationDecisionKind.Needed, DecidedAtUtc = created.AddMinutes(1) }));

        store.Upsert(CreateRecommendation("rec-1", created, evidenceDetail: "refreshed"));
        var found = Assert.IsType<RecommendationRecord>(store.FindById("rec-1"));
        Assert.Equal(RecommendationState.Needed, found.State);
        Assert.Equal("refreshed", found.Evidence.Single().Detail);
        Assert.Contains(store.ListCurrent(created), recommendation => recommendation.Id == "rec-1" && recommendation.State == RecommendationState.Needed);
    }

    [Fact]
    public void CurrentListHonorsExpiryDismissalAndSnooze()
    {
        var now = DateTimeOffset.UtcNow;
        using var store = new RecommendationStore(_directory, _jsonOptions);
        store.Upsert(CreateRecommendation("active", now));
        store.Upsert(CreateRecommendation("expired", now.AddHours(-2), expiresAt: now.AddHours(-1)));
        store.Upsert(CreateRecommendation("dismissed", now));
        store.Upsert(CreateRecommendation("snoozed", now));
        Assert.True(store.RecordFeedback(new RecommendationDecision { RecommendationId = "dismissed", Decision = RecommendationDecisionKind.Dismiss, DecidedAtUtc = now }));
        Assert.True(store.Snooze(new RecommendationDecision { RecommendationId = "snoozed", Decision = RecommendationDecisionKind.Snooze, DecidedAtUtc = now, SnoozedUntilUtc = now.AddMinutes(10) }));

        Assert.Equal(1, store.Expire(now));
        Assert.Equal(["active"], store.ListCurrent(now).Select(x => x.Id));
        var currentAfterSnooze = store.ListCurrent(now.AddMinutes(11));
        Assert.Equal(["active", "snoozed"], currentAfterSnooze.Select(x => x.Id).Order());
        Assert.Equal(RecommendationState.Active, currentAfterSnooze.Single(recommendation => recommendation.Id == "snoozed").State);
    }

    [Fact]
    public void FeedbackAndActionAreAuditable()
    {
        var now = DateTimeOffset.UtcNow;
        using var store = new RecommendationStore(_directory, _jsonOptions);
        store.Upsert(CreateRecommendation("rec-1", now));
        Assert.True(store.RecordFeedback(new RecommendationDecision { RecommendationId = "rec-1", Decision = RecommendationDecisionKind.Dismiss, DecidedAtUtc = now }));
        var measuredOutcome = new ExpectedImpact { PrivateMemoryMb = 96.5, SustainedCpuPct = 1.25 };
        Assert.True(store.RecordAction(new RecommendationActionResult { RecommendationId = "rec-1", Result = ActionResultKind.Succeeded, CompletedAtUtc = now.AddMinutes(1), DetailCode = "closed" }, measuredOutcome));

        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM recommendation_feedback WHERE recommendation_id = $id;";
        command.Parameters.AddWithValue("$id", "rec-1");
        Assert.Equal(2L, Assert.IsType<long>(command.ExecuteScalar()));
        command.Parameters.Clear();
        command.CommandText = "SELECT measured_outcome_json FROM recommendations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", "rec-1");
        var persistedOutcome = JsonSerializer.Deserialize<ExpectedImpact>(Assert.IsType<string>(command.ExecuteScalar()), _jsonOptions);
        Assert.Equal(measuredOutcome, persistedOutcome);
        Assert.Equal(RecommendationState.Acted, store.FindById("rec-1")!.State);
    }

    [Fact]
    public async Task SupportsConcurrentReadersAndWriters()
    {
        var now = DateTimeOffset.UtcNow;
        using var writer = new RecommendationStore(_directory, _jsonOptions);
        writer.Upsert(CreateRecommendation("rec-1", now));
        var failures = new List<Exception>();
        var gate = new object();

        var readers = Enumerable.Range(0, 8).Select(async readerNumber =>
        {
            try
            {
                using var reader = new RecommendationStore(_directory, _jsonOptions);
                for (var index = 0; index < 20; index++)
                {
                    reader.ListCurrent(now);
                    await Task.Yield();
                }
            }
            catch (Exception exception) { lock (gate) failures.Add(exception); }
        });
        var writes = Enumerable.Range(0, 20).Select(async index =>
        {
            try
            {
                writer.Upsert(CreateRecommendation("rec-1", now, evidenceDetail: $"pass-{index}"));
                await Task.Yield();
            }
            catch (Exception exception) { lock (gate) failures.Add(exception); }
        });

        await Task.WhenAll(readers.Concat(writes));
        Assert.Empty(failures);
        Assert.NotNull(writer.FindById("rec-1"));
    }

    [Fact]
    public void MalformedPersistedJsonDoesNotCrashReads()
    {
        var now = DateTimeOffset.UtcNow;
        using var store = new RecommendationStore(_directory, _jsonOptions);
        store.Upsert(CreateRecommendation("bad", now));
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE recommendations SET record_json = '{not-json' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", "bad");
            command.ExecuteNonQuery();
        }

        Assert.Null(store.FindById("bad"));
        Assert.Empty(store.ListCurrent(now));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static RecommendationRecord CreateRecommendation(string id, DateTimeOffset createdAt, DateTimeOffset? expiresAt = null, string evidenceDetail = "idle") => new()
    {
        Id = id,
        TargetGroup = new RecommendationTargetGroup
        {
            Label = "Example app",
            Root = new RecommendationMember(42, 123),
            Members = [new RecommendationMember(42, 123)],
            Resolved = true
        },
        Action = RecommendationAction.CloseGracefully,
        Provenance = new RecommendationProvenance { Source = RecommendationSource.Core },
        Confidence = new RecommendationConfidence { Kind = ConfidenceKind.High, Pct = 80 },
        Risk = ActionRisk.Low,
        ExpectedImpact = new ExpectedImpact { PrivateMemoryMb = 128, SustainedCpuPct = 2.5 },
        Evidence = [new RecommendationEvidence { Code = "idle", Detail = evidenceDetail }],
        CreatedAtUtc = createdAt,
        ExpiresAtUtc = expiresAt ?? createdAt.AddHours(1)
    };
}
