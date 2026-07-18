using System.Text.Json.Serialization;

namespace ProcLens;

internal static class RecommendationSchema
{
    public const int CurrentVersion = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<RecommendationAction>))]
internal enum RecommendationAction
{
    [JsonStringEnumMemberName("investigate")]
    Investigate,
    [JsonStringEnumMemberName("closeGracefully")]
    CloseGracefully,
    [JsonStringEnumMemberName("restart")]
    Restart,
    [JsonStringEnumMemberName("disableStartup")]
    DisableStartup
}

[JsonConverter(typeof(JsonStringEnumConverter<RecommendationSource>))]
internal enum RecommendationSource
{
    [JsonStringEnumMemberName("core")]
    Core,
    [JsonStringEnumMemberName("agent")]
    Agent
}

[JsonConverter(typeof(JsonStringEnumConverter<ConfidenceKind>))]
internal enum ConfidenceKind
{
    [JsonStringEnumMemberName("low")]
    Low,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("high")]
    High
}

[JsonConverter(typeof(JsonStringEnumConverter<ActionRisk>))]
internal enum ActionRisk
{
    [JsonStringEnumMemberName("low")]
    Low,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("high")]
    High,
    [JsonStringEnumMemberName("blocked")]
    Blocked
}

[JsonConverter(typeof(JsonStringEnumConverter<RecommendationState>))]
internal enum RecommendationState
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("needed")]
    Needed,
    [JsonStringEnumMemberName("snoozed")]
    Snoozed,
    [JsonStringEnumMemberName("acted")]
    Acted,
    [JsonStringEnumMemberName("dismissed")]
    Dismissed,
    [JsonStringEnumMemberName("expired")]
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter<RecommendationDecisionKind>))]
internal enum RecommendationDecisionKind
{
    [JsonStringEnumMemberName("needed")]
    Needed,
    [JsonStringEnumMemberName("snooze")]
    Snooze,
    [JsonStringEnumMemberName("dismiss")]
    Dismiss
}

[JsonConverter(typeof(JsonStringEnumConverter<ActionResultKind>))]
internal enum ActionResultKind
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("blocked")]
    Blocked,
    [JsonStringEnumMemberName("identityChanged")]
    IdentityChanged
}

internal sealed record RecommendationMember
{
    [JsonIgnore]
    public ProcessIdentity Identity { get; }

    [JsonPropertyName("pid")]
    public int Pid => Identity.Pid;

    [JsonPropertyName("startTicks")]
    public long StartTicks => Identity.StartTicks;

    [JsonConstructor]
    public RecommendationMember(int pid, long startTicks) : this(new ProcessIdentity(pid, startTicks)) { }

    public RecommendationMember(ProcessIdentity identity) => Identity = identity;
}

internal sealed record RecommendationEvidence
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }

    [JsonPropertyName("confidenceDelta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfidenceDelta { get; init; }

    [JsonPropertyName("confidenceCeilingPct")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfidenceCeilingPct { get; init; }
}

internal sealed record ExpectedImpact
{
    [JsonPropertyName("privateMemoryMb")]
    public required double PrivateMemoryMb { get; init; }

    [JsonPropertyName("sustainedCpuPct")]
    public required double SustainedCpuPct { get; init; }
}

internal sealed record RecommendationConfidence
{
    [JsonPropertyName("kind")]
    public required ConfidenceKind Kind { get; init; }

    [JsonPropertyName("pct")]
    public required int Pct { get; init; }
}

internal sealed record RecommendationTargetGroup
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("root")]
    public required RecommendationMember Root { get; init; }

    [JsonPropertyName("members")]
    public required IReadOnlyList<RecommendationMember> Members { get; init; }

    [JsonPropertyName("resolved")]
    public required bool Resolved { get; init; }
}

internal sealed record RecommendationProvenance
{
    [JsonPropertyName("source")]
    public required RecommendationSource Source { get; init; }

    [JsonPropertyName("advisoryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdvisoryId { get; init; }
}

internal sealed record RecommendationRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("targetGroup")]
    public required RecommendationTargetGroup TargetGroup { get; init; }

    [JsonPropertyName("action")]
    public required RecommendationAction Action { get; init; }

    [JsonPropertyName("provenance")]
    public required RecommendationProvenance Provenance { get; init; }

    [JsonPropertyName("confidence")]
    public required RecommendationConfidence Confidence { get; init; }

    [JsonPropertyName("risk")]
    public required ActionRisk Risk { get; init; }

