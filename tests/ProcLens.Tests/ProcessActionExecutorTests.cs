using Xunit;

namespace ProcLens.Tests;

public sealed class ProcessActionExecutorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "proclens-action-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecutesStoredActiveRecommendationWithOneGracefulRootCloseAndBoundedWait()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now, memberCount: 2);
        store.Upsert(recommendation);
        var runtime = new FakeProcessActionRuntime(TestRecommendationFactory.Group(recommendation, _now));
        var executor = Executor(store, runtime);

        var result = await executor.ExecuteAsync(recommendation.Id, ProcessActionRequestSource.UserDashboard);

        Assert.Equal(ActionResultKind.Succeeded, result.Result);
        Assert.Equal("gracefulClose.completed", result.DetailCode);
        Assert.Equal([recommendation.TargetGroup.Root.Identity], runtime.CloseRequests);
        Assert.Equal(recommendation.TargetGroup.Members.Select(member => member.Identity), runtime.WaitedIdentities);
        Assert.Equal(TimeSpan.FromMilliseconds(250), runtime.WaitTimeout);
        Assert.Equal(RecommendationState.Acted, store.FindById(recommendation.Id)!.State);
    }

    [Fact]
    public async Task RejectsAgentRecommendationBeforeRuntimeProcessAccess()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now, source: RecommendationSource.Agent);
        store.Upsert(recommendation);
        var runtime = new FakeProcessActionRuntime(TestRecommendationFactory.Group(recommendation, _now));

        var result = await Executor(store, runtime).ExecuteAsync(
            recommendation.Id, ProcessActionRequestSource.UserDashboard);

        Assert.Equal(ActionResultKind.Blocked, result.Result);
        Assert.Equal("recommendation.agent", result.DetailCode);
        Assert.Equal(0, runtime.RefreshCount);
        Assert.Empty(runtime.CloseRequests);
    }

    [Fact]
    public async Task RejectsAgentInvocationAndDisabledActionsBeforeRuntimeProcessAccess()
    {
        using var agentStore = Store("agent");
        var agentRecommendation = TestRecommendationFactory.Create(_now, id: "rec-agent-invocation");
        agentStore.Upsert(agentRecommendation);
        var agentRuntime = new FakeProcessActionRuntime(TestRecommendationFactory.Group(agentRecommendation, _now));
        var agentResult = await Executor(agentStore, agentRuntime).ExecuteAsync(
            agentRecommendation.Id, ProcessActionRequestSource.Agent);

        using var disabledStore = Store("disabled");
        var disabledRecommendation = TestRecommendationFactory.Create(_now, id: "rec-disabled");
        disabledStore.Upsert(disabledRecommendation);
        var disabledRuntime = new FakeProcessActionRuntime(TestRecommendationFactory.Group(disabledRecommendation, _now));
        var disabledResult = await Executor(disabledStore, disabledRuntime, enabled: false).ExecuteAsync(
            disabledRecommendation.Id, ProcessActionRequestSource.UserDashboard);

        Assert.Equal("request.sourceRejected", agentResult.DetailCode);
        Assert.Equal("actions.disabled", disabledResult.DetailCode);
        Assert.Equal(0, agentRuntime.RefreshCount);
        Assert.Equal(0, disabledRuntime.RefreshCount);
    }

    [Fact]
    public async Task RejectsPartialOrChangedGroupBeforeClose()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now, memberCount: 2);
        store.Upsert(recommendation);
        var changed = TestRecommendationFactory.Group(recommendation, _now) with
        {
            Members = TestRecommendationFactory.Group(recommendation, _now).Members.Take(1).ToArray()
        };
        var runtime = new FakeProcessActionRuntime(changed);

        var result = await Executor(store, runtime).ExecuteAsync(
            recommendation.Id, ProcessActionRequestSource.UserDashboard);

        Assert.Equal(ActionResultKind.IdentityChanged, result.Result);
        Assert.Equal("target.changed", result.DetailCode);
        Assert.Empty(runtime.CloseRequests);
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("protected")]
    [InlineData("unresolved")]
    [InlineData("neverEnd")]
    public async Task HardSafetyBlocksAreAuditedWithoutClosing(string hardBlock)
    {
        using var store = Store(hardBlock);
        var recommendation = TestRecommendationFactory.Create(_now, id: "rec-" + hardBlock);
        store.Upsert(recommendation);
        var group = TestRecommendationFactory.Group(recommendation, _now);
        group = hardBlock switch
        {
            "foreground" => group with
            {
                Members = group.Members.Select((member, index) => index == 0 ? member with { IsForeground = true } : member).ToArray()
            },
            "protected" => group with { Criticality = ClassificationCriticality.Protected },
            "unresolved" => group with { OwnerResolved = false },
            "neverEnd" => group with { RecommendationPolicy = RecommendationPolicy.NeverEnd },
            _ => group
        };
        var runtime = new FakeProcessActionRuntime(group);

        var result = await Executor(store, runtime).ExecuteAsync(
            recommendation.Id, ProcessActionRequestSource.UserDashboard);

        Assert.Equal(ActionResultKind.Blocked, result.Result);
        Assert.StartsWith("target.", result.DetailCode, StringComparison.Ordinal);
        Assert.Empty(runtime.CloseRequests);
        Assert.Equal(RecommendationState.Acted, store.FindById(recommendation.Id)!.State);
    }

    [Fact]
    public async Task RevalidatesEveryIdentityImmediatelyBeforeClose()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now, memberCount: 2);
        store.Upsert(recommendation);
        var runtime = new FakeProcessActionRuntime(TestRecommendationFactory.Group(recommendation, _now))
        {
            IdentitiesCurrent = false
        };

        var result = await Executor(store, runtime).ExecuteAsync(
            recommendation.Id, ProcessActionRequestSource.UserDashboard);

        Assert.Equal(ActionResultKind.IdentityChanged, result.Result);
        Assert.Equal("target.identityChanged", result.DetailCode);
        Assert.Equal(recommendation.TargetGroup.Members.Select(member => member.Identity), runtime.ValidatedIdentities);
        Assert.Empty(runtime.CloseRequests);
    }

    [Fact]
    public async Task GracefulCloseTimeoutIsRecordedAndNeverEscalates()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now);
        store.Upsert(recommendation);
        var runtime = new FakeProcessActionRuntime(TestRecommendationFactory.Group(recommendation, _now))
        {
            WaitResult = false
        };

        var result = await Executor(store, runtime).ExecuteAsync(
            recommendation.Id, ProcessActionRequestSource.UserDashboard);

        Assert.Equal(ActionResultKind.Failed, result.Result);
        Assert.Equal("gracefulClose.timeout", result.DetailCode);
        Assert.Single(runtime.CloseRequests);
    }

    private RecommendationStore Store(string? suffix = null) =>
        new(suffix is null ? _directory : Path.Combine(_directory, suffix), DashboardData.JsonOptions);

    private ProcessActionExecutor Executor(RecommendationStore store, FakeProcessActionRuntime runtime, bool enabled = true) =>
        new(store, enabled, runtime, timeProvider: new FixedTimeProvider(_now), waitTimeout: TimeSpan.FromMilliseconds(250));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class FakeProcessActionRuntime(ProcessGroupObservation? refreshedGroup) : IProcessActionRuntime
{
    public int RefreshCount { get; private set; }
    public bool IdentitiesCurrent { get; set; } = true;
    public bool WaitResult { get; set; } = true;
    public GracefulCloseRequestResult CloseResult { get; set; } = GracefulCloseRequestResult.Requested;
    public List<ProcessIdentity> CloseRequests { get; } = [];
    public IReadOnlyList<ProcessIdentity> ValidatedIdentities { get; private set; } = [];
    public IReadOnlyList<ProcessIdentity> WaitedIdentities { get; private set; } = [];
    public TimeSpan WaitTimeout { get; private set; }

    public ProcessGroupObservation? RefreshTarget(RecommendationRecord recommendation, DateTimeOffset now)
    {
        RefreshCount++;
        return refreshedGroup;
    }

    public bool IdentitiesAreCurrent(IReadOnlyList<ProcessIdentity> identities)
    {
        ValidatedIdentities = identities.ToArray();
        return IdentitiesCurrent;
    }

    public GracefulCloseRequestResult RequestGracefulClose(ProcessIdentity identity)
    {
        CloseRequests.Add(identity);
        return CloseResult;
    }

    public Task<bool> WaitForExitAsync(IReadOnlyList<ProcessIdentity> identities, TimeSpan timeout, CancellationToken cancellationToken)
    {
        WaitedIdentities = identities.ToArray();
        WaitTimeout = timeout;
        return Task.FromResult(WaitResult);
    }
}

