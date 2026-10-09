using System.Text;
using System.Text.Json;
using VanityStudio.Llm;
using VanityStudio.Infra;

namespace VanityStudio.Agent;

/// <summary>
/// Manages conversation history lifecycle, sliding windows, and token bloat control.
/// Prevents token explosions by:
/// 1. Sanitizing/capping large raw tool outputs (e.g. verbose shell errors, dumps).
/// 2. Compacting older iterations within the active turn into concise progress ledgers.
/// 3. Pruning stale base64 screenshots so only the latest visual frame is retained.
/// 4. Compacting completed conversation turns into User/Assistant pairs.
/// 5. Enforcing a global history token budget.
/// </summary>
public static class HistoryLifecycleManager
{
    // Sized for a BUILDER, not a chat bot: a specialist must keep what it read (an API contract, a template,
    // a reviewer verdict) in full until it has used it. Compaction is token-driven, not iteration-driven —
    // nothing is summarised while the turn is still comfortably inside the budget.
    public const int MaxToolOutputChars = 24_000;
    public const int CompactOlderIterationChars = 2_500;
    public const int MaxHistoryTokens = 60_000;
    public const int MaxCompletedTurnsToKeepDetailed = 5;
    // Keep 8 / trigger 32k erased a builder's evidence before it had acted on it: on a 25k-line stylesheet the
    // estimate crossed 32k at iteration 35, the first pass compacted 27 steps and the ledger kept the last 20, so
    // the three colour measurements from iterations 2-4 were gone from history AND from the ledger, and the coder
    // re-measured the same image and re-read the same ranges for 30 more calls (Westdental 2026-09-22). The same
    // spiral was found and reverted once before (keep 40, 2026-09-10). A safety valve for a runaway run, not a
    // working-memory policy: it must stay out of reach of a normal task.
    public const int DefaultKeepRecentIterations = 40;
    /// <summary>In-turn compaction only starts once the estimated history exceeds this many tokens.</summary>
    public const int CompactionTriggerTokens = 150_000;
    /// <summary>Ledger lines kept in the user message once compaction runs. Dropping the oldest lines is what
    /// deletes a measurement the model has not used yet, so the cap sits far above any real run.</summary>
    public const int MaxLedgerSteps = 200;

