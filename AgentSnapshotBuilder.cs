using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcLens;

internal sealed class AgentSnapshotBuilder
{
    internal const int MaximumAdvisoryBytes = 1024 * 1024;
    internal static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(5);

    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Dictionary<ProcessIdentity, ActivityDetails> _activity = new();

    public AgentSnapshotBuilder(JsonSerializerOptions jsonOptions) =>
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

    public AgentSnapshotDocument Build(
        DateTimeOffset generatedAtUtc,
        int windowMinutes,
        IReadOnlyList<ProcessObservation> observations,
        IReadOnlyList<RecommendationRecord> recommendations,
        int minimumDisplayedConfidencePct = 0)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(recommendations);
        windowMinutes = Math.Clamp(windowMinutes, 1, 1440);

        var groups = RecommendationEngine.Group(observations)
            .Select(group => BuildGroup(group, generatedAtUtc))
            .OrderBy(group => group.Root.Pid)
            .ThenBy(group => group.Root.StartTicks)
            .ToArray();
        minimumDisplayedConfidencePct = Math.Clamp(minimumDisplayedConfidencePct, 0, 95);
        var currentRecommendations = recommendations
            .Where(item => item.Provenance.Source == RecommendationSource.Core &&
                           item.ExpiresAtUtc > generatedAtUtc &&
                           item.Confidence.Pct >= minimumDisplayedConfidencePct)
            .OrderByDescending(item => item.Confidence.Pct)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var sampleCount = observations.Sum(item => Math.Max(0, item.SampleCount));
        var expectedSampleCount = observations.Sum(item => Math.Max(1, item.ExpectedSampleCount));
        var sampled = observations.Where(item => item.SampleCount > 0).ToArray();
        var latest = sampled.Length == 0 ? (DateTimeOffset?)null : sampled.Max(item => item.ObservedAtUtc);
        var freshnessAge = latest is null ? (double?)null : Math.Max(0, (generatedAtUtc - latest.Value).TotalSeconds);
        var coveragePct = expectedSampleCount == 0 ? 0 : Math.Round(Math.Clamp(sampleCount / (double)expectedSampleCount, 0, 1) * 100, 1);