internal static class TestRecommendationFactory
{
    public static RecommendationRecord Create(
        DateTimeOffset now,
        string id = "rec-test",
        int memberCount = 1,
        RecommendationSource source = RecommendationSource.Core) => new()
    {
        Id = id,
        TargetGroup = new RecommendationTargetGroup
        {
            Label = "Disposable test app",
            Root = new RecommendationMember(41_001, now.AddMinutes(-20).UtcTicks),
            Members = Enumerable.Range(0, memberCount)
                .Select(index => new RecommendationMember(41_001 + index, now.AddMinutes(-20 - index).UtcTicks))
                .ToArray(),
            Resolved = true
        },
        Action = RecommendationAction.CloseGracefully,
        Provenance = new RecommendationProvenance { Source = source },
        Confidence = new RecommendationConfidence { Kind = ConfidenceKind.High, Pct = 90 },
        Risk = ActionRisk.Low,
        ExpectedImpact = new ExpectedImpact { PrivateMemoryMb = 256.5, SustainedCpuPct = 7.5 },
        Evidence = [new RecommendationEvidence { Code = "test.safe", Detail = "Test-owned target." }],
        CreatedAtUtc = now.AddMinutes(-2),
        ExpiresAtUtc = now.AddMinutes(20),
        State = RecommendationState.Active
    };

