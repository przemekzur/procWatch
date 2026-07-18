using System.Diagnostics;
using System.Text.Json;

namespace ProcLens;

internal enum ProcessActionRequestSource
{
    UserDashboard,
    Agent
}

internal enum GracefulCloseRequestResult
{
    Requested,
    Unavailable,
    IdentityChanged
}

internal interface IProcessActionRuntime
{
    ProcessGroupObservation? RefreshTarget(RecommendationRecord recommendation, DateTimeOffset now);
    bool IdentitiesAreCurrent(IReadOnlyList<ProcessIdentity> identities);
    GracefulCloseRequestResult RequestGracefulClose(ProcessIdentity identity);
    Task<bool> WaitForExitAsync(IReadOnlyList<ProcessIdentity> identities, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The sole boundary at which ProcLens can ask another process to close.  Callers supply only a
/// stored recommendation id; arbitrary process ids never cross this API.
/// </summary>
internal sealed class ProcessActionExecutor
{
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly RecommendationStore _store;
    private readonly bool _processActionsEnabled;
    private readonly IProcessActionRuntime _runtime;
    private readonly ProcessSafetyPolicy _safetyPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _waitTimeout;

    public ProcessActionExecutor(
        RecommendationStore store,
        bool processActionsEnabled,
        IProcessActionRuntime? runtime = null,
        ProcessSafetyPolicy? safetyPolicy = null,
        TimeProvider? timeProvider = null,
        TimeSpan? waitTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _processActionsEnabled = processActionsEnabled;
        _runtime = runtime ?? new WindowsProcessActionRuntime();
        _safetyPolicy = safetyPolicy ?? new ProcessSafetyPolicy();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _waitTimeout = waitTimeout ?? DefaultWaitTimeout;
        if (_waitTimeout <= TimeSpan.Zero || _waitTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
    }

    public async Task<RecommendationActionResult> ExecuteAsync(
        string recommendationId,
        ProcessActionRequestSource source,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(recommendationId))
            return Result(recommendationId ?? "", ActionResultKind.Blocked, "recommendation.invalid", now);

        RecommendationRecord? recommendation;
        try
        {
            recommendation = _store.FindById(recommendationId);
        }
        catch
        {
            return Result(recommendationId, ActionResultKind.Failed, "recommendation.unavailable", now);
        }

        if (recommendation is null)
            return Result(recommendationId, ActionResultKind.Blocked, "recommendation.notFound", now);

        if (!_processActionsEnabled)
            return Record(recommendation, ActionResultKind.Blocked, "actions.disabled", now);
        if (source != ProcessActionRequestSource.UserDashboard)
            return Record(recommendation, ActionResultKind.Blocked, "request.sourceRejected", now);
        if (recommendation.Provenance.Source == RecommendationSource.Agent)
            return Record(recommendation, ActionResultKind.Blocked, "recommendation.agent", now);
        if (recommendation.State != RecommendationState.Active)
            return Record(recommendation, ActionResultKind.Blocked, "recommendation.inactive", now);
        if (recommendation.ExpiresAtUtc <= now)
            return Record(recommendation, ActionResultKind.Blocked, "recommendation.expired", now);
        if (recommendation.Action != RecommendationAction.CloseGracefully || recommendation.Risk == ActionRisk.Blocked)
            return Record(recommendation, ActionResultKind.Blocked, "recommendation.actionRejected", now);
        if (!recommendation.TargetGroup.Resolved || recommendation.TargetGroup.Members.Count is < 1 or > 256 ||
            !recommendation.TargetGroup.Members.Any(member => member.Identity == recommendation.TargetGroup.Root.Identity))
            return Record(recommendation, ActionResultKind.Blocked, "target.unresolved", now);

        ProcessGroupObservation? refreshed;
        try
        {
            refreshed = _runtime.RefreshTarget(recommendation, now);
        }
        catch
        {
            return Record(recommendation, ActionResultKind.Blocked, "target.unresolved", now);
        }

        if (refreshed is null || !refreshed.OwnerResolved)
            return Record(recommendation, ActionResultKind.Blocked, "target.unresolved", now);
        if (!GroupMatches(recommendation.TargetGroup, refreshed))
            return Record(recommendation, ActionResultKind.IdentityChanged, "target.changed", now);

        var safety = _safetyPolicy.Evaluate(refreshed, now);
        if (safety.IsHardBlocked)
            return Record(recommendation, ActionResultKind.Blocked, SafetyDetail(safety), now);

        var identities = recommendation.TargetGroup.Members.Select(member => member.Identity).ToArray();
        if (!_runtime.IdentitiesAreCurrent(identities))
            return Record(recommendation, ActionResultKind.IdentityChanged, "target.identityChanged", now);

        GracefulCloseRequestResult closeRequest;
        try
        {
            // Closing the resolved group root is the platform's graceful application-close signal.
            // Children are never individually terminated; the bounded wait verifies the whole group.
            closeRequest = _runtime.RequestGracefulClose(recommendation.TargetGroup.Root.Identity);
        }
        catch
        {
            return Record(recommendation, ActionResultKind.Failed, "gracefulClose.failed", _timeProvider.GetUtcNow());
        }

        if (closeRequest == GracefulCloseRequestResult.IdentityChanged)
            return Record(recommendation, ActionResultKind.IdentityChanged, "target.identityChanged", _timeProvider.GetUtcNow());
        if (closeRequest != GracefulCloseRequestResult.Requested)
            return Record(recommendation, ActionResultKind.Failed, "gracefulClose.unavailable", _timeProvider.GetUtcNow());

        try
        {
            var exited = await _runtime.WaitForExitAsync(identities, _waitTimeout, cancellationToken);
            return Record(recommendation,
                exited ? ActionResultKind.Succeeded : ActionResultKind.Failed,
                exited ? "gracefulClose.completed" : "gracefulClose.timeout",
                _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Record(recommendation, ActionResultKind.Failed, "gracefulClose.cancelled", _timeProvider.GetUtcNow());
        }
        catch
        {
            return Record(recommendation, ActionResultKind.Failed, "gracefulClose.failed", _timeProvider.GetUtcNow());
        }
    }

    private RecommendationActionResult Record(
        RecommendationRecord recommendation,
        ActionResultKind kind,
        string detailCode,
        DateTimeOffset completedAtUtc)
    {
        var result = Result(recommendation.Id, kind, detailCode, completedAtUtc);
        try
        {
            return _store.RecordAction(result)
                ? result
                : result with { Result = ActionResultKind.Failed, DetailCode = "audit.failed" };
        }
        catch
        {
            return result with { Result = ActionResultKind.Failed, DetailCode = "audit.failed" };
        }
    }

    private static RecommendationActionResult Result(
        string recommendationId,
        ActionResultKind kind,
        string detailCode,
        DateTimeOffset completedAtUtc) => new()
    {
        RecommendationId = recommendationId,
        Result = kind,
        CompletedAtUtc = completedAtUtc,
        DetailCode = detailCode
    };

    private static bool GroupMatches(RecommendationTargetGroup stored, ProcessGroupObservation refreshed)
    {
        if (stored.Root.Identity != refreshed.Root.Identity || stored.Members.Count != refreshed.Members.Count)
            return false;

        var expected = stored.Members.Select(member => member.Identity).ToHashSet();
        return expected.Count == stored.Members.Count &&
               refreshed.Members.All(member => expected.Contains(member.Identity.Identity));
    }

    private static string SafetyDetail(ProcessSafetyEvaluation safety)
    {
        var code = safety.Evidence.Select(item => item.Code).OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault();
        return code is null ? "target.blocked" : $"target.blocked.{code["safety.".Length..]}";
    }
}

internal sealed class WindowsProcessActionRuntime : IProcessActionRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProcessGroupObservation? RefreshTarget(RecommendationRecord recommendation, DateTimeOffset now)
    {
        var current = Capture(now);
        var observations = new AgentSnapshotBuilder(JsonOptions).BuildObservations(
            Array.Empty<string>(), current, now, 1, 30);
        return RecommendationEngine.Group(observations)
            .SingleOrDefault(group => group.Root.Identity == recommendation.TargetGroup.Root.Identity);
    }

    public bool IdentitiesAreCurrent(IReadOnlyList<ProcessIdentity> identities) =>
        identities.Count > 0 && identities.All(IsCurrent);

    public GracefulCloseRequestResult RequestGracefulClose(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartTicks)
                return GracefulCloseRequestResult.IdentityChanged;
            return process.CloseMainWindow()
                ? GracefulCloseRequestResult.Requested
                : GracefulCloseRequestResult.Unavailable;
        }
        catch (ArgumentException)
        {
            return GracefulCloseRequestResult.IdentityChanged;
        }
        catch (InvalidOperationException)
        {
            return GracefulCloseRequestResult.IdentityChanged;
        }
    }

    public async Task<bool> WaitForExitAsync(
        IReadOnlyList<ProcessIdentity> identities,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + timeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identities.All(identity => !IsCurrent(identity))) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        return identities.All(identity => !IsCurrent(identity));
    }

    private static Dictionary<ProcessIdentity, ProcessState> Capture(DateTimeOffset now)
    {
        var result = new Dictionary<ProcessIdentity, ProcessState>();
        var inspector = new ProcessActivityInspector();
        var globalActivity = inspector.CaptureGlobal(now);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var startUtc = process.StartTime.ToUniversalTime();
                    var identity = new ProcessIdentity(process.Id, startUtc.Ticks);
                    var activity = inspector.Inspect(process, globalActivity);
                    result[identity] = ProcessState.Capture(process, identity, startUtc, null, now.UtcDateTime, activity);
                }
                catch
                {
                    // A disappearing or inaccessible process makes the refreshed group incomplete,
                    // which the executor rejects before requesting any close.
                }
            }
        }
        return result;
    }

    private static bool IsCurrent(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return process.StartTime.ToUniversalTime().Ticks == identity.StartTicks;
        }
        catch
        {
            return false;
        }
    }
}
