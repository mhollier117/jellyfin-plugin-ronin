// Design doc 2026-08-01, D2.3 / test U13: AniDB page scraping gets HTTP 403
// from this host on every request. 1.0.4.0 kept asking for every episode
// anyway (one doomed request per episode, each with its rate-limit delay).
// The validated design: a per-run circuit breaker — on the first 403, AniDB
// lookups are disabled for the remainder of the run.
using System.Net;
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class AniDbCircuitBreakerTests
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

    private static Func<HttpResponseMessage> Status(HttpStatusCode c)
        => () => new HttpResponseMessage(c) { Content = new StringContent(string.Empty) };

    [Fact]
    public void Breaker_StartsClosed_TripOpens()
    {
        var breaker = new ScrapeCircuitBreaker();
        Assert.False(breaker.IsOpen);
        breaker.Trip();
        Assert.True(breaker.IsOpen);
    }

    // Doc U13 — RED against 1.0.4.0 resolver (no breaker: second lookup hits
    // the network again despite the 403).
    [Fact]
    public async Task AniDb_CircuitBreaker_OpensOn403_NoFurtherCalls()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.Forbidden), Status(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var breaker = new ScrapeCircuitBreaker();

        var first = await ResolveEpisodeNumber.AbsoluteFromAniDbAsync(
            "1001", client, 0, CancellationToken.None, breaker);
        Assert.Null(first);
        Assert.True(breaker.IsOpen);
        Assert.Equal(1, handler.Attempts);

        var second = await ResolveEpisodeNumber.AbsoluteFromAniDbAsync(
            "1002", client, 0, CancellationToken.None, breaker);
        Assert.Null(second);
        Assert.Equal(1, handler.Attempts); // no further network calls
    }

    [Fact]
    public async Task AniDb_NonForbiddenFailure_DoesNotTrip()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.NotFound));
        var client = new HttpClient(handler);
        var breaker = new ScrapeCircuitBreaker();

        var result = await ResolveEpisodeNumber.AbsoluteFromAniDbAsync(
            "1001", client, 0, CancellationToken.None, breaker);
        Assert.Null(result);
        Assert.False(breaker.IsOpen);
    }
}
