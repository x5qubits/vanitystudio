
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VanityStudio.Llm;

/// <summary>One JSON-Lines entry per LLM call to logs/prompts/YYYY-MM-DD.jsonl. Ported verbatim.</summary>
public sealed class LlmCallLogger
{
    /// <summary>Global kill switch (operator's call, set at startup in Program.cs): false = no prompt logs are written
    /// anywhere in the process.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>WHAT EVERY LINE HOLDS, so a run can be audited afterwards without a marker file set in advance
    /// (a 2,900-call project build could not be explained from its log: the lines had token counts and nothing the
    /// model was sent or said - 2026-10-05). Each call records its three small parts:
    ///   - the SYSTEM PROMPT, in full the first time that exact text is seen in the day's file, and by its hash after;
    ///   - the NEW INPUT of this call: every message since the model's last reply - the task on a first call, later
    ///     the tool results with the notes the harness attached;
    ///   - the model's REPLY: its text and its tool calls.
    /// Together they are the whole conversation, written once. What is NOT kept by default is the request body: it
    /// repeats the entire conversation on every call, which for that build is several hundred megabytes.
    /// These lines can carry project content and whatever a tool argument or result contained; the files stay next to
    /// the exe, are pruned after three days, and <see cref="Enabled"/> turns them off.</summary>
    /// <remarks>The full request and response BODIES are added too with a "coder-prompts" marker file next to the exe
    /// (read at startup in Program.cs) or in a debug session - for checking the exact bytes one call sent.</remarks>
    public static bool CaptureBodies { get; set; }

    /// <summary>System prompts already written in full, per log file: the text goes in once, later lines carry the hash.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> WrittenSystemPrompts = new(StringComparer.OrdinalIgnoreCase);

