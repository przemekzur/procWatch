using System.Text.Json;

namespace ProcLens.Tests;

public sealed class RecommendationEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GroupsOwnerTreeAndNeverRecommendsAChildAlone()
    {
        var owner = new RecommendationMember(new ProcessIdentity(100, 10_000));
        var observations = new[]
        {
            Observation(100, "chrome", 500, 2, owner: owner, isRoot: true),
            Observation(101, "chrome", 300, 3, owner: owner)
        };

        var recommendation = Assert.Single(new RecommendationEngine().Generate(observations, Now));

        Assert.Equal(RecommendationAction.CloseGracefully, recommendation.Action);
        Assert.Equal(2, recommendation.TargetGroup.Members.Count);
        Assert.Equal(new[] { 100, 101 }, recommendation.TargetGroup.Members.Select(member => member.Pid));
        Assert.Equal(800, recommendation.ExpectedImpact.PrivateMemoryMb);
        Assert.Equal(5, recommendation.ExpectedImpact.SustainedCpuPct);
        Assert.Equal(95, recommendation.Confidence.Pct);
    }

    [Fact]
    public void ElectronStyleChildrenShareTheResolvedOwnerRoot()
    {
        var owner = new RecommendationMember(new ProcessIdentity(200, 20_000));
        var observations = new[]
        {
            Observation(200, "electron-app", 160, 1, owner: owner, isRoot: true),
            Observation(201, "electron-app", 200, 2, owner: owner),
            Observation(202, "electron-app", 240, 3, owner: owner)
        };

        var group = Assert.Single(RecommendationEngine.Group(observations));

        Assert.Equal(200, group.Root.Pid);
        Assert.Equal(3, group.Members.Count);
    }

    [Fact]
    public void ConfidenceCeilingsAreDeterministicAndExplainable()
    {
        var engine = new RecommendationEngine();
        var owner = new RecommendationMember(new ProcessIdentity(300, 30_000));

        var agent = Assert.Single(engine.Generate([Observation(300, "agent-app", 500, 8, owner: owner, isRoot: true)], Now, RecommendationSource.Agent));
        var stale = Assert.Single(engine.Generate([Observation(301, "stale-app", 500, 8, owner: new RecommendationMember(new ProcessIdentity(301, 30_100)), isRoot: true) with { ObservedAtUtc = Now.AddMinutes(-10) }], Now));
        var undersampled = Assert.Single(engine.Generate([Observation(302, "sparse-app", 500, 8, owner: new RecommendationMember(new ProcessIdentity(302, 30_200)), isRoot: true) with { SampleCount = 2, ExpectedSampleCount = 10 }], Now));
        var unresolved = Assert.Single(engine.Generate([Observation(303, "unknown-app", 500, 8, owner: null) with { OwnerResolved = false }], Now));

        Assert.Equal(RecommendationEngine.AgentOnlyCeilingPct, agent.Confidence.Pct);
        Assert.Equal(RecommendationAction.Investigate, agent.Action);
        Assert.Contains(agent.Evidence, item => item.Code == "confidence.ceiling.agentOnly" && item.ConfidenceCeilingPct == 70);
        Assert.Equal(RecommendationEngine.StaleCeilingPct, stale.Confidence.Pct);
        Assert.Contains(stale.Evidence, item => item.Code == "confidence.ceiling.stale");
        Assert.Equal(RecommendationEngine.UndersampledCeilingPct, undersampled.Confidence.Pct);
        Assert.Contains(undersampled.Evidence, item => item.Code == "confidence.ceiling.undersampled");
        Assert.Equal(RecommendationEngine.UnresolvedCeilingPct, unresolved.Confidence.Pct);
        Assert.Equal(ActionRisk.Blocked, unresolved.Risk);
        Assert.Contains(unresolved.Evidence, item => item.Code == "confidence.ceiling.unresolved");
    }

    [Fact]
    public void ImpactDoesNotChangeWhenConfidenceSourceChanges()
    {
        var owner = new RecommendationMember(new ProcessIdentity(400, 40_000));
        var observation = Observation(400, "compute-app", 640.5, 12.25, owner: owner, isRoot: true);
        var engine = new RecommendationEngine();

        var core = Assert.Single(engine.Generate([observation], Now, RecommendationSource.Core));
        var agent = Assert.Single(engine.Generate([observation], Now, RecommendationSource.Agent));

        Assert.Equal(core.ExpectedImpact, agent.ExpectedImpact);
        Assert.NotEqual(core.Confidence.Pct, agent.Confidence.Pct);
    }

    [Fact]
    public void LowImpactObservationsAreOmitted()
    {
        var owner = new RecommendationMember(new ProcessIdentity(500, 50_000));
        var result = new RecommendationEngine().Generate([Observation(500, "tiny-app", 12, 0.1, owner: owner, isRoot: true)], Now);

        Assert.Empty(result);
    }

    public static TheoryData<string, string> HardBlocks => new()
    {
        { "proclens", "safety.proclens" },
        { "system", "safety.system" },
        { "sessionZero", "safety.sessionZero" },
        { "service", "safety.service" },
        { "unresolved", "safety.unresolved" },
        { "foreground", "safety.foreground" },
        { "recent", "safety.recentlyStarted" },
        { "identityInvalid", "safety.identityInvalid" },
        { "neverEnd", "safety.neverEnd" }
    };

    [Theory]
    [MemberData(nameof(HardBlocks))]
    public void SafetyPolicyEnforcesEveryHardBlock(string block, string expectedCode)
    {
        var owner = new RecommendationMember(new ProcessIdentity(600, 60_000));
        var original = Observation(600, "ordinary-app", 500, 8, owner: owner, isRoot: true);
        var observation = block switch
        {
            "proclens" => original with { ProcessName = "ProcLens" },
            "system" => original with { IsSystemLike = true },
            "sessionZero" => original with { IsSessionZero = true },
            "service" => original with { IsServiceLike = true },
            "unresolved" => original with { OwnerResolved = false, OwnerIdentity = null },
            "foreground" => original with { IsForeground = true },
            "recent" => original with { StartedAtUtc = Now.AddMinutes(-1) },
            "identityInvalid" => original with { IdentityValid = false },
            "neverEnd" => original with { RecommendationPolicy = RecommendationPolicy.NeverEnd },
            _ => throw new ArgumentOutOfRangeException(nameof(block))
        };
        var group = Assert.Single(RecommendationEngine.Group([observation]));

        var evaluation = new ProcessSafetyPolicy().Evaluate(group, Now);

        Assert.True(evaluation.IsHardBlocked);
        Assert.Equal(ActionRisk.Blocked, evaluation.Risk);
        Assert.Contains(evaluation.Evidence, item => item.Code == expectedCode);
    }

    [Fact]
    public void ProtectedCriticalityIsAlwaysHardBlocked()
    {
        var owner = new RecommendationMember(new ProcessIdentity(700, 70_000));
        var observation = Observation(700, "explorer", 500, 8, owner: owner, isRoot: true) with
        {
            Criticality = ClassificationCriticality.Protected
        };
        var group = Assert.Single(RecommendationEngine.Group([observation]));

        var evaluation = new ProcessSafetyPolicy().Evaluate(group, Now);

        Assert.True(evaluation.IsHardBlocked);
        Assert.Contains(evaluation.Evidence, item => item.Code == "safety.protected");
    }

    [Fact]
    public void WindowsExplorerIsHardBlockedEvenWithoutRuleMetadata()
    {
        var owner = new RecommendationMember(new ProcessIdentity(701, 70_100));
        var observation = Observation(701, "explorer", 500, 8, owner: owner, isRoot: true);
        var group = Assert.Single(RecommendationEngine.Group([observation]));

        var evaluation = new ProcessSafetyPolicy().Evaluate(group, Now);

        Assert.True(evaluation.IsHardBlocked);
        Assert.Contains(evaluation.Evidence, item => item.Code == "safety.windowsExplorer");
        Assert.Empty(new RecommendationEngine().Generate([observation], Now));
    }

    [Fact]
    public void ModelResidentTargetRequiresExplicitSupportedAdvisory()
    {
        var owner = new RecommendationMember(new ProcessIdentity(800, 80_000));
        var viriVox = Observation(800, "ViriVox", 4_096, 0, owner: owner, isRoot: true) with
        {
            Criticality = ClassificationCriticality.Important,
            RecommendationPolicy = RecommendationPolicy.ModelResident,
            MinimumIdleMinutes = 0
        };
        var engine = new RecommendationEngine();

        Assert.Empty(engine.Generate([viriVox], Now));

        var advisory = Assert.Single(engine.Generate([viriVox with { HasSupportedUnloadModelAdvisory = true }], Now, RecommendationSource.Agent, "adv-1"));
        Assert.Equal(RecommendationAction.Investigate, advisory.Action);
        Assert.Equal(70, advisory.Confidence.Pct);
        Assert.Contains(advisory.Evidence, item => item.Code == "policy.supportedUnloadModelAdvisory");
    }

    [Fact]
    public void RecommendationJsonUsesVersionedPrivacySafePropertyNames()
    {
        var owner = new RecommendationMember(new ProcessIdentity(900, 90_000));
        var recommendation = Assert.Single(new RecommendationEngine().Generate([Observation(900, "safe-app", 500, 8, owner: owner, isRoot: true)], Now));

        var json = JsonSerializer.Serialize(recommendation);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"targetGroup\"", json);
        Assert.Contains("\"pid\":900", json);
        Assert.Contains("\"startTicks\":90000", json);
        Assert.Contains("\"action\":\"closeGracefully\"", json);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machine", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessGroupJsonUsesStableVersionedPropertyNames()
    {
        var owner = new RecommendationMember(new ProcessIdentity(901, 90_100));
        var group = Assert.Single(RecommendationEngine.Group([Observation(901, "safe-app", 500, 8, owner: owner, isRoot: true)]));

        var json = JsonSerializer.Serialize(group);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"groupKey\"", json);
        Assert.Contains("\"ownerResolved\":true", json);
        Assert.Contains("\"minimumIdleMinutes\":15", json);
        Assert.DoesNotContain("GroupKey", json, StringComparison.Ordinal);
    }

    private static ProcessObservation Observation(
        int pid,
        string name,
        double memoryMb,
        double cpuPct,
        RecommendationMember? owner,
        bool isRoot = false) => new()
    {
        Identity = new RecommendationMember(new ProcessIdentity(pid, pid * 100L)),
        ProcessName = name,
        OwnerIdentity = owner,
        OwnerLabel = name,
        OwnerResolved = owner is not null,
        IsOwnerRoot = isRoot,
        PrivateMemoryMb = memoryMb,
        SustainedCpuPct = cpuPct,
        SampleCount = 10,
        ExpectedSampleCount = 10,
        ObservedAtUtc = Now,
        StartedAtUtc = Now.AddHours(-2),
        LastActivityAtUtc = Now.AddHours(-1),
        IdentityValid = true,
        HasRuleEvidence = true,
        MinimumIdleMinutes = 15
    };
}
