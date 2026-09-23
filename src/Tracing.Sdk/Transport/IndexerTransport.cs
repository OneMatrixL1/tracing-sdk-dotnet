using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tracing.Sdk.Transport;

/// <summary>A { hash, signingTime } entry as sent to the Indexer.</summary>
internal sealed record AnchorEntry(string Hash, object SigningTime);

/// <summary>The SDK's contact with the Indexer; swappable in tests.</summary>
internal interface IIndexerTransport : IDisposable
{
    Task<IndexerResponse> SendSingleAsync(AnchorEntry entry, int? timeoutMs, CancellationToken cancellationToken);

    Task<IndexerResponse> SendBatchAsync(IReadOnlyList<AnchorEntry> entries, int? timeoutMs, CancellationToken cancellationToken);

    Task<HttpAnswer> QueryByHashAsync(string hash, int? timeoutMs, CancellationToken cancellationToken);
}

/// <summary>
/// POST /api/anchors for a single record, POST /api/anchors/batch for a
/// batch, and GET /api/anchors?hash=... to look an anchor up. Redirects are
/// never followed.
/// </summary>
internal sealed class IndexerTransport : IIndexerTransport
{
    private readonly HttpClient _client;
    private readonly string _singleUrl;
    private readonly string _batchUrl;
    private readonly int _timeoutMs;

    /// <exception cref="ConfigException">an mTLS certificate or key cannot be loaded</exception>
    public IndexerTransport(string baseEndpoint, AuthConfig auth, int timeoutMs)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

        if (auth is AuthConfig.MtlsAuth mtls)
        {
            ConfigureMtls(handler, mtls);
        }

        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        switch (auth)
        {
            case AuthConfig.ApiTokenAuth apiToken:
                _client.DefaultRequestHeaders.Add("X-API-Key", apiToken.Token);
                break;
            case AuthConfig.BasicAuth basic:
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Password}"));
                _client.DefaultRequestHeaders.Add("Authorization", "Basic " + credentials);
                break;
        }

        var baseUrl = baseEndpoint.TrimEnd('/');
        _singleUrl = baseUrl + "/api/anchors";
        _batchUrl = baseUrl + "/api/anchors/batch";
        _timeoutMs = timeoutMs;
    }

    public Task<IndexerResponse> SendSingleAsync(AnchorEntry entry, int? timeoutMs, CancellationToken cancellationToken) =>
        PostAsync(_singleUrl, ToJson(entry), 1, timeoutMs, cancellationToken);

    public Task<IndexerResponse> SendBatchAsync(IReadOnlyList<AnchorEntry> entries, int? timeoutMs, CancellationToken cancellationToken) =>
        PostAsync(_batchUrl, new JsonArray(entries.Select(e => (JsonNode)ToJson(e)).ToArray()), entries.Count, timeoutMs, cancellationToken);

    /// <summary>
    /// GET /api/anchors?hash=... — the Indexer responds with
    /// { "hash": "&lt;hash hex&gt;", "proof": ["&lt;proof hex&gt;", ...], "proofType": "transactionHash" }.
    /// </summary>
    public Task<HttpAnswer> QueryByHashAsync(string hash, int? timeoutMs, CancellationToken cancellationToken) =>
        ExecuteAsync(HttpMethod.Get, _singleUrl + "?hash=" + Uri.EscapeDataString(hash), null, timeoutMs, cancellationToken);

    public void Dispose() => _client.Dispose();

    private async Task<IndexerResponse> PostAsync(string url, JsonNode payload, int recordCount, int? timeoutMs, CancellationToken cancellationToken)
    {
        var answer = await ExecuteAsync(HttpMethod.Post, url, payload.ToJsonString(), timeoutMs, cancellationToken).ConfigureAwait(false);

        return new IndexerResponse(answer.StatusCode, answer.Body, answer.Json, recordCount);
    }

    private async Task<HttpAnswer> ExecuteAsync(HttpMethod method, string url, string? body, int? timeoutMs, CancellationToken cancellationToken)
    {
        var answer = await HttpRequester.SendAsync(
            _client, method, url, body, timeoutMs ?? _timeoutMs, "HTTP request to Indexer", cancellationToken).ConfigureAwait(false);

        if (answer.StatusCode is < 200 or >= 300)
        {
            throw new TransportException($"Indexer returned HTTP {answer.StatusCode}: {answer.Body}");
        }

        return answer;
    }

    private static JsonObject ToJson(AnchorEntry entry)
    {
        JsonNode? signingTime;

        try
        {
            signingTime = JsonSerializer.SerializeToNode(entry.SigningTime);
        }
        catch (Exception e) when (e is NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new TransportException("Failed to encode request payload: " + e.Message, e);
        }

        return new JsonObject { ["hash"] = entry.Hash, ["signingTime"] = signingTime };
    }

    /// <summary>
    /// Present the client certificate, and when a CA is given trust only it
    /// for the server certificate. Server verification is never disabled.
    /// </summary>
    private static void ConfigureMtls(SocketsHttpHandler handler, AuthConfig.MtlsAuth mtls)
    {
        RequireFile(mtls.Cert, "mTLS client certificate");
        RequireFile(mtls.Key, "mTLS client key");

        if (mtls.CaCert is not null)
        {
            RequireFile(mtls.CaCert, "mTLS CA certificate");
        }

        X509Certificate2 clientCertificate;
        var trustedRoots = new X509Certificate2Collection();

        try
        {
            clientCertificate = mtls.Passphrase is null
                ? X509Certificate2.CreateFromPemFile(mtls.Cert, mtls.Key)
                : X509Certificate2.CreateFromEncryptedPemFile(mtls.Cert, mtls.Passphrase, mtls.Key);

            // SChannel cannot use an ephemeral PEM-loaded key; round-trip it
            // through PKCS#12 so the key is usable for the handshake.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                clientCertificate = X509CertificateLoader.LoadPkcs12(clientCertificate.Export(X509ContentType.Pkcs12), null);
            }

            if (mtls.CaCert is not null)
            {
                trustedRoots.ImportFromPemFile(mtls.CaCert);
            }
        }
        catch (CryptographicException e)
        {
            throw new ConfigException("mTLS certificate or key could not be loaded: " + e.Message, e);
        }

        handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

        if (trustedRoots.Count > 0)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                // Name mismatches and missing certificates stay fatal; only the
                // chain is re-evaluated, against the configured CA.
                if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                {
                    return false;
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                return chain.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
            };
        }
    }

    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"{label} not found: {path}");
        }
    }
}