        var document = new AgentSnapshotDocument
        {
            GeneratedAtUtc = generatedAtUtc,
            Window = new AgentSnapshotWindow
            {
                Minutes = windowMinutes,
                FromUtc = generatedAtUtc.AddMinutes(-windowMinutes),
                ToUtc = generatedAtUtc
            },
            Freshness = new AgentSnapshotFreshness
            {
                LatestSampleAtUtc = latest,
                AgeSeconds = freshnessAge,
                IsFresh = freshnessAge is not null && freshnessAge <= MaximumSnapshotAge.TotalSeconds
            },
            Coverage = new AgentSnapshotCoverage
            {
                SampleCount = sampleCount,
                ExpectedSampleCount = expectedSampleCount,
                Pct = coveragePct
            },
            Groups = groups,
            Recommendations = currentRecommendations,
            SnapshotHash = ""
        };
        return document with { SnapshotHash = ComputeHash(document) };
    }

    public string ComputeHash(AgentSnapshotDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var payload = new
        {
            document.SchemaVersion,
            document.Window.Minutes,
            document.Freshness.LatestSampleAtUtc,
            document.Coverage,
            Groups = document.Groups.Select(group => new
            {
                group.GroupKey,
                group.Label,
                group.Root,
                Members = group.Members.Select(member => new
                {
                    member.Pid,
                    member.StartTicks,
                    member.ProcessName,
                    member.StartedAtUtc,
                    member.ObservedAtUtc,
                    member.LastActivityAtUtc,
                    member.SampleCount,
                    member.ExpectedSampleCount,
                    member.PrivateMemoryMb,
                    member.SustainedCpuPct,
                    member.IsForeground,
                    member.HasVisibleMainWindow,
                    member.SessionId,
                    member.IsSessionZero,
                    member.IsSystemLike,
                    member.IsServiceLike,
                    member.IdentityValid
                }),
                group.OwnerResolved,
                group.Metrics,
                group.LastActivityAtUtc,
                group.Safety
            }),
            Recommendations = document.Recommendations.Select(item => item.Id)
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions))).ToLowerInvariant();
    }

    public IReadOnlyList<ProcessObservation> BuildObservations(
        IEnumerable<string> historyLines,
        IReadOnlyDictionary<ProcessIdentity, ProcessState> current,
        DateTimeOffset now,
        int windowMinutes,
        int sampleIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(historyLines);
        ArgumentNullException.ThrowIfNull(current);
        var cutoff = now.AddMinutes(-Math.Clamp(windowMinutes, 1, 1440));
        var samples = new Dictionary<ProcessIdentity, ObservationAccumulator>();

        foreach (var line in historyLines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "process_sample") continue;
                if (!TryDate(root, "timeUtc", out var observedAt) || observedAt < cutoff || observedAt > now.AddMinutes(1)) continue;
                if (!TryInt(root, "pid", out var pid) || !TryDate(root, "startUtc", out var startedAt)) continue;
                var identity = new ProcessIdentity(pid, startedAt.UtcDateTime.Ticks);
                if (!current.ContainsKey(identity)) continue;
                if (!samples.TryGetValue(identity, out var accumulator))
                {
                    accumulator = new ObservationAccumulator(identity, startedAt);
                    samples.Add(identity, accumulator);
                }
                accumulator.Add(root, observedAt);
            }
            catch (JsonException)
            {
                // Partial and legacy rows never break snapshot generation.
            }
            catch (InvalidOperationException)
            {
                // A row with an incompatible primitive type is skipped.
            }
        }

        var currentByPid = current.Values.GroupBy(item => item.Identity.Pid)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.StartUtc).First());
        var expectedSamples = Math.Max(1, (int)Math.Ceiling(windowMinutes * 60d / Math.Max(1, sampleIntervalSeconds)));
        _activity.Clear();
        foreach (var state in current.Values)
        {
            _activity[state.Identity] = new ActivityDetails(
                state.HasVisibleMainWindow,
                state.SessionId,
                double.IsFinite(state.LastInputAgeSeconds) ? state.LastInputAgeSeconds : null,
                Math.Max(0, (now - state.StartUtc).TotalSeconds));
        }
        return current.Values.OrderBy(item => item.Identity.Pid).ThenBy(item => item.Identity.StartTicks)
            .Select(state => BuildObservation(state, samples.GetValueOrDefault(state.Identity), currentByPid, now, expectedSamples))
            .ToArray();
    }

    public AgentAdvisoryDocument DeserializeAdvisory(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaximumAdvisoryBytes)
            throw new AgentCliException(3, $"Advisory size must be between 1 and {MaximumAdvisoryBytes} bytes.");
        try
        {
            var options = new JsonSerializerOptions(_jsonOptions)
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 32
            };
            return JsonSerializer.Deserialize<AgentAdvisoryDocument>(utf8, options) ??
                   throw new AgentCliException(3, "The advisory document is empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentCliException(3, $"Malformed advisory JSON: {exception.Message}", exception);
        }
    }

    public IReadOnlyList<RecommendationRecord> ValidateAdvisory(
        AgentAdvisoryDocument advisory,
        AgentSnapshotDocument currentSnapshot,
        IReadOnlyList<ProcessObservation> currentObservations,
        DateTimeOffset now,
        int minimumDisplayedConfidencePct = 0)
    {
        ArgumentNullException.ThrowIfNull(advisory);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(currentObservations);
        if (advisory.SchemaVersion != RecommendationSchema.CurrentVersion)
            throw new AgentCliException(3, $"Unsupported advisory schemaVersion {advisory.SchemaVersion}.");
        var advisoryId = advisory.AdvisoryId;
        if (advisoryId is null || !System.Text.RegularExpressions.Regex.IsMatch(advisoryId, "^[A-Za-z0-9._-]{1,80}$"))
            throw new AgentCliException(3, "advisoryId must contain 1-80 safe identifier characters.");
        if (!IsHash(advisory.SnapshotHash) || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(advisory.SnapshotHash), Encoding.ASCII.GetBytes(currentSnapshot.SnapshotHash)))
            throw new AgentCliException(4, "The advisory snapshotHash does not match the current privacy-safe snapshot.");
        if (advisory.SnapshotGeneratedAtUtc > now.AddMinutes(1) || now - advisory.SnapshotGeneratedAtUtc > MaximumSnapshotAge ||
            advisory.CreatedAtUtc > now.AddMinutes(1) || advisory.CreatedAtUtc < advisory.SnapshotGeneratedAtUtc.AddMinutes(-1) ||
            now - advisory.CreatedAtUtc > MaximumSnapshotAge || advisory.ExpiresAtUtc <= now ||
            advisory.ExpiresAtUtc > now.AddHours(1))
            throw new AgentCliException(4, "The advisory is stale, future-dated, or has an invalid expiry.");
        if (!currentSnapshot.Freshness.IsFresh)
            throw new AgentCliException(4, "Current telemetry is stale; advisory import is not safe.");
        if (advisory.Recommendations is null || advisory.Recommendations.Count is < 1 or > 100)
            throw new AgentCliException(3, "recommendations must contain between 1 and 100 entries.");

        minimumDisplayedConfidencePct = Math.Clamp(minimumDisplayedConfidencePct, 0, 95);
        var groups = RecommendationEngine.Group(currentObservations)
            .ToDictionary(group => IdentityKey(group.Root), StringComparer.Ordinal);
        var engine = new RecommendationEngine();
        var safetyPolicy = new ProcessSafetyPolicy();
        var accepted = new List<RecommendationRecord>();
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var submission in advisory.Recommendations)
        {
            ValidateSubmission(submission);
            var key = IdentityKey(submission.Root);
            if (!seenTargets.Add(key)) throw new AgentCliException(3, $"Duplicate target root {key}.");
            if (!groups.TryGetValue(key, out var group))
                throw new AgentCliException(5, $"Unknown or changed target identity {key}.");
            var submittedMembers = submission.Members.Select(IdentityKey).Order(StringComparer.Ordinal).ToArray();
            var currentMembers = group.Members.Select(member => IdentityKey(member.Identity)).Order(StringComparer.Ordinal).ToArray();
            if (!submittedMembers.SequenceEqual(currentMembers, StringComparer.Ordinal))
                throw new AgentCliException(5, $"Member identities changed for target {key}.");
            if (group.Members.Any(member => member.SampleCount == 0 || member.ObservedAtUtc > now.AddMinutes(1) ||
                                            now - member.ObservedAtUtc > MaximumSnapshotAge))
                throw new AgentCliException(4, $"Target telemetry is stale or missing for {key}.");
            if (submission.Action != RecommendationAction.Investigate)
                throw new AgentCliException(5, "Agent imports support only the non-executing investigate action.");

            var safety = safetyPolicy.Evaluate(group, now);
            if (safety.IsHardBlocked)
                throw new AgentCliException(5, $"Target {key} is hard-blocked: {string.Join(',', safety.Evidence.Select(item => item.Code))}.");

            var confidence = engine.CalculateConfidence(group, now, RecommendationSource.Agent, out var confidenceEvidence);
            var confidencePct = Math.Min(Math.Clamp(submission.ConfidencePct, 0, RecommendationEngine.AgentOnlyCeilingPct), confidence.Pct);
            if (confidencePct < minimumDisplayedConfidencePct)
                throw new AgentCliException(5,
                    $"Target {key} confidence {confidencePct}% is below the configured display threshold of {minimumDisplayedConfidencePct}%.");
            var evidence = submission.Evidence.Select(item => new RecommendationEvidence
                {
                    Code = $"agent.{item.Code}",
                    Detail = item.Detail
                })
                .Concat(confidenceEvidence)
                .Append(new RecommendationEvidence
                {
                    Code = "agent.advisoryImported",
                    Detail = "A CLI agent supplied this non-executing advisory; ProcLens recomputed identity, safety, impact, and confidence."
                })
                .ToArray();
            accepted.Add(new RecommendationRecord
            {
                Id = CreateAgentRecommendationId(advisoryId, key),
                TargetGroup = new RecommendationTargetGroup
                {
                    Label = group.Label,
                    Root = group.Root,
                    Members = group.Members.Select(item => item.Identity).OrderBy(item => item.Pid).ThenBy(item => item.StartTicks).ToArray(),
                    Resolved = group.OwnerResolved
                },
                Action = RecommendationAction.Investigate,
                Provenance = new RecommendationProvenance { Source = RecommendationSource.Agent, AdvisoryId = advisoryId },
                Confidence = new RecommendationConfidence
                {
                    Pct = confidencePct,
                    Kind = confidencePct >= 75 ? ConfidenceKind.High : confidencePct >= 50 ? ConfidenceKind.Medium : ConfidenceKind.Low
                },
                Risk = safety.Risk,
                ExpectedImpact = new ExpectedImpact
                {
                    PrivateMemoryMb = Math.Round(group.Members.Sum(item => Math.Max(0, item.PrivateMemoryMb)), 1),
                    SustainedCpuPct = Math.Round(group.Members.Sum(item => Math.Max(0, item.SustainedCpuPct)), 1)
                },
                Evidence = evidence,
                CreatedAtUtc = now,
                ExpiresAtUtc = advisory.ExpiresAtUtc,
                State = RecommendationState.Active
            });
        }

        return accepted.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static ProcessObservation BuildObservation(
        ProcessState state,
        ObservationAccumulator? history,
        IReadOnlyDictionary<int, ProcessState> currentByPid,
        DateTimeOffset now,
        int expectedSamples)
    {
        var ownerState = ResolveOwner(state, currentByPid);
        var owner = ownerState is null ? null : new RecommendationMember(ownerState.Identity);
        var recommendation = ClassificationRules.Current.RecommendationFor(state.Name, state.Path, state.CommandLine);
        var lastActivity = history?.LastForegroundAtUtc;
        if (state.IsForeground && double.IsFinite(state.LastInputAgeSeconds))
            lastActivity = now.AddSeconds(-Math.Max(0, state.LastInputAgeSeconds));
        lastActivity ??= state.StartUtc;
        return new ProcessObservation
        {
            Identity = new RecommendationMember(state.Identity),
            ProcessName = state.Name,
            OwnerIdentity = owner,
            OwnerLabel = ownerState is null ? null : ownerState.Name,
            OwnerResolved = owner is not null,
            IsOwnerRoot = ownerState?.Identity == state.Identity,
            PrivateMemoryMb = history?.AveragePrivateMb ?? Math.Round(state.PrivateBytes / 1024d / 1024d, 1),
            SustainedCpuPct = history?.AverageCpuPct ?? state.CpuPercent,
            SampleCount = history?.Count ?? 0,
            ExpectedSampleCount = expectedSamples,
            ObservedAtUtc = history?.LatestAtUtc ?? state.CapturedUtc,
            StartedAtUtc = state.StartUtc,
            LastActivityAtUtc = lastActivity.Value,
            IdentityValid = true,
            IsForeground = state.IsForeground,
            IsSystemLike = state.Identity.Pid <= 4 || recommendation.Criticality == ClassificationCriticality.System,
            IsSessionZero = state.SessionId == 0,
            IsServiceLike = state.SessionId == 0,
            Criticality = recommendation.Criticality,
            RecommendationPolicy = recommendation.RecommendationPolicy,
            MinimumIdleMinutes = recommendation.MinimumIdleMinutes ?? RecommendationEngineOptions.DefaultMinimumIdleMinutes,
            HasRuleEvidence = recommendation != new ClassificationRecommendation()
        };
    }

    private static ProcessState? ResolveOwner(ProcessState state, IReadOnlyDictionary<int, ProcessState> byPid)
    {
        var current = state;
        var visited = new HashSet<int>();
        for (var depth = 0; depth < 24; depth++)
        {
            var isOwnerRoot = ClassificationRules.Current.IsOwnerRoot(current.Name, current.Path, current.CommandLine);
            if (current.ParentPid <= 0 || !visited.Add(current.ParentPid) ||
                !byPid.TryGetValue(current.ParentPid, out var parent) || parent.StartUtc > current.StartUtc)
                return isOwnerRoot || depth == 0 && current.ParentPid <= 0 ? current : null;

            if (isOwnerRoot &&
                (!ClassificationRules.Current.IsOwnerRoot(parent.Name, parent.Path, parent.CommandLine) ||
                 !IsSameOwnerFamily(current, parent)))
                return current;

            current = parent;
        }
        return null;
    }

    private static bool IsSameOwnerFamily(ProcessState first, ProcessState second) =>
        string.Equals(OwnerFamily(first), OwnerFamily(second), StringComparison.OrdinalIgnoreCase);

    private static string OwnerFamily(ProcessState state) =>
        ClassificationRules.Current.OwnerLabel(state.Name, state.Path, state.CommandLine) ?? state.Name;

    private AgentSnapshotGroup BuildGroup(ProcessGroupObservation group, DateTimeOffset now)
    {
        var safety = new ProcessSafetyPolicy().Evaluate(group, now);
        var members = group.Members.OrderBy(item => item.Identity.Pid).ThenBy(item => item.Identity.StartTicks)
            .Select(item =>
            {
                if (!_activity.TryGetValue(item.Identity.Identity, out var activity))
                    activity = new ActivityDetails(false, -1, null, Math.Max(0, (now - item.StartedAtUtc).TotalSeconds));
                return new AgentSnapshotMember
                {
                Pid = item.Identity.Pid,
                StartTicks = item.Identity.StartTicks,
                ProcessName = item.ProcessName,
                StartedAtUtc = item.StartedAtUtc,
                ObservedAtUtc = item.ObservedAtUtc,
                LastActivityAtUtc = item.LastActivityAtUtc,
                SampleCount = item.SampleCount,
                ExpectedSampleCount = item.ExpectedSampleCount,
                PrivateMemoryMb = item.PrivateMemoryMb,
                SustainedCpuPct = item.SustainedCpuPct,
                IsForeground = item.IsForeground,
                HasVisibleMainWindow = activity.HasVisibleMainWindow,
                SessionId = activity.SessionId,
                LastInputAgeSeconds = activity.LastInputAgeSeconds,
                ProcessAgeSeconds = activity.ProcessAgeSeconds,
                IsSessionZero = item.IsSessionZero,
                IsSystemLike = item.IsSystemLike,
                IsServiceLike = item.IsServiceLike,
                    IdentityValid = item.IdentityValid
                };
            }).ToArray();
        return new AgentSnapshotGroup
        {
            GroupKey = group.GroupKey,
            Label = group.Label,
            Root = group.Root,
            Members = members,
            OwnerResolved = group.OwnerResolved,
            Metrics = new ExpectedImpact
            {
                PrivateMemoryMb = Math.Round(group.Members.Sum(item => Math.Max(0, item.PrivateMemoryMb)), 1),
                SustainedCpuPct = Math.Round(group.Members.Sum(item => Math.Max(0, item.SustainedCpuPct)), 1)
            },
            LastActivityAtUtc = group.Members.Max(item => item.LastActivityAtUtc),
            Safety = new AgentSnapshotSafety
            {
                IsHardBlocked = safety.IsHardBlocked,
                Risk = safety.Risk,
                Flags = safety.Evidence.Select(item => item.Code).Order(StringComparer.Ordinal).ToArray()
            }
        };
    }

    private static void ValidateSubmission(AgentRecommendationSubmission submission)
    {
        if (submission.Root is null || submission.Members is null || submission.Evidence is null)
            throw new AgentCliException(3, "Each recommendation requires root, members, and evidence.");
        if (submission.Root.Pid <= 0 || submission.Root.StartTicks <= 0 || submission.Members.Count is < 1 or > 256)
            throw new AgentCliException(3, "Target identities or member count are invalid.");
        if (submission.ConfidencePct is < 0 or > 100)
            throw new AgentCliException(3, "confidencePct must be between 0 and 100.");
        if (submission.Evidence.Count is < 1 or > 16)
            throw new AgentCliException(3, "evidence must contain between 1 and 16 entries.");
        foreach (var evidence in submission.Evidence)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(evidence.Code ?? "", "^[A-Za-z0-9._-]{1,64}$") ||
                string.IsNullOrWhiteSpace(evidence.Detail) || evidence.Detail.Length > 500 || evidence.Detail.Any(char.IsControl) ||
                !IsPrivacySafeEvidenceDetail(evidence.Detail))
                throw new AgentCliException(3, "Advisory evidence contains an invalid code or detail.");
        }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsPrivacySafeEvidenceDetail(string value) =>
        !System.Text.RegularExpressions.Regex.IsMatch(
            value,
            @"(?ix)(?:[a-z]:[\\/]|\\\\|/(?:home|users)/|--?(?:token|api[-_]?key|password|secret)\b|bearer\s+\S+|\b[A-Z_][A-Z0-9_]{2,}=)");

    private static string IdentityKey(RecommendationMember member) => $"{member.Pid}:{member.StartTicks}";

    private static string CreateAgentRecommendationId(string advisoryId, string targetKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"v1|agent|{advisoryId}|{targetKey}|investigate"));
        return $"rec-{Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant()}";
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryDate(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(name, out var property) && property.TryGetDateTimeOffset(out value);
    }

    private sealed class ObservationAccumulator(ProcessIdentity identity, DateTimeOffset startedAtUtc)
    {
        private double _privateMb;
        private double _cpuPct;
        public ProcessIdentity Identity { get; } = identity;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public int Count { get; private set; }
        public DateTimeOffset LatestAtUtc { get; private set; }
        public DateTimeOffset? LastForegroundAtUtc { get; private set; }
        public double AveragePrivateMb => Count == 0 ? 0 : Math.Round(_privateMb / Count, 1);
        public double AverageCpuPct => Count == 0 ? 0 : Math.Round(_cpuPct / Count, 2);

        public void Add(JsonElement root, DateTimeOffset observedAt)
        {
            Count++;
            LatestAtUtc = observedAt > LatestAtUtc ? observedAt : LatestAtUtc;
            if (root.TryGetProperty("privateMb", out var privateMb) && privateMb.TryGetDouble(out var privateValue)) _privateMb += Math.Max(0, privateValue);
            if (root.TryGetProperty("cpuPercent", out var cpu) && cpu.TryGetDouble(out var cpuValue)) _cpuPct += Math.Max(0, cpuValue);
            if (root.TryGetProperty("isForeground", out var foreground) && foreground.ValueKind is JsonValueKind.True)
            {
                var lastInputAge = root.TryGetProperty("lastInputAgeSeconds", out var age) && age.TryGetDouble(out var ageValue) ? Math.Max(0, ageValue) : 0;
                var activityAt = observedAt.AddSeconds(-lastInputAge);
                LastForegroundAtUtc = LastForegroundAtUtc is null || activityAt > LastForegroundAtUtc ? activityAt : LastForegroundAtUtc;
            }
        }
    }

    private readonly record struct ActivityDetails(
        bool HasVisibleMainWindow,
        int SessionId,
        double? LastInputAgeSeconds,
        double ProcessAgeSeconds);
}

