using System.Text.Json;
using VanityStudio.Infra;
using VanityStudio.Llm;
using VanityStudio.Tools;

namespace VanityStudio.Agent;

/// <summary>What the loop reports as it runs, for the console (or any other host) to render.</summary>
public sealed class AgentEvents
{
    public Action<int, int>? OnStep { get; set; }                       // iteration, max
    public Action<string>? OnThought { get; set; }                       // reasoning or interim text the model wrote with its tool calls
    public Action<string, string>? OnToolCall { get; set; }              // tool, argument preview
    public Action<string, double, int, bool>? OnToolResult { get; set; } // tool, seconds, chars, isError
    public Action<string?, string?>? OnModel { get; set; }               // profile, model that answered
}

/// <summary>Stateful multi-turn conversation with the model: user message in, tool calls executed in parallel and fed
/// back, until the model answers without calling a tool. Ported from VanityStudio's ChatSession without personas and
/// skills: one agent, one prompt, every registered tool.</summary>
public sealed class AgentLoop
{
    private readonly ILlmClient _client;
    private readonly IToolRegistry _tools;
    private readonly UsageTracker? _usage;
    private readonly string _sessionId;
    private readonly List<ConversationMessage> _history = [];
    private readonly AgentEvents _events;

    public string SystemPrompt { get; set; }
    public int MaxIterations { get; set; }
    public string AgentId { get; }
    public IReadOnlyList<ConversationMessage> History => _history;
    public int TurnCount => _history.Count(m => m.Role == MessageRole.User);

    /// <summary>Token usage accumulated across all model calls in the last SendAsync turn.</summary>
    public (int Prompt, int Completion, int Cached) LastTokenUsage { get; private set; }

    /// <summary>Every tool call of the last turn with the start of its result, in call order: what the memory
    /// analyzer reads when the turn ends.</summary>
    public List<Memory.RunStep> LastSteps { get; } = new();
    public string? LastModel { get; private set; }

    private readonly DeferredTools _deferred;

    public AgentLoop(ILlmClient client, IToolRegistry tools, string systemPrompt, AgentEvents? events = null,
        UsageTracker? usage = null, int maxIterations = 60, string? agentId = null, string? sessionId = null,
        IEnumerable<ConversationMessage>? history = null, IEnumerable<string>? deferredTools = null)
    {
        _client = client; _tools = tools; SystemPrompt = systemPrompt; _events = events ?? new AgentEvents();
        _usage = usage; MaxIterations = Math.Max(1, maxIterations);
        AgentId = agentId ?? "main";
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        if (history is not null) _history.AddRange(history);   // a persona switch keeps the conversation
        _deferred = new DeferredTools(tools.All, deferredTools ?? []);
    }