    /// <summary>
    /// Caps large tool outputs at runtime to prevent single-turn token explosions.
    /// Preserves the head (60%) and tail (40%) so error summaries and exit codes are retained.
    /// </summary>
    /// <summary>The header every file read carries: the path and the exact span it returned.</summary>
    private static readonly System.Text.RegularExpressions.Regex FileReadHeader =
        new(@"^--- File: (?<path>.+?) \(lines (?<from>\d+)-(?<to>\d+) of (?<total>\d+)\) ---",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public static ToolResultRecord SanitizeToolResult(ToolResultRecord record, int maxChars = MaxToolOutputChars)
    {
        if (record.Output.Length <= maxChars)
            return record;

        // A FILE READ must never lose its middle. Head+tail is right for a command or a log, where the answer is at
        // both ends; for source it hands back a body with a silent hole in it, so every exact-text edit built from it
        // misses, and the model reads the same region again and again to find what it is missing. Cut at the end on a
        // line boundary and state the span that actually arrived, so the next read continues instead of guessing.
        var fileRead = FileReadHeader.Match(record.Output);
        if (fileRead.Success) return TruncateFileRead(record, fileRead, maxChars);

        var head = (int)(maxChars * 0.6);
        var tail = maxChars - head;
        var omitted = record.Output.Length - maxChars;

        var truncatedOutput = record.Output[..head]
            + $"\n\n[... Output truncated: {omitted:N0} characters omitted for context efficiency ...]\n\n"
            + record.Output[^tail..];

        return new ToolResultRecord
        {
            ToolCallId        = record.ToolCallId,
            ToolName          = record.ToolName,
            Output            = truncatedOutput,
            IsError           = record.IsError,
            ScreenshotDataUrl = record.ScreenshotDataUrl,
        };
    }

    /// <summary>Cap a file read at the last whole line that fits, and rewrite its header to the span that survived.
    /// The line numbers are already in the body, so the true last line is read back from it rather than counted.</summary>
    private static ToolResultRecord TruncateFileRead(ToolResultRecord record, System.Text.RegularExpressions.Match header, int maxChars)
    {
        var cut  = record.Output.LastIndexOf('\n', Math.Min(maxChars, record.Output.Length) - 1);
        var kept = cut > 0 ? record.Output[..cut] : record.Output[..Math.Min(maxChars, record.Output.Length)];

        var numbered = System.Text.RegularExpressions.Regex.Matches(kept, @"(?m)^\s*(\d+)\s*\|");
        var from     = header.Groups["from"].Value;
        var last     = numbered.Count > 0 ? numbered[^1].Groups[1].Value : from;
        var body     = kept[(kept.IndexOf('\n') + 1)..];

        return new ToolResultRecord
        {
            ToolCallId        = record.ToolCallId,
            ToolName          = record.ToolName,
            IsError           = record.IsError,
            ScreenshotDataUrl = record.ScreenshotDataUrl,
            Output = $"--- File: {header.Groups["path"].Value} (lines {from}-{last} of {header.Groups["total"].Value}) ---\n"
                   + body
                   + $"\n[capped here; the rest of the range starts at line {(int.TryParse(last, out var n) ? n + 1 : 0)}]",
        };
    }

    /// <summary>Drop file reads this turn already superseded: a read whose every line a LATER read of the same file
    /// also returned. The file did not change between them, so the older copy is the same text, re-sent on every
    /// call for the rest of the run. Nothing the model has not already been given a fresher copy of is removed, and
    /// it costs no extra call: the newer read is in front of it either way. (Ten reads of one stylesheet, five of
    /// them covering the same sixty lines, were 89% of a task's tool output - 2026-09-22.)</summary>
    public static int DropSupersededReads(List<ConversationMessage> history, int minChars = 400)
    {
        int lastUser = history.FindLastIndex(m => m.Role == MessageRole.User);
        if (lastUser < 0) return 0;

        // Identified by (message, position): tool calls run in parallel, so one message can carry several reads and
        // only some of them superseded. Keyed by message alone, an unsuperseded sibling was dropped with them.
        var reads = new List<(int Msg, int Pos, string Path, int From, int To, int Len)>();
        for (int i = lastUser + 1; i < history.Count; i++)
        {
            if (history[i].Role != MessageRole.Tool) continue;
            for (int j = 0; j < history[i].ToolResults.Count; j++)
            {
                var r = history[i].ToolResults[j];
                var m = FileReadHeader.Match(r.Output ?? "");
                if (m.Success && int.TryParse(m.Groups["from"].Value, out var f) && int.TryParse(m.Groups["to"].Value, out var t))
                    reads.Add((i, j, m.Groups["path"].Value, f, t, (r.Output ?? "").Length));
            }
        }

        // What each read still contributes: the lines no LATER read of the same file returned. Fully covered -> the
        // whole result goes. Partly covered -> it is trimmed to the span it alone still carries. A window that slides
        // (read 190-349, then 230-389, then 232-391) is never CONTAINED in a later one, yet 45% of the lines it
        // returns are already above it - trimming is what removes those, and it removes nothing the model cannot
        // still read in the newer result.
        // A tool run twice with the same arguments over an unchanged workspace returns the same bytes. The second
        // result is the live one; the first is a duplicate re-sent on every later call. Seen repeatedly in real runs:
        // one search issued four times, another twice, inside a single turn (2026-09-22). Matched on the output
        // itself, so it needs no record of the arguments and holds for any tool, not just search.
        var plan = new Dictionary<(int, int), (int From, int To)?>();
        var latestIdentical = new Dictionary<(string Tool, string Output), (int Msg, int Pos)>();
        for (int i = lastUser + 1; i < history.Count; i++)
        {
            if (history[i].Role != MessageRole.Tool) continue;
            for (int j = 0; j < history[i].ToolResults.Count; j++)
            {
                var r = history[i].ToolResults[j];
                var output = r.Output ?? "";
                if (output.Length < minChars || FileReadHeader.IsMatch(output)) continue;   // reads use the span rule below
                var key = (r.ToolName ?? "", output);
                if (latestIdentical.TryGetValue(key, out var earlier)) plan[earlier] = null;
                latestIdentical[key] = (i, j);
            }
        }
        for (int k = 0; k < reads.Count; k++)
        {
            if (reads[k].Len < minChars) continue;
            var keep = UncoveredSpan(reads, k);
            if (keep is null || keep.Value.From != reads[k].From || keep.Value.To != reads[k].To)
                plan[(reads[k].Msg, reads[k].Pos)] = keep;
        }
        if (plan.Count == 0) return 0;

        int dropped = 0;
        foreach (var msgIdx in plan.Keys.Select(s => s.Item1).Distinct().ToList())
        {
            var msg = history[msgIdx];
            var updated = new List<ToolResultRecord>();
            bool changed = false;
            for (int j = 0; j < msg.ToolResults.Count; j++)
            {
                var r = msg.ToolResults[j];
                if (!plan.TryGetValue((msgIdx, j), out var keep)) { updated.Add(r); continue; }
                var output = r.Output ?? "";
                var m = FileReadHeader.Match(output);
                updated.Add(new ToolResultRecord
                {
                    ToolCallId = r.ToolCallId, ToolName = r.ToolName, IsError = r.IsError, ScreenshotDataUrl = null,
                    Output = keep is not null && m.Success
                        ? TrimToSpan(output, m, keep.Value.From, keep.Value.To)
                        : m.Success
                            ? $"[{m.Groups["path"].Value} lines {m.Groups["from"].Value}-{m.Groups["to"].Value} " +
                              $"({output.Length:N0} chars) removed: a later read above returned these same lines]"
                            : $"[{r.ToolName} output ({output.Length:N0} chars) removed: the same call appears " +
                              $"again below with the same result]",
                });
                changed = true; dropped++;
            }
            if (changed) history[msgIdx] = ConversationMessage.FromToolResults(updated);
        }
        return dropped;
    }

    /// <summary>Keep only the numbered lines in [from,to]; the rest are in a later read. Header rewritten to match.</summary>
    private static string TrimToSpan(string output, System.Text.RegularExpressions.Match header, int from, int to)
    {
        var kept = new List<string>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var n = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)\s*\|");
            if (!n.Success) continue;
            var num = int.Parse(n.Groups[1].Value);
            if (num >= from && num <= to) kept.Add(line.TrimEnd('\r'));
        }
        return $"--- File: {header.Groups["path"].Value} (lines {from}-{to} of {header.Groups["total"].Value}) ---\n"
             + string.Join("\n", kept)
             + "\n[the rest of this read is in a later read of the same file above]";
    }

    /// <summary>The span of read <paramref name="k"/> that no later read of the same file returned: null when every
    /// line is covered, otherwise the outermost lines that are not. A covered stretch in the middle is left in place
    /// rather than splicing the body - the saving is in the sliding overlap at the ends.</summary>
    private static (int From, int To)? UncoveredSpan(List<(int Msg, int Pos, string Path, int From, int To, int Len)> reads, int k)
    {
        var a = reads[k];
        var later = reads.Skip(k + 1)
            .Where(r => string.Equals(r.Path, a.Path, StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.From, r.To)).ToList();
        if (later.Count == 0) return (a.From, a.To);

        bool Covered(int line) => later.Any(r => line >= r.From && line <= r.To);
        int lo = a.From; while (lo <= a.To && Covered(lo)) lo++;
        if (lo > a.To) return null;                       // every line is above, in a newer read
        int hi = a.To;   while (hi > lo && Covered(hi)) hi--;
        return (lo, hi);
    }

    /// <summary>Do later reads of the same file, together, cover every line of read <paramref name="k"/>?</summary>
    private static bool CoveredByLater(List<(int Msg, int Pos, string Path, int From, int To, int Len)> reads, int k)
    {
        var a = reads[k];
        var later = reads.Skip(k + 1)
            .Where(r => string.Equals(r.Path, a.Path, StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.From, r.To))
            .OrderBy(r => r.From).ToList();
        int reached = a.From;
        foreach (var (f, t) in later)
        {
            if (f > reached) return false;              // a gap this read covers and no later one does
            if (t >= reached) reached = t + 1;
            if (reached > a.To) return true;
        }
        return reached > a.To;
    }

    /// <summary>
    /// Compacts completed user turns in history.
    /// Once a user turn finishes (assistant returned its final answer), the intermediate
    /// tool calls and tool result dumps are pruned, keeping only the User request and
    /// Assistant final answer. This reduces historical token footprint by 80-95%.
    /// </summary>
    public static void CompactCompletedTurns(List<ConversationMessage> history)
    {
        if (history.Count <= 2) return;

        var compacted = new List<ConversationMessage>();
        int i = 0;

        while (i < history.Count)
        {
            var msg = history[i];

            if (msg.Role == MessageRole.User)
            {
                // Look ahead to find the final assistant response for this user turn
                int nextUserIdx = history.FindIndex(i + 1, m => m.Role == MessageRole.User);
                int turnEndExclusive = nextUserIdx == -1 ? history.Count : nextUserIdx;

                // Check if this turn is fully completed (has an Assistant message without ToolCalls)
                int finalAssistantIdx = -1;
                for (int j = turnEndExclusive - 1; j > i; j--)
                {
                    if (history[j].Role == MessageRole.Assistant && history[j].ToolCalls.Count == 0)
                    {
                        finalAssistantIdx = j;
                        break;
                    }
                }

                // If this turn has completed with an assistant response and had intermediate tool steps
                if (finalAssistantIdx > i)
                {
                    compacted.Add(msg); // The User message

                    // Collect tool names used for brief notation
                    var usedTools = history.GetRange(i + 1, finalAssistantIdx - i)
                        .Where(m => m.Role == MessageRole.Assistant && m.ToolCalls.Count > 0)
                        .SelectMany(m => m.ToolCalls.Select(tc => tc.Name))
                        .Distinct()
                        .ToList();

                    var finalAssistant = history[finalAssistantIdx];
                    if (usedTools.Count > 0)
                    {
                        var toolNote = $"[Actions performed via: {string.Join(", ", usedTools)}]\n" + finalAssistant.Text;
                        compacted.Add(ConversationMessage.FromAssistant(toolNote));
                    }
                    else
                    {
                        compacted.Add(finalAssistant);
                    }

                    i = nextUserIdx == -1 ? history.Count : nextUserIdx;
                    continue;
                }
            }

            compacted.Add(msg);
            i++;
        }

        history.Clear();
        history.AddRange(compacted);
    }

    /// <summary>
    /// Compacts older tool iterations within the active multi-step turn.
    /// Retains the last <paramref name="keepRecentIterations"/> in full fidelity,
    /// condenses prior iterations into a concise progress ledger in the user prompt,
    /// and drops the older assistant/tool message pairs from history.
    /// This stops quadratic token explosion (O(N^2)) in long autonomous runs.
    /// </summary>
    public static void CompactCurrentTurnIterations(List<ConversationMessage> history, int keepRecentIterations = DefaultKeepRecentIterations, int triggerTokens = CompactionTriggerTokens)
    {
        if (history.Count < 4) return;

        // Find the start of the current user turn
        int currentTurnUserIdx = -1;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == MessageRole.User)
            {
                currentTurnUserIdx = i;
                break;
            }
        }

        if (currentTurnUserIdx < 0) return;

        // Collect all Tool messages in current turn
        var toolMessageIndices = new List<int>();
        for (int i = currentTurnUserIdx + 1; i < history.Count; i++)
        {
            if (history[i].Role == MessageRole.Tool && history[i].ToolResults.Count > 0)
                toolMessageIndices.Add(i);
        }

        // Screenshots are the one thing always pruned (each is ~4K tokens and only the latest frame matters).
        // Everything else stays verbatim until the turn actually approaches the budget.
        if (toolMessageIndices.Count <= keepRecentIterations || EstimateTokens(history) <= triggerTokens)
        {
            PruneStaleScreenshots(history, toolMessageIndices);
            return;
        }

        int countToCompact = toolMessageIndices.Count - keepRecentIterations;

        // 1. Extract concise step summaries for the iterations being pruned
        var stepSummaries = new List<string>();
        for (int k = 0; k < countToCompact; k++)
        {
            int tIdx = toolMessageIndices[k];
            int aIdx = tIdx - 1;
            if (aIdx >= 0 && aIdx < history.Count && history[aIdx].Role == MessageRole.Assistant)
            {
                var aMsg = history[aIdx];
                var tMsg = history[tIdx];

                for (int callIdx = 0; callIdx < aMsg.ToolCalls.Count; callIdx++)
                {
                    var tc = aMsg.ToolCalls[callIdx];
                    var target = ExtractToolTarget(tc.Name, tc.ArgsJson);
                    var targetStr = string.IsNullOrEmpty(target) ? "" : $" ({target})";

                    string resSummary = "OK";
                    if (callIdx < tMsg.ToolResults.Count)
                    {
                        var res = tMsg.ToolResults[callIdx];
                        var status = res.IsError ? "FAIL" : "OK";
                        var snippet = TruncateLine((res.Output ?? "").Trim(), 160);
                        resSummary = $"{status}: {snippet}";
                    }

                    stepSummaries.Add($"* {tc.Name}{targetStr} -> {resSummary}");
                }
            }
        }

        // 2. Append step summaries to the User message
        if (stepSummaries.Count > 0)
        {
            var userMsg = history[currentTurnUserIdx];
            var updatedText = AppendExecutionProgress(userMsg.Text, stepSummaries);
            history[currentTurnUserIdx] = new ConversationMessage
            {
                Role = MessageRole.User,
                Text = updatedText,
                ImageDataUrl = userMsg.ImageDataUrl
            };
        }

        // 3. Remove the compacted assistant/tool message pairs from history
        int removeEndInclusive = toolMessageIndices[countToCompact - 1];
        int removeCount = removeEndInclusive - currentTurnUserIdx;
        if (removeCount > 0 && currentTurnUserIdx + 1 + removeCount <= history.Count)
        {
            history.RemoveRange(currentTurnUserIdx + 1, removeCount);
        }

        // 4. In remaining recent iterations, truncate non-latest tool outputs and prune old screenshots
        var remainingToolIndices = new List<int>();
        for (int i = currentTurnUserIdx + 1; i < history.Count; i++)
        {
            if (history[i].Role == MessageRole.Tool)
                remainingToolIndices.Add(i);
        }

        PruneStaleScreenshots(history, remainingToolIndices);

        // Truncate older non-latest tool outputs in the remaining active window
        for (int k = 0; k < remainingToolIndices.Count - 1; k++)
        {
            int idx = remainingToolIndices[k];
            var toolMsg = history[idx];
            bool modified = false;
            var updatedResults = new List<ToolResultRecord>();

            foreach (var res in toolMsg.ToolResults)
            {
                var output = res.Output ?? "";
                if (output.Length > CompactOlderIterationChars)
                {
                    var head = (int)(CompactOlderIterationChars * 0.7);
                    var tail = CompactOlderIterationChars - head;
                    var omitted = output.Length - CompactOlderIterationChars;
                    output = output[..head]
                        + $"\n[... {omitted:N0} chars compacted ...]\n"
                        + output[^tail..];
                    modified = true;
                }

                updatedResults.Add(new ToolResultRecord
                {
                    ToolCallId        = res.ToolCallId,
                    ToolName          = res.ToolName,
                    Output            = output,
                    IsError           = res.IsError,
                    ScreenshotDataUrl = res.ScreenshotDataUrl,
                });
            }

            if (modified)
            {
                history[idx] = ConversationMessage.FromToolResults(updatedResults);
            }
        }
    }

    /// <summary>A step of the plan was just marked done, with its facts written to the board and re-shown to the
    /// model on every call. The raw tool outputs behind it are no longer needed in full: every one older than the
    /// last <paramref name="keepRecent"/> tool messages of this run is replaced by a one-line stub that names the
    /// tool and says how to get the text back. Compaction on the model's own declaration, not on a token count,
    /// so it cannot erase evidence the model has not used yet (the 32k trigger did exactly that, 2026-09-22).
    /// Outputs shorter than <paramref name="minChars"/> and the exempt tools (a loaded skill playbook) stay.</summary>
    public static int StubOlderToolResults(List<ConversationMessage> history, int keepRecent = 1, int minChars = 400, IReadOnlySet<string>? exemptTools = null)
    {
        int lastUser = history.FindLastIndex(m => m.Role == MessageRole.User);
        if (lastUser < 0) return 0;
        var toolIdx = new List<int>();
        for (int i = lastUser + 1; i < history.Count; i++)
            if (history[i].Role == MessageRole.Tool && history[i].ToolResults.Count > 0) toolIdx.Add(i);
        int stubbed = 0;
        for (int k = 0; k < toolIdx.Count - keepRecent; k++)
        {
            var msg = history[toolIdx[k]];
            bool changed = false;
            var updated = new List<ToolResultRecord>();
            foreach (var r in msg.ToolResults)
            {
                var output = r.Output ?? "";
                bool exempt = exemptTools != null && exemptTools.Contains(r.ToolName);
                if (exempt || output.Length < minChars || output.StartsWith("[", StringComparison.Ordinal) && output.Contains("removed after a step was marked done"))
                { updated.Add(r); continue; }
                updated.Add(new ToolResultRecord
                {
                    ToolCallId = r.ToolCallId, ToolName = r.ToolName, IsError = r.IsError, ScreenshotDataUrl = null,
                    Output = $"[{r.ToolName} output ({output.Length:N0} chars) removed after a step was marked done: its facts are in the step result on the board; re-run the tool if you need the text again]",
                });
                changed = true; stubbed++;
            }
            if (changed) history[toolIdx[k]] = ConversationMessage.FromToolResults(updated);
        }
        return stubbed;
    }

    /// <summary>How many of the newest model turns keep the full text of what they wrote. The same age at which the
    /// request builder already rewrites a turn (its thought signature is swapped for the placeholder), so dropping
    /// the text here breaks no cached prefix that was not being rewritten anyway.</summary>
    public const int KeepWrittenTurns = 4;
    private const int WrittenMinChars = 200;
    private static readonly HashSet<string> ChangeTools = new(StringComparer.OrdinalIgnoreCase) { "write_files", "write_file", "edit_file" };

    /// <summary>WHAT THE WORKER WROTE IS ON DISK. Its copy in the conversation - the file contents passed to
    /// write_files, the old and new text of an edit - is the one thing nothing ever removed: tool RESULTS are stubbed
    /// at step boundaries and compacted under pressure, the model's own call arguments never were. In a 2,786-call
    /// project build they were 38% of everything that entered the conversation (2.9M of 7.5M characters), each one
    /// re-sent on every later call of its task. Replaying those same calls with the arguments dropped a few calls
    /// after they were made takes the input from 187.7M tokens to 107.2M (-42%; -44% at one call, -37% at sixteen).
    /// Past <paramref name="keepRecentTurns"/> model turns, the long strings of a change call are replaced by a line
    /// saying how much was written; paths and short strings stay, so the record of WHAT was changed is intact. The
    /// text is one read away, and that read returns the file as it is now rather than as it was when written.</summary>
    public static int StubOldChangeArguments(List<ConversationMessage> history, int keepRecentTurns = KeepWrittenTurns)
    {
        var turns = new List<int>();
        for (int i = 0; i < history.Count; i++)
            if (history[i].Role == MessageRole.Assistant && history[i].ToolCalls is { Count: > 0 }) turns.Add(i);
        int stubbed = 0;
        for (int t = 0; t < turns.Count - keepRecentTurns; t++)
        {
            var msg = history[turns[t]];
            List<ToolCallRecord>? updated = null;
            for (int c = 0; c < msg.ToolCalls.Count; c++)
            {
                var call = msg.ToolCalls[c];
                if (!ChangeTools.Contains(call.Name) || call.ArgsJson.Length < WrittenMinChars) continue;
                var slim = SlimChangeArguments(call.ArgsJson);
                if (slim is null) continue;
                updated ??= new List<ToolCallRecord>(msg.ToolCalls);
                // A NEW record: the run's step log holds the original call and must keep the paths and text it saw.
                updated[c] = new ToolCallRecord { Id = call.Id, Name = call.Name, ArgsJson = slim, ThoughtSignature = call.ThoughtSignature };
                stubbed++;
            }
            if (updated is not null)
                history[turns[t]] = new ConversationMessage { Role = msg.Role, Text = msg.Text, ToolCalls = updated, ToolResults = msg.ToolResults, ImageDataUrl = msg.ImageDataUrl };
        }
        return stubbed;
    }

    /// <summary>The same arguments with every long piece of written text replaced by its size. Null when there is
    /// nothing long in them (also for arguments already slimmed: what replaces the text is short).</summary>
    internal static string? SlimChangeArguments(string argsJson)
    {
        System.Text.Json.Nodes.JsonNode? root;
        try { root = System.Text.Json.Nodes.JsonNode.Parse(argsJson); } catch (JsonException) { return null; }
        if (root is not System.Text.Json.Nodes.JsonObject obj) return null;
        bool changed = false;
        void Slim(System.Text.Json.Nodes.JsonObject o)
        {
            foreach (var (key, note) in new[] { ("content", "written - on disk; read the file for its current text"), ("new_string", "written - on disk; read the file for its current text"), ("old_string", "replaced") })
                if (o[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s.Length >= WrittenMinChars)
                { o[key] = $"[{s.Length:N0} chars {note}]"; changed = true; }
        }
        Slim(obj);
        foreach (var list in new[] { "files", "edits" })
            if (obj[list] is System.Text.Json.Nodes.JsonArray arr)
                foreach (var item in arr)
                    if (item is System.Text.Json.Nodes.JsonObject entry) Slim(entry);
        return changed ? obj.ToJsonString() : null;
    }

    private static void PruneStaleScreenshots(List<ConversationMessage> history, List<int> toolIndices)
    {
        if (toolIndices.Count <= 1) return;

        // Keep the latest two screenshots (a before and an after), drop the rest. With one kept, a look at a page
        // vanished the moment the next tool ran, and the model took the same screenshot again and again
        // (20 of one page in a run, 2026-09-22). The page digest in the text result is what survives long term.
        var keep = new HashSet<int>();
        for (int k = toolIndices.Count - 1; k >= 0 && keep.Count < 2; k--)
        {
            int idx = toolIndices[k];
            if (history[idx].ToolResults.Any(r => !string.IsNullOrEmpty(r.ScreenshotDataUrl))) keep.Add(idx);
        }

        if (keep.Count == 0) return;

        // Clear screenshots in all older tool messages
        for (int k = 0; k < toolIndices.Count; k++)
        {
            int idx = toolIndices[k];
            if (keep.Contains(idx)) continue;

            var toolMsg = history[idx];
            bool hasShot = toolMsg.ToolResults.Any(r => !string.IsNullOrEmpty(r.ScreenshotDataUrl));
            if (hasShot)
            {
                var stripped = toolMsg.ToolResults.Select(r => new ToolResultRecord
                {
                    ToolCallId        = r.ToolCallId,
                    ToolName          = r.ToolName,
                    Output            = r.Output,
                    IsError           = r.IsError,
                    ScreenshotDataUrl = null,
                }).ToList();
                history[idx] = ConversationMessage.FromToolResults(stripped);
            }
        }
    }

    private static string AppendExecutionProgress(string existingText, List<string> newSteps)
    {
        const string ProgressHeader = "\n\n[Prior Execution Steps]:";
        int headerIdx = existingText.IndexOf(ProgressHeader, StringComparison.Ordinal);

        string basePrompt = headerIdx >= 0 ? existingText[..headerIdx] : existingText;
        string existingProgress = headerIdx >= 0 ? existingText[(headerIdx + ProgressHeader.Length)..].Trim() : "";

        var allSteps = new List<string>();
        if (!string.IsNullOrEmpty(existingProgress))
        {
            foreach (var line in existingProgress.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    allSteps.Add(trimmed);
            }
        }

        allSteps.AddRange(newSteps);

        // A cap against unbounded growth only; see MaxLedgerSteps for why it is high.
        if (allSteps.Count > MaxLedgerSteps)
        {
            int omitted = allSteps.Count - (MaxLedgerSteps - 5);
            var condensed = new List<string> { $"* (Steps 1-{omitted} omitted for brevity: earlier setup/exploration)" };
            condensed.AddRange(allSteps.Skip(omitted));
            allSteps = condensed;
        }

        return $"{basePrompt}{ProgressHeader}\n{string.Join("\n", allSteps)}";
    }

    private static string ExtractToolTarget(string toolName, string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("command", out var cmd)) return TruncateLine(cmd.GetString() ?? "", 50);
                if (root.TryGetProperty("path", out var p)) return TruncateLine(p.GetString() ?? "", 40);
                if (root.TryGetProperty("persona", out var per)) return per.GetString() ?? "";
                if (root.TryGetProperty("action", out var act)) return act.GetString() ?? "";
                if (root.TryGetProperty("task", out var t)) return TruncateLine(t.GetString() ?? "", 40);
            }
        }
        catch { }
        return TruncateLine(argsJson, 30);
    }

    /// <summary>
    /// Enforces token budget by sliding history window or compacting very old turns into a summary.
    /// </summary>
    public static void EnforceTokenBudget(List<ConversationMessage> history, int maxTokens = MaxHistoryTokens)
    {
        if (history.Count <= 4) return;

        int estimatedTokens = EstimateTokens(history);
        if (estimatedTokens <= maxTokens) return;

        Log.Info($"[HistoryLifecycle] History estimated at {estimatedTokens} tokens, exceeding budget of {maxTokens}. Compacting...");

        // Identify all user messages
        var userIndices = new List<int>();
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i].Role == MessageRole.User)
                userIndices.Add(i);
        }

        if (userIndices.Count <= 2) return;

        // Always keep the very first user message (turn 0) for original context
        // and the last MaxCompletedTurnsToKeepDetailed turns
        int cutoffTurn = Math.Max(1, userIndices.Count - MaxCompletedTurnsToKeepDetailed);
        if (cutoffTurn <= 1) return;

        int cutoffIndex = userIndices[cutoffTurn];

        var oldMessages = history.GetRange(0, cutoffIndex);
        var recentMessages = history.GetRange(cutoffIndex, history.Count - cutoffIndex);

        var summarySb = new StringBuilder();
        summarySb.AppendLine("[Summary of previous conversation context:]");

        foreach (var m in oldMessages)
        {
            if (m.Role == MessageRole.User)
            {
                summarySb.AppendLine($"- User asked: {TruncateLine(m.Text, 140)}");
            }
            else if (m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Text) && m.ToolCalls.Count == 0)
            {
                summarySb.AppendLine($"  Vanity replied: {TruncateLine(m.Text, 160)}");
            }
        }

        history.Clear();
        // Insert rolling summary as first message
        history.Add(ConversationMessage.FromUser(summarySb.ToString().Trim()));
        // Append recent detailed turns
        history.AddRange(recentMessages);

        Log.Info($"[HistoryLifecycle] History compacted. New estimated tokens: {EstimateTokens(history)}");
    }

    public static int EstimateTokens(IReadOnlyList<ConversationMessage> messages)
    {
        long chars = 0;
        foreach (var m in messages)
        {
            chars += m.Text.Length;
            foreach (var tc in m.ToolCalls)
                chars += tc.Name.Length + tc.ArgsJson.Length + 50;
            foreach (var tr in m.ToolResults)
            {
                chars += tr.Output.Length + tr.ToolName.Length + 50;
                if (!string.IsNullOrEmpty(tr.ScreenshotDataUrl))
                    chars += 4000;
            }
        }
        return (int)(chars / 3.8) + (messages.Count * 4);
    }

    private static string TruncateLine(string text, int max)
    {
        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "...";
    }
}