internal sealed record AgentSnapshotDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;
    [JsonPropertyName("snapshotHash")] public required string SnapshotHash { get; init; }
    [JsonPropertyName("generatedAtUtc")] public required DateTimeOffset GeneratedAtUtc { get; init; }
    [JsonPropertyName("window")] public required AgentSnapshotWindow Window { get; init; }
    [JsonPropertyName("freshness")] public required AgentSnapshotFreshness Freshness { get; init; }
    [JsonPropertyName("coverage")] public required AgentSnapshotCoverage Coverage { get; init; }
    [JsonPropertyName("groups")] public required IReadOnlyList<AgentSnapshotGroup> Groups { get; init; }
    [JsonPropertyName("recommendations")] public required IReadOnlyList<RecommendationRecord> Recommendations { get; init; }
}

internal sealed record AgentSnapshotWindow
{
    [JsonPropertyName("minutes")] public required int Minutes { get; init; }
    [JsonPropertyName("fromUtc")] public required DateTimeOffset FromUtc { get; init; }
    [JsonPropertyName("toUtc")] public required DateTimeOffset ToUtc { get; init; }
}

internal sealed record AgentSnapshotFreshness
{
    [JsonPropertyName("latestSampleAtUtc")] public DateTimeOffset? LatestSampleAtUtc { get; init; }
    [JsonPropertyName("ageSeconds")] public double? AgeSeconds { get; init; }
    [JsonPropertyName("isFresh")] public required bool IsFresh { get; init; }
}

