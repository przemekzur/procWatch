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
    public void OptionalViriCrewPackMatchesWithoutProductCodeDependencies()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "rules.viricrew.example.json");
        Assert.True(File.Exists(path));
        var rules = ClassificationRules.LoadFromFiles(path);

        Assert.Equal("ViriCrew MCP", rules.Classify("node", null, @"node C:\tools\viricrew\dist\index.js"));
        Assert.Equal("Suno", rules.Classify("node", @"C:\Users\user\Projects\suno\server.js", "node server.js"));
        Assert.True(rules.IsOwnerRoot("viricrew-desktop", null, "viricrew-desktop.exe"));
    }

    private static void WriteRules(string path, params ClassificationRule[] rules) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new ClassificationRuleDocument { Rules = rules }));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
