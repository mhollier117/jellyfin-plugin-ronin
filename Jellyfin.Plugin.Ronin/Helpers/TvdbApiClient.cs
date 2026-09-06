using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ronin.Helpers;

/// <summary>
/// Minimal TheTVDB v4 API client for absolute episode numbers.
/// <para>
/// This is the authoritative resolver. Unlike the local order it returns the
/// complete aired order including episodes the user does not own, which is the
/// only way numbering stays correct when a season has gaps - the failure that
/// left Mushoku Tensei permanently half-merged (2026-09-06).
/// </para>
/// <para>
/// Data is licensed from TheTVDB; the free tier requires attribution, which the
/// plugin carries in its README and configuration page.
/// </para>
/// </summary>
public sealed class TvdbApiClient
{
    private const string Base = "https://api4.thetvdb.com/v4";
    private const int MaxPages = 40;   // ~10k episodes; a runaway-loop backstop

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly int _delayMs;
    private string? _token;
    private bool _authFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvdbApiClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client to use.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="requestDelayMs">Minimum delay between requests.</param>
    public TvdbApiClient(HttpClient http, ILogger logger, int requestDelayMs)
    {
        _http = http;
        _logger = logger;
        _delayMs = requestDelayMs > 0 ? requestDelayMs : 0;
    }

    /// <summary>
    /// Gets a value indicating whether authentication failed this run. Once it
    /// has, every later call short-circuits - a bad key must cost one request,
    /// not one per series.
    /// </summary>
    public bool AuthFailed => _authFailed;

    /// <summary>
    /// Logs in and caches the bearer token for the lifetime of this instance.
    /// </summary>
    /// <param name="apiKey">The TheTVDB project API key.</param>
    /// <param name="pin">Optional subscriber PIN; empty for a project key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a token is held.</returns>
    public async Task<bool> LoginAsync(string? apiKey, string? pin, CancellationToken cancellationToken)
    {
        if (_authFailed || string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_token))
        {
            return true;
        }

        var body = string.IsNullOrWhiteSpace(pin)
            ? JsonSerializer.Serialize(new { apikey = apiKey })
            : JsonSerializer.Serialize(new { apikey = apiKey, pin });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/login")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _authFailed = true;
                _logger.LogWarning(
                    "TheTVDB login failed ({Status}); falling back to the local resolver for this run",
                    (int)resp.StatusCode);
                return false;
            }

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("token", out var tok)
                && tok.ValueKind == JsonValueKind.String)
            {
                _token = tok.GetString();
                return !string.IsNullOrEmpty(_token);
            }

            _authFailed = true;
            _logger.LogWarning("TheTVDB login returned no token; falling back to the local resolver");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _authFailed = true;
            _logger.LogWarning(ex, "TheTVDB login errored; falling back to the local resolver");
            return false;
        }
    }

    /// <summary>
    /// Fetches the complete (season, episode) -> absolute map for one series.
    /// </summary>
    /// <param name="seriesTvdbId">The series' TheTVDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The map, or an empty map when unavailable.</returns>
    public async Task<IReadOnlyDictionary<(int Season, int Episode), int>> GetAbsoluteMapAsync(
        string? seriesTvdbId, CancellationToken cancellationToken)
    {
        var empty = new Dictionary<(int, int), int>();
        if (string.IsNullOrWhiteSpace(seriesTvdbId) || string.IsNullOrEmpty(_token))
        {
            return empty;
        }

        var merged = new Dictionary<(int Season, int Episode), int>();
        for (var page = 0; page < MaxPages; page++)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/series/{1}/episodes/default?page={2}",
                Base, Uri.EscapeDataString(seriesTvdbId), page);

            string text;
            try
            {
                if (_delayMs > 0 && page > 0)
                {
                    await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "TheTVDB episodes request for series {Series} returned {Status}",
                        seriesTvdbId, (int)resp.StatusCode);
                    break;
                }

                text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TheTVDB episodes request failed for series {Series}", seriesTvdbId);
                break;
            }

            var slice = TvdbAbsoluteMap.Parse(text);
            foreach (var kv in slice)
            {
                merged.TryAdd(kv.Key, kv.Value);
            }

            if (!HasNextPage(text))
            {
                break;
            }
        }

        return merged;
    }

    private static bool HasNextPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("links", out var links)
                   && links.TryGetProperty("next", out var next)
                   && next.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(next.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
