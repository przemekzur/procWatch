using System.Text;
using System.Text.Json;

namespace ProcLens.Tests;

public sealed class AgentCliTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "proclens-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SnapshotIsVersionedDeterministicAndPrivacySafe()
    {
        var builder = new AgentSnapshotBuilder(_json);
        var observations = new[] { Observation(42), Observation(43, ownerPid: 42) };

        var first = builder.Build(Now, 60, observations, []);
        var second = builder.Build(Now.AddSeconds(2), 60, observations.Reverse().ToArray(), []);
        var text = JsonSerializer.Serialize(first, _json);

        Assert.Equal(RecommendationSchema.CurrentVersion, first.SchemaVersion);
        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
        Assert.Equal(64, first.SnapshotHash.Length);
        Assert.Equal(new[] { 42, 43 }, Assert.Single(first.Groups).Members.Select(member => member.Pid));
        Assert.True(first.Freshness.IsFresh);
        Assert.Equal(20, first.Coverage.SampleCount);
        Assert.Contains("\"processAgeSeconds\"", text);
        Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machine", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dashboardToken", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidAgentAdvisoryIsRecomputedCappedAndPersistable()
    {
        var builder = new AgentSnapshotBuilder(_json);
        var observations = new[] { Observation(50) };
        var snapshot = builder.Build(Now, 60, observations, []);
        var advisory = Advisory(snapshot, 50, confidencePct: 99);

        var accepted = Assert.Single(builder.ValidateAdvisory(advisory, snapshot, observations, Now));

        Assert.Equal(RecommendationSource.Agent, accepted.Provenance.Source);
        Assert.Equal("adv-safe-1", accepted.Provenance.AdvisoryId);
        Assert.Equal(RecommendationAction.Investigate, accepted.Action);
        Assert.Equal(RecommendationEngine.AgentOnlyCeilingPct, accepted.Confidence.Pct);
        Assert.Contains(accepted.Evidence, item => item.Code == "agent.advisoryImported");
        using var store = new RecommendationStore(_directory, _json);
        store.Upsert(accepted, snapshot.SnapshotHash);
        Assert.Equal(accepted.Id, Assert.Single(store.ListCurrent(Now)).Id);
    }

    [Fact]
    public void ImportRejectsWrongHashStaleAndChangedIdentity()
    {
        var builder = new AgentSnapshotBuilder(_json);
        var observations = new[] { Observation(60) };
        var snapshot = builder.Build(Now, 60, observations, []);
        var advisory = Advisory(snapshot, 60);

        Assert.Equal(4, Assert.Throws<AgentCliException>(() => builder.ValidateAdvisory(
            advisory with { SnapshotHash = new string('0', 64) }, snapshot, observations, Now)).ExitCode);
        Assert.Equal(4, Assert.Throws<AgentCliException>(() => builder.ValidateAdvisory(
            advisory with { CreatedAtUtc = Now.AddMinutes(-10), SnapshotGeneratedAtUtc = Now.AddMinutes(-10) }, snapshot, observations, Now)).ExitCode);
        Assert.Equal(5, Assert.Throws<AgentCliException>(() => builder.ValidateAdvisory(
            advisory, snapshot, [Observation(61)], Now)).ExitCode);
    }

    [Fact]
    public void ImportRejectsHardBlockedAndUnsupportedActions()
    {
        var builder = new AgentSnapshotBuilder(_json);
        var foreground = new[] { Observation(70) with { IsForeground = true } };
        var snapshot = builder.Build(Now, 60, foreground, []);

        Assert.Equal(5, Assert.Throws<AgentCliException>(() => builder.ValidateAdvisory(
            Advisory(snapshot, 70), snapshot, foreground, Now)).ExitCode);

        var safe = new[] { Observation(71) };
        var safeSnapshot = builder.Build(Now, 60, safe, []);
        Assert.Equal(5, Assert.Throws<AgentCliException>(() => builder.ValidateAdvisory(
            Advisory(safeSnapshot, 71) with
            {
                Recommendations = [Advisory(safeSnapshot, 71).Recommendations[0] with { Action = RecommendationAction.CloseGracefully }]
            }, safeSnapshot, safe, Now)).ExitCode);
    }

    [Fact]
    public void AdvisoryParsingRejectsMalformedOversizedAndUnknownProperties()
    {
        var builder = new AgentSnapshotBuilder(_json);

        Assert.Equal(3, Assert.Throws<AgentCliException>(() => builder.DeserializeAdvisory("{"u8)).ExitCode);
        Assert.Equal(3, Assert.Throws<AgentCliException>(() => builder.DeserializeAdvisory(new byte[AgentSnapshotBuilder.MaximumAdvisoryBytes + 1])).ExitCode);
        var unknown = """
            {"schemaVersion":1,"advisoryId":"a","snapshotHash":"0000000000000000000000000000000000000000000000000000000000000000","snapshotGeneratedAtUtc":"2026-07-18T12:00:00Z","createdAtUtc":"2026-07-18T12:00:00Z","expiresAtUtc":"2026-07-18T12:10:00Z","recommendations":[],"unexpected":true}
            """;
        Assert.Equal(3, Assert.Throws<AgentCliException>(() => builder.DeserializeAdvisory(Encoding.UTF8.GetBytes(unknown))).ExitCode);

        var observations = new[] { Observation(90) };
        var snapshot = builder.Build(Now, 60, observations, []);
        var unsafeDetail = Advisory(snapshot, 90) with
        {
            Recommendations =
            [
                Advisory(snapshot, 90).Recommendations[0] with
                {
                    Evidence = [new AgentAdvisoryEvidence { Code = "leak", Detail = @"Found C:\Users\person\secret.txt" }]
                }
            ]
        };
        Assert.Equal(3, Assert.Throws<AgentCliException>(() => builder.ValidateAdvisory(unsafeDetail, snapshot, observations, Now)).ExitCode);
    }

    [Fact]
    public void RecommendationListIsVersionedAndDeterministicallySortable()
    {
        var document = new RecommendationListDocument
        {
            GeneratedAtUtc = Now,
            Recommendations = []
        };

        var json = JsonSerializer.Serialize(document, _json);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"recommendations\":[]", json);
    }

    [Fact]
    public void SafeDefaultsEnableShadowAnalysisAndDisableActions()
    {
        var settings = new AppSettings();

        Assert.True(settings.RecommendationsEnabled);
        Assert.False(settings.ProcessActionsEnabled);
        Assert.InRange(settings.RecommendationAnalysisCadenceSeconds, 30, 3600);
        Assert.InRange(settings.MinimumDisplayedRecommendationConfidencePct, 0, 95);
    }

    [Fact]
    public void ChromeOwnerRootChainUsesTopmostApplicationRoot()
    {
        var builder = new AgentSnapshotBuilder(_json);
        var states = new[]
        {
            State(10, "explorer", parentPid: 0, startedAt: Now.AddHours(-4)),
            State(20, "chrome", parentPid: 10, startedAt: Now.AddHours(-3)),
            State(21, "chrome", parentPid: 20, startedAt: Now.AddHours(-2)),
            State(22, "chrome", parentPid: 21, startedAt: Now.AddHours(-1))
        }.ToDictionary(state => state.Identity);

        var observations = builder.BuildObservations([], states, Now, 60, 30);
        var groups = RecommendationEngine.Group(observations);
        var chromeGroup = Assert.Single(groups, group => group.Members.Any(member => member.ProcessName == "chrome"));

        Assert.Equal(20, chromeGroup.Root.Pid);
        Assert.Equal(new[] { 20, 21, 22 }, chromeGroup.Members.Select(member => member.Identity.Pid).Order().ToArray());
        Assert.DoesNotContain(groups, group => group.Root.Pid is 21 or 22);
    }

    [Fact]
    public void ConfidenceThresholdRejectsLowAgentImportAndFiltersMachineReadableOutputs()
    {
        const int minimumConfidencePct = 30;
        var builder = new AgentSnapshotBuilder(_json);
        var observations = new[] { Observation(80) };
        var snapshot = builder.Build(Now, 60, observations, []);

        Assert.Equal(5, Assert.Throws<AgentCliException>(() => builder.ValidateAdvisory(
            Advisory(snapshot, 80, confidencePct: minimumConfidencePct - 1),
            snapshot,
            observations,
            Now,
            minimumConfidencePct)).ExitCode);

        var accepted = Assert.Single(builder.ValidateAdvisory(
            Advisory(snapshot, 80), snapshot, observations, Now, minimumConfidencePct));
        var currentNow = DateTimeOffset.UtcNow;
        var low = accepted with
        {
            Id = "rec-low",
            Provenance = new RecommendationProvenance { Source = RecommendationSource.Core },
            Confidence = new RecommendationConfidence { Pct = minimumConfidencePct - 1, Kind = ConfidenceKind.Low },
            CreatedAtUtc = currentNow,
            ExpiresAtUtc = currentNow.AddMinutes(30)
        };
        var high = accepted with
        {
            Id = "rec-high",
            Provenance = new RecommendationProvenance { Source = RecommendationSource.Core },
            Confidence = new RecommendationConfidence { Pct = minimumConfidencePct, Kind = ConfidenceKind.Low },
            CreatedAtUtc = currentNow,
            ExpiresAtUtc = currentNow.AddMinutes(30)
        };

        Assert.Equal("rec-high", Assert.Single(builder.Build(
            Now, 60, observations, [low, high], minimumConfidencePct).Recommendations).Id);

        using (var store = new RecommendationStore(_directory, _json))
        {
            store.Upsert(low);
            store.Upsert(high);
        }
        var options = Options.Parse(["--data-dir", _directory], new AppSettings
        {
            MinimumDisplayedRecommendationConfidencePct = minimumConfidencePct
        });
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, Program.Recommendations(["list", "--data-dir", _directory], options));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        using var document = JsonDocument.Parse(output.ToString());
        var listed = document.RootElement.GetProperty("recommendations").EnumerateArray().ToArray();
        Assert.Equal("rec-high", Assert.Single(listed).GetProperty("id").GetString());
    }

    [Fact]
    public void InvalidNumericMachineOptionUsesValidationExitCode()
    {
        var exception = Assert.Throws<AgentCliException>(() =>
            Options.Parse(["--minutes", "not-a-number"], new AppSettings()));

        Assert.Equal(3, exception.ExitCode);
        Assert.Contains("--minutes", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static AgentAdvisoryDocument Advisory(AgentSnapshotDocument snapshot, int pid, int confidencePct = 90) => new()
    {
        SchemaVersion = RecommendationSchema.CurrentVersion,
        AdvisoryId = "adv-safe-1",
        SnapshotHash = snapshot.SnapshotHash,
        SnapshotGeneratedAtUtc = Now,
        CreatedAtUtc = Now,
        ExpiresAtUtc = Now.AddMinutes(30),
        Recommendations =
        [
            new AgentRecommendationSubmission
            {
                Root = new RecommendationMember(pid, pid * 1000L),
                Members = [new RecommendationMember(pid, pid * 1000L)],
                Action = RecommendationAction.Investigate,
                ConfidencePct = confidencePct,
                Evidence = [new AgentAdvisoryEvidence { Code = "idle", Detail = "The group appears idle in the supplied snapshot." }]
            }
        ]
    };

    private static ProcessObservation Observation(int pid, int? ownerPid = null) => new()
    {
        Identity = new RecommendationMember(pid, pid * 1000L),
        ProcessName = $"safe-app-{pid}",
        OwnerIdentity = new RecommendationMember(ownerPid ?? pid, (ownerPid ?? pid) * 1000L),
        OwnerLabel = "Safe app",
        OwnerResolved = true,
        IsOwnerRoot = ownerPid is null,
        PrivateMemoryMb = 256,
        SustainedCpuPct = 6,
        SampleCount = 10,
        ExpectedSampleCount = 10,
        ObservedAtUtc = Now.AddSeconds(-10),
        StartedAtUtc = Now.AddHours(-2),
        LastActivityAtUtc = Now.AddHours(-1),
        IdentityValid = true,
        MinimumIdleMinutes = 15,
        HasRuleEvidence = true
    };

    private static ProcessState State(int pid, string name, int parentPid, DateTimeOffset startedAt) => new()
    {
        Identity = new ProcessIdentity(pid, startedAt.UtcDateTime.Ticks),
        StartUtc = startedAt.UtcDateTime,
        Name = name,
        ParentPid = parentPid,
        CommandLine = name,
        CommandHash = $"hash-{pid}",
        Category = "Apps / UI",
        CapturedUtc = Now.UtcDateTime,
        SessionId = 1,
        LastInputAgeSeconds = 3600
    };
}
