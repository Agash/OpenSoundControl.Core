using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Osc.Core.Tests;

[TestClass]
public sealed class OscTests
{
    private static readonly string[] _flattened = ["/a", "/b", "/c"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Codec_EveryType_RoundTrips()
    {
        OscTimeTag tag = OscTimeTag.FromDateTimeOffset(
            new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)
        );
        OscMessage sent = new(
            "/all/types",
            [
                OscArgument.FromInt32(-7),
                OscArgument.FromFloat32(0.5f),
                OscArgument.FromString("héllo"),
                OscArgument.FromBlob([1, 2, 3, 4, 5]),
                OscArgument.FromInt64(long.MaxValue),
                OscArgument.FromFloat64(Math.PI),
                OscArgument.FromTimeTag(tag),
                OscArgument.FromBoolean(true),
                OscArgument.FromBoolean(false),
                OscArgument.Nil,
                OscArgument.Impulse,
            ]
        );

        OscMessage read = OscCodec.Decode(OscCodec.Encode(sent)).Single();

        Assert.AreEqual(sent.Address, read.Address);
        Assert.AreSequenceEqual(
            sent.Arguments.Select(static a => a.Tag).ToArray(),
            read.Arguments.Select(static a => a.Tag).ToArray()
        );
        Assert.IsTrue(read.Arguments[0].TryGetInt32(out int i) && i == -7);
        Assert.IsTrue(read.Arguments[2].TryGetString(out string s) && s == "héllo");
        Assert.IsTrue(
            read.Arguments[3].TryGetBlob(out var blob) && blob.Length == 5 && blob[4] == 5
        );
        Assert.IsTrue(read.Arguments[4].TryGetInt64(out long h) && h == long.MaxValue);
        Assert.IsTrue(read.Arguments[5].TryGetFloat64(out double d) && d == Math.PI);
        Assert.IsTrue(read.Arguments[6].TryGetTimeTag(out OscTimeTag t) && t == tag);
        Assert.AreEqual(
            new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero),
            t.ToDateTimeOffset()
        );
        Assert.IsTrue(read.Arguments[7].TryGetBoolean(out bool yes) && yes);
        Assert.IsTrue(read.Arguments[8].TryGetBoolean(out bool no) && !no);
    }

    [TestMethod]
    public void Codec_NestedBundle_FlattensInOrder()
    {
        byte[] inner = OscCodec.EncodeBundle([new OscMessage("/b"), new OscMessage("/c")]);
        byte[] outer = OscCodec.EncodeBundle([new OscMessage("/a")]);
        byte[] combined =
        [
            .. outer,
            .. BitConverter.GetBytes(
                System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(inner.Length)
            ),
            .. inner,
        ];

        Assert.AreSequenceEqual(
            _flattened,
            OscCodec.Decode(combined).Select(static m => m.Address).ToArray()
        );
    }

    [TestMethod]
    public void Codec_UnknownTypeTag_SkipsTheMessage()
    {
        byte[] packet = OscCodec.Encode(new OscMessage("/x", [OscArgument.FromInt32(1)]));
        packet[Array.IndexOf(packet, (byte)'i')] = (byte)'q';

        Assert.IsEmpty(OscCodec.Decode(packet));
    }

    [TestMethod]
    [DataRow("/avatar/parameters/*", "/avatar/parameters/Smile", true)]
    [DataRow("/avatar/parameters/*", "/avatar/parameters/face/Smile", false)]
    [DataRow("/a/b?d", "/a/bcd", true)]
    [DataRow("/a/[bc]x", "/a/cx", true)]
    [DataRow("/a/[!bc]x", "/a/cx", false)]
    [DataRow("/a/[a-c]x", "/a/bx", true)]
    [DataRow("/VMC/Ext/{Blend,Bone}/*", "/VMC/Ext/Bone/Pos", true)]
    [DataRow("/VMC/Ext/{Blend,Bone}/*", "/VMC/Ext/Root/Pos", false)]
    public void Pattern_MatchesOsc10Rules(string pattern, string address, bool matches) =>
        Assert.AreEqual(matches, OscAddressPattern.Parse(pattern).IsMatch(address));

    [TestMethod]
    public void Router_ExactPatternAndTyped_AllReceiveAndUnregister()
    {
        OscRouter router = new();
        List<string> seen = [];
        IDisposable exact = router.On("/avatar/parameters/Smile", m => seen.Add("exact"));
        using IDisposable pattern = router.On("/avatar/parameters/*", m => seen.Add("pattern"));
        using IDisposable typed = router.On<SmileMessage>(m => seen.Add($"typed {m.Value}"));

        int called = router.Dispatch(new SmileMessage(0.75f).ToOsc());
        exact.Dispose();
        int afterRemoval = router.Dispatch(new SmileMessage(1).ToOsc());

        Assert.AreEqual(3, called);
        Assert.AreEqual(2, afterRemoval);
        CollectionAssert.Contains(seen, "typed 0.75");
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Udp_SendBundle_ArrivesInDatagramsUnderTheLimit()
    {
        using OscUdpReceiver receiver = new(0);
        using OscUdpSender sender = new(
            new IPEndPoint(IPAddress.Loopback, receiver.Port),
            maxDatagramBytes: 200
        );
        OscMessage[] messages =
        [
            .. Enumerable
                .Range(0, 20)
                .Select(static n => new OscMessage($"/n/{n}", [OscArgument.FromInt32(n)])),
        ];

        await sender.SendBundleAsync(messages, ct: TestContext.CancellationToken);
        List<int> values = [];
        await foreach (OscReceived received in receiver.ReadAllAsync(TestContext.CancellationToken))
        {
            values.Add(received.Message.Arguments[0].TryGetInt32(out int n) ? n : -1);
            if (values.Count == messages.Length)
            {
                break;
            }
        }

        Assert.AreSequenceEqual(Enumerable.Range(0, 20).ToArray(), values.ToArray());
    }

    private sealed record SmileMessage(float Value) : IOscMessage<SmileMessage>
    {
        public static string Address => "/avatar/parameters/Smile";

        public static bool TryRead(OscMessage message, [NotNullWhen(true)] out SmileMessage? value)
        {
            value =
                message.Arguments is [var first] && first.TryGetFloat32(out float f)
                    ? new SmileMessage(f)
                    : null;
            return value is not null;
        }

        public OscMessage ToOsc() => new(Address, [OscArgument.FromFloat32(Value)]);
    }
}