    public static ProcessGroupObservation Group(RecommendationRecord recommendation, DateTimeOffset now) => new()
    {
        GroupKey = $"{recommendation.TargetGroup.Root.Pid}:{recommendation.TargetGroup.Root.StartTicks}",
        Label = recommendation.TargetGroup.Label,
        Root = recommendation.TargetGroup.Root,
        Members = recommendation.TargetGroup.Members.Select(member => new ProcessObservation
        {
            Identity = member,
            ProcessName = "DisposableTestApp",
            OwnerIdentity = recommendation.TargetGroup.Root,
            OwnerLabel = recommendation.TargetGroup.Label,
            OwnerResolved = true,
            IsOwnerRoot = member.Identity == recommendation.TargetGroup.Root.Identity,
            PrivateMemoryMb = 128,
            SustainedCpuPct = 2,
            SampleCount = 20,
            ExpectedSampleCount = 20,
            ObservedAtUtc = now,
            StartedAtUtc = now.AddMinutes(-20),
            LastActivityAtUtc = now.AddMinutes(-20),
            IdentityValid = true,
            Criticality = ClassificationCriticality.Normal,
            RecommendationPolicy = RecommendationPolicy.Default,
            MinimumIdleMinutes = 15,
            HasRuleEvidence = true
        }).ToArray(),
        OwnerResolved = true,
        Criticality = ClassificationCriticality.Normal,
        RecommendationPolicy = RecommendationPolicy.Default,
        MinimumIdleMinutes = 15
    };
}
