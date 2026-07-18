using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProcLens;

internal sealed class ClassificationRules
{
    private static readonly Lazy<ClassificationRules> CurrentRules = new(LoadInstalledRules);
    private readonly IReadOnlyList<ClassificationRule> _rules;

    private ClassificationRules(IReadOnlyList<ClassificationRule> rules) => _rules = rules;

    public static ClassificationRules Current => CurrentRules.Value;

    public string Classify(string processName, string? executablePath, string commandLine) =>
        Match(processName, executablePath, commandLine)?.Category ?? processName;

    public bool IsOwnerRoot(string processName, string? executablePath, string commandLine) =>
        Match(processName, executablePath, commandLine)?.OwnerRoot == true;

    public bool ShouldTrack(string processName, string? executablePath, string commandLine) =>
        Match(processName, executablePath, commandLine)?.Track == true;

    public string? OwnerLabel(string processName, string? executablePath, string commandLine) =>
        Match(processName, executablePath, commandLine) is { OwnerRoot: true } rule
            ? rule.OwnerLabel ?? rule.Category
            : null;

    public ClassificationRecommendation RecommendationFor(string processName, string? executablePath, string commandLine)
    {
        var matches = MatchingRules(processName, executablePath, commandLine).ToArray();
        return matches.Length == 0
            ? new ClassificationRecommendation()
            : new ClassificationRecommendation
            {
                Criticality = matches.Max(rule => rule.Criticality),
                RecommendationPolicy = matches.Max(rule => rule.RecommendationPolicy),
                MinimumIdleMinutes = matches
                    .Where(rule => rule.MinimumIdleMinutes.HasValue)
                    .Select(rule => rule.MinimumIdleMinutes)
                    .Max()
            };
    }

    internal static ClassificationRules LoadFromFiles(params string[] paths)
    {
        var rules = new List<ClassificationRule>();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                var document = JsonSerializer.Deserialize<ClassificationRuleDocument>(File.ReadAllText(path), options);
                if (document?.Rules is not null) rules.AddRange(document.Rules.Where(IsValid));
            }
            catch (JsonException)
            {
                // A malformed optional rule file must never stop collection.
            }
        }
        return new ClassificationRules(rules);
    }

    private ClassificationRule? Match(string processName, string? executablePath, string commandLine)
        => MatchingRules(processName, executablePath, commandLine).FirstOrDefault();

    private IEnumerable<ClassificationRule> MatchingRules(string processName, string? executablePath, string commandLine)
    {
        var path = executablePath ?? "";
        var command = commandLine ?? "";
        var combined = $"{path} {command}";
        return _rules.Where(rule => Matches(rule, processName, path, command, combined));
    }

    private static bool Matches(ClassificationRule rule, string processName, string path, string command, string combined)
    {
        if (!string.IsNullOrWhiteSpace(rule.Process) && !processName.Equals(rule.Process, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!ContainsAll(path, rule.PathContainsAll) || !ContainsAll(command, rule.CommandContainsAll) ||
            !ContainsAny(combined, rule.TextContainsAny))
            return false;
        if (string.IsNullOrWhiteSpace(rule.TextRegex)) return true;
        try { return Regex.IsMatch(combined, rule.TextRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return false; }
    }

    private static bool ContainsAll(string source, string[]? values) =>
        values is null || values.Length == 0 || values.All(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string source, string[]? values) =>
        values is null || values.Length == 0 || values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsValid(ClassificationRule rule) =>
        !string.IsNullOrWhiteSpace(rule.Category) && rule.MinimumIdleMinutes is null or >= 0;

    private static ClassificationRules LoadInstalledRules()
    {
        var custom = Path.Combine(AppSettings.AppDirectory, "rules.json");
        var defaults = Path.Combine(AppContext.BaseDirectory, "rules.default.json");
        return LoadFromFiles(custom, defaults);
    }
}

internal sealed record ClassificationRuleDocument
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("rules")]
    public ClassificationRule[] Rules { get; init; } = [];
}

internal sealed record ClassificationRule
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("process")]
    public string? Process { get; init; }

    [JsonPropertyName("pathContainsAll")]
    public string[]? PathContainsAll { get; init; }

    [JsonPropertyName("commandContainsAll")]
    public string[]? CommandContainsAll { get; init; }

    [JsonPropertyName("textContainsAny")]
    public string[]? TextContainsAny { get; init; }

    [JsonPropertyName("textRegex")]
    public string? TextRegex { get; init; }

    [JsonPropertyName("ownerRoot")]
    public bool OwnerRoot { get; init; }

    [JsonPropertyName("ownerLabel")]
    public string? OwnerLabel { get; init; }

    [JsonPropertyName("track")]
    public bool Track { get; init; } = true;

    [JsonPropertyName("criticality")]
    public ClassificationCriticality Criticality { get; init; }

    [JsonPropertyName("recommendationPolicy")]
    public RecommendationPolicy RecommendationPolicy { get; init; }

    [JsonPropertyName("minimumIdleMinutes")]
    public int? MinimumIdleMinutes { get; init; }
}

internal sealed record ClassificationRecommendation
{
    [JsonPropertyName("criticality")]
    public ClassificationCriticality Criticality { get; init; }

    [JsonPropertyName("recommendationPolicy")]
    public RecommendationPolicy RecommendationPolicy { get; init; }

    [JsonPropertyName("minimumIdleMinutes")]
    public int? MinimumIdleMinutes { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassificationCriticality>))]
internal enum ClassificationCriticality
{
    [JsonStringEnumMemberName("normal")]
    Normal,
    [JsonStringEnumMemberName("important")]
    Important,
    [JsonStringEnumMemberName("protected")]
    Protected,
    [JsonStringEnumMemberName("system")]
    System
}

[JsonConverter(typeof(JsonStringEnumConverter<RecommendationPolicy>))]
internal enum RecommendationPolicy
{
    [JsonStringEnumMemberName("default")]
    Default,
    [JsonStringEnumMemberName("investigateOnly")]
    InvestigateOnly,
    [JsonStringEnumMemberName("modelResident")]
    ModelResident,
    [JsonStringEnumMemberName("neverEnd")]
    NeverEnd
}