    [JsonPropertyName("expectedImpact")]
    public required ExpectedImpact ExpectedImpact { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<RecommendationEvidence> Evidence { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("expiresAtUtc")]
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    [JsonPropertyName("state")]
    public RecommendationState State { get; init; } = RecommendationState.Active;
}

internal sealed record RecommendationDecision
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;

    [JsonPropertyName("recommendationId")]
    public required string RecommendationId { get; init; }

    [JsonPropertyName("decision")]
    public required RecommendationDecisionKind Decision { get; init; }

    [JsonPropertyName("decidedAtUtc")]
    public required DateTimeOffset DecidedAtUtc { get; init; }

    [JsonPropertyName("snoozedUntilUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SnoozedUntilUtc { get; init; }
}

internal sealed record RecommendationActionResult
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;

    [JsonPropertyName("recommendationId")]
    public required string RecommendationId { get; init; }

    [JsonPropertyName("result")]
    public required ActionResultKind Result { get; init; }

    [JsonPropertyName("completedAtUtc")]
    public required DateTimeOffset CompletedAtUtc { get; init; }

    [JsonPropertyName("detailCode")]
    public required string DetailCode { get; init; }
}

internal sealed record ProcessObservation
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;

    [JsonPropertyName("identity")]
    public required RecommendationMember Identity { get; init; }

    [JsonPropertyName("processName")]
    public required string ProcessName { get; init; }

    [JsonPropertyName("ownerIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecommendationMember? OwnerIdentity { get; init; }

    [JsonPropertyName("ownerLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerLabel { get; init; }

    [JsonPropertyName("ownerResolved")]
    public bool OwnerResolved { get; init; } = true;

    [JsonPropertyName("isOwnerRoot")]
    public bool IsOwnerRoot { get; init; }

    [JsonPropertyName("privateMemoryMb")]
    public double PrivateMemoryMb { get; init; }

    [JsonPropertyName("sustainedCpuPct")]
    public double SustainedCpuPct { get; init; }

    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; init; }

    [JsonPropertyName("expectedSampleCount")]
    public int ExpectedSampleCount { get; init; }

    [JsonPropertyName("observedAtUtc")]
    public required DateTimeOffset ObservedAtUtc { get; init; }

    [JsonPropertyName("startedAtUtc")]
    public required DateTimeOffset StartedAtUtc { get; init; }

    [JsonPropertyName("lastActivityAtUtc")]
    public required DateTimeOffset LastActivityAtUtc { get; init; }

    [JsonPropertyName("identityValid")]
    public bool IdentityValid { get; init; } = true;

    [JsonPropertyName("isForeground")]
    public bool IsForeground { get; init; }

    [JsonPropertyName("isSystemLike")]
    public bool IsSystemLike { get; init; }

    [JsonPropertyName("isSessionZero")]
    public bool IsSessionZero { get; init; }

    [JsonPropertyName("isServiceLike")]
    public bool IsServiceLike { get; init; }

    [JsonPropertyName("criticality")]
    public ClassificationCriticality Criticality { get; init; }

    [JsonPropertyName("recommendationPolicy")]
    public RecommendationPolicy RecommendationPolicy { get; init; }

    [JsonPropertyName("minimumIdleMinutes")]
    public int MinimumIdleMinutes { get; init; } = RecommendationEngineOptions.DefaultMinimumIdleMinutes;

    [JsonPropertyName("hasRuleEvidence")]
    public bool HasRuleEvidence { get; init; } = true;

    [JsonPropertyName("hasSupportedUnloadModelAdvisory")]
    public bool HasSupportedUnloadModelAdvisory { get; init; }
}

internal sealed record ProcessGroupObservation
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = RecommendationSchema.CurrentVersion;

    [JsonPropertyName("groupKey")]
    public required string GroupKey { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("root")]
    public required RecommendationMember Root { get; init; }

    [JsonPropertyName("members")]
    public required IReadOnlyList<ProcessObservation> Members { get; init; }

    [JsonPropertyName("ownerResolved")]
    public required bool OwnerResolved { get; init; }

    [JsonPropertyName("criticality")]
    public required ClassificationCriticality Criticality { get; init; }

    [JsonPropertyName("recommendationPolicy")]
    public required RecommendationPolicy RecommendationPolicy { get; init; }

    [JsonPropertyName("minimumIdleMinutes")]
    public required int MinimumIdleMinutes { get; init; }
}
