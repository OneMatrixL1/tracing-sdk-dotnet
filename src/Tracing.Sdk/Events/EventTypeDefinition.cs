using System.Diagnostics.CodeAnalysis;

namespace Tracing.Sdk.Events;

/// <summary>How an event type is anchored on chain.</summary>
public enum AnchoringMode
{
    /// <summary>One log per request; the record is found again by its transaction.</summary>
    TransactionHash,

    /// <summary>The request carries leaves; the backend builds a Merkle tree and anchors its root.</summary>
    Merkle,
}

/// <summary>Where the key of a log comes from.</summary>
public enum KeyMode
{
    /// <summary>The client sends it, in the field with <see cref="FieldRole.Key"/> (or there is none).</summary>
    Client,

    /// <summary>The backend derives it and answers it as <c>key</c> (e.g. the tickets drawRoot).</summary>
    Derived,
}

/// <summary>The type of a request field, as the backend declares it.</summary>
public enum FieldType
{
    /// <summary><c>string</c>: a JSON string.</summary>
    String,

    /// <summary><c>bytes32</c>: 64 hex digits, <c>0x</c> optional, not all zeros.</summary>
    Bytes32,

    /// <summary><c>bytes</c>: <c>0x</c>-prefixed hex.</summary>
    Bytes,

    /// <summary><c>uint64</c>: a JSON integer from 0 to 2^64-1.</summary>
    Uint64,

    /// <summary><c>bytes32[]</c>: an array of bytes32.</summary>
    Bytes32Array,
}

/// <summary>What a field means to the anchoring, beyond its value.</summary>
public enum FieldRole
{
    None,

    /// <summary>The bytes32 the log is keyed by (the record hash).</summary>
    Key,

    /// <summary>The bytes32[] leaves of a <see cref="AnchoringMode.Merkle"/> type.</summary>
    Leaves,

    /// <summary>
    /// SDK only, not sent to the backend as a role: the uint64 that numbers
    /// the first leaf, so leaf k is number <c>sequence + k</c> in a portal link.
    /// </summary>
    Sequence,
}

/// <summary>One field of an event type's request body.</summary>
/// <param name="Name">the JSON member name</param>
/// <param name="Type">its type</param>
/// <param name="Required">whether the backend refuses a request without it</param>
/// <param name="Role">what it means to the anchoring</param>
public sealed record FieldDefinition(string Name, FieldType Type, bool Required = false, FieldRole Role = FieldRole.None);

/// <summary>How an event type is anchored.</summary>
/// <param name="Mode">one log per request, or a Merkle tree over the request's leaves</param>
/// <param name="Key">whether the client sends the key or the backend derives it</param>
/// <param name="MaxTreeSize">the most leaves one request may carry (Merkle only); null for no SDK-side limit</param>
public sealed record AnchoringDefinition(
    AnchoringMode Mode = AnchoringMode.TransactionHash,
    KeyMode Key = KeyMode.Client,
    int? MaxTreeSize = null);

/// <summary>How records of an event type are hashed by <see cref="TracingSdk.HashFor(string, string, SendOptions?)"/>.</summary>
/// <param name="DataType">how a record is canonicalized; null falls back to the SDK options</param>
/// <param name="LeafPrefix">the byte put before a leaf's canonical bytes (Merkle only); null means 0x00</param>
public sealed record HashingDefinition(DataType? DataType = null, byte? LeafPrefix = null);

/// <summary>What an event type's portal verification link carries.</summary>
/// <param name="Fields">the request fields put in the link, in order; null means every
/// required field of type string, bytes32 or uint64 that is not the leaves</param>
/// <param name="LeafParam">the link parameter that carries the leaf (Merkle only)</param>
/// <param name="KeyParam">the link parameter that carries a derived key</param>
public sealed record LinkDefinition(IReadOnlyList<string>? Fields = null, string LeafParam = "ticketHash", string KeyParam = "key");

/// <summary>
/// One event type the Indexer accepts, mirroring an <c>eventTypes</c> entry of
/// the backend's <c>plugin.yaml</c>. Building one validates nothing; registering
/// it in an <see cref="EventTypeRegistry"/> does.
/// </summary>
public sealed record EventTypeDefinition
{
    /// <summary>The <c>eventType</c> query value, e.g. <c>batch</c>.</summary>
    public required string Name { get; init; }

