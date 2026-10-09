using System.Collections.Concurrent;
using System.Diagnostics;

namespace VanityStudio.Llm;

/// <summary>
/// How long a (login, model) pair may stay silent before a call is given up and the router moves to the next login.
/// A fixed timeout cannot tell a slow answer from a dead request; silence can. A working request keeps sending bytes
/// (headers, thought chunks, text), a dead one sends nothing: four requests on 2026-10-05 were accepted by the provider
/// and never answered, each costing the full 600 s HTTP ceiling while the next login answered the same prompt in
/// seconds (44 of a run's 116 minutes). Each pair earns its own allowance: three times the longest a successful call
/// of that pair has ever taken, never under <see cref="FloorSec"/> and never over <see cref="CeilingSec"/>. Before the
/// first success the pair gets the ceiling, so nothing is cut sooner than today until the pair has shown what it
/// needs. The values live in memory with the router's cooldowns.
/// Why the whole call and not the longest gap, and why 180 s: Gemini streams text and thoughts in chunks, but a tool
/// call arrives as ONE part when it is complete, so a model writing a 13K-token edit_file is silent the whole time.
/// With a 60 s floor learned from 17 s gaps, exactly that was cut as a hang on 2026-10-06 (the fallback then wrote
/// the same edit in 30 s). A real hang now costs three minutes instead of ten; a long edit on a thinking model fits.
/// </summary>
public sealed class SilencePolicy
{
    public const int FloorSec = 180, CeilingSec = 600, Factor = 3;

    /// <summary>The process-wide policy every client uses unless given another (tests).</summary>
    public static SilencePolicy Shared { get; } = new();

    private readonly ConcurrentDictionary<string, double> _longestSec = new(StringComparer.OrdinalIgnoreCase);

    private static string Pair(string profile, string model) => profile + "|" + model;

    public TimeSpan AllowanceFor(string profile, string model) =>
        TimeSpan.FromSeconds(_longestSec.TryGetValue(Pair(profile, model), out var s)
            ? Math.Clamp(s * Factor, FloorSec, CeilingSec)
            : CeilingSec);

    /// <summary>A successful call reports how long it took in all; the allowance only ever grows from it.</summary>
    public void Learn(string profile, string model, TimeSpan wholeCall)
    {
        var sec = wholeCall.TotalSeconds;
        if (sec <= 0) return;
        _longestSec.AddOrUpdate(Pair(profile, model), sec, (_, old) => Math.Max(old, sec));
    }

    internal double? LongestSecondsFor(string profile, string model) =>
        _longestSec.TryGetValue(Pair(profile, model), out var s) ? s : null;
}

/// <summary>One call's silence clock: when each byte arrived, how long the first one took and the longest gap between
/// two. The client cuts a wait that exceeds <see cref="Allowance"/>; a completed call hands <see cref="LongestSilence"/>
/// to the policy.</summary>
public sealed class SilenceWatch
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastByteAt = TimeSpan.Zero;

    public SilenceWatch(TimeSpan allowance) { Allowance = allowance; }

    public TimeSpan Allowance { get; }
    public int AllowanceMs => (int)Math.Min(int.MaxValue, Allowance.TotalMilliseconds);
    /// <summary>Time from the request to its first byte (the response headers); null until it arrives.</summary>
    public TimeSpan? FirstByte { get; private set; }
    /// <summary>The longest wait between two bytes, the wait for the first one included.</summary>
    public TimeSpan LongestSilence { get; private set; }
    /// <summary>Time since the request was sent: what a completed call hands to the policy.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Something arrived: headers, an SSE line, a chunk.</summary>
    public void Received()
    {
        var now = _clock.Elapsed;
        var gap = now - _lastByteAt;
        FirstByte ??= now;
        if (gap > LongestSilence) LongestSilence = gap;
        _lastByteAt = now;
    }
}
