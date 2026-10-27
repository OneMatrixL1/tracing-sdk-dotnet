// Runnable examples, one scenario per file. Set your endpoint and token in
// ExampleSettings.cs (or the token in TRACING_API_KEY), then run:
//
//   dotnet run --project examples/Tracing.Sdk.Examples -- <example>
using Tracing.Sdk;
using Tracing.Sdk.Examples;

var examples = new Dictionary<string, Func<Task>>
{
    // The tickets plugin: event types from config/tickets-events.json.
    ["tickets-batch"] = TicketsBatchExample.RunAsync,     // TicketsBatchExample.cs
    ["tickets-lock"] = TicketsLockExample.RunAsync,       // TicketsLockExample.cs
    ["tickets-records"] = TicketsRecordsExample.RunAsync, // TicketsRecordsExample.cs
    ["custom"] = CustomEventExample.RunAsync,             // CustomEventExample.cs
};

if (args.Length != 1 || !examples.TryGetValue(args[0], out var example))
{
    Console.Error.WriteLine($"usage: dotnet run -- <{string.Join("|", examples.Keys)}>");
    return 2;
}

try
{
    await example();
    return 0;
}
catch (TracingSdkException e)
{
    Console.Error.WriteLine($"[error] {e.GetType().Name}: {e.Message}");
    return 1;
}
