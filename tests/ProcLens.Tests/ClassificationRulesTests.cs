using System.Text.Json;

namespace ProcLens.Tests;

public sealed class ClassificationRulesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "proclens-rules-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CustomRulesOverrideDefaultsAndCanDefineOwnerRoots()
    {
        Directory.CreateDirectory(_directory);
        var customPath = Path.Combine(_directory, "custom.json");
        var defaultPath = Path.Combine(_directory, "default.json");
        WriteRules(customPath, new ClassificationRule
        {
            Category = "Custom Node Service",
            Process = "node",
            CommandContainsAll = ["service.mjs"],
            OwnerRoot = true,
            OwnerLabel = "Custom Service"
        });
        WriteRules(defaultPath, new ClassificationRule { Category = "Other Node", Process = "node" });

        var rules = ClassificationRules.LoadFromFiles(customPath, defaultPath);

        Assert.Equal("Custom Node Service", rules.Classify("node", null, "node service.mjs"));
        Assert.True(rules.IsOwnerRoot("node", null, "node service.mjs"));
        Assert.Equal("Custom Service", rules.OwnerLabel("node", null, "node service.mjs"));
        Assert.Equal("Other Node", rules.Classify("node", null, "node unrelated.js"));
    }

    [Fact]
    public void MalformedOptionalFileDoesNotStopDefaultRules()
    {
        Directory.CreateDirectory(_directory);
        var malformed = Path.Combine(_directory, "broken.json");
        var defaults = Path.Combine(_directory, "default.json");
        File.WriteAllText(malformed, "{not json");
        WriteRules(defaults, new ClassificationRule { Category = "Browser", Process = "chrome" });

        var rules = ClassificationRules.LoadFromFiles(malformed, defaults);

        Assert.Equal("Browser", rules.Classify("chrome", null, "chrome.exe"));
    }

    [Fact]
    public void SchemaVersionOneFilesRemainCompatible()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "v1.json");
        File.WriteAllText(path, """
            { "schemaVersion": 1, "rules": [ { "category": "Legacy", "process": "legacy" } ] }
            """);

        var rules = ClassificationRules.LoadFromFiles(path);
        var recommendation = rules.RecommendationFor("legacy", null, "legacy.exe");

        Assert.Equal("Legacy", rules.Classify("legacy", null, "legacy.exe"));
        Assert.Equal(ClassificationCriticality.Normal, recommendation.Criticality);
        Assert.Equal(RecommendationPolicy.Default, recommendation.RecommendationPolicy);
        Assert.Null(recommendation.MinimumIdleMinutes);
    }

    [Fact]
    public void CurrentRuleDocumentsSerializeWithStableSchemaVersionTwoNames()
    {
        var json = JsonSerializer.Serialize(new ClassificationRuleDocument
        {
            Rules = [new ClassificationRule
            {
                Category = "Protected",
                Process = "protected-app",
                Criticality = ClassificationCriticality.Protected,
                RecommendationPolicy = RecommendationPolicy.NeverEnd,
                MinimumIdleMinutes = 60
            }]
        });

        Assert.Contains("\"schemaVersion\":2", json);
        Assert.Contains("\"recommendationPolicy\":\"neverEnd\"", json);
        Assert.Contains("\"minimumIdleMinutes\":60", json);
        Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendationPolicyFieldsAreParsedAndCustomRulesCanNarrowBehavior()
    {
        Directory.CreateDirectory(_directory);
        var customPath = Path.Combine(_directory, "custom.json");
        var defaultPath = Path.Combine(_directory, "default.json");
        WriteRules(customPath, new ClassificationRule
        {
            Category = "Protected Browser",
            Process = "chrome",
            Criticality = ClassificationCriticality.Protected,
            RecommendationPolicy = RecommendationPolicy.NeverEnd,
            MinimumIdleMinutes = 90
        });
        WriteRules(defaultPath, new ClassificationRule { Category = "Browser", Process = "chrome" });

        var recommendation = ClassificationRules.LoadFromFiles(customPath, defaultPath)
            .RecommendationFor("chrome", null, "chrome.exe");

        Assert.Equal(ClassificationCriticality.Protected, recommendation.Criticality);
        Assert.Equal(RecommendationPolicy.NeverEnd, recommendation.RecommendationPolicy);
        Assert.Equal(90, recommendation.MinimumIdleMinutes);
    }

    [Fact]
    public void InvalidMinimumIdleRuleIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        var customPath = Path.Combine(_directory, "custom.json");
        var defaultPath = Path.Combine(_directory, "default.json");
        WriteRules(customPath, new ClassificationRule { Category = "Invalid", Process = "node", MinimumIdleMinutes = -1 });
        WriteRules(defaultPath, new ClassificationRule { Category = "Other Node", Process = "node" });

        var rules = ClassificationRules.LoadFromFiles(customPath, defaultPath);

        Assert.Equal("Other Node", rules.Classify("node", null, "node app.js"));
    }

    [Fact]
    public void OptionalViriCrewPackMatchesWithoutProductCodeDependencies()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "rules.viricrew.example.json");
        Assert.True(File.Exists(path));
        var rules = ClassificationRules.LoadFromFiles(path);

        Assert.Equal("ViriCrew MCP", rules.Classify("node", null, @"node C:\tools\viricrew\dist\index.js"));
        Assert.Equal("Suno", rules.Classify("node", @"C:\Users\user\Projects\suno\server.js", "node server.js"));
        Assert.True(rules.IsOwnerRoot("viricrew-desktop", null, "viricrew-desktop.exe"));
        var viriVox = rules.RecommendationFor("ViriVox", null, "ViriVox.exe");
        Assert.Equal(ClassificationCriticality.Important, viriVox.Criticality);
        Assert.Equal(RecommendationPolicy.ModelResident, viriVox.RecommendationPolicy);
        Assert.Equal(0, viriVox.MinimumIdleMinutes);
    }

    [Fact]
    public void DefaultRulesProtectExplorerProcLensAndSystemLikeTargets()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "rules.default.json");
        var rules = ClassificationRules.LoadFromFiles(path);

        var explorer = rules.RecommendationFor("explorer", null, "explorer.exe");
        var procLens = rules.RecommendationFor("ProcLens", null, "ProcLens.exe");
        var wsl = rules.RecommendationFor("vmmemWSL", null, "vmmemWSL.exe");

        Assert.Equal(ClassificationCriticality.Protected, explorer.Criticality);
        Assert.Equal(RecommendationPolicy.NeverEnd, explorer.RecommendationPolicy);
        Assert.Equal(ClassificationCriticality.Protected, procLens.Criticality);
        Assert.Equal(RecommendationPolicy.NeverEnd, procLens.RecommendationPolicy);
        Assert.Equal(ClassificationCriticality.System, wsl.Criticality);
        Assert.Equal(RecommendationPolicy.NeverEnd, wsl.RecommendationPolicy);
    }

    [Fact]
    public void CustomRulesCannotLoosenBuiltInRecommendationSafety()
    {
        Directory.CreateDirectory(_directory);
        var customPath = Path.Combine(_directory, "custom.json");
        var defaultPath = Path.Combine(AppContext.BaseDirectory, "rules.default.json");
        WriteRules(customPath,
            new ClassificationRule { Category = "Custom Explorer", Process = "explorer" },
            new ClassificationRule { Category = "Custom WSL", Process = "vmmemWSL" });

        var rules = ClassificationRules.LoadFromFiles(customPath, defaultPath);
        var explorer = rules.RecommendationFor("explorer", null, "explorer.exe");
        var wsl = rules.RecommendationFor("vmmemWSL", null, "vmmemWSL.exe");

        Assert.Equal("Custom Explorer", rules.Classify("explorer", null, "explorer.exe"));
        Assert.Equal(ClassificationCriticality.Protected, explorer.Criticality);
        Assert.Equal(RecommendationPolicy.NeverEnd, explorer.RecommendationPolicy);
        Assert.Equal("Custom WSL", rules.Classify("vmmemWSL", null, "vmmemWSL.exe"));
        Assert.Equal(ClassificationCriticality.System, wsl.Criticality);
        Assert.Equal(RecommendationPolicy.NeverEnd, wsl.RecommendationPolicy);
    }

    private static void WriteRules(string path, params ClassificationRule[] rules) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new ClassificationRuleDocument { Rules = rules }));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
