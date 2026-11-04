using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace OpenSoundControl.Core;

/// <summary>
/// OSC 1.0 packets with the OSC 1.1 common types (<c>h d t T F N I</c>): messages, and bundles flattened in order.
/// A packet that cannot be read, or carries a type tag this codec does not know, yields what was read before it,
/// as receivers skip what they cannot read.
/// </summary>
public static class OscCodec
{
    private const int MaxBundleDepth = 8;
    private static readonly byte[] _bundleTag = "#bundle\0"u8.ToArray();

    /// <summary>Encodes a message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The packet.</returns>
    public static byte[] Encode(OscMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using MemoryStream stream = new();
        Write(stream, message);
        return stream.ToArray();
    }

    /// <summary>Encodes a bundle of messages.</summary>
    /// <param name="messages">The messages.</param>
    /// <param name="timeTag">When to apply them; <see cref="OscTimeTag.Immediately"/> by default.</param>
    /// <returns>The packet.</returns>
    public static byte[] EncodeBundle(IEnumerable<OscMessage> messages, OscTimeTag? timeTag = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        using MemoryStream stream = new();
        stream.Write(_bundleTag);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(number, (timeTag ?? OscTimeTag.Immediately).Value);
        stream.Write(number);
        foreach (OscMessage message in messages)
        {
            byte[] element = Encode(message);
            BinaryPrimitives.WriteInt32BigEndian(number, element.Length);
            stream.Write(number[..4]);
            stream.Write(element);
        }

        return stream.ToArray();
    }

    /// <summary>The size of a message's encoding, for packing bundles.</summary>
    /// <param name="message">The message.</param>
    /// <returns>Its size in bytes.</returns>
    public static int EncodedSize(OscMessage message) => Encode(message).Length;

    /// <summary>Decodes a packet: a message, or a bundle of messages and bundles, flattened in order.</summary>
    /// <param name="packet">The packet.</param>
    /// <returns>The messages read.</returns>
    public static ImmutableArray<OscMessage> Decode(ReadOnlySpan<byte> packet)
    {
        ImmutableArray<OscMessage>.Builder messages = ImmutableArray.CreateBuilder<OscMessage>();
        _ = TryDecode(packet, messages, depth: 0);
        return messages.ToImmutable();
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> packet,
        ImmutableArray<OscMessage>.Builder messages,
        int depth
    )
    {
        if (depth > MaxBundleDepth)
        {
            return false;
        }

        if (packet.StartsWith(_bundleTag))
        {
            int at = 16;
            while (at + 4 <= packet.Length)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(packet[at..]);
                at += 4;
                if (
                    size <= 0
                    || at + size > packet.Length
                    || !TryDecode(packet.Slice(at, size), messages, depth + 1)
                )
                {
                    return false;
                }

                at += size;
            }

            return true;
        }

        int offset = 0;
        if (!TryReadString(packet, ref offset, out string address) || !address.StartsWith('/'))
        {
            return false;
        }

        // OSC 1.0 allows a message without a type tag string; it has no arguments.
        if (offset >= packet.Length)
        {
            messages.Add(new OscMessage(address));
            return true;
        }

        if (!TryReadString(packet, ref offset, out string tags) || !tags.StartsWith(','))
        {
            return false;
        }