    public AnchoringDefinition Anchoring { get; init; } = new();

    /// <summary>Every field the request body may hold; any other field is refused.</summary>
    public required IReadOnlyList<FieldDefinition> RequestFields { get; init; }

    public HashingDefinition? Hashing { get; init; }

    public LinkDefinition? Link { get; init; }

    /// <summary>The field with <see cref="FieldRole.Leaves"/>, if any.</summary>
    public FieldDefinition? LeavesField => RequestFields.FirstOrDefault(f => f.Role == FieldRole.Leaves);

    /// <summary>The field with <see cref="FieldRole.Sequence"/>, if any.</summary>
    public FieldDefinition? SequenceField => RequestFields.FirstOrDefault(f => f.Role == FieldRole.Sequence);

    /// <summary>The request fields a portal link carries, resolved from <see cref="Link"/>.</summary>
    public IReadOnlyList<FieldDefinition> LinkFields =>
        Link?.Fields is { } names
            ? names.Select(n => RequestFields.First(f => f.Name == n)).ToList()
            : RequestFields.Where(f => f.Required && f.Role != FieldRole.Leaves && IsScalar(f.Type)).ToList();

    internal static bool IsScalar(FieldType type) => type is FieldType.String or FieldType.Bytes32 or FieldType.Uint64;

    /// <exception cref="ConfigException">the definition is inconsistent</exception>
    internal void Validate()
    {
        [DoesNotReturn]
        void Fail(string message) => throw new ConfigException($"Event type \"{Name}\": {message}");

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ConfigException("An event type needs a non-empty name");
        }

        if (RequestFields is null)
        {
            Fail("requestFields is required");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in RequestFields!)
        {
            if (field is null || string.IsNullOrEmpty(field.Name))
            {
                Fail("every field needs a non-empty name");
            }

            if (!seen.Add(field!.Name))
            {
                Fail($"field \"{field.Name}\" is declared twice");
            }

            var expected = field.Role switch
            {
                FieldRole.Key => FieldType.Bytes32,
                FieldRole.Leaves => FieldType.Bytes32Array,
                FieldRole.Sequence => FieldType.Uint64,
                _ => field.Type,
            };

            if (field.Type != expected)
            {
                Fail($"field \"{field.Name}\" has role {field.Role} and must be of type {expected}");
            }
        }

        foreach (var role in new[] { FieldRole.Key, FieldRole.Leaves, FieldRole.Sequence })
        {
            if (RequestFields!.Count(f => f.Role == role) > 1)
            {
                Fail($"at most one field may have role {role}");
            }
        }

        var merkle = Anchoring.Mode == AnchoringMode.Merkle;

        if (merkle && LeavesField is null)
        {
            Fail("a merkle type needs a field with role Leaves");
        }

        if (!merkle && (LeavesField is not null || SequenceField is not null))
        {
            Fail("roles Leaves and Sequence belong to a merkle type");
        }

        if (!merkle && (Anchoring.MaxTreeSize is not null || Hashing?.LeafPrefix is not null))
        {
            Fail("maxTreeSize and leafPrefix belong to a merkle type");
        }

        if (Anchoring.MaxTreeSize is <= 0)
        {
            Fail("maxTreeSize must be positive");
        }

        if (Anchoring.Key == KeyMode.Derived && RequestFields!.Any(f => f.Role == FieldRole.Key))
        {
            Fail("a derived key cannot also be a request field");
        }

        if (Hashing?.LeafPrefix is 0x01 or 0x02)
        {
            Fail("leafPrefix 0x01 and 0x02 are reserved for inner nodes and roots");
        }

        if (Link is not null)
        {
            if (string.IsNullOrEmpty(Link.LeafParam) || string.IsNullOrEmpty(Link.KeyParam))
            {
                Fail("link leafParam and keyParam must be non-empty");
            }

            foreach (var name in Link.Fields ?? [])
            {
                var field = RequestFields!.FirstOrDefault(f => f.Name == name);

                if (field is null)
                {
                    Fail($"link field \"{name}\" is not a request field");
                }

                if (!IsScalar(field!.Type))
                {
                    Fail($"link field \"{name}\" must be of type string, bytes32 or uint64");
                }
            }
        }
    }
}