internal sealed record AgentSnapshotCoverage
{
    [JsonPropertyName("sampleCount")] public required int SampleCount { get; init; }
    [JsonPropertyName("expectedSampleCount")] public required int ExpectedSampleCount { get; init; }
    [JsonPropertyName("pct")] public required double Pct { get; init; }
}

internal sealed record AgentSnapshotGroup
{
    [JsonPropertyName("groupKey")] public required string GroupKey { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("root")] public required RecommendationMember Root { get; init; }
    [JsonPropertyName("members")] public required IReadOnlyList<AgentSnapshotMember> Members { get; init; }
    [JsonPropertyName("ownerResolved")] public required bool OwnerResolved { get; init; }
    [JsonPropertyName("metrics")] public required ExpectedImpact Metrics { get; init; }
    [JsonPropertyName("lastActivityAtUtc")] public required DateTimeOffset LastActivityAtUtc { get; init; }
    [JsonPropertyName("safety")] public required AgentSnapshotSafety Safety { get; init; }
}

internal sealed record AgentSnapshotMember
{
    [JsonPropertyName("pid")] public required int Pid { get; init; }
    [JsonPropertyName("startTicks")] public required long StartTicks { get; init; }
    [JsonPropertyName("processName")] public required string ProcessName { get; init; }
    [JsonPropertyName("startedAtUtc")] public required DateTimeOffset StartedAtUtc { get; init; }
    [JsonPropertyName("observedAtUtc")] public required DateTimeOffset ObservedAtUtc { get; init; }
    [JsonPropertyName("lastActivityAtUtc")] public required DateTimeOffset LastActivityAtUtc { get; init; }
    [JsonPropertyName("sampleCount")] public required int SampleCount { get; init; }
    [JsonPropertyName("expectedSampleCount")] public required int ExpectedSampleCount { get; init; }
    [JsonPropertyName("privateMemoryMb")] public required double PrivateMemoryMb { get; init; }
    [JsonPropertyName("sustainedCpuPct")] public required double SustainedCpuPct { get; init; }
    [JsonPropertyName("isForeground")] public required bool IsForeground { get; init; }
    [JsonPropertyName("hasVisibleMainWindow")] public required bool HasVisibleMainWindow { get; init; }
    [JsonPropertyName("sessionId")] public required int SessionId { get; init; }
    [JsonPropertyName("lastInputAgeSeconds")] public double? LastInputAgeSeconds { get; init; }
    [JsonPropertyName("processAgeSeconds")] public required double ProcessAgeSeconds { get; init; }
    [JsonPropertyName("isSessionZero")] public required bool IsSessionZero { get; init; }
    [JsonPropertyName("isSystemLike")] public required bool IsSystemLike { get; init; }
    [JsonPropertyName("isServiceLike")] public required bool IsServiceLike { get; init; }
    [JsonPropertyName("identityValid")] public required bool IdentityValid { get; init; }
}

