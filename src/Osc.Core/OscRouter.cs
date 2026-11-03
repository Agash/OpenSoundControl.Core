using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Osc.Core;

/// <summary>
/// An OSC 1.0 address pattern: <c>?</c> one character, <c>*</c> any run of characters within a part, <c>[abc]</c>,
/// <c>[a-z]</c> and <c>[!a-z]</c> character sets, and <c>{foo,bar}</c> alternatives. Parts split at <c>/</c> and
/// never match across one.
/// </summary>
public sealed class OscAddressPattern
{
    private static readonly char[] _special = ['?', '*', '[', '{'];

    private OscAddressPattern(string pattern) => Pattern = pattern;

    /// <summary>The pattern text.</summary>
    public string Pattern { get; }

    /// <summary>Whether the text uses any pattern character.</summary>
    /// <param name="address">An address or pattern.</param>
    /// <returns>Whether it is a pattern.</returns>
    public static bool IsPattern(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AsSpan().IndexOfAny(_special) >= 0;
    }

    /// <summary>A pattern.</summary>
    /// <param name="pattern">The pattern, starting with <c>/</c>.</param>
    /// <returns>The parsed pattern.</returns>
    public static OscAddressPattern Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return pattern[0] != '/'
            ? throw new FormatException($"An OSC address pattern starts with '/': '{pattern}'.")
            : new OscAddressPattern(pattern);
    }

    /// <summary>Whether an address matches.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether it matches.</returns>
    public bool IsMatch(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Match(Pattern.AsSpan(), address.AsSpan());
    }

    private static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        while (!pattern.IsEmpty)
        {
            char c = pattern[0];
            switch (c)
            {
                case '*':
                    pattern = pattern[1..];
                    for (int i = 0; i <= text.Length; i++)
                    {
                        if (i > 0 && text[i - 1] == '/')
                        {
                            return false;
                        }

                        if (Match(pattern, text[i..]))
                        {
                            return true;
                        }
                    }

                    return false;
                case '?':
                    if (text.IsEmpty || text[0] == '/')
                    {
                        return false;
                    }

                    pattern = pattern[1..];
                    text = text[1..];
                    break;
                case '[':
                    int close = pattern.IndexOf(']');
                    if (
                        close < 0
                        || text.IsEmpty
                        || text[0] == '/'
                        || !InSet(pattern[1..close], text[0])
                    )
                    {
                        return false;
                    }

                    pattern = pattern[(close + 1)..];
                    text = text[1..];
                    break;
                case '{':
                    int end = pattern.IndexOf('}');
                    if (end < 0)
                    {
                        return false;
                    }

                    ReadOnlySpan<char> rest = pattern[(end + 1)..];
                    foreach (Range option in pattern[1..end].Split(','))
                    {
                        ReadOnlySpan<char> alternative = pattern[1..end][option];
                        if (
                            text.StartsWith(alternative, StringComparison.Ordinal)
                            && Match(rest, text[alternative.Length..])
                        )
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    if (text.IsEmpty || text[0] != c)
                    {
                        return false;
                    }

                    pattern = pattern[1..];
                    text = text[1..];
                    break;
            }
        }

        return text.IsEmpty;
    }

    private static bool InSet(ReadOnlySpan<char> set, char c)
    {
        bool negate = !set.IsEmpty && set[0] == '!';
        if (negate)
        {
            set = set[1..];
        }

        bool found = false;
        for (int i = 0; i < set.Length; i++)
        {
            if (i + 2 < set.Length && set[i + 1] == '-')
            {
                found |= c >= set[i] && c <= set[i + 2];
                i += 2;
            }
            else
            {
                found |= set[i] == c;
            }
        }

        return found != negate;
    }
}

/// <summary>
/// Routes incoming messages to handlers by address: exact addresses through a frozen map, patterns (such as
/// <c>/avatar/parameters/*</c>) in registration order. Several handlers may share an address; each is called.
/// Registration returns an <see cref="IDisposable"/> that removes the handler.
/// </summary>
public sealed class OscRouter
{
    private readonly Lock _gate = new();
    private ImmutableList<Route> _routes = [];
    private FrozenDictionary<string, ImmutableArray<Route>> _exact = FrozenDictionary<
        string,
        ImmutableArray<Route>
    >.Empty;
    private ImmutableArray<Route> _patterns = [];

    /// <summary>Calls <paramref name="handler"/> for every message on an address or matching a pattern.</summary>
    /// <param name="addressOrPattern">An address, or an OSC address pattern.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>Removes the handler when disposed.</returns>
    public IDisposable On(string addressOrPattern, Action<OscMessage> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressOrPattern);
        ArgumentNullException.ThrowIfNull(handler);
        Route route = new(
            this,
            addressOrPattern,
            OscAddressPattern.IsPattern(addressOrPattern)
                ? OscAddressPattern.Parse(addressOrPattern)
                : null,
            handler
        );
        lock (_gate)
        {
            _routes = _routes.Add(route);
            Rebuild();
        }

        return route;
    }

    /// <summary>Calls <paramref name="handler"/> with each message of a typed protocol message's address that reads as one.</summary>
    /// <typeparam name="TMessage">The typed message.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <returns>Removes the handler when disposed.</returns>
    public IDisposable On<TMessage>(Action<TMessage> handler)
        where TMessage : IOscMessage<TMessage>
    {
        ArgumentNullException.ThrowIfNull(handler);
        return On(
            TMessage.Address,
            message =>
            {
                if (TMessage.TryRead(message, out TMessage? typed))
                {
                    handler(typed);
                }
            }
        );
    }

    /// <summary>Calls every handler whose address or pattern matches.</summary>
    /// <param name="message">The message.</param>
    /// <returns>How many handlers were called.</returns>
    public int Dispatch(OscMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int called = 0;
        if (_exact.TryGetValue(message.Address, out ImmutableArray<Route> exact))
        {
            foreach (Route route in exact)
            {
                route.Handler(message);
                called++;
            }
        }

        foreach (Route route in _patterns)
        {
            if (route.Pattern!.IsMatch(message.Address))
            {
                route.Handler(message);
                called++;
            }
        }

        return called;
    }

    private void Remove(Route route)
    {
        lock (_gate)
        {
            _routes = _routes.Remove(route);
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _exact = _routes
            .Where(static r => r.Pattern is null)
            .GroupBy(static r => r.Address, StringComparer.Ordinal)
            .ToFrozenDictionary(
                static g => g.Key,
                static g => g.ToImmutableArray(),
                StringComparer.Ordinal
            );
        _patterns = [.. _routes.Where(static r => r.Pattern is not null)];
    }

    private sealed class Route(
        OscRouter router,
        string address,
        OscAddressPattern? pattern,
        Action<OscMessage> handler
    ) : IDisposable
    {
        private int _disposed;

        public string Address { get; } = address;

        public OscAddressPattern? Pattern { get; } = pattern;

        public Action<OscMessage> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                router.Remove(this);
            }
        }
    }
}
