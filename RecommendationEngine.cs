using System.Security.Cryptography;
using System.Text;

namespace ProcLens;

internal sealed record RecommendationEngineOptions
{
    public const int DefaultMinimumIdleMinutes = 15;
    public int MinimumCloseConfidencePct { get; init; } = 75;
    public int MinimumInvestigateConfidencePct { get; init; } = 30;
    public double MinimumPrivateMemoryMb { get; init; } = 128;
    public double MinimumSustainedCpuPct { get; init; } = 5;
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RecommendationLifetime { get; init; } = TimeSpan.FromMinutes(30);
}

internal sealed class RecommendationEngine
{
    internal const int MaximumConfidencePct = 95;
    internal const int AgentOnlyCeilingPct = 70;
    internal const int UndersampledCeilingPct = 55;
    internal const int StaleCeilingPct = 60;
    internal const int UnresolvedCeilingPct = 40;

    private readonly RecommendationEngineOptions _options;
    private readonly ProcessSafetyPolicy _safety;

    public RecommendationEngine(RecommendationEngineOptions? options = null, ProcessSafetyPolicy? safety = null)
    {
        _options = options ?? new RecommendationEngineOptions();
        _safety = safety ?? new ProcessSafetyPolicy();
    }

    public IReadOnlyList<RecommendationRecord> Generate(
        IEnumerable<ProcessObservation> observations,
        DateTimeOffset now,
        RecommendationSource source = RecommendationSource.Core,
        string? advisoryId = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return Group(observations)
            .Select(group => CreateRecommendation(group, now, source, advisoryId))
            .Where(recommendation => recommendation is not null)
            .Cast<RecommendationRecord>()
            .OrderByDescending(recommendation => recommendation.Confidence.Pct)
            .ThenByDescending(recommendation => recommendation.ExpectedImpact.PrivateMemoryMb)
            .ThenBy(recommendation => recommendation.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<ProcessGroupObservation> Group(IEnumerable<ProcessObservation> observations)
    {
        return observations
            .GroupBy(GroupKey, StringComparer.Ordinal)
            .Select(BuildGroup)
            .OrderBy(group => group.GroupKey, StringComparer.Ordinal)
            .ToArray();
    }

    private RecommendationRecord? CreateRecommendation(
        ProcessGroupObservation group,
        DateTimeOffset now,
        RecommendationSource source,
        string? advisoryId)
    {
        var impact = new ExpectedImpact
        {
            PrivateMemoryMb = Math.Round(group.Members.Sum(member => Math.Max(0, member.PrivateMemoryMb)), 1),
            SustainedCpuPct = Math.Round(group.Members.Sum(member => Math.Max(0, member.SustainedCpuPct)), 1)
        };
        var meaningfulImpact = impact.PrivateMemoryMb >= _options.MinimumPrivateMemoryMb ||
                               impact.SustainedCpuPct >= _options.MinimumSustainedCpuPct;
        if (!meaningfulImpact) return null;

        if (group.RecommendationPolicy == RecommendationPolicy.ModelResident &&
            !group.Members.Any(member => member.HasSupportedUnloadModelAdvisory))
            return null;

        var (confidence, confidenceEvidence) = CalculateConfidence(group, now, source);
        var safety = _safety.Evaluate(group, now);
        var evidence = confidenceEvidence.Concat(safety.Evidence).ToList();

        if (IsAlwaysOmitted(group, safety)) return null;

        var action = RecommendationAction.Investigate;
        if (!safety.IsHardBlocked &&
            group.RecommendationPolicy == RecommendationPolicy.Default &&
            confidence.Pct >= _options.MinimumCloseConfidencePct &&
            IsIdle(group, now))
        {
            action = RecommendationAction.CloseGracefully;
            evidence.Add(new RecommendationEvidence
            {
                Code = "action.closeEligible",
                Detail = "Core confidence, activity, impact, and safety thresholds allow a graceful-close recommendation."
            });
        }
        else if (confidence.Pct < _options.MinimumInvestigateConfidencePct)
        {
            return null;
        }

        if (group.RecommendationPolicy == RecommendationPolicy.ModelResident)
        {
            evidence.Add(new RecommendationEvidence
            {
                Code = "policy.supportedUnloadModelAdvisory",
                Detail = "A model-resident target is surfaced only because an explicit supported unload-model advisory exists."
            });
        }

        return new RecommendationRecord
        {
            Id = CreateId(group.GroupKey, action, source),
            TargetGroup = new RecommendationTargetGroup
            {
                Label = group.Label,
                Root = group.Root,
                Members = group.Members.Select(member => member.Identity).OrderBy(member => member.Pid).ThenBy(member => member.StartTicks).ToArray(),
                Resolved = group.OwnerResolved
            },
            Action = action,
            Provenance = new RecommendationProvenance { Source = source, AdvisoryId = advisoryId },
            Confidence = confidence,
            Risk = safety.Risk,
            ExpectedImpact = impact,
            Evidence = evidence,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + _options.RecommendationLifetime
        };
    }

    internal RecommendationConfidence CalculateConfidence(
        ProcessGroupObservation group,
        DateTimeOffset now,
        RecommendationSource source,
        out IReadOnlyList<RecommendationEvidence> evidence)
    {
        var result = CalculateConfidence(group, now, source);
        evidence = result.Evidence;
        return result.Confidence;
    }

    private (RecommendationConfidence Confidence, IReadOnlyList<RecommendationEvidence> Evidence) CalculateConfidence(
        ProcessGroupObservation group,
        DateTimeOffset now,
        RecommendationSource source)
    {
        var evidence = new List<RecommendationEvidence>();
        var expectedSamples = group.Members.Sum(member => Math.Max(1, member.ExpectedSampleCount));
        var actualSamples = group.Members.Sum(member => Math.Max(0, member.SampleCount));
        var coverage = Math.Clamp(actualSamples / (double)expectedSamples, 0, 1);
        var coveragePoints = (int)Math.Round(25 * coverage, MidpointRounding.AwayFromZero);
        AddSupport("confidence.coverage", $"Observable sample coverage is {coverage:P0}.", coveragePoints);

        if (group.Members.All(member => member.IdentityValid))
            AddSupport("confidence.identity", "All member identities remain valid.", 20);
        if (group.OwnerResolved)
            AddSupport("confidence.ownerResolved", "The group owner/root is resolved.", 15);
        if (IsIdle(group, now))
            AddSupport("confidence.activityIdle", "No meaningful activity occurred inside the configured idle window.", 25);
        if (group.Members.Any(member => member.HasRuleEvidence))
            AddSupport("confidence.rule", "A classification rule supports the group policy.", 10);

        var score = evidence.Sum(item => item.ConfidenceDelta ?? 0);
        var ceiling = MaximumConfidencePct;
        ApplyCeiling(source == RecommendationSource.Agent, AgentOnlyCeilingPct,
            "confidence.ceiling.agentOnly", "Agent-only evidence cannot exceed 70% confidence.");
        ApplyCeiling(coverage < 0.60, UndersampledCeilingPct,
            "confidence.ceiling.undersampled", "Sample coverage is below 60%.");
        ApplyCeiling(group.Members.Any(member => now - member.ObservedAtUtc > _options.StaleAfter), StaleCeilingPct,
            "confidence.ceiling.stale", "At least one observation is stale.");
        ApplyCeiling(!group.OwnerResolved, UnresolvedCeilingPct,
            "confidence.ceiling.unresolved", "The group owner/root is unresolved.");

        var pct = Math.Clamp(Math.Min(score, ceiling), 0, MaximumConfidencePct);
        return (new RecommendationConfidence
        {
            Pct = pct,
            Kind = pct >= 75 ? ConfidenceKind.High : pct >= 50 ? ConfidenceKind.Medium : ConfidenceKind.Low
        }, evidence);

        void AddSupport(string code, string detail, int delta) => evidence.Add(new RecommendationEvidence
        {
            Code = code,
            Detail = detail,
            ConfidenceDelta = delta
        });

        void ApplyCeiling(bool applies, int value, string code, string detail)
        {
            if (!applies) return;
            ceiling = Math.Min(ceiling, value);
            evidence.Add(new RecommendationEvidence
            {
                Code = code,
                Detail = detail,
                ConfidenceCeilingPct = value
            });
        }
    }

    private static bool IsIdle(ProcessGroupObservation group, DateTimeOffset now) =>
        group.Members.All(member => now - member.LastActivityAtUtc >= TimeSpan.FromMinutes(group.MinimumIdleMinutes));

    private static bool IsAlwaysOmitted(ProcessGroupObservation group, ProcessSafetyEvaluation safety)
    {
        if (!safety.IsHardBlocked) return false;
        return group.Criticality is ClassificationCriticality.Protected or ClassificationCriticality.System ||
               group.RecommendationPolicy == RecommendationPolicy.NeverEnd ||
               safety.Evidence.Any(item => item.Code is "safety.proclens" or "safety.windowsExplorer" or "safety.system" or "safety.sessionZero" or "safety.service");
    }

    private static string GroupKey(ProcessObservation observation)
    {
        var identity = observation.OwnerResolved && observation.OwnerIdentity is not null
            ? observation.OwnerIdentity.Identity
            : observation.Identity.Identity;
        return $"{identity.Pid}:{identity.StartTicks}";
    }

    private static ProcessGroupObservation BuildGroup(IGrouping<string, ProcessObservation> grouping)
    {
        var members = grouping.OrderBy(member => member.Identity.Pid).ThenBy(member => member.Identity.StartTicks).ToArray();
        var rootObservation = members.FirstOrDefault(member => member.IsOwnerRoot) ??
                              members.FirstOrDefault(member => member.OwnerIdentity?.Identity == member.Identity.Identity) ??
                              members[0];
        var root = members.Select(member => member.OwnerIdentity).FirstOrDefault(owner => owner is not null) ?? rootObservation.Identity;
        return new ProcessGroupObservation
        {
            GroupKey = grouping.Key,
            Label = members.Select(member => member.OwnerLabel).FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? rootObservation.ProcessName,
            Root = root,
            Members = members,
            OwnerResolved = members.All(member => member.OwnerResolved && member.OwnerIdentity is not null),
            Criticality = members.Max(member => member.Criticality),
            RecommendationPolicy = members.Max(member => member.RecommendationPolicy),
            MinimumIdleMinutes = members.Max(member => Math.Max(0, member.MinimumIdleMinutes))
        };
    }

    private static string CreateId(string groupKey, RecommendationAction action, RecommendationSource source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"v1|{groupKey}|{action}|{source}"));
        return $"rec-{Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant()}";
    }
}
