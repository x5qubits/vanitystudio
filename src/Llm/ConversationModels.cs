
namespace VanityStudio.Llm;

public sealed class ToolDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Dictionary<string, object> Parameters { get; init; } = new();
}

public enum MessageRole { User, Assistant, Tool }

public sealed class LlmResponse
{
    public string? Text { get; init; }
    /// <summary>The model's reasoning summary for this reply, when the provider returned one. Shown to the operator
    /// and logged; never added to the conversation, so it is not re-sent and never counts as the answer.</summary>
    public string? Thoughts { get; init; }
    public List<LlmToolCall> ToolCalls { get; init; } = [];
    public string? FinishReason { get; init; }
    public string? ProfileName { get; set; }
    public string? ModelName { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    /// <summary>Provider-reported hidden reasoning/thinking tokens, when exposed separately.</summary>
    public int? ReasoningTokens { get; set; }
    /// <summary>Provider-reported tokens used to process tool results, when exposed separately.</summary>
    public int? ToolUsePromptTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int? CachedTokens { get; set; }  // from usage.prompt_tokens_details.cached_tokens
}

public sealed class LlmToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ArgsJson { get; init; } = "{}";
    /// <summary>Gemini 3 / Antigravity returns an opaque thoughtSignature alongside each functionCall. It MUST be
    /// echoed back on that call in later turns or the API rejects the whole request (HTTP 400). Null elsewhere.</summary>
    public string? ThoughtSignature { get; init; }
}

public sealed class ConversationMessage
{
    public MessageRole Role { get; init; }
    public string Text { get; init; } = "";
    public List<ToolCallRecord> ToolCalls { get; init; } = [];
    public List<ToolResultRecord> ToolResults { get; init; } = [];
    public string? ImageDataUrl { get; init; }

    public static ConversationMessage FromUser(string text) =>
        new() { Role = MessageRole.User, Text = text };
    public static ConversationMessage FromUserWithImage(string text, string imageDataUrl) =>
        new() { Role = MessageRole.User, Text = text, ImageDataUrl = imageDataUrl };
    public static ConversationMessage FromAssistant(string text, List<ToolCallRecord>? toolCalls = null) =>
        new() { Role = MessageRole.Assistant, Text = text, ToolCalls = toolCalls ?? [] };
    public static ConversationMessage FromToolResults(List<ToolResultRecord> results) =>
        new() { Role = MessageRole.Tool, ToolResults = results };
}

public sealed class ToolCallRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ArgsJson { get; init; } = "";
    /// <summary>Gemini 3 / Antigravity thoughtSignature for this call - preserved so it can be echoed back on the
    /// gemini round-trip (missing it = HTTP 400). Null for providers that do not use it.</summary>
    public string? ThoughtSignature { get; init; }

    /// <summary>Copy a model-produced tool call into a history record, PRESERVING the thoughtSignature. Use this
    /// everywhere instead of hand-copying fields, so the Gemini round-trip can never silently drop the signature.</summary>
    public static ToolCallRecord From(LlmToolCall tc) =>
        new() { Id = tc.Id, Name = tc.Name, ArgsJson = tc.ArgsJson, ThoughtSignature = tc.ThoughtSignature };
}

public sealed class ToolResultRecord
{
    public string         ToolCallId        { get; init; } = "";
    public string         ToolName          { get; init; } = "";
    public string         Output            { get; init; } = "";
    public bool           IsError           { get; init; }
    public string?        ScreenshotDataUrl { get; init; }
    /// <summary>Multiple images returned by one tool call (e.g. PDF pages). When set, used instead of ScreenshotDataUrl.</summary>
    public List<string>?  ImageDataUrls     { get; init; }
}

/// <summary>Stable router surface. CallAsync uses the "orchestrator" layer; CallWithLayerAsync picks one.</summary>
public interface ILlmClient
{
    Task<LlmResponse> CallAsync(
        string systemPrompt,
        IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);

    // Was a C# 8 default interface method (=> CallAsync(...)). net472's CLR does not support default interface
    // implementations (CS8701), so it's a plain member here and each implementer provides the trivial forward.
    Task<LlmResponse> CallWithLayerAsync(
        string layer,
        string systemPrompt,
        IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct,
        long userId = 0);
}

/// <summary>The conversation a model call belongs to, flowed to the transport through the async context. The agent
/// loop sets it to its session id at run start; a transport that routes or caches by session (the Cloud Code
/// envelope's sessionId) sends it instead of a fresh id per call. A new id on every request landed each call of one
/// conversation on a different shard, so the prompt cache almost never hit (measured 0% on 8 QA calls, 2026-09-10).</summary>
public static class LlmCallScope
{
    public static readonly AsyncLocal<string?> SessionId = new AsyncLocal<string?>();

    /// <summary>An output cap for the calls made while it is set, applied under the profile's own cap on every
    /// provider path. A side call that asks for a short structured answer (the memory filter) sets it, so a model
    /// that wanders cannot write 26K tokens of transcript on a 2K-token question (Opus, 264 s, 2026-10-06).</summary>
    public static readonly AsyncLocal<int?> MaxOutputTokens = new AsyncLocal<int?>();

    /// <summary>"profile|model" the run would like to keep: the pair that answered its previous step. The router
    /// tries it first; when it fails the normal walk runs and whatever answers becomes the next preference. A run
    /// stops hopping between its pin and a fallback as the jails come and go; a new run starts from the pin.</summary>
    public static readonly AsyncLocal<string?> PreferredPair = new AsyncLocal<string?>();

    /// <summary>The layer <see cref="PreferredPair"/> was earned on. The preference applies to calls on that layer
    /// only: a side call made inside a step (the plan reviewer, the memory filter) runs on its own layer's pin. Without
    /// it the reviewer ran on the coder's model, its 503 jailed that model for 15 minutes, and the coder's next step
    /// moved to the fallback for the rest of the run (websisco task 15, 2026-10-06). Null = any layer.</summary>
    public static readonly AsyncLocal<string?> PreferredLayer = new AsyncLocal<string?>();

    /// <summary>A host that wants to show the model's words as they arrive sets this for the duration of a turn. A
    /// streaming transport calls it with ("thought", chunk) for reasoning and ("text", chunk) for the reply; buffered
    /// transports never call it, and the host shows the complete text when the call returns.</summary>
    public static readonly AsyncLocal<Action<string, string>?> OnDelta = new AsyncLocal<Action<string, string>?>();
}
