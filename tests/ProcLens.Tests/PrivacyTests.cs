namespace ProcLens.Tests;

public sealed class PrivacyTests
{
    [Fact]
    public void SanitizerRedactsCommonSecrets()
    {
        var result = Program.SanitizeCommand("tool --token abc123 --api-key=xyz --password \"open sesame\" Authorization: Bearer bearer-token");

        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("xyz", result);
        Assert.DoesNotContain("open sesame", result);
        Assert.DoesNotContain("bearer-token", result);
        Assert.Contains("<redacted>", result);
    }

    [Fact]
    public void NormalizerRemovesUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = Program.NormalizePath(Path.Combine(profile, "Projects", "private-project"));

        Assert.NotNull(result);
        Assert.DoesNotContain(profile, result!, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("%USERPROFILE%", result!, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandHashIsStableAndShort()
    {
        var first = Program.HashCommand("node example.js");
        var second = Program.HashCommand("node example.js");

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
    }
}
