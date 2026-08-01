using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Retry wrapper for the TVDB/AniDB scrape calls.
///
/// Why this exists: HttpClient throws TaskCanceledException on timeout, which
/// is indistinguishable by type from a real cancellation. Left uncaught it
/// propagates out of a scheduled task, and Jellyfin reports the whole run as
/// "Cancelled" with no error message. Observed live 2026-08-01: three merge
/// runs each died exactly 100s (the default HttpClient timeout) after their
/// last activity, losing all remaining work.
///
/// A transient failure must cost one episode, never the entire task.
/// </summary>
public static class HttpRetry
{
    /// <summary>Attempts a request, retrying transient failures with growing backoff.</summary>
    /// <returns>The response, or null when every attempt failed transiently.</returns>
    public static async Task<HttpResponseMessage?> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        int maxAttempts,
        Func<TimeSpan, Task> delayFn,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // A genuine cancellation must never be retried or swallowed.
            ct.ThrowIfCancellationRequested();

            try
            {
                using var request = requestFactory();
                return await client.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException)
            {
                // Distinguish "the caller cancelled us" from "the request timed out".
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                if (attempt >= maxAttempts)
                {
                    return null;   // give up on THIS item, let the task continue
                }

                await delayFn(TimeSpan.FromSeconds(Math.Pow(2, attempt))).ConfigureAwait(false);
            }
        }

        return null;
    }
}
