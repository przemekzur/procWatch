using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ProcLens.Tests;

public sealed class DashboardRecommendationTests : IDisposable
{
    private const string Token = "dashboard-test-token";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "proclens-dashboard-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task DashboardProjectionIncludesRecommendationDetailsAndPotentialSavings()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now);
        store.Upsert(recommendation);
        _ = DashboardData.Build(_directory, 60);
        using var fixture = StartServer(store, recommendation);

        var response = await SendAsync(fixture.Server.BoundPort,
            $"GET /api/dashboard?token={Token} HTTP/1.1\r\nHost: 127.0.0.1:{fixture.Server.BoundPort}\r\n\r\n");

        Assert.Equal(200, response.Status);
        Assert.Contains("Content-Security-Policy:", response.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cache-Control: no-store", response.Headers, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(response.Body);
        var card = Assert.Single(json.RootElement.GetProperty("recommendations").EnumerateArray());
        Assert.Equal(recommendation.Id, card.GetProperty("id").GetString());
        Assert.Equal("core", card.GetProperty("provenance").GetProperty("source").GetString());
        Assert.Equal(90, card.GetProperty("confidence").GetProperty("pct").GetInt32());
        Assert.Equal("low", card.GetProperty("risk").GetString());
        Assert.Equal("active", card.GetProperty("state").GetString());
        Assert.Equal("test.safe", card.GetProperty("evidence")[0].GetProperty("code").GetString());
        var savings = json.RootElement.GetProperty("potentialSavings");
        Assert.Equal(256.5, savings.GetProperty("privateMemoryMb").GetDouble());
        Assert.Equal(7.5, savings.GetProperty("sustainedCpuPct").GetDouble());
        Assert.Equal(1, savings.GetProperty("recommendationCount").GetInt32());
    }

    [Fact]
    public async Task NeededAndSnoozeRoutesPersistStrictUserDecisions()
    {
        using var store = Store();
        var needed = TestRecommendationFactory.Create(_now, id: "rec-needed");
        var snoozed = TestRecommendationFactory.Create(_now, id: "rec-snoozed");
        store.Upsert(needed);
        store.Upsert(snoozed);
        using var fixture = StartServer(store, needed);

        var neededResponse = await PostAsync(fixture.Server.BoundPort, "/api/recommendations/needed",
            "{\"recommendationId\":\"rec-needed\"}");
        var snoozeResponse = await PostAsync(fixture.Server.BoundPort, "/api/recommendations/snooze",
            "{\"recommendationId\":\"rec-snoozed\",\"snoozeMinutes\":30}");

        Assert.Equal(200, neededResponse.Status);
        Assert.Equal(200, snoozeResponse.Status);
        Assert.Equal(RecommendationState.Needed, store.FindById(needed.Id)!.State);
        Assert.Equal(RecommendationState.Snoozed, store.FindById(snoozed.Id)!.State);
    }

