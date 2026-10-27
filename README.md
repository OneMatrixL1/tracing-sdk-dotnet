# Tracing SDK (.NET)

.NET implementation of the Tracing SDK. It sends records to an Indexer service as **event types**: the default `anchor` type, the six types of the tickets plugin, or any schema described in code or JSON, with no SDK change. For each record it can canonicalize and hash the data with **Keccak-256**, validate the request against the event type's fields, send it, and build the link to the **integrity portal** that verifies it. Lookup and on-chain verification happen in the portal.

A record hashes to the same value in this SDK as in the PHP and JavaScript SDKs — all three are tested against the same `testdata/` vectors.

Requires .NET 10 or later.

## Install

```bash
dotnet add package VMatrix.Tracing.Sdk
```

## Usage

```csharp
using Tracing.Sdk;
using Tracing.Sdk.Events;

using var sdk = new TracingSdk(new TracingSdkConfig
{
    Endpoint = "https://indexer.example.com",
    Auth = AuthConfig.ApiToken("your-api-token"),           // or AuthConfig.Basic(...) / AuthConfig.Mtls(...)
    Options = new SendOptions(DataType.Json, 60_000),       // hash JSON tickets; batches wait for the chain
    EventTypes = EventTypeRegistry.Default().Register(EventPresets.Tickets), // or EventTypeRegistry.FromJsonFile(...)
});

// keccak256(0x00 || canonical JSON) per ticket, in order.
var ticketHashes = sdk.LeavesFor(Tickets.Batch, ticketsAsJson);

// Validate + POST {endpoint}/api/anchors?eventType=batch
var batch = await sdk.AnchorAsync(Tickets.Batch, new Dictionary<string, object?>
{
    ["draw"] = "draw-1", ["channel"] = "channel-1", ["sequence"] = 0UL,
    ["ticketHashes"] = ticketHashes, ["value"] = 30_000UL,
});

// One portal link per ticket:
// https://integrity.vmatrix.com/v1/?type=batch&draw=draw-1&channel=channel-1&sequence=0&ticketHash=0x…#ticketId=…
var links = sdk.VerificationLinks(batch, k => ticketIds[k]);
```

Everything else — defining event types in JSON, hashing helpers, the lock and timestamp steps, the auth types, timeouts and errors — is covered in the usage guide:

- **[docs/USAGE.md](docs/USAGE.md)** — full usage guide
- **[`examples/Tracing.Sdk.Examples`](examples/Tracing.Sdk.Examples/)** — runnable examples for every step of a draw, and a custom schema

## Design notes

- **Canonicalization.** JSON follows [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) (JCS): member names sorted by UTF-16 code unit (ordinal), numbers formatted per ECMAScript `Number::toString` by the ES6 number serializer of RFC 8785 co-author Anders Rundgren, vendored unmodified apart from its visibility (see [`Vendor/Es6NumberSerializer`](src/Tracing.Sdk/Vendor/Es6NumberSerializer/README.md)). Parsing is `System.Text.Json`, set up to accept and reject exactly what the PHP SDK does (at most 511 levels of nesting, last duplicate member wins, no BOM or lone surrogates). XML is canonicalized with [Exclusive XML Canonicalization 1.0](https://www.w3.org/TR/xml-exc-c14n/) without comments by .NET's `XmlDsigExcC14NTransform`. On top of it, the SDK reproduces what the PHP SDK's libxml does, so the output bytes are identical: insignificant whitespace is dropped by libxml's exact `preserveWhiteSpace = false` rules, namespace URIs must be absolute RFC 3986 URIs, and bytes are decoded by their BOM or declared encoding. DTDs are ignored, never processed, so untrusted input can never trigger XXE. `DataType.Raw` skips canonicalization entirely — the input is hashed exactly as given.
- **Hashing.** Keccak-256 (the original Keccak, as used by Ethereum — not FIPS-202 SHA3-256, which is what .NET's `SHA3_256` implements), via [BouncyCastle](https://www.bouncycastle.org/)'s `KeccakDigest`. Strings are hashed as UTF-8. Hashes are returned as the raw 32-byte digest (`byte[]`); `Keccak256Hasher.ToHex()` gives the `0x`-prefixed hex form the Indexer and the chain use. A Merkle leaf is `keccak256(prefix || canonical bytes)`, with prefix `0x00` by default; `0x01` and `0x02` are refused because they mark inner nodes and roots.
- **Event types.** A definition mirrors an `eventTypes` entry of the backend's `plugin.yaml`: its request fields with their types and roles, its anchoring mode (`transactionHash` or `merkle`), and two SDK additions, `hashing` and `link`. Requests are validated against it before anything is sent: undeclared fields are refused, bytes32 values are normalized, uint64 stays exact above 2^53. The SDK builds no Merkle tree and derives no key; roots, keys and ids come only from the Indexer's answer.
- **Verification.** The SDK does not query the chain. It builds a link to the integrity portal (`https://integrity.vmatrix.com/v1/` by default) carrying the fields the portal looks the record up by. For a Merkle type there is one link per leaf, with your opaque `ticketId` after `#` so it never reaches a server log.
- **Transport.** `HttpClient` over `SocketsHttpHandler`, one per SDK instance (dispose the SDK, or keep one for the application's lifetime). Redirects are never followed, and the timeout bounds the whole request.

## Testing

```bash
dotnet test
```
