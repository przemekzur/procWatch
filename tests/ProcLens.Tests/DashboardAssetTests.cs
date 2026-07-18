using System.Runtime.CompilerServices;
using Xunit;

namespace ProcLens.Tests;

public sealed class DashboardAssetTests
{
    private readonly string _html = Asset("index.html");
    private readonly string _script = Asset("app.js");
    private readonly string _styles = Asset("styles.css");

    [Fact]
    public void DashboardDeclaresSavingsQueueAndAccessibleOutcomeHooks()
    {
        Assert.Contains("id=\"potentialSavings\"", _html);
        Assert.Contains("id=\"savingsMemory\"", _html);
        Assert.Contains("id=\"savingsCpu\"", _html);
        Assert.Contains("id=\"recommendations\"", _html);
        Assert.Contains("aria-label=\"Process optimization recommendations\"", _html);
        Assert.Contains("id=\"queueOutcome\"", _html);
        Assert.Contains("aria-live=\"polite\"", _html);

        var optimization = _html.IndexOf("id=\"optimization\"", StringComparison.Ordinal);
        var unresolved = _html.IndexOf("id=\"activity\"", StringComparison.Ordinal);
        Assert.True(optimization >= 0 && unresolved > optimization,
            "The optimization queue must appear before the unresolved-owner/activity section.");
    }

    [Fact]
    public void RecommendationRendererSeparatesConfidenceImpactRiskAndProvenance()
    {
        Assert.Contains("Evidence confidence", _script);
        Assert.Contains("Expected impact", _script);
        Assert.Contains("estimate, not confidence", _script);
        Assert.Contains("Action risk", _script);
        Assert.Contains("Provenance & freshness", _script);
        Assert.Contains("Last meaningful activity", _script);
        Assert.Contains("Review evidence", _script);
        Assert.Contains("<details class=\"evidence\">", _script);
        Assert.Contains("data-confidence=", _script);
        Assert.Contains("Agent advisories are evidence only", _script);
    }

    [Fact]
    public void RecommendationActionsUseAuthenticatedPostRoutesAndRefresh()
    {
        Assert.Contains("needed: \"/api/recommendations/needed\"", _script);
        Assert.Contains("snooze: \"/api/recommendations/snooze\"", _script);
        Assert.Contains("closeGracefully: \"/api/recommendations/closeGracefully\"", _script);
        Assert.Contains("${routes[action]}?token=", _script);
        Assert.Contains("method: \"POST\"", _script);
        Assert.Contains("\"Content-Type\": \"application/json\"", _script);
        Assert.Contains("body: JSON.stringify(body)", _script);
        Assert.Contains("snoozeMinutes = 30", _script);
        Assert.Contains("state.pendingRecommendation", _script);
        Assert.Contains("await load(false, true)", _script);
        Assert.Contains("data-recommendation-action=\"needed\"", _script);
        Assert.Contains("data-recommendation-action=\"snooze\"", _script);
        Assert.Contains("data-recommendation-action=\"closeGracefully\"", _script);
    }

    [Fact]
    public void PendingDecisionDisablesTheWholeQueueAndRejectsCompetingRefreshes()
    {
        Assert.Contains("const queuePending = state.pendingRecommendation !== null;", _script);
        Assert.Contains("queue.setAttribute(\"aria-busy\", String(queuePending))", _script);
        Assert.Contains("!canDecide || queuePending", _script);
        Assert.Contains("!canClose || queuePending", _script);
        Assert.Contains("if (state.pendingRecommendation && !decisionRefresh) return false;", _script);
        Assert.Contains("state.loadVersion += 1;", _script);
        Assert.Contains("requestVersion !== state.loadVersion", _script);
    }

    [Fact]
    public void CompletedServerResponsesRefreshBeforeAnnouncingSuccessOrRejection()
    {
        Assert.Contains("responseReceived = true;", _script);
        Assert.Contains("if (responseReceived) refreshed = await load(false, true);", _script);

        var rejection = _script.IndexOf("Close rejected:", StringComparison.Ordinal);
        var refresh = _script.IndexOf("if (responseReceived) refreshed = await load(false, true);", StringComparison.Ordinal);
        var announcement = _script.IndexOf("announceQueue(outcomeMessage, outcomeKind);", StringComparison.Ordinal);
        Assert.True(rejection >= 0 && refresh > rejection && announcement > refresh,
            "Every completed server response must refresh the queue before its specific outcome is announced.");
    }

    [Fact]
    public void GracefulCloseRequiresGroupSpecificImpactConfirmationAndSafetyGating()
    {
        Assert.Contains("window.confirm(confirmation)", _script);
        Assert.Contains("Close “${label}” gracefully?", _script);
        Assert.Contains("Expected impact: reclaim about ${memory", _script);
        Assert.Contains("request a graceful close only", _script);
        Assert.Contains("item.provenance?.source === \"agent\"", _script);
        Assert.Contains("item.risk === \"blocked\"", _script);
        Assert.Contains("item.action !== \"closeGracefully\"", _script);
        Assert.Contains("!item.targetGroup?.resolved", _script);
        Assert.DoesNotContain("forceTerminate", _script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/recommendations/kill", _script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UntrustedRecommendationStringsUseEscapingOrTextOnlyAssignment()
    {
        Assert.Contains("const esc =", _script);
        Assert.Contains("${esc(label)}", _script);
        Assert.Contains("${esc(entry.code", _script);
        Assert.Contains("${esc(entry.detail", _script);
        Assert.Contains("${esc(activity)}", _script);
        Assert.Contains("outcome.textContent = message", _script);
        Assert.Contains("savingsCount\").textContent", _script);
        Assert.DoesNotContain("innerHTML = message", _script);
    }

    [Fact]
    public void QueueDesignCoversOperationalStatesAndInputModes()
    {
        Assert.Contains("Queue clear", _script);
        Assert.Contains("Optimization queue offline", _script);
        Assert.Contains("currentState === \"active\"", _script);
        Assert.Contains("item.state === \"expired\"", _script);
        Assert.Contains("Close rejected:", _script);
        Assert.Contains("data-kind=\"success\"", _styles);
        Assert.Contains("data-kind=\"error\"", _styles);
        Assert.Contains("data-confidence=\"low\"", _styles);
        Assert.Contains(":focus-visible", _styles);
        Assert.Contains("@media (pointer: coarse)", _styles);
        Assert.Contains("min-height: 44px", _styles);
        Assert.Contains("@media (max-width: 650px)", _styles);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", _styles);
        Assert.Contains("setInterval(load, 15000)", _script);
        Assert.Contains("--secondary-surface", _styles);
        Assert.Contains("--expired-surface", _styles);
        Assert.DoesNotContain(".recommendation-card[data-confidence=\"low\"] { opacity", _styles);
        Assert.DoesNotContain(".recommendation-card[data-state=\"expired\"] { opacity", _styles);
    }

    private static string Asset(string name, [CallerFilePath] string sourcePath = "")
    {
        var testDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("Could not locate the test source directory.");
        var path = Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "wwwroot", name));
        return File.ReadAllText(path);
    }
}
