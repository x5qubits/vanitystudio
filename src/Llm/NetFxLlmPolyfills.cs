
namespace VanityStudio.Llm;

internal static class NetFxLlmPolyfills
{
    // HttpContent gained CancellationToken overloads in .NET 5; net472 has only the argless forms. Cancellation is
    // best-effort here - the underlying read is not actually cancelled.
    public static Task<string> ReadAsStringAsync(this HttpContent c, CancellationToken ct) => c.ReadAsStringAsync();
    public static Task<Stream> ReadAsStreamAsync(this HttpContent c, CancellationToken ct) => c.ReadAsStreamAsync();

    // StreamReader.ReadLineAsync(CancellationToken) is .NET 7+; forward to the argless net472 form.
    public static Task<string?> ReadLineAsync(this StreamReader r, CancellationToken ct) => r.ReadLineAsync();

    // string.Contains(string, StringComparison) is .NET Core 2.1+; net472 lacks it.
    public static bool Contains(this string s, string value, StringComparison comparison)
        => (s ?? "").IndexOf(value, comparison) >= 0;

    // KeyValuePair<,> gained Deconstruct in netstandard2.1; net472 lacks it, so `foreach (var (k, v) in dict)` needs this.
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
    {
        key = kv.Key;
        value = kv.Value;
    }
}

/// <summary>net472 stub of the engine's Data.AiUsageLog. The residential slave has no ai_usage table or dashboard, and
/// the port's call sites are explicitly best-effort ("never breaks the call"), so recording is a no-op here.</summary>
internal static class AiUsageLog
{
    public static void TryRecord(long userId, string layer, string? provider, string? profile, string? model,
        int promptTokens, int completionTokens, int cachedTokens, int totalTokens, bool hadImage)
    { /* no-op on the slave: no usage store here */ }
}
