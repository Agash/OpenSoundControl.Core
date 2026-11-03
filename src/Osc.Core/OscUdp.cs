using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Osc.Core;

/// <summary>Sends OSC over UDP to one target, packing bundles so each datagram fits one Ethernet frame.</summary>
public sealed class OscUdpSender : IDisposable
{
    /// <summary>The datagram size bundles are packed to.</summary>
    public const int DefaultMaxDatagramBytes = 1500;

    private readonly UdpClient _udp;
    private readonly int _maxDatagramBytes;

    /// <summary>A sender.</summary>
    /// <param name="target">Where to send.</param>
    /// <param name="maxDatagramBytes">The largest datagram a bundle is packed into.</param>
    public OscUdpSender(IPEndPoint target, int maxDatagramBytes = DefaultMaxDatagramBytes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDatagramBytes, 64);
        Target = target;
        _maxDatagramBytes = maxDatagramBytes;
        _udp = new UdpClient(target.AddressFamily);
    }

    /// <summary>A sender to a host and port.</summary>
    /// <param name="host">A host name or address.</param>
    /// <param name="port">The port.</param>
    /// <returns>The sender.</returns>
    public static OscUdpSender Create(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        IPAddress address = IPAddress.TryParse(host, out IPAddress? parsed)
            ? parsed
            : Dns.GetHostAddresses(host)[0];
        return new OscUdpSender(new IPEndPoint(address, port));
    }

    /// <summary>Where the sender sends.</summary>
    public IPEndPoint Target { get; }

    /// <summary>Sends one message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>When it is sent.</returns>
    public async ValueTask SendAsync(OscMessage message, CancellationToken ct = default) =>
        _ = await _udp.SendAsync(OscCodec.Encode(message), Target, ct).ConfigureAwait(false);

    /// <summary>Sends a typed protocol message.</summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>When it is sent.</returns>
    public ValueTask SendAsync<TMessage>(TMessage message, CancellationToken ct = default)
        where TMessage : IOscMessage<TMessage>
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendAsync(message.ToOsc(), ct);
    }

    /// <summary>Sends messages as bundles, as few as fit the datagram size, in order.</summary>
    /// <param name="messages">The messages.</param>
    /// <param name="timeTag">When the receiver applies them.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>When they are sent.</returns>
    public async ValueTask SendBundleAsync(
        IEnumerable<OscMessage> messages,
        OscTimeTag? timeTag = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<OscMessage> bundle = [];
        int size = 16;
        foreach (OscMessage message in messages)
        {
            int length = OscCodec.EncodedSize(message) + 4;
            if (bundle.Count > 0 && size + length > _maxDatagramBytes)
            {
                _ = await _udp.SendAsync(OscCodec.EncodeBundle(bundle, timeTag), Target, ct)
                    .ConfigureAwait(false);
                bundle.Clear();
                size = 16;
            }

            bundle.Add(message);
            size += length;
        }

        if (bundle.Count > 0)
        {
            _ = await _udp.SendAsync(OscCodec.EncodeBundle(bundle, timeTag), Target, ct)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _udp.Dispose();
}

/// <summary>A message received, with where it came from.</summary>
/// <param name="Message">The message.</param>
/// <param name="Sender">The sender's address.</param>
public readonly record struct OscReceived(OscMessage Message, IPEndPoint Sender);

/// <summary>Receives OSC over UDP on one port, on loopback unless another address is given.</summary>
public sealed partial class OscUdpReceiver : IDisposable
{
    private readonly UdpClient _udp;
    private readonly ILogger _logger;

    /// <summary>A receiver.</summary>
    /// <param name="port">The port; 0 picks a free one.</param>
    /// <param name="bind">The local address; loopback by default, so nothing listens beyond this machine unasked.</param>
    /// <param name="logger">Where receive faults are logged.</param>
    public OscUdpReceiver(int port, IPAddress? bind = null, ILogger<OscUdpReceiver>? logger = null)
    {
        _udp = new UdpClient(new IPEndPoint(bind ?? IPAddress.Loopback, port));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The port it listens on.</summary>
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    /// <summary>Every message received, bundles flattened, until cancelled.</summary>
    /// <param name="ct">Ends the reading.</param>
    /// <returns>The messages.</returns>
    public async IAsyncEnumerable<OscReceived> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (SocketException ex)
            {
                // A previous send's ICMP error surfaces on the next receive on Windows; the socket stays usable.
                LogReceiveFailed(_logger, ex);
                continue;
            }

            foreach (OscMessage message in OscCodec.Decode(datagram.Buffer))
            {
                yield return new OscReceived(message, datagram.RemoteEndPoint);
            }
        }
    }

    /// <summary>Reads until cancelled and dispatches every message to a router.</summary>
    /// <param name="router">The router.</param>
    /// <param name="ct">Ends the reading.</param>
    /// <returns>When reading ends.</returns>
    public async Task RunAsync(OscRouter router, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        await foreach (OscReceived received in ReadAllAsync(ct).ConfigureAwait(false))
        {
            _ = router.Dispatch(received.Message);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _udp.Dispose();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "An OSC receive failed; receiving continues."
    )]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception);
}