    private static readonly System.Text.RegularExpressions.Regex TextToolCall = new(
        @"(?m)^\s*(?:call:\w+:\w+\s*\{|```tool_code|print\(default_api\.|default_api\.\w+\()",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<string> SendAsync(string userMessage, CancellationToken ct = default, string? imageDataUrl = null)
    {
        AgentToolContext.AgentId = AgentId;
        AgentToolContext.RunId = Guid.NewGuid().ToString("N");

        HistoryLifecycleManager.EnforceTokenBudget(_history);
        LastSteps.Clear();

        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var text = $"{userMessage}\n\n[Current Time: {stamp} UTC]";
        _history.Add(imageDataUrl is null ? ConversationMessage.FromUser(text) : ConversationMessage.FromUserWithImage(text, imageDataUrl));
        Log.Debug($"[{AgentId}] turn {TurnCount} >> {Head(userMessage, 80)}");

        string reply = "";
        int totalPrompt = 0, totalCompletion = 0, totalCached = 0;
        int emptyRetries = 0, textSlips = 0;
        int maxIter = MaxIterations;

        for (int iter = 1; iter <= maxIter; iter++)
        {
            _events.OnStep?.Invoke(iter, maxIter);
            LlmCallScope.SessionId.Value = _sessionId;

            // Deferred tools: their one-line catalog rides in the prompt, their schemas only once loaded.
            var toolDefs = _deferred.Active();
            var catalog = _deferred.CatalogBlock();
            var systemPrompt = catalog.Length > 0 ? SystemPrompt + "\n\n---\n" + catalog : SystemPrompt;

            var response = await _client.CallAsync(systemPrompt, _history, toolDefs, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response.ProfileName) && !string.IsNullOrEmpty(response.ModelName))
            {
                LlmCallScope.PreferredPair.Value = response.ProfileName + "|" + response.ModelName;
                LlmCallScope.PreferredLayer.Value = "any";
            }
            totalPrompt += response.PromptTokens ?? 0;
            totalCompletion += response.CompletionTokens ?? 0;
            totalCached += response.CachedTokens ?? 0;
            _usage?.Track(response.PromptTokens ?? 0, response.CompletionTokens ?? 0, response.CachedTokens ?? 0, response.ModelName);
            LastModel = response.ModelName;
            _events.OnModel?.Invoke(response.ProfileName, response.ModelName);

            reply = response.Text ?? "";
            if (!string.IsNullOrWhiteSpace(response.Thoughts)) _events.OnThought?.Invoke(response.Thoughts!);

            // An empty completion (no text, no calls) is no answer: one retry, then a concrete failure.
            if (response.ToolCalls.Count == 0 && string.IsNullOrWhiteSpace(reply))
            {
                if (emptyRetries++ > 0) throw new InvalidOperationException("The model returned an empty response twice; no result was produced.");
                maxIter = Math.Max(maxIter, iter + 1);
                _history.Add(ConversationMessage.FromUser("[harness] Your last response was empty and no tool ran. Continue the task: use tools for unfinished work, or give your answer."));
                Log.Warn($"[{AgentId}] empty model response: retrying once");
                continue;
            }

            if (response.ToolCalls.Count == 0)
            {
                _history.Add(ConversationMessage.FromAssistant(reply));
                // A tool call written as prose is a transport slip, not an answer: hand it back once.
                if (LooksLikeTextToolCall(reply) && textSlips < 2)
                {
                    textSlips++;
                    maxIter = Math.Max(maxIter, iter + 2);
                    _history.Add(ConversationMessage.FromUser("[harness] that reply was a tool call written as text; nothing ran. Make the same call through the tool interface."));
                    continue;
                }
                break;
            }

            if (!string.IsNullOrWhiteSpace(reply)) _events.OnThought?.Invoke(reply);

            var callRecords = response.ToolCalls.Select(ToolCallRecord.From).ToList();
            _history.Add(ConversationMessage.FromAssistant(reply, callRecords));

            var tasks = response.ToolCalls.Select(tc => ExecuteToolAsync(tc, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (int i = 0; i < response.ToolCalls.Count && i < results.Length; i++)
            {
                var output = results[i].Output ?? "";
                LastSteps.Add(new Memory.RunStep(LastSteps.Count + 1, response.ToolCalls[i].Name, response.ToolCalls[i].ArgsJson ?? "",
                    output.Length > 1500 ? output[..1500] : output, results[i].IsError));
            }
            var sanitized = results.Select(r => HistoryLifecycleManager.SanitizeToolResult(r)).ToList();

            // Budget line on the last result: the model sees how many turns remain and finishes in time.
            if (sanitized.Count > 0)
            {
                var left = Math.Max(0, maxIter - iter);
                var note = left <= 3
                    ? $"\n\n[harness] turn {iter}/{maxIter} used — {left} left. Finish now: reply with the result."
                    : $"\n\n[harness] turn {iter}/{maxIter} used — {left} left.";
                var li = sanitized.Count - 1; var lr = sanitized[li];
                sanitized[li] = new ToolResultRecord
                {
                    ToolCallId = lr.ToolCallId, ToolName = lr.ToolName, Output = lr.Output + note, IsError = lr.IsError,
                    ScreenshotDataUrl = lr.ScreenshotDataUrl, ImageDataUrls = lr.ImageDataUrls,
                };
            }
            _history.Add(ConversationMessage.FromToolResults(sanitized));

            HistoryLifecycleManager.DropSupersededReads(_history);
            HistoryLifecycleManager.StubOldChangeArguments(_history);
            HistoryLifecycleManager.CompactCurrentTurnIterations(_history);
        }

        if (string.IsNullOrWhiteSpace(reply) || _history[^1].Role != MessageRole.Assistant || _history[^1].ToolCalls.Count > 0)
        {
            // The loop ran out of turns mid-work: close the turn with what the model last said.
            var last = _history.LastOrDefault(m => m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))?.Text;
            reply = string.IsNullOrWhiteSpace(last) ? "[the turn limit was reached before the task finished; ask me to continue]"
                : last + "\n\n[the turn limit was reached before the task finished; ask me to continue]";
            _history.Add(ConversationMessage.FromAssistant(reply));
        }

        HistoryLifecycleManager.CompactCompletedTurns(_history);
        HistoryLifecycleManager.EnforceTokenBudget(_history);
        LastTokenUsage = (totalPrompt, totalCompletion, totalCached);
        Log.Debug($"[{AgentId}] turn {TurnCount} << {Head(reply, 80)}");
        return reply;
    }

    public void Reset()
    {
        _history.Clear();
        Log.Info($"[{AgentId}] history cleared");
    }

    private async Task<ToolResultRecord> ExecuteToolAsync(LlmToolCall tc, CancellationToken ct)
    {
        if (_deferred.IsLoadCall(tc.Name))
        {
            _events.OnToolCall?.Invoke(tc.Name, ArgPreview(tc.Name, tc.ArgsJson));
            var loaded = _deferred.HandleLoad(tc);
            _events.OnToolResult?.Invoke(tc.Name, 0, loaded.Output.Length, loaded.IsError);
            return loaded;
        }
        if (!_tools.TryGet(tc.Name, out var tool) || tool is null)
        {
            Log.Warn($"[{AgentId}] tool not found: {tc.Name}");
            _events.OnToolResult?.Invoke(tc.Name, 0, 0, true);
            return new ToolResultRecord { ToolCallId = tc.Id, ToolName = tc.Name, Output = $"Tool '{tc.Name}' is not available. Use one of: {string.Join(", ", _tools.All.Select(t => t.Name))}.", IsError = true };
        }

        _events.OnToolCall?.Invoke(tc.Name, ArgPreview(tc.Name, tc.ArgsJson));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ToolResultRecord record;
            if (tool is IVisualTool visual)
                record = await visual.ExecuteVisualAsync(tc.Id, tc.ArgsJson, ct).ConfigureAwait(false);
            else
            {
                var output = await tool.ExecuteAsync(tc.ArgsJson, ct).ConfigureAwait(false);
                record = new ToolResultRecord { ToolCallId = tc.Id, ToolName = tc.Name, Output = output };
            }
            sw.Stop();
            _events.OnToolResult?.Invoke(tc.Name, sw.Elapsed.TotalSeconds, record.Output.Length, record.IsError);
            return record;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error($"[{AgentId}] tool:{tc.Name} failed: {ex.Message}");
            _events.OnToolResult?.Invoke(tc.Name, sw.Elapsed.TotalSeconds, ex.Message.Length, true);
            return new ToolResultRecord { ToolCallId = tc.Id, ToolName = tc.Name, Output = $"Tool error: {ex.Message}", IsError = true };
        }
    }

    private static bool LooksLikeTextToolCall(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length < 4000 && TextToolCall.IsMatch(text);

    internal static string ArgPreview(string tool, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in new[] { "command", "file_path", "path", "pattern", "query", "url", "task", "action", "format" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return Head(v.GetString() ?? "", 100);
            if (root.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
                return string.Join(", ", names.EnumerateArray().Select(n => n.ToString()));
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                return $"{files.GetArrayLength()} file(s)";
            if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                return $"{edits.GetArrayLength()} edit(s)";
            if (root.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
                return $"{jobs.GetArrayLength()} job(s)";
            return Head(argsJson.Trim(), 100);
        }
        catch { return ""; }
    }

    private static string Head(string s, int max)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
