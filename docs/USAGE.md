# Tracing SDK (.NET) — Usage Guide

The SDK sends records to an Indexer service as **event types**: the default `anchor` type, the types of the tickets plugin, or any schema you describe in code or JSON ([§7](#7-event-types-tickets-and-other-schemas)). For each record it can **canonicalize** and **hash** the data with Keccak-256, **validate** the request against the event type's fields, **send** it, and build the **link to the integrity portal** that verifies it. Lookup and on-chain verification happen in the portal, not in the SDK.

Network calls are `async` and take a `CancellationToken`, and the configuration and results are typed.

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
using Tracing.Sdk.Events;
using Tracing.Sdk.Hash; // Keccak256Hasher.ToHex, to print a hash as 0x-hex

using var sdk = new TracingSdk(new TracingSdkConfig
{
    // Base URL of your Indexer. Paths are appended by the SDK.
    Endpoint = "https://indexer.example.com",

    // Default options for every call. Optional if every call passes its own SendOptions.
    Options = new SendOptions(
        DataType.Json,  // dataType, for hashing
        60_000),        // timeoutMs

    // Required.
    Auth = AuthConfig.ApiToken("your-api-token"),

    // The event types AnchorAsync() accepts; without it, only "anchor".
    EventTypes = EventTypeRegistry.FromJsonFile("tickets-events.json"),
});
```

| Property | Required | Description |
| --- | --- | --- |
| `Endpoint` | yes | Indexer base URL. A trailing `/` is trimmed. |
| `Auth` | yes | See [Authentication](#5-authentication). |
| `Options` | no | A `SendOptions` used as the default for every call. |
| `EventTypes` | no | The event types `AnchorAsync()` accepts ([§7.1](#71-defining-event-types)). Default: only `anchor`. |
| `Portal` | no | Where verification links point ([§7.4](#74-verification-links)). Default: `https://integrity.vmatrix.com/v1/`. |

Invalid or missing config throws `ConfigException` **at construction time**, so a missing certificate file or an inconsistent event type fails immediately rather than on the first send.

`TracingSdk` owns its HTTP connections and is safe to share: create one and keep it for the application's lifetime (for example as a singleton in dependency injection), or dispose it when done.

---

## 3. Sending and hashing records

### 3.1 Sending — `AnchorAsync()`

Every record is sent with `AnchorAsync()` to `POST {endpoint}/api/anchors?eventType=<name>`. The default `anchor` type is always registered and takes a record hash and its signing time:

```csharp
var hash = sdk.Hash(JsonSerializer.Serialize(new { orderId = 1, amount = 250000 }));

var result = await sdk.AnchorAsync("anchor", new Dictionary<string, object?>
{
    ["hash"] = hash,                                              // byte[] or "0x…" hex
    ["signingTime"] = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
});

Console.WriteLine($"{result.StatusCode} {result.AnchorId}");     // 202 9b2f4c1e-…
Console.WriteLine(sdk.VerificationLink(result));                 // the portal link that verifies it
```

Other types (tickets, your own schemas), how fields are validated, what `AnchorResult` holds, and verification links are covered in [§7](#7-event-types-tickets-and-other-schemas).

### 3.2 Hashing — `Hash()`

`Hash()` canonicalizes and hashes a record without sending it. It is synchronous.

```csharp
byte[] a = sdk.Hash(JsonSerializer.Serialize(new { orderId = 1 }));   // config default type
byte[] b = sdk.Hash(xmlString, SendOptions.ForDataType(DataType.Xml)); // per-call type
string hex = Keccak256Hasher.ToHex(a);                               // "0x…", as the Indexer shows it
```

`rawData` is a `string` (hashed as its UTF-8 bytes) or a `byte[]`. A string holding an unpaired surrogate has no UTF-8 form and throws `CanonicalizationException`.

Hashing is deterministic: the same record always produces the same hash, on any machine, in any SDK language. `HashFor()` and `LeavesFor()` hash the way a given event type says, and `Hashes.*` offers the same without an SDK instance ([§7.3](#73-hashing-records)).

---

## 4. Options: data type & timeout

`SendOptions` is an immutable object carrying two settings, `DataType` and `TimeoutMs`. It can be given as the config default, per call, or both.

### 4.1 Data types & canonicalization

| Value | Behaviour |
| --- | --- |
| `DataType.Json` | [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) JSON Canonicalization Scheme — keys sorted by UTF-16 code unit, numbers normalized per ECMAScript `Number::toString` (`1`, `1.0`, `1e0` are identical; `-0` → `0`). Input must be valid UTF-8 without a BOM. |
| `DataType.Xml` | [Exclusive XML Canonicalization 1.0](https://www.w3.org/TR/xml-exc-c14n/), without comments — normalizes attribute order and insignificant whitespace, and keeps only the namespace declarations the document actually uses. Namespace prefixes remain significant. Output is byte-identical to the PHP SDK's. Bytes are decoded by their BOM or declared `encoding` (UTF-8 by default). DTDs are ignored; referencing any entity beyond the five predefined ones is an error (XXE protection). Namespace URIs must be absolute RFC 3986 URIs. |
| `DataType.Raw` | No parsing at all — the bytes you pass are hashed exactly as given. Use when the caller already guarantees one deterministic representation. |

Resolution order:

```csharp
// 1. Config default
sdk.Hash(rawJson);

// 2. Per-call override — wins over the config default
sdk.Hash(rawXml, SendOptions.ForDataType(DataType.Xml));
```

`HashFor()` and `LeavesFor()` look at the event type's `hashing.dataType` first. If no data type is found anywhere, `ConfigException` is thrown.

### 4.2 Request timeout

`TimeoutMs` caps how long a single HTTP request to the Indexer may take, in milliseconds. It defaults to **10 000 ms (10 s)** and covers the whole request: connecting, sending, and reading the full response. A Merkle event type such as tickets `batch` answers only once its transaction is confirmed on chain, so give it a much longer timeout.

```csharp
// For one call only.
await sdk.AnchorAsync(Tickets.Batch, fields, SendOptions.ForTimeoutMs(120_000));
```

A per-call `TimeoutMs` wins over the config one; a call that supplies none keeps the config default. A value of `0` or less throws `ConfigException` when the `SendOptions` is built. Exceeding the timeout surfaces as `TransportException`. Cancelling through your own `CancellationToken` surfaces as `OperationCanceledException` instead, as usual in .NET.

### 4.3 `SendOptions` is immutable

```csharp
var json = new SendOptions(DataType.Json);   // same as SendOptions.ForDataType(DataType.Json)
var xml  = json.WithDataType(DataType.Xml);  // returns a copy; json is unchanged
var slow = json.WithTimeoutMs(60_000);       // likewise
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
| `ConfigException` | Missing/invalid config, an inconsistent event type, an unknown event type, a request field that is unknown, missing or invalid for its type, a `TimeoutMs` of `0` or less, no data type to hash with, a link that cannot be built, or an mTLS file that cannot be loaded. |
| `CanonicalizationException` | The raw data cannot be canonicalized for the chosen data type (e.g. malformed JSON/XML), or a string holds an unpaired surrogate. |
| `TransportException` | The HTTP request failed: network, or it exceeded `TimeoutMs`. |
| `IndexerException` | A `TransportException` for a non-2xx Indexer answer. It carries `StatusCode`, `Body`, and the `ErrorCode`, `Field`, `Index`, `ServerMessage` and `BatchId` of its `{ "error": … }` body, so you can branch on e.g. `CONFIRMATION_TIMEOUT`. |

`ConfigException` and `CanonicalizationException` are thrown **before** anything leaves the process, so a refused request is never sent. The underlying exception, when there is one, is the `InnerException`.

```csharp
try
{
    await sdk.AnchorAsync(eventType, fields);
}
catch (Exception e) when (e is ConfigException or CanonicalizationException)
{
    // Bad input — retrying the same payload will not help.
}
catch (IndexerException e) when (e.StatusCode is 400 or 409 or 422)
{
    // The Indexer refused the request; e.ErrorCode says why.
}
catch (TransportException)
{
    // Transient — safe to retry or queue for later.
}
```

The SDK does not retry on your behalf. Because hashing is deterministic, retrying with the same payload re-sends the same hash, and a retried tickets `batch` answers the same tree. A retry policy such as Polly's works as usual.

---

## 7. Event types (tickets and other schemas)

The Indexer runs **event types** declared by backend plugins; the default `anchor` type is one of them. Each type has its own request fields. The tickets plugin is one such plugin, covered in [`TICKETS-INTEGRATION.md`](../../docs/TICKETS-INTEGRATION.md): `batch`, `lock`, `lockts`, `events`, `recon` and `subagent`. The SDK does not hard-code any of them. It reads a **definition** of each type and uses it to:

- validate and send a record: `AnchorAsync()` → `POST {endpoint}/api/anchors?eventType=<name>`;
- hash a record the way the type expects: `HashFor()` and `LeavesFor()`;
- build a **verification link** to the integrity portal: `VerificationLink()` and `VerificationLinks()`. Looking a record up and checking it against the chain happens in the portal, not in the SDK.

A schema that differs, or a plugin the SDK has never seen, needs only a new definition and no SDK change.

### 7.1 Defining event types

A definition mirrors an `eventTypes` entry of the backend's `plugin.yaml`. Event types are the only part of the configuration that comes from files; endpoint, auth, options and portal are set in code (§2). Load definitions from a JSON file:

```json
{
  "plugin": "tickets",
  "eventTypes": [
    {
      "name": "batch",
      "anchoring": { "mode": "merkle", "merkle": { "params": { "maxTreeSize": 100000 } } },
      "request": {
        "fields": [
          { "name": "draw",         "type": "string",    "required": true },
          { "name": "channel",      "type": "string",    "required": true },
          { "name": "sequence",     "type": "uint64",    "required": true, "role": "sequence" },
          { "name": "ticketHashes", "type": "bytes32[]", "required": true, "role": "leaves" },
          { "name": "value",        "type": "uint64",    "required": true }
        ]
      },
      "hashing": { "leafPrefix": "0x00" },
      "link": { "fields": ["draw", "channel", "sequence"], "leafParam": "ticketHash" }
    },
    {
      "name": "lock",
      "anchoring": { "mode": "transactionHash", "key": "derived" },
      "request": { "fields": [ { "name": "draw", "type": "string", "required": true } ] }
    }
  ]
}
```

| Member | Meaning |
| --- | --- |
| `name` | The `eventType` query value. |
| `anchoring.mode` | `transactionHash` (one log per request, the default) or `merkle` (the request carries leaves, and the backend builds a tree and anchors its root). |
| `anchoring.key` | `client` (the default) or `derived`. With `derived`, the backend computes the key (e.g. the tickets drawRoot) and answers it as `key`. |
| `anchoring.merkle.params.maxTreeSize` | The most leaves one request may carry, checked before sending. |
| `request.fields[]` | Every field the body may hold. `type` is `string`, `bytes32`, `bytes`, `uint64` or `bytes32[]`. `required` defaults to `false`. |
| `role` | `key`: the bytes32 the log is keyed by. `leaves`: the bytes32[] of a `merkle` type. `sequence` (SDK only): the uint64 that numbers the first leaf, so leaf *k* is number `sequence + k` in its link. |
| `hashing` (SDK only) | `dataType` (`json`, `xml` or `raw`) and `leafPrefix` (one hex byte, `merkle` only, default `0x00`). See [§7.3](#73-hashing-records). |
| `link` (SDK only) | `fields`: the request fields a link carries, in order. The default is every required `string`, `bytes32` or `uint64` field except the leaves. `leafParam` (default `ticketHash`) and `keyParam` (default `key`) name the leaf and derived-key parameters. |

`response`, `chain` and any other member the SDK does not use are ignored, so you can convert a `plugin.yaml` to JSON and use it as it is. Comments and trailing commas are allowed.

The definitions are checked when they are registered. Each of these throws `ConfigException`:
- a field declared twice, or an unknown type or role;
- a `key` field that is not `bytes32`, or a `merkle` type without a `leaves` field;
- a derived key that is also a request field;
- a link field that does not exist or is not a scalar;
- a leaf prefix of `0x01` or `0x02`, which are reserved for inner nodes and roots.

Register the definitions with the SDK through `TracingSdkConfig.EventTypes`:

```csharp
using Tracing.Sdk.Events;

using var sdk = new TracingSdk(new TracingSdkConfig
{
    Endpoint = "https://indexer.example.com",
    Auth = AuthConfig.ApiToken("your-api-token"),
    Options = new SendOptions(DataType.Json, 60_000),
    EventTypes = EventTypeRegistry.FromJsonFile("tickets-events.json"),
    // or in code, from the shipped presets:
    // EventTypes = EventTypeRegistry.Default().Register(EventPresets.Tickets),
});
```

- `EventPresets.Anchor` and `EventPresets.Tickets` are the backend's built-in types as C# definitions. [`examples/…/config/tickets-events.json`](../examples/Tracing.Sdk.Examples/config/tickets-events.json) is the same tickets set as JSON; a test keeps the two identical.
- Without `EventTypes`, only `anchor` is registered.
- The SDK copies the registry when it is constructed, so registering more types later does not affect it.
- A definition is a plain `record` (`EventTypeDefinition`, `FieldDefinition`, ...), so you can also build one in code, or derive one from a preset with `with { ... }`.

### 7.2 Anchoring a record — `AnchorAsync()`

```csharp
var result = await sdk.AnchorAsync(Tickets.Batch, new Dictionary<string, object?>
{
    ["draw"] = "draw-1",
    ["channel"] = "channel-1",
    ["sequence"] = 0UL,
    ["ticketHashes"] = sdk.LeavesFor(Tickets.Batch, ticketsAsJson), // see §7.3
    ["value"] = 30_000UL,
});

result.TryGetUInt64("batchId", out var batchId);
result.TryGetString("batchRoot", out var batchRoot);
Console.WriteLine($"{result.StatusCode} {result.AnchorId} batch {batchId} root {batchRoot}");
```

The fields are validated against the definition **before** anything is sent:
- An undeclared field is refused, and so is a missing required one.
- `bytes32` takes 64 hex digits (`0x` optional) or a 32-byte `byte[]`. It must not be all zeros, and it is sent as lowercase `0x` hex.
- `bytes` takes `0x` hex or a `byte[]`.
- `uint64` takes any non-negative integer type or a `BigInteger`, and is written as an exact JSON integer, also above 2^53.
- `bytes32[]` takes any list of bytes32 values, holding 1 to `maxTreeSize` of them with no repeats.

A `JsonObject` overload takes the body as JSON instead.

`AnchorResult` keeps the answer as is:

| Member | |
| --- | --- |
| `StatusCode` | `200` for a `merkle` type, which answers once the tree is confirmed on chain. `202` for a submitted direct anchor. |
| `AnchorId` | Names the anchor. Its webhook repeats it as `data.anchorId`. |
| `Key` | The derived key (the drawRoot of `lock` / `lockts`), or `null`. |
| `TryGetString(name)`, `TryGetUInt64(name)` | Any other member: `batchId`, `batchRoot`, `ticketRoot`, `txHash`, `blockchainStatus`, ... |
| `Json`, `Body`, `RequestBody` | The parsed answer, the raw answer, and the body exactly as sent. |

The SDK builds no tree and derives no key: roots, keys and ids come only from the Indexer. `AnchorAsync()` sends one record per call; the `/api/anchors/batch` route is not used for event types.

A `merkle` type answers only once its transaction is confirmed, so give those calls a long `TimeoutMs` (the examples use 60 s). When the Indexer refuses a request, `AnchorAsync()` throws `IndexerException`, whose `ErrorCode` says why:

```csharp
try
{
    result = await sdk.AnchorAsync(Tickets.Batch, fields);
}
catch (IndexerException e) when (e.ErrorCode == "CONFIRMATION_TIMEOUT")
{
    // The batch is stored and still anchoring: send the same request again.
    // The retry answers the same tree and anchors nothing twice.
}
catch (IndexerException e) when (e.ErrorCode == "DRAW_HAS_NO_BATCH")
{
    // A lock before any batch of the draw was confirmed.
}
```

### 7.3 Hashing records

The backend never hashes a record. Every bytes32 you send (a record hash, a ticket hash) is yours, and whoever checks the record later must hash it the same way. Two helpers follow the event type's definition:

```csharp
// Direct type: keccak256(canonical bytes).
byte[] hash = sdk.HashFor(Tickets.Events, payoutsJson);

// Merkle type: keccak256(leafPrefix || canonical bytes), one per leaf, in order.
IReadOnlyList<byte[]> leaves = sdk.LeavesFor(Tickets.Batch, ticketsAsJson);
```

The data type is the event type's `hashing.dataType`, then the per-call `SendOptions.DataType`, then the config's. The leaf prefix is `hashing.leafPrefix`, by default `0x00`, so that a leaf can never be mistaken for an inner node (`0x01`) or a batch root (`0x02`).

The same hashes are available without an SDK instance, in `Tracing.Sdk.Hash.Hashes`:

| Helper | Returns |
| --- | --- |
| `Hashes.Record(rawData, dataType)` | keccak256 of the canonical bytes. This is the same as `sdk.Hash()`. |
| `Hashes.Leaf(rawData, dataType, prefix = 0x00)` | keccak256(prefix ‖ canonical bytes). `0x01` and `0x02` are refused. |
| `Hashes.Leaves(rawData[], dataType, prefix = 0x00)` | `Leaf` of each item, in order. |
| `Hashes.DrawHash(draw)` | keccak256(utf8(draw)): the `drawHash` topic of the `events` / `recon` logs. |
| `Hashes.LockTimestampImprint(drawRoot)` | SHA-256 over the 32 raw bytes of a drawRoot: the imprint a timestamp authority must sign for `lockts`. |

Every helper returns the raw 32-byte digest; `Keccak256Hasher.ToHex()` gives the `0x` form.

### 7.4 Verification links

Verification happens in the integrity portal. The portal looks a record up by the fields in its link, checks it against the chain in the browser, and shows the result. The SDK only builds the link, without any network call:

```
https://integrity.vmatrix.com/v1/?type=<eventType>&<field>=<value>&…[#ticketId=<base64url>]
```

```csharp
// Direct types: the link fields of the request, plus the derived key if any.
string lockLink = sdk.VerificationLink(lockResult);
// https://integrity.vmatrix.com/v1/?type=lock&draw=draw-1&key=0xefa5…6f41

// Merkle types: one link per leaf, with that leaf's own number and hash,
// and your ticket id after '#'.
IReadOnlyList<string> ticketLinks = sdk.VerificationLinks(batchResult, k => tickets[k].Id);
// https://integrity.vmatrix.com/v1/?type=batch&draw=draw-1&channel=channel-1&sequence=3&ticketHash=0x76d4…95f5#ticketId=VC1kcmF3LTEtMDAwMDAz

// From values you kept, without an AnchorResult.
string link = sdk.VerificationLink(Tickets.Batch, new Dictionary<string, object?>
{
    ["draw"] = "draw-1", ["channel"] = "channel-1", ["sequence"] = 3UL, ["ticketHash"] = leaf,
}, ticketId: "T-draw-1-000003");
```

| Event type | Link carries |
| --- | --- |
| `batch` | `draw`, `channel`, `sequence` (the ticket's own number), `ticketHash`, `#ticketId` |
| `lock`, `lockts` | `draw`, `key` (the drawRoot). The `lockts` token is `bytes` and never goes in a link. |
| `events`, `recon` | `hash`, `draw` |
| `subagent` | `hash` |

- **`ticketId`** is your own id of one leaf (a ticket), opaque to the portal. It is UTF-8 encoded, base64url-encoded, and placed after `#`, so the browser never sends it to a server. It applies only to the leaves of a `merkle` type. Passing one for another type throws `ConfigException`.
- **Values** are written in their normalized form, so a link built from kept fields matches the one built from the answer. bytes32 is lowercase `0x` hex, uint64 is decimal, and every value is URL-encoded.
- **Portal config** (`TracingSdkConfig.Portal`, e.g. `new PortalConfig { BaseUrl = "…" }`) is optional. `BaseUrl` defaults to `https://integrity.vmatrix.com/v1/`; a base URL with its own query is extended with `&`, and a fragment is refused. `ParamNames` renames the `type` and `ticketId` parameters. A field named like one of them throws `ConfigException` when the link is built.

### 7.5 The life of a draw, in code

This follows [`TICKETS-INTEGRATION.md`](../../docs/TICKETS-INTEGRATION.md):

```csharp
// 1. Sell: one batch at a time; next sequence = previous sequence + previous count.
var batch = await sdk.AnchorAsync(Tickets.Batch, new Dictionary<string, object?>
{
    ["draw"] = draw, ["channel"] = channel, ["sequence"] = sequence,
    ["ticketHashes"] = sdk.LeavesFor(Tickets.Batch, tickets), ["value"] = total,
});
var links = sdk.VerificationLinks(batch, k => ticketIds[k]);

// 2. Lock, once the last batch has answered 200. The backend derives the drawRoot.
var locked = await sdk.AnchorAsync(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = draw });
byte[] imprint = Hashes.LockTimestampImprint(locked.Key!); // have a TSA sign this

// 3. Timestamp: the TSA's DER answer goes on chain whole.
await sdk.AnchorAsync(Tickets.LockTs, new Dictionary<string, object?> { ["draw"] = draw, ["signature"] = tsaResponseDer });

// 4. Records, whenever they exist.
await sdk.AnchorAsync(Tickets.Events, new Dictionary<string, object?> { ["hash"] = sdk.HashFor(Tickets.Events, payouts), ["draw"] = draw });
await sdk.AnchorAsync(Tickets.Recon, new Dictionary<string, object?> { ["hash"] = sdk.HashFor(Tickets.Recon, reconciliation), ["draw"] = draw });
await sdk.AnchorAsync(Tickets.Subagent, new Dictionary<string, object?> { ["hash"] = sdk.HashFor(Tickets.Subagent, subagentList) });
```

The runnable versions are the `tickets-batch`, `tickets-lock` and `tickets-records` examples ([§8](#8-runnable-examples)). [`config/custom-events.example.json`](../examples/Tracing.Sdk.Examples/config/custom-events.example.json) and the `custom` example show the same calls for a schema the SDK does not ship.

---

## 8. Runnable examples

[`examples/Tracing.Sdk.Examples`](../examples/Tracing.Sdk.Examples/) holds one example per file:

| Name | File | Shows |
| --- | --- | --- |
| `tickets-batch` | [`TicketsBatchExample.cs`](../examples/Tracing.Sdk.Examples/TicketsBatchExample.cs) | Hash tickets with `LeavesFor()`, anchor two consecutive `batch` requests (retrying `CONFIRMATION_TIMEOUT`), print one portal link per ticket |
| `tickets-lock` | [`TicketsLockExample.cs`](../examples/Tracing.Sdk.Examples/TicketsLockExample.cs) | `lock` a draw, print the TSA imprint, anchor the TSA token (`resp.tsr`, or `TSA_RESPONSE`) with `lockts` |
| `tickets-records` | [`TicketsRecordsExample.cs`](../examples/Tracing.Sdk.Examples/TicketsRecordsExample.cs) | Hash and anchor `events`, `recon` and `subagent` records, print their links |
| `custom` | [`CustomEventExample.cs`](../examples/Tracing.Sdk.Examples/CustomEventExample.cs) | A `shipment` type from [`custom-events.example.json`](../examples/Tracing.Sdk.Examples/config/custom-events.example.json) |

Set the endpoint and token in [`ExampleSettings.cs`](../examples/Tracing.Sdk.Examples/ExampleSettings.cs); the `TRACING_API_KEY` environment variable replaces the token, so the key never has to be written into the code. The event types come from the JSON files in [`config/`](../examples/Tracing.Sdk.Examples/config/). Then run an example by name:

```bash
TRACING_API_KEY=<your key> dotnet run --project examples/Tracing.Sdk.Examples -- tickets-batch
```

---

## 9. API reference

```csharp
new TracingSdk(TracingSdkConfig config)

Task<AnchorResult> AnchorAsync(string eventType, IReadOnlyDictionary<string, object?> | JsonObject fields,
                               SendOptions? options = null, CancellationToken ct = default)
    // AnchorResult: EventType, StatusCode, AnchorId, Key, Json, Body, RequestBody, TryGetString(name), TryGetUInt64(name)

byte[] Hash(string | byte[] rawData, SendOptions? options = null)       // 32-byte Keccak-256 digest
byte[] HashFor(string eventType, string | byte[] rawData, SendOptions? options = null)
IReadOnlyList<byte[]> LeavesFor(string eventType, IEnumerable<string> | IEnumerable<byte[]> rawData, SendOptions? options = null)

string VerificationLink(AnchorResult result)                                                  // direct types
IReadOnlyList<string> VerificationLinks(AnchorResult result, Func<int, string?>? ticketIdFor = null)  // merkle types, one per leaf
string VerificationLink(string eventType, IReadOnlyDictionary<string, object?> fields, string? ticketId = null)

static EventTypeRegistry EventTypeRegistry.FromJson(string json) / FromJsonFile(path) / Default()
EventTypeRegistry.Register(EventTypeDefinition | IEnumerable<EventTypeDefinition>)
EventPresets.Anchor / EventPresets.Tickets      // Tickets.Batch, .Lock, .LockTs, .Events, .Recon, .Subagent: the names

static byte[] Hashes.Record(rawData, DataType) / Leaf(rawData, DataType, byte prefix = 0x00) / DrawHash(string draw)
static IReadOnlyList<byte[]> Hashes.Leaves(rawData[], DataType, byte prefix = 0x00)
static byte[] Hashes.LockTimestampImprint(string | byte[] drawRoot)
static string Keccak256Hasher.ToHex(byte[] bytes)             // "0x…" lowercase hex, for display or storage
```

`SendOptions`:

```csharp
new SendOptions(DataType? dataType = null, int? timeoutMs = null)
SendOptions.ForDataType(DataType) / ForTimeoutMs(int)
options.DataType / options.TimeoutMs
options.WithDataType(...) / WithTimeoutMs(...)   // return a copy
```

The only Indexer endpoint used is `POST /api/anchors?eventType=…`.

---

## 10. Hashes across SDKs

The same record produces the same hash in the PHP, JavaScript and .NET SDKs. This is verified against the shared `testdata/` vectors and by differential testing against the PHP SDK. Known exceptions:

- **Integers between 2^53 and 2^63 in JSON.** RFC 8785 treats every JSON number as an IEEE 754 double, so `9007199254740993` canonicalizes as `9007199254740992` here. The PHP SDK keeps such values as exact 64-bit integers, so their hashes differ. Integers beyond 2^63 and all integers up to 2^53 agree.
- **`<!ELEMENT>` declarations in an XML DTD.** libxml consults them when deciding which whitespace to drop, but this SDK never reads a DTD: under `<!ELEMENT r ANY>`, the PHP SDK keeps the space in `<r><a/> <b/></r>`, but this SDK drops it. Documents without element declarations are unaffected.
- **An empty prefixed namespace declaration (`xmlns:p=""`).** XML Namespaces 1.0 forbids it. .NET's parser rejects it, while libxml accepts it with a warning.
