using System.Collections.Immutable;
using System.Globalization;

namespace Osc.Core;

/// <summary>An OSC argument's type, by its type tag.</summary>
public enum OscType
{
    /// <summary><c>i</c>: 32-bit integer.</summary>
    IntegerNumber,

    /// <summary><c>f</c>: 32-bit float.</summary>
    FloatNumber,

    /// <summary><c>s</c>: UTF-8 string.</summary>
    Text,

    /// <summary><c>b</c>: byte blob.</summary>
    Blob,

    /// <summary><c>h</c>: 64-bit integer.</summary>
    LongInteger,

    /// <summary><c>d</c>: 64-bit float.</summary>
    DoubleFloat,

    /// <summary><c>t</c>: NTP time tag.</summary>
    TimeTag,

    /// <summary><c>T</c> or <c>F</c>: a boolean carried in the type tag alone.</summary>
    Boolean,

    /// <summary><c>N</c>: nil.</summary>
    Nil,

    /// <summary><c>I</c>: impulse (a bang, no value).</summary>
    Impulse,
}

/// <summary>One OSC argument. Numeric reads widen where no precision is lost, so an <c>i</c> reads as a float.</summary>
public readonly record struct OscArgument
{
    private readonly long _integer;
    private readonly double _real;
    private readonly string? _text;
    private readonly ImmutableArray<byte> _blob;

    private OscArgument(
        OscType type,
        long integer = 0,
        double real = 0,
        string? text = null,
        ImmutableArray<byte> blob = default
    )
    {
        Type = type;
        _integer = integer;
        _real = real;
        _text = text;
        _blob = blob;
    }

    /// <summary>The argument's type.</summary>
    public OscType Type { get; }

    /// <summary>An <c>i</c> argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromInt32(int value) => new(OscType.IntegerNumber, integer: value);

    /// <summary>An <c>f</c> argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromFloat32(float value) => new(OscType.FloatNumber, real: value);

    /// <summary>An <c>s</c> argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(OscType.Text, text: value);
    }

    /// <summary>A <c>b</c> argument.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromBlob(ReadOnlySpan<byte> value) =>
        new(OscType.Blob, blob: [.. value]);

    /// <summary>An <c>h</c> argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromInt64(long value) => new(OscType.LongInteger, integer: value);

    /// <summary>A <c>d</c> argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromFloat64(double value) => new(OscType.DoubleFloat, real: value);

    /// <summary>A <c>t</c> argument.</summary>
    /// <param name="value">The time tag.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromTimeTag(OscTimeTag value) =>
        new(OscType.TimeTag, integer: unchecked((long)value.Value));

    /// <summary>A <c>T</c> or <c>F</c> argument.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The argument.</returns>
    public static OscArgument FromBoolean(bool value) =>
        new(OscType.Boolean, integer: value ? 1 : 0);

    /// <summary>The <c>N</c> argument.</summary>
    public static OscArgument Nil { get; } = new(OscType.Nil);

    /// <summary>The <c>I</c> argument.</summary>
    public static OscArgument Impulse { get; } = new(OscType.Impulse);

    /// <summary>Reads an integer: <c>i</c>, <c>h</c> within range, or a boolean as 0 or 1.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether the argument is one.</returns>
    public bool TryGetInt32(out int value)
    {
        bool ok =
            Type is OscType.IntegerNumber or OscType.Boolean
            || (Type is OscType.LongInteger && _integer is >= int.MinValue and <= int.MaxValue);
        value = ok ? (int)_integer : 0;
        return ok;
    }

    /// <summary>Reads a 64-bit integer: <c>h</c>, <c>i</c> or a boolean.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether the argument is one.</returns>
    public bool TryGetInt64(out long value)
    {
        bool ok = Type is OscType.LongInteger or OscType.IntegerNumber or OscType.Boolean;
        value = ok ? _integer : 0;
        return ok;
    }

    /// <summary>Reads a float: <c>f</c>, <c>d</c>, or an integer.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether the argument is numeric.</returns>
    public bool TryGetFloat32(out float value)
    {
        bool ok = TryGetFloat64(out double real);
        value = (float)real;
        return ok;
    }

    /// <summary>Reads a double: <c>d</c>, <c>f</c>, or an integer.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether the argument is numeric.</returns>
    public bool TryGetFloat64(out double value)
    {
        value = Type switch
        {
            OscType.FloatNumber or OscType.DoubleFloat => _real,
            OscType.IntegerNumber or OscType.LongInteger or OscType.Boolean => _integer,
            _ => 0,
        };
        return Type
            is OscType.FloatNumber
                or OscType.DoubleFloat
                or OscType.IntegerNumber
                or OscType.LongInteger
                or OscType.Boolean;
    }

    /// <summary>Reads a string.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether the argument is one.</returns>
    public bool TryGetString(out string value)
    {
        value = _text ?? string.Empty;
        return Type is OscType.Text;
    }

    /// <summary>Reads a boolean: <c>T</c>/<c>F</c>, or an integer as non-zero.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether the argument is one.</returns>
    public bool TryGetBoolean(out bool value)
    {
        value = _integer != 0;
        return Type is OscType.Boolean or OscType.IntegerNumber or OscType.LongInteger;
    }

    /// <summary>Reads a blob.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>Whether the argument is one.</returns>
    public bool TryGetBlob(out ImmutableArray<byte> value)
    {
        value = Type is OscType.Blob ? _blob : [];
        return Type is OscType.Blob;
    }

    /// <summary>Reads a time tag.</summary>
    /// <param name="value">The time tag.</param>
    /// <returns>Whether the argument is one.</returns>
    public bool TryGetTimeTag(out OscTimeTag value)
    {
        value = new OscTimeTag(unchecked((ulong)_integer));
        return Type is OscType.TimeTag;
    }

    /// <summary>The type tag character this argument is written with.</summary>
    public char Tag =>
        Type switch
        {
            OscType.IntegerNumber => 'i',
            OscType.FloatNumber => 'f',
            OscType.Text => 's',
            OscType.Blob => 'b',
            OscType.LongInteger => 'h',
            OscType.DoubleFloat => 'd',
            OscType.TimeTag => 't',
            OscType.Boolean => _integer != 0 ? 'T' : 'F',
            OscType.Nil => 'N',
            OscType.Impulse => 'I',
            _ => throw new InvalidOperationException($"Unknown OSC type {Type}."),
        };

    /// <inheritdoc/>
    public override string ToString() =>
        Type switch
        {
            OscType.IntegerNumber or OscType.LongInteger => _integer.ToString(
                CultureInfo.InvariantCulture
            ),
            OscType.FloatNumber or OscType.DoubleFloat => _real.ToString(
                CultureInfo.InvariantCulture
            ),
            OscType.Text => _text ?? string.Empty,
            OscType.Blob => $"blob[{_blob.Length}]",
            OscType.TimeTag => new OscTimeTag(unchecked((ulong)_integer)).ToString(),
            OscType.Boolean => _integer != 0 ? "true" : "false",
            OscType.Nil => "nil",
            OscType.Impulse => "impulse",
            _ => string.Empty,
        };
}