    internal static string HashOf(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>What a call adds on the input side: every message after the model's last reply. Images are counted,
    /// not copied.</summary>
    public static string NewInputOf(IReadOnlyList<ConversationMessage> history)
    {
        int from = history.Count;
        while (from > 0 && history[from - 1].Role != MessageRole.Assistant) from--;
        var sb = new System.Text.StringBuilder();
        for (int i = from; i < history.Count; i++)
        {
            var m = history[i];
            if (!string.IsNullOrEmpty(m.Text)) sb.Append('[').Append(m.Role.ToString().ToLowerInvariant()).Append("] ").Append(m.Text).Append('\n');
            if (!string.IsNullOrEmpty(m.ImageDataUrl)) sb.Append("[1 image]\n");
            foreach (var r in m.ToolResults ?? [])
            {
                sb.Append("[result ").Append(r.ToolName).Append(r.IsError ? ", error" : "").Append("] ").Append(r.Output).Append('\n');
                int images = r.ImageDataUrls is { Count: > 0 } ? r.ImageDataUrls.Count : string.IsNullOrEmpty(r.ScreenshotDataUrl) ? 0 : 1;
                if (images > 0) sb.Append('[').Append(images).Append(images == 1 ? " image]\n" : " images]\n");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions LogJson = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LlmCallLogger(string logsDir)
    {
        _dir = Path.Combine(logsDir, "prompts");
    }

    public async Task LogAsync(LlmCallLogEntry entry)
    {
        if (!Enabled) return;   // operator turned prompt logging off - the call itself is unaffected
        // Logging is an audit side-channel. A missing, moved, read-only or malformed project folder must never turn
        // a successful provider response into a failed LLM call (the old eager Directory.CreateDirectory plus an
        // uncaught AppendAllText did exactly that and surfaced as "all profiles failed").
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            var hash = HashOf(entry.SystemPrompt);
            bool firstSight = hash.Length > 0 && WrittenSystemPrompts.TryAdd(path + "|" + hash, 0);
            var line = JsonSerializer.Serialize(Metadata(entry, hash, firstSight), LogJson) + "\n";
            await _lock.WaitAsync().ConfigureAwait(false);
            try   { File.AppendAllText(path, line); }   // net472 has no File.AppendAllTextAsync; append is quick under the lock
            finally { _lock.Release(); }
        }
        catch
        {
            // Best-effort only. Usage/task ledgers have their own guarded writers; losing prompt metadata is preferable
            // to losing the actual model result or retrying every configured profile for a filesystem problem.
        }
    }

    // Every call logs one line: profile / model / tokens / finishReason / counts, the system prompt (in full on first
    // sight, its hash afterwards), what the call added on the input side and what the model replied. The request and
    // response BODIES stay out unless CaptureBodies is on: they repeat the whole conversation on every call.
    private static LlmCallLogEntry Metadata(LlmCallLogEntry e, string systemPromptHash, bool firstSight)
    {
        return new()
        {
            Timestamp = e.Timestamp, Profile = e.Profile, Model = e.Model, TurnMs = e.TurnMs,
            IsError = e.IsError, Error = e.Error,
            MessageCount = e.MessageCount, ToolCount = e.ToolCount,
            ToolCallCount = e.ToolCallCount, FinishReason = e.FinishReason,
            PromptTokens = e.PromptTokens, CompletionTokens = e.CompletionTokens,
            ReasoningTokens = e.ReasoningTokens, ToolUsePromptTokens = e.ToolUsePromptTokens,
            TotalTokens = e.TotalTokens, CachedTokens = e.CachedTokens,
            FirstByteMs = e.FirstByteMs, LongestSilenceMs = e.LongestSilenceMs, SilenceAllowanceMs = e.SilenceAllowanceMs,
            SystemPrompt = CaptureBodies || firstSight ? e.SystemPrompt : "",
            SystemPromptHash = systemPromptHash.Length > 0 ? systemPromptHash : null,
            Tools        = e.Tools,
            NewInput     = e.NewInput,
            RawRequest   = CaptureBodies ? e.RawRequest : "",
            ResponseText = e.ResponseText,
            Thoughts     = e.Thoughts,
            ToolCalls    = e.ToolCalls,
            RawResponse  = null,
        };
    }

}

public sealed class LlmCallLogEntry
{
    public string  Timestamp    { get; init; } = DateTime.UtcNow.ToString("O");
    public string  Profile      { get; init; } = "";
    public string  Model        { get; init; } = "";
    public int     TurnMs       { get; init; }
    public bool    IsError      { get; init; }
    public string? Error        { get; init; }
    public string  SystemPrompt { get; init; } = "";
    /// <summary>Names the exact system prompt of this call; the line that first carried this hash holds the text.</summary>
    public string? SystemPromptHash { get; init; }
    /// <summary>The names of the tools offered on this call, in the order sent.</summary>
    public string? Tools        { get; init; }
    /// <summary>Every message since the model's last reply (see <see cref="LlmCallLogger.NewInputOf"/>).</summary>
    public string? NewInput     { get; init; }
    public int     MessageCount { get; init; }
    public int     ToolCount    { get; init; }
    public string  RawRequest   { get; init; } = "";
    public string? ResponseText { get; init; }
    /// <summary>The model's reasoning summary for this reply, when the provider returned one.</summary>
    public string? Thoughts     { get; init; }
    /// <summary>The model's tool calls as "name argsJson" lines; captured with the bodies. The last call of a session
    /// (a chat turn's board add) has no later request to recover its arguments from.</summary>
    public string? ToolCalls    { get; init; }
    public int     ToolCallCount { get; init; }
    public string? FinishReason { get; init; }
    public string? RawResponse  { get; init; }
    public int?    PromptTokens     { get; init; }
    public int?    CompletionTokens { get; init; }
    public int?    ReasoningTokens  { get; init; }
    public int?    ToolUsePromptTokens { get; init; }
    public int?    TotalTokens      { get; init; }
    public int?    CachedTokens     { get; init; }
    /// <summary>Streamed calls: time to the response headers, the longest gap between two bytes, and the silence the
    /// call was allowed before being given up (see <see cref="SilencePolicy"/>). Null on buffered calls.</summary>
    public int?    FirstByteMs        { get; init; }
    public int?    LongestSilenceMs   { get; init; }
    public int?    SilenceAllowanceMs { get; init; }
}
