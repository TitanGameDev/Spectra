using System.Net;

namespace Spectra.Api.Services;

// Sits under every request GraphAppClient's HttpClient sends (registered via
// AddHttpMessageHandler in Program.cs) and retries a request throttled with
// 429, honoring Graph's Retry-After header rather than guessing a backoff.
// Without this, a burst of concurrent per-user calls (see
// CustomerCollectionService's Parallel.ForEachAsync loops) silently drops
// whichever users got throttled — that's what was producing "—" instead of
// "No" for MFA on users Graph never actually failed to have data for.
public class GraphRetryHandler(ILogger<GraphRetryHandler> logger, CollectionProgressTracker progressTracker) : DelegatingHandler
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var attempt = 1;
        var current = request;
        while (true)
        {
            var response = await base.SendAsync(current, ct);
            var retryable = attempt < MaxAttempts && response.StatusCode is
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (!retryable)
            {
                return response;
            }

            var delay = GetRetryDelay(response);
            logger.LogInformation(
                "Graph request throttled ({StatusCode}), retrying in {DelaySeconds}s (attempt {Attempt}/{MaxAttempts}): {Url}",
                (int)response.StatusCode, delay.TotalSeconds, attempt, MaxAttempts, current.RequestUri);
            progressTracker.Report($"Rate limited by Graph ({(int)response.StatusCode}) — waiting {delay.TotalSeconds:0}s before retrying…");
            response.Dispose();
            await Task.Delay(delay, ct);

            current = await CloneAsync(request, ct);
            attempt++;
        }
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date.HasValue == true ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null)
            ?? MinDelay;
        return delay < MinDelay ? MinDelay : delay > MaxDelay ? MaxDelay : delay;
    }

    // The original request can only be sent once (HttpClient marks it as
    // sent), so retries need a fresh HttpRequestMessage built from it.
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = content;
        }

        return clone;
    }
}
