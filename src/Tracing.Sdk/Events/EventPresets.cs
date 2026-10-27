namespace Tracing.Sdk.Events;

/// <summary>
/// Event types the backend ships, as definitions ready to register. Each
/// mirrors its backend <c>plugin.yaml</c>; another schema is a definition of
/// its own, in code or JSON, with no SDK change.
/// </summary>
public static class EventPresets
{
    /// <summary>The default <c>anchor</c> type: one hash and its signingTime per log.</summary>
    public static EventTypeDefinition Anchor { get; } = new()
    {
        Name = "anchor",
        RequestFields =
        [
            new("hash", FieldType.Bytes32, Required: true, Role: FieldRole.Key),
            new("signingTime", FieldType.Uint64, Required: true),
        ],
    };

    /// <summary>
    /// The six types of the tickets plugin (see docs/TICKETS-INTEGRATION.md):
    /// <c>batch</c>, <c>lock</c>, <c>lockts</c>, <c>events</c>, <c>recon</c>, <c>subagent</c>.
    /// </summary>
    public static IReadOnlyList<EventTypeDefinition> Tickets { get; } =
    [
        new()
        {
            Name = Events.Tickets.Batch,
            Anchoring = new(AnchoringMode.Merkle, MaxTreeSize: 100_000),
            RequestFields =
            [
                new("draw", FieldType.String, Required: true),
                new("channel", FieldType.String, Required: true),
                new("sequence", FieldType.Uint64, Required: true, Role: FieldRole.Sequence),
                new("ticketHashes", FieldType.Bytes32Array, Required: true, Role: FieldRole.Leaves),
                new("value", FieldType.Uint64, Required: true),
            ],
            // A leaf must not start with 0x01 (inner node) or 0x02 (batch root).
            Hashing = new(LeafPrefix: 0x00),
            // One link per ticket: its draw, channel, own number and hash.
            Link = new(["draw", "channel", "sequence"], LeafParam: "ticketHash"),
        },
        new()
        {
            Name = Events.Tickets.Lock,
            Anchoring = new(Key: KeyMode.Derived),
            RequestFields = [new("draw", FieldType.String, Required: true)],
        },
        new()
        {
            Name = Events.Tickets.LockTs,
            Anchoring = new(Key: KeyMode.Derived),
            RequestFields =
            [
                new("draw", FieldType.String, Required: true),
                new("signature", FieldType.Bytes, Required: true),
            ],
        },
        new()
        {
            Name = Events.Tickets.Events,
            RequestFields =
            [
                new("hash", FieldType.Bytes32, Required: true, Role: FieldRole.Key),
                new("draw", FieldType.String, Required: true),
            ],
        },
        new()
        {
            Name = Events.Tickets.Recon,
            RequestFields =
            [
                new("hash", FieldType.Bytes32, Required: true, Role: FieldRole.Key),
                new("draw", FieldType.String, Required: true),
            ],
        },
        new()
        {
            Name = Events.Tickets.Subagent,
            RequestFields = [new("hash", FieldType.Bytes32, Required: true, Role: FieldRole.Key)],
        },
    ];
}
