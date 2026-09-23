// Runnable examples, one scenario per file. Set your endpoint and token (and
// RPC URL for "verify") in ExampleSettings.cs, then run:
//
//   dotnet run --project examples/Tracing.Sdk.Examples -- <example>
using Tracing.Sdk;
using Tracing.Sdk.Examples;

var examples = new Dictionary<string, Func<Task>>
{
    ["single"] = SingleSendExample.RunAsync, // SingleSendExample.cs
    ["batch"] = BatchExample.RunAsync,       // BatchExample.cs
    ["xml"] = XmlExample.RunAsync,           // XmlExample.cs
    ["query"] = QueryExample.RunAsync,       // QueryExample.cs
    ["verify"] = VerifyExample.RunAsync,     // VerifyExample.cs
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
    // A TransportException from "verify" can also mean the transaction is not
    // mined yet, or the RPC node does not have it — retry rather than conclude.
    Console.Error.WriteLine($"[error] {e.GetType().Name}: {e.Message}");
    return 1;
}
