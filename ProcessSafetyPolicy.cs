namespace ProcLens;

internal sealed record ProcessSafetyEvaluation
{
    public required bool IsHardBlocked { get; init; }
    public required ActionRisk Risk { get; init; }
    public required IReadOnlyList<RecommendationEvidence> Evidence { get; init; }
}

internal sealed class ProcessSafetyPolicy
{
    internal static readonly TimeSpan DefaultMinimumProcessAge = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _minimumProcessAge;

    public ProcessSafetyPolicy(TimeSpan? minimumProcessAge = null) =>
        _minimumProcessAge = minimumProcessAge ?? DefaultMinimumProcessAge;

    public ProcessSafetyEvaluation Evaluate(ProcessGroupObservation group, DateTimeOffset now)
    {
        var blocks = new List<RecommendationEvidence>();

        AddIf(group.Members.Any(member => IsProcLens(member.ProcessName)), "safety.proclens", "ProcLens cannot recommend ending itself.");
        AddIf(group.Members.Any(member => IsWindowsExplorer(member.ProcessName)),
            "safety.windowsExplorer", "Windows Explorer is a protected shell process.");
        AddIf(group.Criticality is ClassificationCriticality.Protected or ClassificationCriticality.System,
            "safety.protected", "The classification policy marks this target as protected.");
        AddIf(group.Members.Any(member => member.IsSystemLike), "safety.system", "A system-like process is present in the target group.");
        AddIf(group.Members.Any(member => member.IsSessionZero), "safety.sessionZero", "A session-0 process is present in the target group.");
        AddIf(group.Members.Any(member => member.IsServiceLike), "safety.service", "A service-like process is present in the target group.");
        AddIf(!group.OwnerResolved, "safety.unresolved", "The owner/root identity is unresolved.");
        AddIf(group.Members.Any(member => member.IsForeground), "safety.foreground", "The target group owns the current foreground process.");
        AddIf(group.Members.Any(member => now - member.StartedAtUtc < _minimumProcessAge),
            "safety.recentlyStarted", "At least one group member started too recently.");
        AddIf(group.Members.Any(member => !member.IdentityValid), "safety.identityInvalid", "At least one process identity is no longer valid.");
        AddIf(group.RecommendationPolicy == RecommendationPolicy.NeverEnd,
            "safety.neverEnd", "The matched rule explicitly forbids ending this target.");

        return new ProcessSafetyEvaluation
        {
            IsHardBlocked = blocks.Count > 0,
            Risk = blocks.Count > 0 ? ActionRisk.Blocked : DetermineRisk(group),
            Evidence = blocks
        };

        void AddIf(bool condition, string code, string detail)
        {
            if (condition) blocks.Add(new RecommendationEvidence { Code = code, Detail = detail });
        }
    }

    private static bool IsProcLens(string processName) =>
        processName.Equals("ProcLens", StringComparison.OrdinalIgnoreCase) ||
        processName.StartsWith("ProcLens.", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsExplorer(string processName) =>
        processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);

    private static ActionRisk DetermineRisk(ProcessGroupObservation group)
    {
        if (group.RecommendationPolicy is RecommendationPolicy.InvestigateOnly or RecommendationPolicy.ModelResident ||
            group.Criticality == ClassificationCriticality.Important)
            return ActionRisk.High;

        return group.Members.Count > 1 ? ActionRisk.Medium : ActionRisk.Low;
    }
}