internal sealed record AgentSnapshotSafety
{
    [JsonPropertyName("isHardBlocked")] public required bool IsHardBlocked { get; init; }
    [JsonPropertyName("risk")] public required ActionRisk Risk { get; init; }
    [JsonPropertyName("flags")] public required IReadOnlyList<string> Flags { get; init; }
}

internal sealed record AgentAdvisoryDocument
{
    [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("advisoryId")] public required string AdvisoryId { get; init; }
    [JsonPropertyName("snapshotHash")] public required string SnapshotHash { get; init; }
    [JsonPropertyName("snapshotGeneratedAtUtc")] public required DateTimeOffset SnapshotGeneratedAtUtc { get; init; }
    [JsonPropertyName("createdAtUtc")] public required DateTimeOffset CreatedAtUtc { get; init; }
    [JsonPropertyName("expiresAtUtc")] public required DateTimeOffset ExpiresAtUtc { get; init; }
    [JsonPropertyName("recommendations")] public required IReadOnlyList<AgentRecommendationSubmission> Recommendations { get; init; }
}

internal sealed record AgentRecommendationSubmission
{
    [JsonPropertyName("root")] public required RecommendationMember Root { get; init; }
    [JsonPropertyName("members")] public required IReadOnlyList<RecommendationMember> Members { get; init; }
    [JsonPropertyName("action")] public required RecommendationAction Action { get; init; }
    [JsonPropertyName("confidencePct")] public required int ConfidencePct { get; init; }
    [JsonPropertyName("evidence")] public required IReadOnlyList<AgentAdvisoryEvidence> Evidence { get; init; }
}

internal sealed record AgentAdvisoryEvidence
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("detail")] public required string Detail { get; init; }
}

internal sealed record RecommendationListDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;
    [JsonPropertyName("generatedAtUtc")] public required DateTimeOffset GeneratedAtUtc { get; init; }
    [JsonPropertyName("recommendations")] public required IReadOnlyList<RecommendationRecord> Recommendations { get; init; }
}

internal sealed record RecommendationImportResult
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;
    [JsonPropertyName("advisoryId")] public required string AdvisoryId { get; init; }
    [JsonPropertyName("acceptedCount")] public required int AcceptedCount { get; init; }
    [JsonPropertyName("recommendationIds")] public required IReadOnlyList<string> RecommendationIds { get; init; }
}

internal sealed class AgentCliException : Exception
{
    public AgentCliException(int exitCode, string message, Exception? innerException = null) : base(message, innerException) => ExitCode = exitCode;
    public int ExitCode { get; }
}
