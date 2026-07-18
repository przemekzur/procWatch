using System.Text.Json;
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
    {
        var path = executablePath ?? "";
        var command = commandLine ?? "";
        var combined = $"{path} {command}";
        return _rules.FirstOrDefault(rule => Matches(rule, processName, path, command, combined));
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

    private static bool IsValid(ClassificationRule rule) => !string.IsNullOrWhiteSpace(rule.Category);

    private static ClassificationRules LoadInstalledRules()
    {
        var custom = Path.Combine(AppSettings.AppDirectory, "rules.json");
        var defaults = Path.Combine(AppContext.BaseDirectory, "rules.default.json");
        return LoadFromFiles(custom, defaults);
    }
}

internal sealed record ClassificationRuleDocument
{
    public int SchemaVersion { get; init; } = 1;
    public ClassificationRule[] Rules { get; init; } = [];
}

internal sealed record ClassificationRule
{
    public required string Category { get; init; }
    public string? Process { get; init; }
    public string[]? PathContainsAll { get; init; }
    public string[]? CommandContainsAll { get; init; }
    public string[]? TextContainsAny { get; init; }
    public string? TextRegex { get; init; }
    public bool OwnerRoot { get; init; }
    public string? OwnerLabel { get; init; }
    public bool Track { get; init; } = true;
}
