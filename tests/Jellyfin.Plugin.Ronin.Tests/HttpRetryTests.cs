// Edge-case inventory — reproduces the live defect observed 2026-08-01:
// three merge runs died after EXACTLY 100s of silence (.NET HttpClient default
// timeout). A hung scrape threw TaskCanceledException, nothing caught it, and
// Jellyfin reported the task as "Cancelled" with ErrorMessage=None.
//
// - transient timeout then success -> retried, returns the response
// - every attempt times out -> returns null after MaxAttempts, DOES NOT THROW
//   (this is the regression guard: throwing is what killed the task)
// - attempts are capped (no infinite retry)
// - REAL user cancellation -> rethrows immediately, never retried, never
//   swallowed (cancel must still work from the dashboard)
// - non-transient HTTP status (403 from AniDB) -> returned as-is, not retried
// - backoff delay applied between attempts
using System.Net;
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class HttpRetryTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps;
        public int Attempts { get; private set; }
        public ScriptedHandler(params Func<HttpResponseMessage>[] steps) => _steps = new(steps);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            var step = _steps.Count > 0 ? _steps.Dequeue() : () => new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(step());
        }
    }

    private static Func<HttpResponseMessage> Timeout()
        => () => throw new TaskCanceledException("timed out", new TimeoutException());
    private static Func<HttpResponseMessage> Ok()
        => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") };
    private static Func<HttpResponseMessage> Status(HttpStatusCode c)
        => () => new HttpResponseMessage(c) { Content = new StringContent(string.Empty) };

    private static Task<TimeSpan> Recorded(List<TimeSpan> sink, TimeSpan d)
    { sink.Add(d); return Task.FromResult(d); }

    [Fact]
    public async Task TransientTimeout_ThenSuccess_Retries()
    {
        var h = new ScriptedHandler(Timeout(), Timeout(), Ok());
        var delays = new List<TimeSpan>();
        var resp = await HttpRetry.SendWithRetryAsync(
            new HttpClient(h), () => new HttpRequestMessage(HttpMethod.Get, "https://x/"),
            maxAttempts: 3, delayFn: d => Recorded(delays, d), ct: CancellationToken.None);
        Assert.NotNull(resp);
        Assert.Equal(HttpStatusCode.OK, resp!.StatusCode);
        Assert.Equal(3, h.Attempts);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task AllAttemptsTimeOut_ReturnsNull_DoesNotThrow()
    {
        var h = new ScriptedHandler(Timeout(), Timeout(), Timeout());
        var resp = await HttpRetry.SendWithRetryAsync(
            new HttpClient(h), () => new HttpRequestMessage(HttpMethod.Get, "https://x/"),
            maxAttempts: 3, delayFn: _ => Task.FromResult(TimeSpan.Zero), ct: CancellationToken.None);
        Assert.Null(resp);
        Assert.Equal(3, h.Attempts);   // capped, no infinite retry
    }

    [Fact]
    public async Task RealCancellation_RethrowsImmediately_NotRetried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var h = new ScriptedHandler(Timeout(), Ok());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpRetry.SendWithRetryAsync(
                new HttpClient(h), () => new HttpRequestMessage(HttpMethod.Get, "https://x/"),
                maxAttempts: 3, delayFn: _ => Task.FromResult(TimeSpan.Zero), ct: cts.Token));
    }

    [Fact]
    public async Task NonTransientStatus_ReturnedAsIs_NotRetried()
    {
        var h = new ScriptedHandler(Status(HttpStatusCode.Forbidden), Ok());
        var resp = await HttpRetry.SendWithRetryAsync(
            new HttpClient(h), () => new HttpRequestMessage(HttpMethod.Get, "https://x/"),
            maxAttempts: 3, delayFn: _ => Task.FromResult(TimeSpan.Zero), ct: CancellationToken.None);
        Assert.NotNull(resp);
        Assert.Equal(HttpStatusCode.Forbidden, resp!.StatusCode);
        Assert.Equal(1, h.Attempts);
    }

    [Fact]
    public async Task Backoff_Increases_BetweenAttempts()
    {
        var h = new ScriptedHandler(Timeout(), Timeout(), Ok());
        var delays = new List<TimeSpan>();
        await HttpRetry.SendWithRetryAsync(
            new HttpClient(h), () => new HttpRequestMessage(HttpMethod.Get, "https://x/"),
            maxAttempts: 3, delayFn: d => Recorded(delays, d), ct: CancellationToken.None);
        Assert.Equal(2, delays.Count);
        Assert.True(delays[1] > delays[0], $"expected growing backoff, got {delays[0]} then {delays[1]}");
    }
}
