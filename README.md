# Tracing SDK (.NET)

.NET implementation of the Tracing SDK, with the same behaviour as [`tracing-sdk-php`](https://github.com/OneMatrixL1/tracing-sdk-php). It canonicalizes a record, hashes it with **Keccak-256**, and sends the hash to an Indexer service. Canonicalization and hashing happen inside `SendAsync()`/`SendBatchAsync()`, which return the hash alongside the Indexer's response. The SDK has no buffering, timers, or background sending — you decide when to send, one record at a time or a batch. Once a record is anchored, it can be looked up again by its hash and verified against the chain through your own RPC node.

A record hashes to the same value in this SDK as in the PHP and JavaScript SDKs — all three are tested against the same `testdata/` vectors.

Requires .NET 10 or later.

## Install

```bash
dotnet add package VMatrix.Tracing.Sdk
```

## Usage

```csharp
using Tracing.Sdk;
using Tracing.Sdk.Hash; // Keccak256Hasher.ToHex, to print a hash as 0x-hex

using var sdk = new TracingSdk(new TracingSdkConfig
{
    Endpoint = "https://indexer.example.com",
    Options = SendOptions.ForDataType(DataType.Json), // Json | Xml | Raw
    Auth = AuthConfig.ApiToken("your-api-token"),     // or AuthConfig.Basic(...) / AuthConfig.Mtls(...)
});

// Canonicalize + hash + POST {endpoint}/api/anchors
var result = await sdk.SendAsync(rawJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
// result.Hash is the 32-byte digest; Keccak256Hasher.ToHex(result.Hash) == "0x1c8a…"
// result.Response.StatusCode == 200, result.Response.RecordCount == 1
```

That is the whole happy path for anchoring. Everything else — batching, XML and raw data, timeouts, the auth types, looking an anchor up by hash, and verifying it against the chain — is covered in the usage guide:

- **[docs/USAGE.md](docs/USAGE.md)** — full usage guide
- **[`examples/Tracing.Sdk.Examples`](examples/Tracing.Sdk.Examples/)** — runnable examples, one per file, including [`VerifyExample.cs`](examples/Tracing.Sdk.Examples/VerifyExample.cs) for the query-then-verify flow

## Design notes

- **Canonicalization.** JSON follows [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) (JCS): member names sorted by UTF-16 code unit (ordinal), numbers formatted per ECMAScript `Number::toString` by the ES6 number serializer of RFC 8785 co-author Anders Rundgren, vendored unmodified apart from its visibility (see [`Vendor/Es6NumberSerializer`](src/Tracing.Sdk/Vendor/Es6NumberSerializer/README.md)). Parsing is `System.Text.Json`, set up to accept and reject exactly what the PHP SDK does (at most 511 levels of nesting, last duplicate member wins, no BOM or lone surrogates). XML is canonicalized with [Exclusive XML Canonicalization 1.0](https://www.w3.org/TR/xml-exc-c14n/) without comments by .NET's `XmlDsigExcC14NTransform`. On top of it, the SDK reproduces what the PHP SDK's libxml does, so the output bytes are identical: insignificant whitespace is dropped by libxml's exact `preserveWhiteSpace = false` rules, namespace URIs must be absolute RFC 3986 URIs, and bytes are decoded by their BOM or declared encoding. DTDs are ignored, never processed, so untrusted input can never trigger XXE. `DataType.Raw` skips canonicalization entirely — the input is hashed exactly as given.
- **Hashing.** Keccak-256 (the original Keccak, as used by Ethereum — not FIPS-202 SHA3-256, which is what .NET's `SHA3_256` implements), via [BouncyCastle](https://www.bouncycastle.org/)'s `KeccakDigest`. Strings are hashed as UTF-8. Hashes are returned as the raw 32-byte digest (`byte[]`); `Keccak256Hasher.ToHex()` gives the `0x`-prefixed hex form the Indexer and the chain use.
- **Verification.** `VerifyAsync()` trusts nothing but the chain: it reads the proof transaction's receipt straight from an RPC endpoint you configure, decodes the logs whose `topics[0]` is `keccak256("Anchored(bytes32,uint64)")`, and compares the event's `bytes32` argument with the record hash, or with the Merkle root the record's proof leads to (OpenZeppelin-style sorted-pair Keccak-256 trees, mode `merkleProof`). The Indexer is never asked to vouch for itself.
- **Transport.** `HttpClient` over `SocketsHttpHandler`, one per SDK instance (dispose the SDK, or keep one for the application's lifetime). Redirects are never followed, and the timeout bounds the whole request.

## Testing

```bash
dotnet test
```
