using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tracing.Sdk.Transport;

/// <summary>What an HTTP endpoint answered.</summary>
internal sealed record HttpAnswer(int StatusCode, string Body, JsonElement? Json);

/// <summary>
/// One HTTP exchange with a whole-request timeout: connecting, sending, and
/// reading the full response must all finish within <c>timeoutMs</c>.
/// Cancellation through the caller's token surfaces as
/// <see cref="OperationCanceledException"/>, not as a transport failure.
/// </summary>
internal static class HttpRequester
{
    /// <summary>Timeout applied when neither the config nor the call supplies one.</summary>
    public const int DefaultTimeoutMs = 10000;

    public static async Task<HttpAnswer> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? jsonBody,
        int timeoutMs,
        string label,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, new UTF8Encoding(false), "application/json");
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            return new HttpAnswer((int)response.StatusCode, body, TryParseJson(body));
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException($"{label} failed: timed out after {timeoutMs} ms", e);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            throw new TransportException($"{label} failed: {Describe(e)}", e);
        }
    }

    private static JsonElement? TryParseJson(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The message chain, since the outer one is often just "an error occurred".</summary>
    private static string Describe(Exception e)
    {
        var messages = new List<string>();

        for (Exception? current = e; current is not null; current = current.InnerException)
        {
            if (!messages.Any(m => m.Contains(current.Message, StringComparison.Ordinal)))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(": ", messages);
    }
}
