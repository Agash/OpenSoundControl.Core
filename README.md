# Osc.Core

Open Sound Control for .NET 10 and 11, Native AOT compatible.

- **Codec:** OSC 1.0 messages and bundles, and the common OSC 1.1 types: `i f s b h d t T F N I`. Bundles
  decode flattened, in order. A packet with a type tag this library does not know yields what was read before
  it, as receivers skip what they cannot read.
- **Address patterns:** OSC 1.0 matching, `?`, `*`, `[abc]`, `[a-z]`, `[!a-z]` and `{foo,bar}`, never across a
  `/`.
- **Router:** handlers by exact address (a frozen map) or by pattern, several per address; registering returns
  an `IDisposable` that removes the handler.
- **Typed protocol messages:** a protocol on OSC (VMC, VRChat, an app's own addresses) implements
  `IOscMessage<TSelf>` with static abstract `Address` and `TryRead`, so `router.On<TMessage>(...)` and
  `sender.SendAsync(message)` route without reflection.
- **UDP:** `OscUdpSender` packs bundles so each datagram fits one Ethernet frame; `OscUdpReceiver` binds to
  loopback unless told otherwise and reads until cancelled, or feeds a router.

```csharp
using OscUdpReceiver receiver = new(9001);
OscRouter router = new();
using IDisposable smile = router.On("/avatar/parameters/*", message => Console.WriteLine(message));
await receiver.RunAsync(router, ct);

using OscUdpSender sender = OscUdpSender.Create("127.0.0.1", 9000);
await sender.SendAsync(new OscMessage("/avatar/parameters/Smile", [OscArgument.FromFloat32(1)]), ct);
```

Protocol libraries built on it: [`Vmc.Client`](https://github.com/Agash/Vmc.Client) (the VMC protocol).

## License

MIT