    [Fact]
    public async Task PostRequiresTokenHostOriginAndExactJsonContentType()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now);
        store.Upsert(recommendation);
        using var fixture = StartServer(store, recommendation);
        var port = fixture.Server.BoundPort;
        const string body = "{\"recommendationId\":\"rec-test\"}";

        var invalidToken = await SendPostAsync(port, "/api/recommendations/needed", body,
            token: "wrong", host: $"127.0.0.1:{port}", origin: $"http://127.0.0.1:{port}", contentType: "application/json");
        var invalidHost = await SendPostAsync(port, "/api/recommendations/needed", body,
            token: Token, host: "example.com", origin: $"http://127.0.0.1:{port}", contentType: "application/json");
        var invalidOrigin = await SendPostAsync(port, "/api/recommendations/needed", body,
            token: Token, host: $"127.0.0.1:{port}", origin: $"http://localhost:{port}", contentType: "application/json");
        var invalidContentType = await SendPostAsync(port, "/api/recommendations/needed", body,
            token: Token, host: $"127.0.0.1:{port}", origin: $"http://127.0.0.1:{port}", contentType: "application/json; charset=utf-8");

        Assert.Equal(403, invalidToken.Status);
        Assert.Equal(403, invalidHost.Status);
        Assert.Equal(403, invalidOrigin.Status);
        Assert.Equal(415, invalidContentType.Status);
        Assert.Equal(RecommendationState.Active, store.FindById(recommendation.Id)!.State);
    }

    [Fact]
    public async Task PostRejectsOversizedBodiesUnknownRoutesAndExtraJsonFields()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now);
        store.Upsert(recommendation);
        using var fixture = StartServer(store, recommendation);
        var port = fixture.Server.BoundPort;

        var oversized = await SendAsync(port,
            $"POST /api/recommendations/needed?token={Token} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{port}\r\nOrigin: http://127.0.0.1:{port}\r\n" +
            "Content-Type: application/json\r\nContent-Length: 8193\r\n\r\n");
        var unknown = await PostAsync(port, "/api/recommendations/delete", "{\"recommendationId\":\"rec-test\"}");
        var extraField = await PostAsync(port, "/api/recommendations/needed",
            "{\"recommendationId\":\"rec-test\",\"pid\":4}");

        Assert.Equal(413, oversized.Status);
        Assert.Equal(404, unknown.Status);
        Assert.Equal(400, extraField.Status);
        Assert.Equal(RecommendationState.Active, store.FindById(recommendation.Id)!.State);
    }

    [Fact]
    public async Task GetRoutesNeverApplyRecommendationDecisions()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now);
        store.Upsert(recommendation);
        using var fixture = StartServer(store, recommendation);
        var port = fixture.Server.BoundPort;

        var response = await SendAsync(port,
            $"GET /api/recommendations/needed?token={Token} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");

        Assert.Equal(404, response.Status);
        Assert.Equal(RecommendationState.Active, store.FindById(recommendation.Id)!.State);
    }

    [Fact]
    public async Task CloseGracefullyRouteUsesExecutorWithoutAcceptingPidInput()
    {
        using var store = Store();
        var recommendation = TestRecommendationFactory.Create(_now);
        store.Upsert(recommendation);
        var runtime = new FakeProcessActionRuntime(TestRecommendationFactory.Group(recommendation, _now));
        using var fixture = StartServer(store, recommendation, runtime);

        var response = await PostAsync(fixture.Server.BoundPort, "/api/recommendations/closeGracefully",
            "{\"recommendationId\":\"rec-test\"}");

        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal("succeeded", json.RootElement.GetProperty("result").GetString());
        Assert.Equal([recommendation.TargetGroup.Root.Identity], runtime.CloseRequests);
    }

    private RecommendationStore Store() => new(_directory, DashboardData.JsonOptions);

    private DashboardFixture StartServer(
        RecommendationStore store,
        RecommendationRecord recommendation,
        FakeProcessActionRuntime? runtime = null)
    {
        runtime ??= new FakeProcessActionRuntime(TestRecommendationFactory.Group(recommendation, _now));
        var executor = new ProcessActionExecutor(store, true, runtime,
            timeProvider: new FixedTimeProvider(_now), waitTimeout: TimeSpan.FromMilliseconds(250));
        var server = new DashboardServer(_directory, 0, Token, store, executor);
        var cancellation = new CancellationTokenSource();
        server.Start(cancellation.Token);
        return new DashboardFixture(server, cancellation);
    }

    private static Task<TestHttpResponse> PostAsync(int port, string path, string body) =>
        SendPostAsync(port, path, body, Token, $"127.0.0.1:{port}", $"http://127.0.0.1:{port}", "application/json");

    private static Task<TestHttpResponse> SendPostAsync(
        int port,
        string path,
        string body,
        string token,
        string host,
        string origin,
        string contentType)
    {
        var length = Encoding.UTF8.GetByteCount(body);
        return SendAsync(port,
            $"POST {path}?token={token} HTTP/1.1\r\nHost: {host}\r\nOrigin: {origin}\r\n" +
            $"Content-Type: {contentType}\r\nContent-Length: {length}\r\n\r\n{body}");
    }

    private static async Task<TestHttpResponse> SendAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
        using var response = new MemoryStream();
        var buffer = new byte[2048];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) response.Write(buffer, 0, read);
        var text = Encoding.UTF8.GetString(response.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(split >= 0, "The server returned a complete HTTP response.");
        var headers = text[..split];
        var status = int.Parse(headers.Split("\r\n", 2)[0].Split(' ')[1]);
        return new TestHttpResponse(status, headers, text[(split + 4)..]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class DashboardFixture(DashboardServer server, CancellationTokenSource cancellation) : IDisposable
    {
        public DashboardServer Server { get; } = server;

        public void Dispose()
        {
            cancellation.Cancel();
            Server.Dispose();
            cancellation.Dispose();
        }
    }

    private sealed record TestHttpResponse(int Status, string Headers, string Body);
}