        ImmutableArray<OscArgument>.Builder arguments = ImmutableArray.CreateBuilder<OscArgument>(
            tags.Length - 1
        );
        foreach (char tag in tags.AsSpan(1))
        {
            OscArgument? argument = tag switch
            {
                'i' when offset + 4 <= packet.Length => OscArgument.FromInt32(
                    BinaryPrimitives.ReadInt32BigEndian(packet[offset..])
                ),
                'f' when offset + 4 <= packet.Length => OscArgument.FromFloat32(
                    BinaryPrimitives.ReadSingleBigEndian(packet[offset..])
                ),
                'h' when offset + 8 <= packet.Length => OscArgument.FromInt64(
                    BinaryPrimitives.ReadInt64BigEndian(packet[offset..])
                ),
                'd' when offset + 8 <= packet.Length => OscArgument.FromFloat64(
                    BinaryPrimitives.ReadDoubleBigEndian(packet[offset..])
                ),
                't' when offset + 8 <= packet.Length => OscArgument.FromTimeTag(
                    new OscTimeTag(BinaryPrimitives.ReadUInt64BigEndian(packet[offset..]))
                ),
                'T' => OscArgument.FromBoolean(true),
                'F' => OscArgument.FromBoolean(false),
                'N' => OscArgument.Nil,
                'I' => OscArgument.Impulse,
                _ => null,
            };
            if (argument is { } fixedSize)
            {
                offset += tag switch
                {
                    'i' or 'f' => 4,
                    'h' or 'd' or 't' => 8,
                    _ => 0,
                };
                arguments.Add(fixedSize);
            }
            else if (tag is 's' && TryReadString(packet, ref offset, out string text))
            {
                arguments.Add(OscArgument.FromString(text));
            }
            else if (tag is 'b' && offset + 4 <= packet.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(packet[offset..]);
                offset += 4;
                if (length < 0 || offset + length > packet.Length)
                {
                    return false;
                }

                arguments.Add(OscArgument.FromBlob(packet.Slice(offset, length)));
                offset += (length + 3) & ~3;
            }
            else
            {
                return false;
            }
        }

        messages.Add(new OscMessage(address, arguments.MoveToImmutable()));
        return true;
    }

    private static void Write(MemoryStream stream, OscMessage message)
    {
        WriteString(stream, message.Address);
        StringBuilder tags = new(",", message.Arguments.Length + 1);
        foreach (OscArgument argument in message.Arguments)
        {
            _ = tags.Append(argument.Tag);
        }

        WriteString(stream, tags.ToString());
        Span<byte> number = stackalloc byte[8];
        foreach (OscArgument argument in message.Arguments)
        {
            switch (argument.Type)
            {
                case OscType.IntegerNumber when argument.TryGetInt32(out int i):
                    BinaryPrimitives.WriteInt32BigEndian(number, i);
                    stream.Write(number[..4]);
                    break;
                case OscType.FloatNumber when argument.TryGetFloat32(out float f):
                    BinaryPrimitives.WriteSingleBigEndian(number, f);
                    stream.Write(number[..4]);
                    break;
                case OscType.LongInteger when argument.TryGetInt64(out long h):
                    BinaryPrimitives.WriteInt64BigEndian(number, h);
                    stream.Write(number);
                    break;
                case OscType.DoubleFloat when argument.TryGetFloat64(out double d):
                    BinaryPrimitives.WriteDoubleBigEndian(number, d);
                    stream.Write(number);
                    break;
                case OscType.TimeTag when argument.TryGetTimeTag(out OscTimeTag t):
                    BinaryPrimitives.WriteUInt64BigEndian(number, t.Value);
                    stream.Write(number);
                    break;
                case OscType.Text when argument.TryGetString(out string s):
                    WriteString(stream, s);
                    break;
                case OscType.Blob when argument.TryGetBlob(out ImmutableArray<byte> blob):
                    BinaryPrimitives.WriteInt32BigEndian(number, blob.Length);
                    stream.Write(number[..4]);
                    stream.Write(blob.AsSpan());
                    stream.Write(new byte[(4 - (blob.Length % 4)) % 4]);
                    break;
                default:
                    // T, F, N and I carry no bytes beyond their tag.
                    break;
            }
        }
    }

    // OSC strings are UTF-8, terminated by a null and padded with nulls to a multiple of four bytes.
    private static void WriteString(MemoryStream stream, string text)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        int padded = (length + 4) & ~3;
        Span<byte> bytes = padded <= 256 ? stackalloc byte[padded] : new byte[padded];
        bytes.Clear();
        _ = Encoding.UTF8.GetBytes(text, bytes);
        stream.Write(bytes);
    }

    private static bool TryReadString(ReadOnlySpan<byte> packet, ref int offset, out string text)
    {
        if (offset >= packet.Length)
        {
            text = string.Empty;
            return false;
        }

        int end = packet[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            text = string.Empty;
            return false;
        }

        text = Encoding.UTF8.GetString(packet.Slice(offset, end));
        offset += (end + 4) & ~3;
        return offset <= packet.Length;
    }
}
