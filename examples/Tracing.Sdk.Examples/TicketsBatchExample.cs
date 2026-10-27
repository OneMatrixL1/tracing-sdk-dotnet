using System.Text.Json;
using Tracing.Sdk.Events;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>
/// Step 1 of a draw (docs/TICKETS-INTEGRATION.md, "Sell"): hash the tickets,
/// anchor them in consecutive batches with eventType=batch, and print one
/// portal link per ticket.
/// </summary>
internal static class TicketsBatchExample
{
    private const string Channel = "channel-1";

    public static async Task RunAsync()
    {
        using var sdk = CreateTicketsSdk();

        // Two batches of one draw and channel. Batch n starts where batch n-1
        // ended: next sequence = previous sequence + previous count.
        var batches = new[] { MakeTickets(first: 0, count: 3), MakeTickets(first: 3, count: 2) };
        ulong sequence = 0;

        foreach (var tickets in batches)
        {
            // Leaves are keccak256(0x00 || canonical JSON of the ticket), in
            // order; the prefix comes from config/tickets-events.json. Whoever
            // verifies a ticket later must hash it the same way.
            var ticketHashes = sdk.LeavesFor(Tickets.Batch, tickets.Select(t => JsonSerializer.Serialize(t)));

            var fields = new Dictionary<string, object?>
            {
                ["draw"] = Draw,
                ["channel"] = Channel,
                ["sequence"] = sequence,
                ["ticketHashes"] = ticketHashes,
                ["value"] = (ulong)tickets.Sum(t => t.Price),
            };

            var result = await AnchorBatchAsync(sdk, fields);

            result.TryGetUInt64("batchId", out var batchId);
            result.TryGetString("batchRoot", out var batchRoot);
            result.TryGetString("txHash", out var txHash);
            Console.WriteLine($"[batch] sequence {sequence}, {tickets.Length} tickets -> anchorId {result.AnchorId}, batchId {batchId}, batchRoot {batchRoot}, tx {txHash}");

            // One link per ticket, in leaf order. The ticketId is your own id,
            // opaque to the portal; it travels after '#'.
            var links = sdk.VerificationLinks(result, k => tickets[k].TicketId);

            for (var k = 0; k < links.Count; k++)
            {
                Console.WriteLine($"  [link] ticket {sequence + (ulong)k}: {links[k]}");
            }

            // Take the next sequence only once this batch has answered 200.
            sequence += (ulong)tickets.Length;
        }
    }

    /// <summary>
    /// Anchor one batch. On 504 CONFIRMATION_TIMEOUT the batch is stored and
    /// still being anchored: send the same request again, which answers the
    /// same tree and anchors nothing twice.
    /// </summary>
    private static async Task<AnchorResult> AnchorBatchAsync(TracingSdk sdk, Dictionary<string, object?> fields)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await sdk.AnchorAsync(Tickets.Batch, fields);
            }
            catch (IndexerException e) when (e.ErrorCode == "CONFIRMATION_TIMEOUT" && attempt < 5)
            {
                Console.WriteLine($"[batch] not confirmed yet (attempt {attempt}), sending the same batch again");
            }
        }
    }

    /// <summary>Stand-in tickets; your own ticket record goes here.</summary>
    private static Ticket[] MakeTickets(int first, int count) =>
        Enumerable.Range(first, count)
            .Select(n => new Ticket($"T-{Draw}-{n:D6}", Draw, [n % 45 + 1, n % 44 + 2, n % 43 + 3, 7, 8, 9], Price: 10_000))
            .ToArray();

    private sealed record Ticket(string TicketId, string Draw, int[] Numbers, long Price);
}
