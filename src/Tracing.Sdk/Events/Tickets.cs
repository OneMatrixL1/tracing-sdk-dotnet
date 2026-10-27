namespace Tracing.Sdk.Events;

/// <summary>Names of the tickets plugin's event types (see <see cref="EventPresets.Tickets"/>).</summary>
public static class Tickets
{
    /// <summary>One batch of tickets of a draw, anchored as a Merkle tree.</summary>
    public const string Batch = "batch";

    /// <summary>Locks a draw; the backend derives the drawRoot.</summary>
    public const string Lock = "lock";

    /// <summary>The RFC 3161 timestamp of a lock.</summary>
    public const string LockTs = "lockts";

    /// <summary>The hash of a payout-events record of a draw.</summary>
    public const string Events = "events";

    /// <summary>The hash of the auditor's reconciliation of a draw.</summary>
    public const string Recon = "recon";

    /// <summary>The hash of the list of sub-agents.</summary>
    public const string Subagent = "subagent";
}
