using Tracing.Sdk.Events;
using Tracing.Sdk.Hash;
using static Tracing.Sdk.Examples.ExampleSettings;

namespace Tracing.Sdk.Examples;

/// <summary>
/// Steps 2 and 3 of a draw (docs/TICKETS-INTEGRATION.md, "Lock" and
/// "Timestamp"): lock the draw, ask a timestamp authority to sign its
/// drawRoot, then anchor that token with eventType=lockts. Without a real
/// authority's answer in resp.tsr, a mock one (MockTsa.cs) signs it instead.
/// </summary>
internal static class TicketsLockExample
{
    /// <summary>The TSA's DER answer; set TSA_RESPONSE to point elsewhere.</summary>
    private static readonly string TsaResponse = Environment.GetEnvironmentVariable("TSA_RESPONSE") ?? "resp.tsr";

    /// <summary>Where the mock answer is written, to look at with `openssl ts -reply -in resp.mock.tsr -text`.</summary>
    private const string MockResponse = "resp.mock.tsr";

    public static async Task RunAsync()
    {
        using var sdk = CreateTicketsSdk();

        // Lock only once the draw's last batch has answered 200. The backend
        // derives drawRoot (the hash of the block holding that batch) and
        // returns it as key.
        AnchorResult locked;

        try
        {
            locked = await sdk.AnchorAsync(Tickets.Lock, new Dictionary<string, object?> { ["draw"] = Draw });
        }
        catch (IndexerException e) when (e.ErrorCode == "DRAW_HAS_NO_BATCH")
        {
            Console.WriteLine($"[lock] {Draw} has no confirmed batch yet; run the tickets-batch example first");
            return;
        }

        var drawRoot = locked.Key!;
        Console.WriteLine($"[lock] {Draw} -> drawRoot {drawRoot}, anchorId {locked.AnchorId}");
        Console.WriteLine($"  [link] {sdk.VerificationLink(locked)}");

        // The TSA signs SHA-256 over the 32 raw bytes of drawRoot, not its hex text.
        var imprint = Hashes.LockTimestampImprint(drawRoot);
        Console.WriteLine($"[lockts] imprint to timestamp: sha256 {Convert.ToHexStringLower(imprint)}");

        byte[] token;

        if (File.Exists(TsaResponse))
        {
            token = File.ReadAllBytes(TsaResponse);
            Console.WriteLine($"[lockts] using the TSA answer in {TsaResponse}");
        }
        else
        {
            // The token is valid for this drawRoot only: locking again after a
            // new batch changes it, so a fresh mock is made on every run.
            token = MockTsa.TimeStamp(imprint);
            File.WriteAllBytes(MockResponse, token);
            Console.WriteLine($"""
                [lockts] no TSA answer at {TsaResponse}: using a MOCK token (self-signed, examples only), saved as {MockResponse}
                  For a real one:
                  printf '%s' '{drawRoot[2..]}' | xxd -r -p > drawroot.bin
                  openssl ts -query -data drawroot.bin -sha256 -cert -out req.tsq
                  curl -s -H 'Content-Type: application/timestamp-query' --data-binary @req.tsq "$TSA_URL" -o {TsaResponse}
                """);
        }

        // A bytes field takes the DER as a byte[]; it goes on chain whole.
        try
        {
            var stamped = await sdk.AnchorAsync(Tickets.LockTs, new Dictionary<string, object?>
            {
                ["draw"] = Draw,
                ["signature"] = token,
            });

            Console.WriteLine($"[lockts] {Draw} -> drawRoot {stamped.Key}, anchorId {stamped.AnchorId}");
            Console.WriteLine($"  [link] {sdk.VerificationLink(stamped)}");
        }
        catch (IndexerException e) when (e.ErrorCode is "INVALID_TIMESTAMP" or "DRAW_NOT_LOCKED")
        {
            // The token was for another drawRoot (locking again replaces it), or failed a check.
            Console.WriteLine($"[lockts] refused: {e.ErrorCode}: {e.ServerMessage}");
        }
    }
}