/// <summary>An OSC time tag: NTP seconds since 1900 in the high 32 bits, the fraction in the low 32.</summary>
/// <param name="Value">The raw 64-bit value.</param>
public readonly record struct OscTimeTag(ulong Value)
{
    private static readonly DateTimeOffset _ntpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The special value 1: process immediately.</summary>
    public static OscTimeTag Immediately { get; } = new(1);

    /// <summary>The time tag for an instant.</summary>
    /// <param name="time">The instant.</param>
    /// <returns>The time tag.</returns>
    public static OscTimeTag FromDateTimeOffset(DateTimeOffset time)
    {
        TimeSpan since = time - _ntpEpoch;
        ulong seconds = (ulong)since.Ticks / TimeSpan.TicksPerSecond;
        ulong fraction =
            (ulong)(since.Ticks % TimeSpan.TicksPerSecond) * (1UL << 32) / TimeSpan.TicksPerSecond;
        return new((seconds << 32) | fraction);
    }

    /// <summary>The instant, or null for <see cref="Immediately"/>.</summary>
    public DateTimeOffset? ToDateTimeOffset() =>
        Value == Immediately.Value
            ? null
            : _ntpEpoch
                + TimeSpan.FromTicks(
                    (long)(Value >> 32) * TimeSpan.TicksPerSecond
                        + (long)((Value & 0xFFFFFFFF) * TimeSpan.TicksPerSecond >> 32)
                );

    /// <inheritdoc/>
    public override string ToString() =>
        ToDateTimeOffset()?.ToString("O", CultureInfo.InvariantCulture) ?? "immediately";
}

/// <summary>An OSC message: an address and its arguments.</summary>
/// <param name="Address">The address, starting with <c>/</c>.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record OscMessage(string Address, ImmutableArray<OscArgument> Arguments)
{
    /// <summary>A message without arguments.</summary>
    /// <param name="address">The address.</param>
    public OscMessage(string address)
        : this(address, []) { }
}

/// <summary>
/// A typed message of a protocol on OSC (a VMC or VRChat message, say): its fixed address, how it is read from and
/// written to an <see cref="OscMessage"/>. Static members let a router dispatch to it without reflection.
/// </summary>
/// <typeparam name="TSelf">The message type.</typeparam>
public interface IOscMessage<TSelf>
    where TSelf : IOscMessage<TSelf>
{
    /// <summary>The address the message is sent on.</summary>
    static abstract string Address { get; }

    /// <summary>Reads the message, when the arguments fit.</summary>
    /// <param name="message">The OSC message.</param>
    /// <param name="value">The typed message.</param>
    /// <returns>Whether it was this message.</returns>
    static abstract bool TryRead(
        OscMessage message,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TSelf? value
    );

    /// <summary>The OSC message to send.</summary>
    /// <returns>The message.</returns>
    OscMessage ToOsc();
}
