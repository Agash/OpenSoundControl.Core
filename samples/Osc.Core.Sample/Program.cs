using System.Net;
using Osc.Core;

// Native AOT smoke test: route a message through the router over a loopback socket.
using OscUdpReceiver receiver = new(0);
using OscUdpSender sender = new(new IPEndPoint(IPAddress.Loopback, receiver.Port));
OscRouter router = new();
TaskCompletionSource<float> received = new();
using IDisposable route = router.On(
    "/sample/*",
    message =>
        received.TrySetResult(
            message.Arguments[0].TryGetFloat32(out float value) ? value : float.NaN
        )
);

using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
Task listening = receiver.RunAsync(router, stop.Token);
await sender.SendAsync(
    new OscMessage("/sample/value", [OscArgument.FromFloat32(0.5f)]),
    stop.Token
);
float value = await received.Task.WaitAsync(stop.Token);
await stop.CancelAsync();
await listening;
Console.WriteLine($"Received {value} on /sample/value");
return value == 0.5f ? 0 : 1;
