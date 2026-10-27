using System.Text.Json;
using Tracing.Sdk.Events;
using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>
/// Step 4 of a draw (docs/TICKETS-INTEGRATION.md, "Records"): hash a payout
/// events record, a reconciliation and the sub-agent list, and anchor each
/// hash with eventType=events, recon and subagent. The backend never sees the
/// records, only their hashes.
/// </summary>
internal static class TicketsRecordsExample
{
    public static async Task RunAsync()
    {
        using var sdk = CreateTicketsSdk();

        // What each record holds is agreed between you and whoever verifies it;
        // these shapes are stand-ins.
        var payouts = new
        {
            draw = Draw,
            events = new object[]
            {
                new { ticketId = $"T-{Draw}-000001", type = "paid", amount = 50_000 },
                new { ticketId = $"T-{Draw}-000004", type = "void" },
            },
        };
        var reconciliation = new { draw = Draw, auditor = "auditor-1", sold = 5, value = 50_000, matched = true };
        var subagents = new { version = 3, subagents = new[] { "agent-001", "agent-002" } };

        await AnchorRecordAsync(sdk, Tickets.Events, JsonSerializer.Serialize(payouts), Draw);
        await AnchorRecordAsync(sdk, Tickets.Recon, JsonSerializer.Serialize(reconciliation), Draw);
        await AnchorRecordAsync(sdk, Tickets.Subagent, JsonSerializer.Serialize(subagents), draw: null);
    }

    private static async Task AnchorRecordAsync(TracingSdk sdk, string eventType, string record, string? draw)
    {
        // Canonical JSON (the dataType in ExampleSettings.cs), then keccak256.
        var hash = sdk.HashFor(eventType, record);

        var fields = new Dictionary<string, object?> { ["hash"] = hash };

        if (draw is not null)
        {
            fields["draw"] = draw;
        }

        var result = await sdk.AnchorAsync(eventType, fields);

        Console.WriteLine($"[{eventType}] {Keccak256Hasher.ToHex(hash)} -> HTTP {result.StatusCode}, anchorId {result.AnchorId}");
        Console.WriteLine($"  [link] {sdk.VerificationLink(result)}");
    }
}
