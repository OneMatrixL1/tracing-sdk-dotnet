# Tracing SDK (.NET) — Usage Guide

The SDK does three things for every record you hand it: **canonicalize** it, **hash** it with Keccak-256, and **send** the hash to an Indexer service. Once anchored, a record can be looked up by its hash ([§7](#7-querying-an-anchor-by-hash)) and checked against the chain itself ([§8](#8-verifying-an-anchor-against-the-chain)).

The API mirrors the PHP SDK; the differences are .NET idioms — network calls are `async` and take a `CancellationToken`, configuration and results are typed, and the data type is an enum.

---

## 1. Requirements & install

- .NET **10** or later

```bash
dotnet add package VMatrix.Tracing.Sdk
```

---

## 2. Creating the client

```csharp
using Tracing.Sdk;

using var sdk = new TracingSdk(new TracingSdkConfig
{
    // Base URL of your Indexer. Paths are appended by the SDK.
    Endpoint = "https://indexer.example.com",

    // Default options for every call. Optional if every call passes its own SendOptions.
    Options = new SendOptions(
        DataType.Json,              // dataType
        5000,                       // timeoutMs
        "https://rpc.example.com"), // rpcUrl — only needed for VerifyAsync()

    // Required.
    Auth = AuthConfig.ApiToken("your-api-token"),
});
```

| Property | Required | Description |
| --- | --- | --- |
| `Endpoint` | yes | Indexer base URL. A trailing `/` is trimmed. |
| `Auth` | yes | See [Authentication](#5-authentication). |
| `Options` | no | A `SendOptions` used as the default for every call. |

Invalid or missing config throws `ConfigException` **at construction time**, so a missing certificate file fails immediately rather than on the first send.

`TracingSdk` owns its HTTP connections and is safe to share: create one and keep it for the application's lifetime (for example as a singleton in dependency injection), or dispose it when done.

---

## 3. Sending records

### 3.1 One record — `SendAsync()`

`SendAsync()` canonicalizes, hashes, and POSTs `{endpoint}/api/anchors`.

```csharp
var rawData = JsonSerializer.Serialize(new { orderId = 1, amount = 250000 });
var signingTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

try
{
    var result = await sdk.SendAsync(rawData, signingTime);

    Console.WriteLine(result.Hash);                   // 0x1c8a…
    Console.WriteLine(result.Response.StatusCode);    // 200
    Console.WriteLine(result.Response.RecordCount);   // 1
    Console.WriteLine(result.Response.Body);          // raw response body
    // result.Response.Json is the body parsed as a JsonElement, or null if it is not JSON.
}
catch (TransportException e)
{
    // Network failure, timeout, or the Indexer answered with a non-2xx status.
    Console.Error.WriteLine(e.Message);
}
```

`rawData` is a `string` (hashed as its UTF-8 bytes) or a `byte[]`. A string holding an unpaired surrogate has no UTF-8 form and throws `CanonicalizationException`.

`signingTime` is any value `System.Text.Json` can serialize, passed through to the Indexer untouched — whatever your Indexer expects (Unix timestamp, ISO-8601 string) is what you should send. It may not be `null`.

### 3.2 Several records — `SendBatchAsync()`

`SendBatchAsync()` hashes every record, then POSTs them all in **one** request to `{endpoint}/api/anchors/batch`.

```csharp
var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

var results = await sdk.SendBatchAsync(
[
    new BatchRecord(JsonSerializer.Serialize(new { orderId = 1 }), now),
    new BatchRecord(JsonSerializer.Serialize(new { orderId = 2 }), now),
    new BatchRecord(JsonSerializer.Serialize(new { orderId = 3 }), now),
]);

foreach (var (result, i) in results.Select((r, i) => (r, i)))
{
    Console.WriteLine($"#{i} {result.Hash} -> HTTP {result.Response.StatusCode}");
}
```

- One result is returned per input record, **in input order**.
- Each result carries its own `Hash`, and all results share the single `Response` of that one request (`RecordCount` = number of records sent).
- Every record in a batch is canonicalized with the **same** data type. Mixed types → separate calls.
- A `null` record, or one without `RawData`/`SigningTime`, throws `ConfigException` *before* anything is sent.

### 3.3 Hash without sending — `Hash()`

Synchronous. Useful for pre-computing a hash, deduplicating, or storing it locally before deciding to anchor.

```csharp
var a = sdk.Hash(JsonSerializer.Serialize(new { orderId = 1 }));   // config default type
var b = sdk.Hash(xmlString, SendOptions.ForDataType(DataType.Xml)); // per-call type
```

Hashing is deterministic: the same record always produces the same hash, on any machine, in any SDK language.

---

## 4. Options: data type, timeout & RPC URL

`SendOptions` is an immutable object carrying three settings — `DataType`, `TimeoutMs`, and `RpcUrl`. It can be given as the config default, per call, or both.

### 4.1 Data types & canonicalization

| Value | Behaviour |
| --- | --- |
| `DataType.Json` | [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) JSON Canonicalization Scheme — keys sorted by UTF-16 code unit, numbers normalized per ECMAScript `Number::toString` (`1`, `1.0`, `1e0` are identical; `-0` → `0`). Input must be valid UTF-8 without a BOM. |
| `DataType.Xml` | [Exclusive XML Canonicalization 1.0](https://www.w3.org/TR/xml-exc-c14n/), without comments — normalizes attribute order and insignificant whitespace, and keeps only the namespace declarations the document actually uses. Namespace prefixes remain significant. Output is byte-identical to the PHP SDK's. Bytes are decoded by their BOM or declared `encoding` (UTF-8 by default). DTDs are ignored; referencing any entity beyond the five predefined ones is an error (XXE protection). Namespace URIs must be absolute RFC 3986 URIs. |
| `DataType.Raw` | No parsing at all — the bytes you pass are hashed exactly as given. Use when the caller already guarantees one deterministic representation. |

Resolution order:

```csharp
// 1. Config default
await sdk.SendAsync(rawJson, signingTime);

// 2. Per-call override — wins over the config default
await sdk.SendAsync(rawXml, signingTime, SendOptions.ForDataType(DataType.Xml));
await sdk.SendBatchAsync(xmlRecords, SendOptions.ForDataType(DataType.Xml));
```

If neither the call nor the config supplies a data type, `ConfigException` is thrown.

### 4.2 Request timeout

`TimeoutMs` caps how long a single HTTP request to the Indexer (or the RPC node) may take, in milliseconds. It defaults to **10 000 ms (10 s)** and covers the whole request — connecting, sending, and reading the full response.

```csharp
// For one call only — e.g. a large batch that deserves more headroom.
await sdk.SendBatchAsync(records, SendOptions.ForTimeoutMs(30000));
await sdk.SendAsync(rawXml, signingTime, SendOptions.ForDataType(DataType.Xml).WithTimeoutMs(3000));
```

A per-call `TimeoutMs` wins over the config one; a call that supplies none keeps the config default. A value of `0` or less throws `ConfigException` when the `SendOptions` is built. Exceeding the timeout surfaces as `TransportException`. Cancelling through your own `CancellationToken` surfaces as `OperationCanceledException` instead, as usual in .NET.

### 4.3 RPC URL

`RpcUrl` is the JSON-RPC endpoint of a chain node you trust. It is used **only** by `VerifyAsync()` (see [§8](#8-verifying-an-anchor-against-the-chain)).

```csharp
var options = SendOptions.ForDataType(DataType.Json).WithRpcUrl("https://rpc.example.com");

// For one call only.
await sdk.VerifyAsync(hash, txHash, TracingSdk.ModeTransactionHash, SendOptions.ForRpcUrl("https://other-rpc.example.com"));
```

A per-call `RpcUrl` wins over the config one. Calling `VerifyAsync()` with neither throws `ConfigException`; a blank string throws when the `SendOptions` is built.

### 4.4 `SendOptions` is immutable

```csharp
var json = new SendOptions(DataType.Json);               // same as SendOptions.ForDataType(DataType.Json)
var xml  = json.WithDataType(DataType.Xml);               // returns a copy; json is unchanged
var fast = json.WithTimeoutMs(2000);                      // likewise
var node = json.WithRpcUrl("https://rpc.example.com");    // likewise
```

---

## 5. Authentication

```csharp
// API token — sent as the "X-API-Key" header.
Auth = AuthConfig.ApiToken("your-api-token"),

// HTTP Basic — sent as "Authorization: Basic <base64>".
Auth = AuthConfig.Basic("your-username", "your-password"),

// Mutual TLS — PEM client certificate presented during the TLS handshake.
Auth = AuthConfig.Mtls(
    cert: "/path/to/client.crt",
    key: "/path/to/client.key",
    caCert: "/path/to/ca.crt",        // optional: trust only this CA for the server certificate
    passphrase: "key-passphrase"),    // optional: encrypted private key
```

With mTLS the server certificate is always verified: against the system trust store by default, or only against `caCert` when one is given. Host-name checks always apply. Certificate files are loaded when the SDK is constructed; a missing or unreadable file throws `ConfigException`.

---

## 6. Error handling

All SDK exceptions derive from `TracingSdkException`, so a single `catch` can cover everything.

| Exception | Thrown when |
| --- | --- |
| `ConfigException` | Missing/invalid config, an unsupported verify mode, a `TimeoutMs` of `0` or less, a blank `RpcUrl` or none at all when verifying, a `null` `signingTime`, an incomplete batch record, an empty `hash`, a `dataHash`/`proof` that is not a 32-byte hex string, or an mTLS file that cannot be loaded. |
| `CanonicalizationException` | The raw data cannot be canonicalized for the chosen data type (e.g. malformed JSON/XML), or a string holds an unpaired surrogate. |
| `TransportException` | The HTTP request failed (network, or it exceeded `TimeoutMs`), the Indexer answered with a non-2xx status, or the RPC node rejected the call / does not know the proof transaction. |

`ConfigException` and `CanonicalizationException` are thrown **before** anything leaves the process, so a rejected call never half-sends a batch. The underlying exception, when there is one, is the `InnerException`.

```csharp
try
{
    await sdk.SendAsync(rawData, signingTime);
}
catch (Exception e) when (e is ConfigException or CanonicalizationException)
{
    // Bad input — retrying the same payload will not help.
}
catch (TransportException)
{
    // Transient — safe to retry or queue for later.
}
```

The SDK does not retry on your behalf. Because hashing is deterministic and a send is a plain POST, retrying with the same payload re-sends the same hash — a retry policy such as Polly's works as usual.

---

## 7. Querying an anchor by hash

`QueryByHashAsync()` resolves a record's hash to the on-chain proofs that anchored it, via `GET {endpoint}/api/anchors?hash=<hash>`. The hash is URL-encoded for you, and the configured auth is applied exactly as it is for sending.

```csharp
var anchor = await sdk.QueryByHashAsync("0x1c8a…");
// anchor.Hash == "0x1c8a…", anchor.Proof == ["0x9f42…", "0x3b07…"], anchor.ProofType == "transactionHash"
```

| Property | Description |
| --- | --- |
| `Hash` | The record hash queried, as echoed back by the Indexer. |
| `Proof` | Every on-chain proof the record was anchored by — with `ProofType == "transactionHash"`, the transaction hashes. A record can be anchored more than once, so always iterate. |
| `ProofType` | How each entry in `Proof` should be resolved on chain. Pass it straight to `VerifyAsync()` as its `mode`. Defaults to `"transactionHash"` if an older Indexer omits the field. |

Errors: `ConfigException` for an empty `hash`; `TransportException` for a failed request, a non-2xx status (including the `404` for a hash that was never anchored), or a body that is not a `{ hash, proof }` object.

---

## 8. Verifying an anchor against the chain

`QueryByHashAsync()` tells you what the Indexer says. `VerifyAsync()` checks that claim against the chain itself, over an RPC endpoint **you** choose — so a compromised or mistaken Indexer cannot vouch for itself.

```csharp
var hash = sdk.Hash(rawData);                 // or the hash returned by SendAsync()
var anchor = await sdk.QueryByHashAsync(hash);

foreach (var proof in anchor.Proof)
{
    // ProofType tells VerifyAsync how to resolve the proof — no need to hardcode a mode.
    if (await sdk.VerifyAsync(anchor.Hash, proof, anchor.ProofType))
    {
        Console.WriteLine($"anchored on chain in {proof}");
        break;
    }
}
```

What it does:

1. Calls `eth_getTransactionReceipt` with the proof transaction hash on the configured `RpcUrl` — a plain JSON-RPC 2.0 POST, with no Indexer auth attached.
2. Walks the receipt's logs and keeps the ones whose `topics[0]` equals `keccak256("Anchored(bytes32,uint64)")`.
3. ABI-decodes each of those as `Anchored(bytes32 dataHash, uint64 signingTime)`.
4. Returns `true` as soon as one decoded `dataHash` equals the hash you passed, `false` if none does.

Both event layouts decode: an indexed `bytes32` is read from `topics[1]`, a non-indexed one from the log data. Comparison is on normalized hashes, so case and a missing `0x` prefix do not matter.

| Outcome | Meaning |
| --- | --- |
| `true` | The transaction really does contain an `Anchored` event carrying this data hash. |
| `false` | The transaction exists, but no `Anchored` event in it carries this hash. |
| `ConfigException` | Malformed `dataHash`/`proof`, unsupported `mode`, or no `RpcUrl` anywhere. |
| `TransportException` | The RPC endpoint was unreachable, returned a non-2xx status or a JSON-RPC error, or does not know the transaction (unmined, dropped, or wrong-chain hash). |

A `false` is a real answer about a real transaction; a missing transaction is an exception, because "the node has never heard of it" says nothing about whether the record was anchored.

---

## 9. Runnable examples

[`examples/Tracing.Sdk.Examples`](../examples/Tracing.Sdk.Examples/Program.cs) holds one example per scenario:

| Name | Shows |
| --- | --- |
| `single` | One JSON record with `SendAsync()` |
| `batch` | Several JSON records in one request with `SendBatchAsync()` |
| `xml` | The batch flow with `DataType.Xml` |
| `query` | Sending a record, then looking the anchor up with `QueryByHashAsync()` |
| `verify` | Query, then `VerifyAsync()` each proof transaction against an RPC node |

Edit the endpoint, token (and RPC URL for `verify`) at the top of `Program.cs`, then run:

```bash
dotnet run --project examples/Tracing.Sdk.Examples -- single
```

---

## 10. API reference

```csharp
new TracingSdk(TracingSdkConfig config)

Task<SendResult> SendAsync(string | byte[] rawData, object signingTime, SendOptions? options = null, CancellationToken ct = default)
    // SendResult(string Hash, IndexerResponse Response)
    // IndexerResponse(int StatusCode, string Body, JsonElement? Json, int RecordCount)

Task<IReadOnlyList<SendResult>> SendBatchAsync(IEnumerable<BatchRecord> records, SendOptions? options = null, CancellationToken ct = default)

string Hash(string | byte[] rawData, SendOptions? options = null)          // "0x…" Keccak-256 hex

Task<QueryResult> QueryByHashAsync(string hash, SendOptions? options = null, CancellationToken ct = default)
    // QueryResult(string Hash, IReadOnlyList<string> Proof, string ProofType)

Task<bool> VerifyAsync(string dataHash, string proof, string mode = TracingSdk.ModeTransactionHash,
                       SendOptions? options = null, CancellationToken ct = default)

TracingSdk.ModeTransactionHash   // "transactionHash" — the only verify mode today
```

`SendOptions`:

```csharp
new SendOptions(DataType? dataType = null, int? timeoutMs = null, string? rpcUrl = null)
SendOptions.ForDataType(DataType) / ForTimeoutMs(int) / ForRpcUrl(string)
options.DataType / options.TimeoutMs / options.RpcUrl
options.WithDataType(...) / WithTimeoutMs(...) / WithRpcUrl(...)   // return a copy
```

Indexer endpoints used: `POST /api/anchors`, `POST /api/anchors/batch`, `GET /api/anchors?hash=…`. `VerifyAsync()` additionally calls `eth_getTransactionReceipt` on `RpcUrl`.

---

## 11. Differences from the PHP SDK

The same record produces the same hash in the PHP, JavaScript and .NET SDKs. This is verified against the shared `testdata/` vectors and by differential testing against the PHP SDK. Known exceptions:

- **Integers between 2^53 and 2^63 in JSON.** RFC 8785 treats every JSON number as an IEEE 754 double, so `9007199254740993` canonicalizes as `9007199254740992` here. The PHP SDK keeps such values as exact 64-bit integers, so their hashes differ. Integers beyond 2^63 and all integers up to 2^53 agree.
- **`<!ELEMENT>` declarations in an XML DTD.** libxml consults them when deciding which whitespace to drop, but this SDK never reads a DTD: under `<!ELEMENT r ANY>`, the PHP SDK keeps the space in `<r><a/> <b/></r>`, but this SDK drops it. Documents without element declarations are unaffected.
- **An empty prefixed namespace declaration (`xmlns:p=""`).** XML Namespaces 1.0 forbids it. .NET's parser rejects it, while libxml accepts it with a warning.
