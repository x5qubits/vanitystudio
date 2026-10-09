using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VanityStudio.Agent;
using VanityStudio.Auth;
using VanityStudio.Infra;
using VanityStudio.Llm;
using VanityStudio.Memory;
using VanityStudio.Tools;
using VanityStudio.Video;

namespace VanityStudio.Host;

/// <summary>The console: argument parsing, the first-run setup, the direct video commands (render, blocks, site, jobs,
/// doctor), the chat with its slash commands, the background video jobs' progress, and the rendering of what the
/// agent does while it works.</summary>
public sealed class ConsoleHost
{
    private const string Version = "1.0.1";

    private readonly Options _o;
    private string _workspace;
    private AiOptions _opts = new();
    private LlmRouter _router = null!;
    private ToolRegistry _tools = null!;
    private AgentLoop _loop = null!;
    private UsageTracker _usage = null!;
    private JsonFileMemoryStore _memory = null!;
    private MemoryAutoSave? _autoSave;
    private CancellationTokenSource? _turnCts;
    private PromptLibrary _library = null!;
    private PersonaDefinition? _persona;
    private string? _personaName;
    private readonly HashSet<string> _pinnedSkills = new(StringComparer.OrdinalIgnoreCase);
    private StudioProject _project = null!;
    private VideoJobs? _jobs;
    private string _request = "";
    private volatile bool _atPrompt;

    private sealed class Options
    {
        public string? Cwd;
        public string? Profile;
        public string? Model;
        public int MaxTurns = 60;
        public bool Sandbox;
        public bool Verbose;
        public bool NoMemory;
        public bool NoWait;
        public string? Login;
        public string? SiteLogin;
        public string? Persona;
        public bool Usage;
        public string? Command;
        public List<string> CommandArgs = [];
        public string? Out;
        public string? Format;
        public List<string> Clicks = [];
        public bool SiteLoginFlag;
        public List<string> Skills = [];
        public List<string> Deny = [];
        public List<string> Prompt = [];
        public bool Help, ShowVersion;
    }

    // ── entry ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static async Task<int> RunAsync(string[] args)
    {
        // A crash must leave a trace: the exception goes to the log and to stderr in full before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { Log.Error("UNHANDLED: " + e.ExceptionObject); Log.Flush(); } catch { }
            try { Console.Error.WriteLine("fatal: " + e.ExceptionObject); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { try { Log.Error("UNOBSERVED: " + e.Exception); } catch { } e.SetObserved(); };
        try { Console.OutputEncoding = Encoding.UTF8; Console.InputEncoding = Encoding.UTF8; } catch { }
        Options o;
        try { o = Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine("error: " + ex.Message); return 2; }
        if (o.Help) { PrintUsage(); return 0; }
        if (o.ShowVersion) { Console.WriteLine("vanity-studio " + Version); return 0; }

        AgentConfig.EnsureDirs();
        var imported = ImportOldConfig();
        try { if (!Directory.EnumerateFiles(Path.Combine(AgentConfig.Dir, "skills"), "*.md").Any()) PromptLibrary.ScaffoldGlobal(); } catch { }
        Log.Initialize(Path.Combine(AgentConfig.LogsDir, "vanity-studio.log"));
        Log.Verbose = o.Verbose;
        // The per-call prompt log (logs/_shared/prompts) carries project content; VANITY_STUDIO_PROMPT_LOG=0 turns it off.
        LlmCallLogger.Enabled = Environment.GetEnvironmentVariable("VANITY_STUDIO_PROMPT_LOG") is not ("0" or "false" or "off");
        Log.Info("vanity-studio " + Version + " starting");
        OAuthTokenRefresher.Persist = (_, p) => AgentConfig.PersistOAuth(p);
        OAuthTokenRefresher.Reload  = (_, name) => AgentConfig.ReloadProfile(name);
        VanityPathHelper.Sandbox = o.Sandbox;
        VanityPathHelper.DeniedPaths = o.Deny.ToArray();
        StudioOps.OwnProcess = ChildJob.Own;
        if (imported is not null) Dim("  " + imported);

        var host = new ConsoleHost(o);
        try
        {
            if (o.Login is not null)
            {
                await host.LoginAsync(o.Login, null, CancellationToken.None);
                return 0;
            }
            if (o.SiteLogin is not null) { host.SiteLoginWindow(o.SiteLogin, wait: true); return 0; }
            // the commands that need no model
            switch (o.Command)
            {
                case "blocks": return await host.BlocksAsync();
                case "site": return await host.SiteAsync();
                case "read": Console.WriteLine(await WebTool.ReadAsync(o.CommandArgs.FirstOrDefault() ?? "", o.Clicks, CancellationToken.None)); return 0;
                case "doctor": return await host.DoctorAsync();
                case "jobs": host.OpenProject(); host.PrintJobs(); return 0;
                case "docs": return await host.ApiDocsAsync();
                case "edit": host._opts = AgentConfig.Load(); return await host.EditCommandAsync();
                case "tool": host._opts = AgentConfig.Load(); return await host.RunToolAsync();
            }
            if (o.Command == "render")
            {
                // a script's voice lines and AI pictures need profiles; a script without them, or a doc, does not
                host._opts = AgentConfig.Load();
                return await host.RenderFileAsync();
            }
            if (!await host.EnsureProfilesAsync()) { HoldWindow(); return 2; }
            host.Build();
            if (o.Usage) { await host.PrintUsageStatsAsync(CancellationToken.None); return 0; }

            string? prompt = o.Prompt.Count > 0 ? string.Join(" ", o.Prompt) : null;
            if (prompt is null && Console.IsInputRedirected)
            {
                var piped = (await Console.In.ReadToEndAsync()).Trim();
                if (piped.Length > 0) prompt = piped;
            }
            if (prompt is not null) return await host.OneShotAsync(prompt);
            await host.ReplAsync();
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            Log.Error(ex);
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine("details: " + AgentConfig.LogsDir);
            HoldWindow();
            return 1;
        }
        finally { host._jobs?.Dispose(); Log.Flush(); }
    }

    /// <summary>First run of Vanity Studio on a machine that has the general agent's state: its profiles (keys, logins,
    /// settings) are copied over once, so nobody signs in twice. The old folder is left as it is.</summary>
    private static string? ImportOldConfig()
    {
        try
        {
            if (File.Exists(AgentConfig.ConfigFile) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VANITY_STUDIO_HOME"))) return null;
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vanity-agent", "config.json");
            if (!File.Exists(old)) return null;
            File.Copy(old, AgentConfig.ConfigFile);
            return $"[profiles imported from {old}]";
        }
        catch { return null; }
    }

    /// <summary>A window opened by double-clicking the exe closes the instant the process ends, taking the error
    /// with it. When the console is interactive, wait for Enter before leaving on a failure.</summary>
    private static void HoldWindow()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  Press Enter to close.");
            Console.ResetColor();
            Console.ReadLine();
        }
        catch { }
    }

    private static readonly string[] Commands = ["render", "blocks", "site", "read", "doctor", "jobs", "docs", "tool", "edit"];

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            // a direct command is the first word, and only in the form it takes (`render a reel for my shop` is a request)
            if (i == 0 && Commands.Contains(a))
            {
                var rest = args.Skip(1).Where(x => !x.StartsWith('-')).ToList();
                bool isCommand = a switch
                {
                    "render" => rest.Count >= 1 && rest[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase),
                    "site" or "read" => rest.Count >= 1 && Uri.TryCreate(rest[0], UriKind.Absolute, out var u) && u.Scheme is "http" or "https",
                    "tool" => rest.Count >= 1 && rest.Count <= 2,
                    "edit" => rest.Count == 1 && long.TryParse(rest[0].TrimStart('#'), out _),
                    _ => rest.Count == 0,
                };
                if (isCommand) { o.Command = a; continue; }
            }
            switch (a)
            {
                case "-h": case "--help": o.Help = true; break;
                case "-V": case "--version": o.ShowVersion = true; break;
                case "-C": case "--cwd": case "--workspace": o.Cwd = Next(); break;
                case "-P": case "--profile": o.Profile = Next(); break;
                case "-m": case "--model": o.Model = Next(); break;
                case "--max-turns": o.MaxTurns = int.Parse(Next()); break;
                case "--sandbox": o.Sandbox = true; break;
                case "-v": case "--verbose": o.Verbose = true; break;
                case "--no-memory": o.NoMemory = true; break;
                case "--no-wait": o.NoWait = true; break;
                case "--deny": o.Deny.Add(Next()); break;
                case "--login": o.Login = Next(); break;
                case "--site-login": o.SiteLogin = Next(); break;
                case "--persona": o.Persona = Next(); break;
                case "--usage": o.Usage = true; break;
                case "--skill": o.Skills.Add(Next()); break;
                case "-o": case "--out": o.Out = Next(); break;
                case "--format": o.Format = Next(); break;
                case "--click": case "--clicks": o.Clicks.AddRange(Next().Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--logged-in": o.SiteLoginFlag = true; break;
                case "-p": case "--print": if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) o.Prompt.Add(Next()); break;
                default:
                    if (o.Command is not null && !a.StartsWith('-')) { o.CommandArgs.Add(a); break; }
                    if (a.StartsWith('-') && o.Prompt.Count == 0) throw new ArgumentException("unknown option " + a + " (see --help)");
                    o.Prompt.Add(a); break;
            }
        }
        return o;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            vanity-studio - make and edit videos from the command line with Vanity Studio (photovideoeditor.com)

            usage:
              vanity-studio                              chat: ask for a video, change it, look at it (/help inside)
              vanity-studio "<request>"                  one request, then wait for its videos and exit
              vanity-studio render <script.json|doc.json> [-o dir]
                                                         render a video script or a Studio project without a chat
              vanity-studio blocks                       the Studio's scene blocks (what a script can use)
              vanity-studio read <url> [--click "A"]      what a page says: title, prices, product data, text, pictures
              vanity-studio site <url> [--click "A > B"] [--format reel] [--logged-in]
                                                         what a web page shows, as a tutorial step names it
              vanity-studio jobs                         the video jobs of this folder
              vanity-studio edit <n>                     open finished video n in the Studio's editor (its link is served until Enter)
              vanity-studio docs                         the Studio's video API reference (the project doc format)
              vanity-studio doctor                       check the browser, the Studio, ffprobe and the AI profiles
              vanity-studio tool <name> '<json>'         run one tool without a model (make_video, edit_video, files, brand,
                                                         read_file, memory), e.g. tool edit_video '{"action":"open","source":"3"}'

            options:
              -C, --cwd <dir>        work in <dir> (the project folder) instead of the current directory
              -P, --profile <name>   use this AI profile first (see /profiles)
              -m, --model <model>    use this model on the active profile for this run
              --no-wait              with a request: exit without waiting for the videos (they resume on the next run)
              --max-turns <n>        tool-call turns one request may take (default 60)
              --sandbox              only files inside the project folder may be used
              --deny <path>          a folder that must not be touched (repeatable)
              --no-memory            do not load or save project memory notes
              --login <provider>     sign in and exit: openai | grok | antigravity | anthropic
              --site-login <url>     open the login browser at <url>: log in to a site once for tutorial steps behind a login
              --usage                what is left on each login and what this project spent, then exit
              --persona <name>       run as a persona from .vanity-studio/personas or ~/.vanity-studio/personas
              --skill <name>         pin a skill for the session (repeatable)
              -v, --verbose          echo the diagnostic log to the console
              -V, --version          print the version
              -h, --help             this text

            a project folder holds media/ (your pictures and clips), videos/ (finished videos), edits/ (projects being
            edited) and .vanity-studio/ (brand.json, brand.md, instructions, personas, skills, jobs, memory).
            state lives in %USERPROFILE%\.vanity-studio (VANITY_STUDIO_HOME overrides it); VANITY_STUDIO_URL points at
            another Studio.
            """);
    }

    private ConsoleHost(Options o)
    {
        _o = o;
        _workspace = Path.GetFullPath(o.Cwd ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(_workspace)) throw new DirectoryNotFoundException(_workspace);
    }

    // ── setup ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At least one usable profile, or the first-run setup. False when the operator declined.</summary>
    private async Task<bool> EnsureProfilesAsync()
    {
        _opts = AgentConfig.Load();
        if (_opts.Profiles.Any(Usable)) return true;
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"No AI profile configured. Run `vanity-studio` interactively once, or `vanity-studio --login antigravity`, or create {AgentConfig.ConfigFile} (see config.example.json).");
            return false;
        }
        Dim("");
        Cyan("  Welcome to Vanity Studio. No AI provider is configured yet.");
        Dim($"  Config: {AgentConfig.ConfigFile}");
        Dim("  The model writes the video scripts; voice, AI pictures and AI clips use API-key profiles (see /roles after setup).");
        Dim("");
        var ok = await SetupWizardAsync(CancellationToken.None);
        _opts = AgentConfig.Load();
        return ok && _opts.Profiles.Any(Usable);
    }

    private static bool Usable(AiProfile p) =>
        p.Enabled && (p.ApiKeys.Length == 0 || p.ApiKeys.Any(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_")));

    private async Task<bool> SetupWizardAsync(CancellationToken ct)
    {
        Console.WriteLine("  How do you want to connect?");
        Console.WriteLine("    1  Google       - Antigravity sign in (subscription, browser; also draws AI pictures)");
        Console.WriteLine("    2  OpenAI       - sign in with ChatGPT (subscription, device code; also draws AI pictures)");
        Console.WriteLine("    3  OpenAI       - API key (also voice and AI pictures)");
        Console.WriteLine("    4  Gemini       - API key (also voice)");
        Console.WriteLine("    5  Anthropic    - API key or token");
        Console.WriteLine("    6  Grok (xAI)   - sign in (subscription, device code)");
        Console.WriteLine("    7  Grok (xAI)   - API key");
        Console.WriteLine("    8  Alibaba / DeepSeek / Mistral / Groq / OpenRouter / other OpenAI-compatible - API key");
        Console.WriteLine("    9  Ollama       - local, no key");
        Console.WriteLine("    0  quit");
        var choice = (await AskAsync("  Choice: ", ct) ?? "").Trim();
        try
        {
            switch (choice)
            {
                case "1": await LoginAsync("antigravity", null, ct); return true;
                case "2": await LoginAsync("openai", null, ct); return true;
                case "3": await KeyAsync("openai", [], ct); return true;
                case "4": await KeyAsync("gemini", [], ct); return true;
                case "5": await LoginAsync("anthropic", null, ct); return true;
                case "6": await LoginAsync("grok", null, ct); return true;
                case "7": await KeyAsync("grok", [], ct); return true;
                case "8": await KeyAsync(null, [], ct); return true;
                case "9": await KeyAsync("ollama", [], ct); return true;
                default: return false;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Red("  " + ex.Message); return false; }
    }

    /// <summary>The project and its job runner: started once per folder, the jobs it left resume here.</summary>
    private void OpenProject()
    {
        if (_jobs is not null && string.Equals(_jobs.Project.Root, _workspace, StringComparison.OrdinalIgnoreCase)) return;
        _jobs?.Dispose();
        _project = new StudioProject(_workspace);
        _project.AdoptHomeState();
        _jobs = new VideoJobs(_project, () => _opts);
        _jobs.OnEvent += JobEvent;
        _jobs.OnRenderLog = line => { if (Log.Verbose) Print(ConsoleColor.DarkGray, "  " + line); };
        _jobs.EditLink = id => EditServer().EditLink(id);
        _jobs.Start();
        var open = _jobs.Store.All().Where(j => j.Open).ToList();
        _openNote = _jobs.Passive ? "  [another Vanity Studio session is open in this folder: it makes the videos asked for here, and shows their progress]"
                  : open.Count > 0 ? $"  [{open.Count} video job(s) of this folder resume: {string.Join(", ", open.Select(j => "#" + j.Id))}]" : null;
        if (_started) PrintOpenNote();
    }

    // Until the banner (or a command's first line) is out, the resume note and the jobs' events wait: they came above
    // the banner and in the middle of it (2026-10-09).
    private string? _openNote;
    private volatile bool _started;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(VideoJob Job, string Line)> _heldEvents = new();

    private void PrintOpenNote()
    {
        if (_openNote is { } note) Print(ConsoleColor.DarkGray, note);
        _openNote = null;
    }

    /// <summary>The session is on screen: the resume note, then what the jobs said meanwhile, then everything as it comes.</summary>
    private void Started()
    {
        PrintOpenNote();
        lock (_heldEvents)
        {
            _started = true;
            while (_heldEvents.TryDequeue(out var e)) JobEvent(e.Job, e.Line);
        }
    }

    private void Build()
    {
        _opts = AgentConfig.Load();
        if (_o.Profile is not null) AgentConfig.MoveFirst(_opts, _o.Profile);
        if (_o.Model is not null && _opts.Profiles.Count > 0)
        {
            var p = _opts.Profiles[0];
            p.Models = new[] { _o.Model }.Concat(p.Models.Where(m => !m.Equals(_o.Model, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        foreach (var p in _opts.Profiles) if (p.Layers.Length == 0) p.Layers = ["any"];
        _router = new LlmRouter(_opts, LlmRouter.CallLogger);
        OpenProject();

        // Per-workspace state: in the project's own .vanity-studio folder when it exists (so it travels with the
        // project), otherwise under the studio home.
        var project = PromptLibrary.HasProjectDir(_workspace) ? PromptLibrary.ProjectDir(_workspace) : AgentConfig.ProjectDir(_workspace);
        _usage  = new UsageTracker(Path.Combine(project, "usage.json"));
        _memory = new JsonFileMemoryStore(Path.Combine(project, "memory.json"));
        // The smart memory: after every turn that used tools, a background model call records what the steps proved.
        _autoSave = _o.NoMemory ? null : new MemoryAutoSave(_memory, SideCallAsync, line => { if (Log.Verbose) Dim("  [" + line + "]"); else Log.Info("[memory] " + line); });

        _library = PromptLibrary.Load(_workspace);
        _personaName ??= _o.Persona;
        _persona = _library.Persona(_personaName);
        if (_personaName is not null && _persona is null)
        {
            Red($"  no persona '{_personaName}' (/personas lists them); running as the default videographer");
            _personaName = null;
        }
        foreach (var s in _o.Skills) if (_library.Skill(s) is not null) _pinnedSkills.Add(s); else Red($"  no skill '{s}' (/skills lists them)");
        _tools  = BuildTools(_persona);

        var keep = _loop?.History.ToList();
        var maxTurns = _persona is { MaxTurns: > 0 } ? _persona.MaxTurns : _o.MaxTurns;
        _loop = new AgentLoop(new LayerClient(this), _tools, "", ConsoleEvents(), _usage, maxTurns, "main", Guid.NewGuid().ToString("N"), keep);
    }

    private ToolRegistry BuildTools(PersonaDefinition? persona)
    {
        var tools = new ToolRegistry();
        tools.Register(new MakeVideoTool(_project, _jobs!, () => _opts, () => _request));
        tools.Register(new EditVideoTool(_project, _jobs!, () => _opts));
        tools.Register(new FilesTool(_project));
        tools.Register(new WebTool(_project));
        tools.Register(new BrandTool(_project));
        tools.Register(new ReadFileTool(_workspace));
        tools.Register(new SkillViewTool(() => _library));
        if (!_o.NoMemory) tools.Register(new MemoryTool(_memory));

        // A persona with a `tools:` list gets exactly those (plus skill_view, which is harmless).
        if (persona is { Tools.Length: > 0 })
        {
            var allowed = new ToolRegistry();
            foreach (var t in tools.Tools)
                if (persona.Tools.Contains(t.Definition.Name, StringComparer.OrdinalIgnoreCase) || t.Definition.Name is "skill_view")
                    allowed.Register(t);
            tools = allowed;
        }
        return tools;
    }

    /// <summary>The skills inlined in the prompt (the persona's, the pinned ones, the `always` ones) and the rest,
    /// which the model may load with skill_view.</summary>
    private (List<SkillDefinition> Active, List<SkillDefinition> Loadable) SkillsFor(PersonaDefinition? persona, bool includePinned)
    {
        var active = new List<SkillDefinition>();
        foreach (var s in _library.Skills)
        {
            bool pinned = s.Always
                || (persona?.Skills.Contains(s.Name, StringComparer.OrdinalIgnoreCase) ?? false)
                || (includePinned && _pinnedSkills.Contains(s.Name));
            if (pinned) active.Add(s);
        }
        var loadable = _library.Skills.Where(s => !active.Contains(s)).ToList();
        return (active, loadable);
    }

    private AgentEvents ConsoleEvents()
    {
        const string pad = "  ";
        return new AgentEvents
        {
            OnStep = (iter, max) => { EndStream(); SpinStart(iter == 1 ? "waiting for " + (ActiveModel() ?? "the model") : $"waiting for {ActiveModel()} (step {iter})"); },
            OnModel = (_, _) => { SpinStop(); EndStream(); },
            // one quiet line per step; a step that worked says nothing more (-v shows its size and time)
            OnToolCall = (tool, preview) => Print(ConsoleColor.DarkGray, $"{pad}· {StepText(tool, preview)}"),
            OnToolResult = (tool, sec, chars, err) =>
            {
                if (!err && Log.Verbose) Dim($"{pad}  ← {tool} {sec.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s, {chars:N0} chars");
            },
            // a refused step is the model's to fix, not the operator's to read: it goes to the log, and on screen only with -v
            // (refusals printed in yellow looked like the product failing, 2026-10-09)
            OnToolError = (tool, text) =>
            {
                Log.Info($"[refused] {tool}: {Clip(text ?? "", 600)}");
                if (!Log.Verbose) return;
                var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('-').Trim()).Where(l => l.Length > 0).ToList();
                if (lines.Count > 0 && lines[0].StartsWith("Error:", StringComparison.Ordinal)) lines[0] = lines[0][6..].Trim();
                // "The script was not queued: 2 problem(s)..." is the header; the problems are what matter
                var reasons = lines.Count > 1 && lines[0].Contains("not queued") ? lines.Skip(1).TakeWhile(l => !l.StartsWith("Fixed in")).ToList() : lines.Take(1).ToList();
                foreach (var l in reasons.Take(3)) Print(ConsoleColor.DarkYellow, $"{pad}  ✗ {Clip(l, 150)}");
                if (reasons.Count > 3) Print(ConsoleColor.DarkYellow, $"{pad}    and {reasons.Count - 3} more");
            },
            // Reasoning that already streamed live is not printed a second time when the call returns.
            OnThought = text => { if (_streamedThought > 0) return; lock (ConsoleGate) { Console.ForegroundColor = ConsoleColor.DarkGray; foreach (var line in Wrap(text.Trim(), 110)) Console.WriteLine(pad + line); Console.ResetColor(); } },
        };
    }

    // ── the video jobs' progress, printed as it happens ──────────────────────────────────────────────────────────

    // what a job already said, so a stage is announced once ("rendering…", not every 10 %)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _jobSaid = new();

    private void JobEvent(VideoJob job, string line)
    {
        if (!_started)
            lock (_heldEvents)
                if (!_started) { _heldEvents.Enqueue((job, line)); return; }
        if (line.StartsWith("✓") || line.StartsWith("✗")) { PrintFinished(job); return; }
        if (Log.Verbose) { Print(ConsoleColor.DarkMagenta, $"  #{job.Id} {line}"); return; }
        // the progress worth a line: rendering started, the site being captured, a made picture or voice that failed,
        // a provider that is busy or out of quota
        string? say = null;
        var stage = System.Text.RegularExpressions.Regex.Match(line, @"^(capture|compile|check|render|sheet|collect|fonts|running|waiting)\b").Groups[1].Value;
        if (stage is "render" or "compile" or "check" or "fonts" or "running") say = "rendering…";
        else if (stage == "capture") say = "capturing the site's screens…";
        else if (stage == "waiting") say = "waiting for the renderer…";
        else if (line.StartsWith("making a still")) say = "making AI pictures…";
        else if (line.StartsWith("making a clip")) say = "making AI clips…";
        else if (line.StartsWith("voice ") && line.Contains("spoken")) say = "recording the voice-over…";
        // a provider's refusal while the job works around it (another profile, a fallback) is the log's; what the video
        // lost in the end is in its finished block
        else if (line.Contains("could not") || line.Contains("used up") || line.Contains("went wrong") || line.Contains("failed")) return;
        if (say is null || (_jobSaid.TryGetValue(job.Id, out var had) && had == say)) return;
        _jobSaid[job.Id] = say;
        Print(ConsoleColor.Magenta, $"  #{job.Id} {say}");
    }

    /// <summary>A finished job, short: title and length, the folder, one line per scene, what fell back, and the link
    /// that opens it in the Studio's editor (the long report with every path goes to the conversation, not the screen).</summary>
    private void PrintFinished(VideoJob job)
    {
        _jobSaid.TryRemove(job.Id, out _);
        // one block, written at once: line by line, the prompt written back after each line came between them
        // ("You:     1 title-card ...", 2026-10-09)
        var block = new List<(ConsoleColor, string)> { (ConsoleColor.Gray, "") };
        try { Finished(job, (color, text) => block.Add((color, text)), text => block.Add((ConsoleColor.DarkGray, text))); }
        finally { PrintBlock(block); }
    }

    private void Finished(VideoJob job, Action<ConsoleColor, string> print, Action<string> dim)
    {
        if (job.Status != VideoJob.Done)
        {
            print(ConsoleColor.Red, $"  ✗ {job.Title} · job #{job.Id} was not made");
            print(ConsoleColor.DarkYellow, $"    {Clip(VideoText.ErrorText(job.Error ?? "it stopped without a reason"), 300)}");
            return;
        }
        var script = VideoText.ParseObject(job.ScriptJson);
        var report = VideoText.ParseObject(job.ReportJson ?? "{}");
        var format = VideoText.Str(script["format"]) ?? (job.Kind == "doc" ? "project" : "reel");
        var secs = (VideoText.Num(report["duration"]) ?? job.Duration).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        print(ConsoleColor.Green, $"  ✓ {job.Title} · {secs} s · {format} · job #{job.Id}");
        var folder = job.OutDir is null ? "" : job.OutDir.Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        print(ConsoleColor.Gray, $"    {folder,-52} /open {job.Id} · /folder {job.Id}");
        var scenes = (report["scenes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var written = (script["scenes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        for (int i = 0; i < Math.Max(scenes.Count, written.Count); i++)
        {
            var r = i < scenes.Count ? scenes[i] : null; var w = i < written.Count ? written[i] : null;
            var block = VideoText.Str(r?["block"]) ?? VideoText.Str(w?["block"]) ?? "";
            var line = (VideoText.Str(r?["line"]) ?? VideoText.Str(w?["line"]) ?? "").Replace("*", "");
            dim($"    {i + 1} {block,-16} {Clip(line, 90)}");
        }
        // what did not come out as written: a scene that fell back, a picture that could not be made
        var notes = (report["notes"] as JsonArray ?? []).Concat(VideoText.ParseObject(job.AssetsJson)["notes"] as JsonArray ?? [])
            .Select(VideoText.Text).Where(t => t is { Length: > 0 }).Select(t => VideoText.ErrorText(t!)).Distinct().ToList();
        foreach (var n in notes.Take(3)) dim($"    note: {Clip(Calm(n), 150)}");
        if (notes.Count > 3) dim($"    note: and {notes.Count - 3} more (/job {job.Id})");
        if (job.Project is not null)
        {
            print(ConsoleColor.Cyan, "    Edit in Vanity Studio (Ctrl+click or copy):");
            print(ConsoleColor.Cyan, $"    {EditServer().EditLink(job.Id)}");
            // a command that exits takes the link server with it
            if (_exitsAfterRender) dim($"    (the link needs Vanity Studio running: vanity-studio edit {job.Id} serves it and opens it)");
        }
    }

    /// <summary>A note on a finished video as the operator reads it: what the video lost, without the picture's prompt and the
    /// provider's own words (those are in the log and in /job N).</summary>
    private static string Calm(string note) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(note, @"\s*\(""[^""]*""\)", ""),
            @"could not be made:.*?(?=\s*The scene uses|$)", "could not be made.", System.Text.RegularExpressions.RegexOptions.Singleline);

    private bool _exitsAfterRender;

    private static string Kilo(long n) => n >= 1000 ? (n / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The model's answer as the console shows it: its markdown read, not printed (headings and **bold** in
    /// white, `code` in cyan, bullets as •), wrapped to the window, under "Vanity:".</summary>
    private void PrintReply(string reply)
    {
        lock (ConsoleGate)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Vanity:");
            Console.ResetColor();
            var width = Console.IsOutputRedirected ? 110 : Math.Max(60, Math.Min(120, Console.WindowWidth - 4));
            foreach (var raw in (reply ?? "").Replace("\r", "").Trim().Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) { Console.WriteLine(); continue; }
                var heading = System.Text.RegularExpressions.Regex.Match(line, @"^\s*#{1,6}\s+(.*)$");
                if (heading.Success) { Console.ForegroundColor = ConsoleColor.White; Console.WriteLine("  " + heading.Groups[1].Value.Replace("**", "")); Console.ResetColor(); continue; }
                var indent = "  ";
                var bullet = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)[-*+]\s+(.*)$");
                if (bullet.Success) { indent = "  " + bullet.Groups[1].Value + "• "; line = bullet.Groups[2].Value; }
                var first = true;
                foreach (var part in Wrap(line, width - indent.Length))
                {
                    Console.Write(first ? indent : new string(' ', indent.Length));
                    WriteInline(part);
                    Console.WriteLine();
                    first = false;
                }
            }
        }
    }

    // **bold** in white, `code` in cyan, the rest in the normal colour; the markers themselves are not printed
    private static void WriteInline(string text)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\*\*(.+?)\*\*|`([^`]+)`|\*(?!\s)([^*]+?)\*|([^*`]+|[*`])"))
        {
            if (m.Groups[1].Success) { Console.ForegroundColor = ConsoleColor.White; Console.Write(m.Groups[1].Value); }
            else if (m.Groups[2].Success) { Console.ForegroundColor = ConsoleColor.Cyan; Console.Write(m.Groups[2].Value); }
            else if (m.Groups[3].Success) { Console.ForegroundColor = ConsoleColor.White; Console.Write(m.Groups[3].Value); }
            else { Console.ResetColor(); Console.Write(m.Groups[4].Value); }
            Console.ResetColor();
        }
    }

    private static string Clip(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    /// <summary>A step as the operator reads it: what the tool does with what, not its arguments.</summary>
    private static string StepText(string tool, string preview)
    {
        preview = Clip(preview, 90);
        return tool switch
        {
            "web" => preview.StartsWith("http") ? "reading " + preview : preview.Contains("download") ? "downloading pictures" : "web " + preview,
            "make_video" => preview switch
            {
                "blocks" => "reading the Studio's scenes",
                "submit" => "sending the script",
                "status" => "checking the jobs",
                "remix" => "remixing",
                "look" => "looking at a picture",
                _ when preview.StartsWith("http") => "walking " + preview,
                _ => "make_video " + preview,
            },
            "edit_video" => "editing the project" + (preview.Length > 0 ? ": " + preview : ""),
            "brand" => preview == "read" ? "reading the brand" : "saving the brand",
            "files" => "looking for files" + (preview.Length > 0 && preview != "list" ? ": " + preview : ""),
            "read_file" => "reading " + Path.GetFileName(preview.TrimEnd('…')),
            "memory" => preview.StartsWith("save") ? "saving a note" : preview.StartsWith("delete") ? "forgetting a note" : "reading notes",
            "skill_view" => "reading a playbook",
            _ => tool + (preview.Length > 0 ? " " + preview : ""),
        };
    }

    /// <summary>One line from any thread: the spinner's line is cleared first, and the prompt is written again when the
    /// operator was at it.</summary>
    private void Print(ConsoleColor color, string text) => PrintBlock([(color, text)]);

    /// <summary>Lines from any thread, written together: the spinner's or the prompt's line is cleared once, and the
    /// prompt is written back once, after the last line.</summary>
    private void PrintBlock(IReadOnlyList<(ConsoleColor Color, string Text)> lines)
    {
        lock (ConsoleGate)
        {
            if (!Console.IsOutputRedirected) Console.Write("\r" + new string(' ', Math.Max(0, Math.Min(Console.BufferWidth - 1, 100))) + "\r");
            if (_streamKind is not null) { Console.WriteLine(); _streamKind = null; }
            foreach (var (color, text) in lines)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text);
            }
            Console.ResetColor();
            if (_atPrompt) { Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("You: "); Console.ResetColor(); }
        }
    }

    // ── live thoughts: the model's reasoning as it streams (providers that stream it; others show it at the end) ──

    private int _streamedThought;
    private string? _streamKind;
    private int _streamCol;

    private void OnDelta(string kind, string chunk)
    {
        if (AgentToolContext.Depth != 0 || Console.IsOutputRedirected || kind != "thought") return;
        lock (ConsoleGate)
        {
            if (_streamKind is null)
            {
                SpinStop();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  · ");
                _streamKind = kind; _streamCol = 4;
            }
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var ch in chunk.Replace("\r", ""))
            {
                if (ch == '\n') { Console.Write("\n    "); _streamCol = 4; continue; }
                if (_streamCol >= 110 && ch == ' ') { Console.Write("\n    "); _streamCol = 4; continue; }
                Console.Write(ch); _streamCol++;
            }
            Console.ResetColor();
            _streamedThought += chunk.Length;
        }
    }

    private void EndStream()
    {
        lock (ConsoleGate)
        {
            if (_streamKind is not null) { Console.WriteLine(); _streamKind = null; }
        }
    }

    private string? ActiveModel() => ActiveProfile()?.Models.FirstOrDefault();

    private AiProfile? ActiveProfile() => _opts.Profiles.FirstOrDefault(p => Usable(p) && !LlmRouter.MediaOnly(p));

    /// <summary>The memory block for this request: the notes whole when the store is small, a model-filtered
    /// selection when it is large. Waits briefly for the previous turn's analysis so its facts are included.</summary>
    private async Task<string?> MemoryBlockAsync(string? task, CancellationToken ct)
    {
        if (_o.NoMemory) return null;
        await MemoryAutoSave.WhenSavedAsync(_memory, TimeSpan.FromSeconds(4), ct);
        var block = await PromptMemory.BlockAsync(_memory, task, SideCallAsync, ct);
        return block.Length == 0 ? null : block;
    }

    /// <summary>A short side call on the configured provider (the memory analyzer, judge and filter): no tools, a
    /// hard output cap, the same failover as every other call.</summary>
    private async Task<string> SideCallAsync(string system, string user, CancellationToken ct)
    {
        var before = LlmCallScope.MaxOutputTokens.Value;
        LlmCallScope.MaxOutputTokens.Value = 2500;
        var prevDelta = LlmCallScope.OnDelta.Value;
        LlmCallScope.OnDelta.Value = null;   // never stream a side call's reasoning to the console
        try
        {
            var r = await _router.CallWithLayerAsync("any", system, [ConversationMessage.FromUser(user)], [], ct);
            _usage?.Track(r.PromptTokens ?? 0, r.CompletionTokens ?? 0, r.CachedTokens ?? 0, r.ModelName);
            return r.Text ?? "";
        }
        finally { LlmCallScope.MaxOutputTokens.Value = before; LlmCallScope.OnDelta.Value = prevDelta; }
    }

    /// <summary>The loop's client: every call goes to the current router (rebuilt after a config change) on the one layer.</summary>
    private sealed class LayerClient(ConsoleHost host) : ILlmClient
    {
        public Task<LlmResponse> CallAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
            => host._router.CallWithLayerAsync("any", systemPrompt, history, tools, ct);
        public Task<LlmResponse> CallWithLayerAsync(string layer, string systemPrompt, IReadOnlyList<ConversationMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken ct, long userId = 0)
            => host._router.CallWithLayerAsync(layer, systemPrompt, history, tools, ct);
    }

    // ── direct commands (no chat) ────────────────────────────────────────────────────────────────────────────────

    private async Task<int> BlocksAsync()
    {
        var (cat, error) = await MakeVideoTool.CatalogAsync(CancellationToken.None);
        if (cat is null) { Red("  " + error); return 1; }
        Console.WriteLine(MakeVideoTool.Describe(cat));
        return 0;
    }

    /// <summary>`tool <name> '<json>'`: one tool call without a model, for scripts and checks. A picture the tool
    /// returns is saved next to the project's state and its path printed.</summary>
    private async Task<int> RunToolAsync()
    {
        OpenProject();
        _usage = new UsageTracker(Path.Combine(AgentConfig.ProjectDir(_workspace), "usage.json"));
        _memory = new JsonFileMemoryStore(Path.Combine(PromptLibrary.HasProjectDir(_workspace) ? PromptLibrary.ProjectDir(_workspace) : AgentConfig.ProjectDir(_workspace), "memory.json"));
        _library = PromptLibrary.Load(_workspace);
        var tools = BuildTools(null);
        var name = _o.CommandArgs.ElementAtOrDefault(0) ?? "";
        var args = _o.CommandArgs.ElementAtOrDefault(1) ?? "{}";
        if (!tools.TryGet(name, out var tool) || tool is null) { Red($"  no tool '{name}'; the tools are: {string.Join(", ", tools.All.Select(t => t.Name))}"); return 2; }
        ToolResultRecord r = tool is IVisualTool v ? await v.ExecuteVisualAsync("cli", args) : new ToolResultRecord { Output = await tool.ExecuteAsync(args) };
        Console.WriteLine(r.Output);
        foreach (var (img, i) in (r.ImageDataUrls ?? (r.ScreenshotDataUrl is null ? [] : [r.ScreenshotDataUrl])).Select((x, i) => (x, i)))
        {
            var comma = img.IndexOf(',');
            var ext = img.StartsWith("data:image/jpeg") ? ".jpg" : ".png";
            var file = Path.Combine(AgentConfig.ProjectDir(_workspace), $"tool-image-{i + 1}{ext}");
            File.WriteAllBytes(file, Convert.FromBase64String(img[(comma + 1)..]));
            Dim("  picture: " + file);
        }
        // a job the tool queued is waited for, as a one-shot request would
        var open = _jobs!.Store.All().Where(j => j.Open).Select(j => j.Id).ToList();
        if (open.Count > 0 && !_o.NoWait) await _jobs.WaitIdleAsync(CancellationToken.None);
        return r.IsError || r.Output.StartsWith("Error") ? 1 : 0;
    }

    private async Task<int> ApiDocsAsync()
    {
        try { Console.WriteLine(await EditVideoTool.DocsAsync(CancellationToken.None)); return 0; }
        catch (Exception ex) { Red("  " + ex.Message); return 1; }
    }

    private async Task<int> SiteAsync()
    {
        var url = _o.CommandArgs.FirstOrDefault() ?? "";
        try { Console.WriteLine(await MakeVideoTool.SiteTextAsync(url, _o.Clicks, _o.SiteLoginFlag, (_o.Format ?? "landscape").ToLowerInvariant(), CancellationToken.None)); return 0; }
        catch (Exception ex) { Red("  " + ex.Message); return 1; }
    }

    /// <summary>`render file.json`: a make_video script (validated, its voice and AI pictures made) or a Studio doc,
    /// rendered here and waited for; the files land in videos/ (or --out).</summary>
    private async Task<int> RenderFileAsync()
    {
        _exitsAfterRender = true;
        var file = Path.GetFullPath(_o.CommandArgs[0]);
        if (!File.Exists(file)) { Red("  no such file: " + file); return 2; }
        OpenProject();
        Started();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var text = await File.ReadAllTextAsync(file);
        JsonObject? node;
        try { node = MakeVideoTool.ParseScript(text) as JsonObject; }
        catch (JsonException ex) { Red("  " + MakeVideoTool.ScriptError(text, ex)); return 2; }
        if (node is null) { Red("  the file is not one JSON object"); return 2; }
        List<long> ids;
        // a script is scenes made of blocks (with or without its "script": 1); anything else is a Studio project doc
        if (node["script"] is not null || (node["scenes"] is JsonArray ss && ss.OfType<JsonObject>().Any(s => s["block"] is not null)))
        {
            var tool = new MakeVideoTool(_project, _jobs!, () => _opts, () => "");
            var (queued, problem, fixes) = await tool.QueueScriptAsync(text, "", cts.Token);
            foreach (var f in fixes) Dim("  fixed: " + f);
            if (problem is not null) { Red("  " + problem); return 1; }
            ids = queued;
        }
        else
        {
            var title = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(file), @"(\.vstudio)?\.json$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            ids = [_jobs!.QueueDoc(file, title)];
        }
        Dim($"  queued {string.Join(", ", ids.Select(i => "#" + i))}; rendering (Ctrl+C stops waiting, the job resumes on the next run)");
        try { await _jobs!.WaitIdleAsync(cts.Token, ids); }
        catch (OperationCanceledException) { return 130; }
        var jobs = ids.Select(i => _jobs.Store.Get(i)).Where(j => j is not null).Cast<VideoJob>().ToList();
        if (_o.Out is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            foreach (var j in jobs.Where(j => j.OutDir is not null))
                foreach (var f in Directory.GetFiles(Path.Combine(_workspace, j.OutDir!)))
                    File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true);
            Dim("  copied to " + Path.GetFullPath(outDir));
        }
        return jobs.All(j => j.Status == VideoJob.Done) ? 0 : 1;
    }

    private async Task<int> DoctorAsync()
    {
        int problems = 0;
        void Okay(string s) => Print(ConsoleColor.Green, "  ✓ " + s);
        void Bad(string s, string fix) { problems++; Print(ConsoleColor.Red, "  ✗ " + s); Dim("      " + fix); }
        void Warn(string s, string fix) { Print(ConsoleColor.Yellow, "  ! " + s); Dim("      " + fix); }
        var exe = StudioOps.FindBrowser();
        if (exe is null) Bad("no Chrome or Edge", "install Google Chrome (or set the BrowserPath setting: /set BrowserPath <path to chrome>)");
        else Okay("browser: " + StudioOps.BrowserLabel(exe) + " (" + exe + ")");
        var (cat, error) = await MakeVideoTool.CatalogAsync(CancellationToken.None);
        if (cat is null) Bad("Studio: " + error, "check the connection to " + MakeVideoTool.StudioUrl());
        else Okay($"Studio: {MakeVideoTool.StudioUrl()} · catalog v{cat.Version}, {cat.Blocks.Count} blocks, {cat.Looks.Count} looks, {cat.Music.Count} music beds");
        if (VideoText.HasFfprobe()) Okay("ffprobe: found (voice lines are measured exactly)");
        else Warn("ffprobe: not found", "voice lines are measured from their WAV header; install ffmpeg for MP3 voices");
        _opts = AgentConfig.Load();
        var chat = ActiveProfile();
        if (chat is null) Bad("no AI profile to write scripts", "vanity-studio --login antigravity (or /login, /key in the chat)");
        else Okay($"scripts: {chat.Name} ({chat.Provider}/{chat.Models.FirstOrDefault()})");
        PrintRoles(indent: "  ");
        Dim("  login browser (tutorial steps behind a login): " + (Directory.Exists(StudioOps.LoginProfile) ? StudioOps.LoginProfile : "not set up (vanity-studio --site-login <url>)"));
        Dim("  render jobs: " + StudioOps.Root + $" · {StudioOps.MaxParallel} at a time (VANITY_VIDEO_PARALLEL)");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>What this machine can make right now, for the model: voice, AI pictures, AI clips, each yes or no with
    /// what to do instead. A script that asked for a voice nobody can speak, or pictures past a used-up quota, was refused
    /// at submit and cost a round trip (2026-10-09).</summary>
    private string MediaState()
    {
        var voice = VoiceMaker.Candidates(_opts);
        var stills = ImageMaker.Candidates(_opts);
        var free = stills.Where(c => !VideoJobs.IsSpent(c.Name, out _)).ToList();
        var clips = ClipMaker.Candidates(_opts);
        var sb = new StringBuilder();
        sb.AppendLine(voice.Count > 0
            ? $"- voice-over: yes ({string.Join(", ", voice.Select(v => v.Name))})"
            : "- voice-over: NO. No profile can speak: write \"voice\": false; music and the words on screen carry the video.");
        if (free.Count > 0) sb.AppendLine($"- AI pictures ({{\"make\": \"still\"}}): yes ({string.Join(", ", free.Select(p => p.Name))})");
        else if (stills.Count > 0)
            sb.AppendLine("- AI pictures: NO until " + string.Join(", ", stills.Select(c => { VideoJobs.IsSpent(c.Name, out var u); return $"{u:HH:mm} ({c.Name}'s image quota is used up)"; })) +
                          ". Use the site's own pictures (web download), its screens (screen-demo), the project's files, or blocks that need no picture.");
        else sb.AppendLine("- AI pictures: NO image profile. Use the site's own pictures (web download), its screens (screen-demo), the project's files, or blocks that need no picture.");
        sb.AppendLine(clips.Count > 0
            ? $"- AI clips ({{\"make\": \"clip\"}}): yes ({string.Join(", ", clips.Select(c => c.Name))})"
            : "- AI clips: NO (no Alibaba key): use the operator's own clips, or blocks that need none.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The Studio's blocks, one line each, for the instructions (the catalog is cached ten minutes); none when the
    /// Studio cannot be reached, and then make_video action=blocks says why.</summary>
    private static async Task<string?> BlocksBriefAsync(CancellationToken ct)
    {
        try { var (cat, _) = await MakeVideoTool.CatalogAsync(ct); return cat is null ? null : MakeVideoTool.Brief(cat); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("[blocks] " + ex.Message); return null; }
    }

    private void PrintRoles(string indent = "  ")
    {
        var voice = VoiceMaker.Candidates(_opts);
        var stills = ImageMaker.Candidates(_opts);
        var clips = ClipMaker.Candidates(_opts);
        var chat = ActiveProfile();
        Console.WriteLine(indent + "chat   (the agent thinks)    " + (chat is null ? "none: /login or /key" : $"{chat.Name} ({chat.Provider}/{ActiveModel()})"));
        Console.WriteLine(indent + "voice  (\"voice\": true)      " + (voice.Count > 0 ? string.Join(" → ", voice.Select(v => $"{v.Name} ({v.ProviderId}/{v.Model}{(v.Models.Count > 1 ? $", then {v.Models.Count - 1} more models" : "")}, {v.Keys.Count} key{(v.Keys.Count == 1 ? "" : "s")})")) : "none: /voice <profile> on a Gemini, OpenAI or Alibaba API-key profile"));
        Console.WriteLine(indent + "stills ({\"make\":\"still\"})   " + (stills.Count > 0 ? string.Join(" → ", stills.Select(p => $"{p.Name} ({p.Provider})")) : "none: an OpenAI profile (key or login), the Antigravity login or an Alibaba key"));
        Console.WriteLine(indent + "clips  ({\"make\":\"clip\"})    " + (clips.Count > 0 ? string.Join(" → ", clips.Select(c => $"{c.Name} ({c.Model})")) : "none: /key alibaba (DashScope) or DASHSCOPE_API_KEY"));
    }

    private void SiteLoginWindow(string url, bool wait)
    {
        var p = StudioOps.OpenLoginWindow(url);
        Cyan($"  The login browser is open at {url}.");
        Dim("  Log in there, then close that window: tutorial steps with login=true use this session (profile: " + StudioOps.LoginProfile + ").");
        if (!wait) return;
        try { p.WaitForExit(); } catch { }
        Dim("  [login window closed]");
    }

    // ── running ──────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> OneShotAsync(string prompt)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Started();
        var reply = await TurnAsync(prompt, cts.Token, printReply: false);
        if (reply is null) return 1;
        Console.WriteLine(reply);
        var open = _jobs!.Store.All().Where(j => j.Open).Select(j => j.Id).ToList();
        if (open.Count > 0 && !_o.NoWait)
        {
            Dim($"  waiting for {open.Count} video job(s): {string.Join(", ", open.Select(i => "#" + i))} (Ctrl+C stops waiting; the jobs resume on the next run)");
            try { await _jobs.WaitIdleAsync(cts.Token); } catch (OperationCanceledException) { }
        }
        await MemoryAutoSave.WhenSavedAsync(_memory, TimeSpan.FromSeconds(60), CancellationToken.None);   // let the analysis land before exit
        var done = _jobs.Store.All().Where(j => open.Contains(j.Id)).ToList();
        return done.Any(j => j.Status == VideoJob.Failed) ? 1 : 0;
    }

    private async Task ReplAsync()
    {
        var p = ActiveProfile();
        Console.WriteLine();
        Cyan("  Vanity Studio " + Version + " · " + _workspace);
        Dim($"  profile: {p?.Name} ({p?.Provider}/{ActiveModel()})" + (OAuthTokenRefresher.UsesOAuth(p!) ? " · signed in" + (string.IsNullOrEmpty(p!.OAuthAccountId) ? "" : " as " + p.OAuthAccountId) : " · api key"));
        PrintContextLine();
        PrintOpenNote();
        Dim("  Ask for a video (\"a 20 s reel for my bakery, use the photos on my desktop\") · /help for commands · Ctrl+C stops the current request");
        Console.WriteLine();
        Started();

        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C during a request stops it; at the prompt it ends the program (open jobs resume next time).
            var cts = _turnCts;
            if (cts is { IsCancellationRequested: false }) { e.Cancel = true; cts.Cancel(); Dim("  [stopping…]"); }
            else { Log.Flush(); }
        };

        while (true)
        {
            lock (ConsoleGate)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("You: ");
                Console.ResetColor();
                _atPrompt = true;
            }
            var input = Console.ReadLine();
            _atPrompt = false;
            if (input is null) break;
            input = input.Trim();
            if (input.Length == 0) continue;
            if (input.StartsWith('/'))
            {
                if (!await CommandAsync(input)) break;
                continue;
            }
            using var cts = new CancellationTokenSource();
            _turnCts = cts;
            try { await TurnAsync(input, cts.Token, printReply: true); }
            finally { _turnCts = null; }
        }
        var open = _jobs?.Store.All().Count(j => j.Open) ?? 0;
        if (open > 0) Dim($"  [{open} video job(s) still open; they resume the next time vanity-studio runs in this folder]");
        await MemoryAutoSave.WhenSavedAsync(_memory, TimeSpan.FromSeconds(20), CancellationToken.None);
        Log.Info("vanity-studio done");
    }

    private void PrintContextLine()
    {
        var parts = new List<string>();
        var ins = SystemPrompt.ProjectInstructions(_workspace);
        if (ins.Count > 0) parts.Add("instructions: " + string.Join(", ", ins.Select(i => i.File)));
        var brand = _project.Brand();
        if (VideoText.Str(brand["name"]) is { Length: > 0 } bn) parts.Add("brand: " + bn);
        if (_persona is not null) parts.Add("persona: " + _persona.Name);
        var (active, loadable) = SkillsFor(_persona, includePinned: true);
        if (active.Count > 0) parts.Add("skills: " + string.Join(", ", active.Select(s => s.Name)));
        if (loadable.Count > 0) parts.Add($"{loadable.Count} loadable skill(s)");
        var jobs = _jobs?.Store.All() ?? [];
        if (jobs.Count > 0) parts.Add($"{jobs.Count} video job(s) (/jobs)");
        if (parts.Count > 0) Dim("  " + string.Join(" · ", parts));
        if (!PromptLibrary.HasProjectDir(_workspace)) Dim("  /init sets this folder up as a video project (brand, media/, examples)");
    }

    private async Task<string?> TurnAsync(string message, CancellationToken ct, bool printReply)
    {
        try
        {
            _library = PromptLibrary.Load(_workspace);   // re-read every turn: a persona or skill edited on disk applies at once
            if (_personaName is not null) _persona = _library.Persona(_personaName) ?? _persona;
            var (active, loadable) = SkillsFor(_persona, includePinned: true);
            _loop.SystemPrompt = SystemPrompt.Build(_workspace, ActiveModel(), _tools.All.Select(t => t.Name), await MemoryBlockAsync(message, ct),
                persona: _persona, activeSkills: active, loadableSkills: loadable, media: MediaState(), blocks: await BlocksBriefAsync(ct));
            _request = message;
            // finished videos the conversation has not heard of yet go in front of the operator's words
            var notices = _jobs?.TakeNotices() ?? [];
            var sent = notices.Count == 0 ? message : string.Join("\n\n", notices) + "\n\n[The operator now says:]\n" + message;
            _streamedThought = 0; _streamKind = null;
            LlmCallScope.OnDelta.Value = OnDelta;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reply = await _loop.SendAsync(sent, ct);
            clock.Stop();
            EndStream();
            _autoSave?.Handle(message, reply, _loop.LastSteps.ToList(), _workspace);
            if (printReply)
            {
                PrintReply(reply);
                var (pt, ctok, cached) = _loop.LastTokenUsage;
                Dim($"  {_loop.LastModel} · {clock.Elapsed.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} s · {Kilo(pt)} in · {Kilo(ctok)} out");
                Console.WriteLine();
            }
            return reply;
        }
        catch (OperationCanceledException) { SpinStop(); Dim("  [stopped]"); return null; }
        catch (Exception ex)
        {
            SpinStop();
            Log.Error(ex);
            Red("  [error] " + ex.Message);
            if (ex.Message.Contains("No enabled AI profiles") || ex.Message.Contains("All ") && ex.Message.Contains("profile(s) failed"))
                Dim("  Check /profiles, /models and the key or login of the active profile. The log is in " + AgentConfig.LogsDir);
            return null;
        }
        finally { SpinStop(); EndStream(); LlmCallScope.OnDelta.Value = null; }
    }

    // ── commands ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Handles a slash command; false means quit.</summary>
    private async Task<bool> CommandAsync(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var rest = parts.Skip(1).ToArray();
        var ct = CancellationToken.None;
        try
        {
            switch (cmd)
            {
                case "/quit": case "/exit": case "/q": return false;
                case "/help": case "/?": PrintHelp(); break;
                // videos
                case "/jobs": PrintJobs(); break;
                case "/job": PrintJob(rest.ElementAtOrDefault(0)); break;
                case "/cancel": CancelJobs(rest.ElementAtOrDefault(0)); break;
                case "/open": OpenOutput(rest.ElementAtOrDefault(0), folder: false); break;
                case "/folder": OpenOutput(rest.ElementAtOrDefault(0), folder: true); break;
                case "/edit": EditInStudio(rest.ElementAtOrDefault(0)); break;
                case "/blocks": await BlocksAsync(); break;
                case "/brand": Console.WriteLine(new BrandTool(_project).Read()); Dim("  files: " + _project.BrandJsonPath + " · " + _project.BrandMdPath); break;
                case "/media": foreach (var m in _project.Media()) Console.WriteLine($"  {m.Rel,-50} {m.Kind,-8} {m.Dims}"); break;
                case "/roles": PrintRoles(); Dim("  /voice <profile> [model] · /images <profile> · /clips <profile> [model] assign a role; the chat model is /use"); break;
                case "/voice": SetRole(rest, "voice"); break;
                case "/images": SetRole(rest, "photo_gen"); break;
                case "/clips": SetRole(rest, "video_gen"); break;
                case "/site-login": if (rest.Length == 0) Red("  /site-login <url>"); else SiteLoginWindow(rest[0], wait: false); break;
                case "/doctor": await DoctorAsync(); break;
                case "/studio": Console.WriteLine("  " + MakeVideoTool.StudioUrl()); Dim("  VANITY_STUDIO_URL or /set StudioUrl <url> points at another Studio"); break;
                // models and logins
                case "/login": await LoginAsync(rest.ElementAtOrDefault(0), rest.ElementAtOrDefault(1), ct); Rebuild(); break;
                case "/key": await KeyAsync(rest.ElementAtOrDefault(0), rest.Skip(1).ToArray(), ct); Rebuild(); break;
                case "/profiles": case "/profile": PrintProfiles(); break;
                case "/use": Use(rest.ElementAtOrDefault(0)); break;
                case "/model": SetModel(rest.ElementAtOrDefault(0)); break;
                case "/models": await ListModelsAsync(ct); break;
                case "/remove": case "/logout": Remove(rest.ElementAtOrDefault(0)); break;
                case "/reset": case "/clear": case "/new": _loop.Reset(); Dim("  [conversation cleared]"); break;
                case "/usage": await PrintUsageStatsAsync(ct); break;
                case "/cwd": case "/cd": ChangeWorkspace(rest.Length > 0 ? string.Join(' ', rest) : null); break;
                case "/tools": foreach (var t in _tools.All.OrderBy(t => t.Name)) Console.WriteLine($"  {t.Name,-16} {FirstLine(t.Description)}"); break;
                case "/memory": await MemoryCommandAsync(rest, ct); break;
                case "/persona": SetPersona(rest.ElementAtOrDefault(0)); break;
                case "/personas": PrintPersonas(); break;
                case "/skills": PrintSkills(); break;
                case "/skill": PinSkill(rest.ElementAtOrDefault(0)); break;
                case "/init": Dim("  " + PromptLibrary.Scaffold(_workspace)); Build(); PrintContextLine(); break;
                case "/project": Console.WriteLine("  " + PromptLibrary.ProjectDir(_workspace) + (PromptLibrary.HasProjectDir(_workspace) ? "" : "  (not created yet; /init creates it)")); Console.WriteLine("  global personas/skills: " + AgentConfig.Dir); break;
                case "/sandbox": VanityPathHelper.Sandbox = rest.ElementAtOrDefault(0) is not "off"; Dim("  [sandbox " + (VanityPathHelper.Sandbox ? "on" : "off") + "]"); break;
                case "/verbose": Log.Verbose = rest.ElementAtOrDefault(0) is not "off"; Dim("  [verbose " + (Log.Verbose ? "on" : "off") + "]"); break;
                case "/config": Console.WriteLine("  " + AgentConfig.ConfigFile); Console.WriteLine("  " + AgentConfig.ProjectDir(_workspace)); foreach (var kv in AgentConfig.Settings) Console.WriteLine($"  setting {kv.Key} = {Mask(kv.Value)}"); break;
                case "/set": case "/secret": await SetSettingAsync(rest, ct); break;
                case "/tune": Tune(rest); break;
                default: Red($"  unknown command {cmd}; /help lists them"); break;
            }
        }
        catch (OperationCanceledException) { Dim("  [cancelled]"); }
        catch (Exception ex) { Log.Error(ex); Red("  " + ex.Message); }
        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
              Videos
              /jobs                              the video jobs of this folder
              /job <n>                           one job: its status, files and report
              /cancel <n|all>                    stop a job (or every job still being made)
              /open <n>                          play a finished video · /folder <n> shows its files
              /edit <n>                          open a finished video's project in the Studio's editor in your browser
              /blocks                            the Studio's scene blocks
              /brand, /media                     the brand of this folder; its pictures and clips
              /roles                             which profile speaks, draws stills and makes clips
              /voice <profile> [model]           give a Gemini / OpenAI / Alibaba API-key profile the voice role
              /images <profile>                  prefer this profile for AI stills
              /clips <profile> [model]           the Alibaba profile (and Wan model) for AI clips
              /site-login <url>                  log in to a site once, for tutorial steps behind a login
              /doctor                            check the browser, the Studio, ffprobe and the profiles
              /studio                            the Studio's address
              Models
              /login [openai|grok|antigravity|anthropic] [name]   sign in with a subscription (or paste an Anthropic key/token)
              /key [provider] [key] [model]      add a profile with an API key (interactive when arguments are missing)
              /profiles, /use <name>             list the profiles; make one the active one
              /model <model>, /models            set the model of the active profile; list what it can use
              /tune [setting value]              temperature, top_p, max_tokens, thinking on|off, timeout <s>, ctx <tokens>
              /remove <name>                     delete a profile (and its stored tokens)
              /usage                             what is left on each login and what this project spent
              Session
              /reset                             clear the conversation
              /cwd [dir]                         show or change the project folder
              /tools                             list the tools
              /memory [list|add <text>|delete <n>]   notes kept between sessions of this folder
              /init                              set this folder up: .vanity-studio/ (brand, instructions, personas, skills), media/
              /project                           where the project and the global personas/skills live
              /personas, /persona <name|off>     list personas; switch persona (the conversation is kept)
              /skills, /skill <name>             list skills; pin or unpin one for this session
              /sandbox on|off                    only files inside the project folder may be used
              /verbose on|off                    echo the diagnostic log (and the renderer's)
              /config, /set <name> [value]       where config lives; store a machine-local setting (BrowserPath, StudioUrl,
                                                 VideoParallel, GeminiVoice, OpenAiVoice, QwenVoice, GoogleClientSecret)
              /quit                              exit (open jobs resume next time)
            """);
    }

    private VideoJob? JobArg(string? arg)
    {
        var all = _jobs!.Store.All();
        if (string.IsNullOrWhiteSpace(arg) || arg is "latest" or "last") return all.FirstOrDefault();
        return long.TryParse(arg.TrimStart('#'), out var id) ? _jobs.Store.Get(id) : null;
    }

    private void PrintJobs()
    {
        var jobs = _jobs!.Store.All();
        if (jobs.Count == 0) { Dim("  (no video jobs in this folder yet)"); return; }
        foreach (var j in jobs.Take(30))
            Print(j.Status switch { VideoJob.Done => ConsoleColor.Green, VideoJob.Failed => ConsoleColor.Red, VideoJob.Cancelled => ConsoleColor.DarkGray, _ => ConsoleColor.White },
                "  " + VideoJobs.Line(j));
    }

    private void PrintJob(string? arg)
    {
        var j = JobArg(arg);
        if (j is null) { Red("  no such job (/jobs lists them)"); return; }
        Console.WriteLine("  " + VideoJobs.Line(j));
        if (j.Finished && j.Status != VideoJob.Cancelled) foreach (var l in _jobs!.BuildReport(j).raw.Split('\n')) Console.WriteLine("  " + l);
        if (j.OutDir is not null) Dim("  folder: " + Path.Combine(_workspace, j.OutDir));
    }

    private void CancelJobs(string? arg)
    {
        if (arg is "all" or null)
        {
            var open = _jobs!.Store.All().Where(j => j.Open).ToList();
            if (open.Count == 0) { Dim("  nothing is being made"); return; }
            foreach (var j in open) Console.WriteLine("  " + _jobs.Cancel(j.Id));
            return;
        }
        if (!long.TryParse(arg.TrimStart('#'), out var id)) { Red("  /cancel <n|all>"); return; }
        Console.WriteLine("  " + _jobs!.Cancel(id));
    }

    private void OpenOutput(string? arg, bool folder)
    {
        var j = JobArg(arg);
        if (j is null || j.Video is null) { Red("  no finished video" + (arg is null ? "" : " #" + arg) + " (/jobs lists them)"); return; }
        var target = Path.Combine(_workspace, folder ? j.OutDir! : j.Video);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); Dim("  [opened " + target + "]"); }
        catch (Exception ex) { Red("  could not open " + target + ": " + ex.Message); }
    }

    private void EditInStudio(string? arg)
    {
        var j = JobArg(arg);
        if (j?.Project is null) { Red("  no finished video with a project" + (arg is null ? "" : " #" + arg) + " (/jobs lists them)"); return; }
        var link = EditServer().Open(j.Id);
        Dim($"  [opening {link} : the Studio loads the project into its editor]");
        Dim("  if the browser asks to let the Studio reach this computer, allow it; otherwise Import video project with " + Path.Combine(_workspace, j.Project));
    }

    /// <summary>The session's edit-link server, serving the projects of this folder's finished jobs.</summary>
    private ProjectServer EditServer() => ProjectServer.For(id =>
        _jobs?.Store.Get(id) is { Project: { } p } ? Path.GetFullPath(Path.Combine(_workspace, p)) : null);

    /// <summary>`edit <n>`: a finished video's project opened in the Studio's editor, the link served until Enter.</summary>
    private async Task<int> EditCommandAsync()
    {
        OpenProject();
        var j = JobArg(_o.CommandArgs.FirstOrDefault());
        if (j?.Project is null) { Red("  no finished video with a project here (vanity-studio jobs lists them)"); return 1; }
        var server = EditServer();
        Cyan($"  {server.EditLink(j.Id)}");
        Dim($"  opening it in your browser: the Studio loads \"{j.Title}\" into its editor (allow it to reach this computer if asked)");
        server.Open(j.Id);
        if (Console.IsInputRedirected) { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
        Dim("  press Enter when the Studio shows the project (the link works until then)");
        await Task.Run(Console.ReadLine);
        return 0;
    }

    /// <summary>/voice, /images, /clips: gives a profile the role's layer (and, for voice and clips, the model).</summary>
    private void SetRole(string[] rest, string layer)
    {
        var name = rest.ElementAtOrDefault(0);
        if (string.IsNullOrWhiteSpace(name)) { PrintRoles(); return; }
        var opts = AgentConfig.Load();
        var p = opts.Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (p is null) { Red($"  no profile '{name}' (/profiles lists them; /key adds one)"); return; }
        if (!p.Layers.Contains(layer, StringComparer.OrdinalIgnoreCase)) p.Layers = p.Layers.Concat([layer]).ToArray();
        // While another profile chats, this one keeps to media: the key added for the voice never answers the chat (and
        // bills for it) when the login is busy. A profile that is the only one that can chat keeps chatting.
        var otherChats = opts.Profiles.Any(o => !ReferenceEquals(o, p) && Usable(o) && o.Layers.Contains("any", StringComparer.OrdinalIgnoreCase));
        if (otherChats) p.Layers = p.Layers.Where(l => !l.Equals("any", StringComparison.OrdinalIgnoreCase)).ToArray();
        else if (!p.Layers.Contains("any", StringComparer.OrdinalIgnoreCase)) p.Layers = p.Layers.Concat(["any"]).ToArray();
        if (rest.ElementAtOrDefault(1) is { Length: > 0 } model) p.RoleModels[layer] = model;
        AgentConfig.Save(opts);
        _opts = AgentConfig.Load();
        Rebuild();
        PrintRoles();
    }

    private void Rebuild()
    {
        var keepHistory = _loop?.History.ToList();
        Build();
        Dim($"  [active: {ActiveProfile()?.Name} · {ActiveModel()}]");
    }

    private async Task LoginAsync(string? provider, string? name, CancellationToken ct)
    {
        provider = (provider ?? "").Trim().ToLowerInvariant();
        if (provider is "google" or "gemini-cli") provider = "antigravity";
        if (provider is "chatgpt" or "codex") provider = "openai";
        if (provider is "xai") provider = "grok";
        if (provider is "claude") provider = "anthropic";
        if (provider is not ("openai" or "grok" or "antigravity" or "anthropic"))
        {
            Console.WriteLine("  Sign in with: /login openai (ChatGPT) · /login grok (xAI) · /login antigravity (Google) · /login anthropic (paste key/token)");
            Console.WriteLine("  API keys for any provider: /key");
            return;
        }
        var ui = new LoginUi { Say = Console.WriteLine, Emphasis = s => { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }, Ask = AskAsync };
        var profileName = string.IsNullOrWhiteSpace(name) ? provider switch { "openai" => "chatgpt", "antigravity" => "antigravity", "grok" => "grok", _ => "anthropic" } : name!;
        AiProfile profile;
        switch (provider)
        {
            case "openai": profile = await OAuthFlows.LoginOpenAiAsync(profileName, ui, ct); break;
            case "grok": profile = await OAuthFlows.LoginGrokAsync(profileName, ui, ct); break;
            case "antigravity": profile = await OAuthFlows.LoginAntigravityAsync(profileName, "cli", ui, ct); break;
            default:
                Console.WriteLine("  Paste an Anthropic API key (sk-ant-api...) from https://console.anthropic.com/settings/keys,");
                Console.WriteLine("  or a bearer token (sk-ant-oat...). The key is stored in " + AgentConfig.ConfigFile);
                var secret = await ReadSecretAsync("  Key or token: ", ct);
                profile = OAuthFlows.AnthropicFromSecret(profileName, secret ?? "");
                break;
        }
        // Keep the models an existing profile of that name already had.
        var existing = AgentConfig.Load().Profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (existing is { Models.Length: > 0 }) profile.Models = existing.Models;
        AgentConfig.Upsert(profile);
        // one line (/help shows /model and /models)
        Green($"  Signed in as {(string.IsNullOrEmpty(profile.OAuthAccountId) ? profile.Name : profile.OAuthAccountId)} ({profile.Provider} · {profile.Models.FirstOrDefault()})");
    }

    private static readonly (string Id, string Label, string DefaultModel)[] KeyProviders =
    [
        ("openai",     "OpenAI",                      "gpt-5.5"),
        ("anthropic",  "Anthropic",                   "claude-sonnet-5-5"),
        ("gemini",     "Google Gemini",               "gemini-3.1-pro"),
        ("grok",       "Grok (xAI)",                  "grok-4.5"),
        ("deepseek",   "DeepSeek",                    "deepseek-chat"),
        ("mistral",    "Mistral",                     "mistral-large-latest"),
        ("groq",       "Groq",                        "llama-3.3-70b-versatile"),
        ("openrouter", "OpenRouter",                  "anthropic/claude-sonnet-4.5"),
        ("alibaba",    "Alibaba DashScope (Qwen; Wan clips, Qwen voice)", "qwen-plus"),
        ("perplexity", "Perplexity",                  "sonar-pro"),
        ("ollama",     "Ollama (local)",              "qwen3:32b"),
        ("custom",     "Custom OpenAI-compatible URL", ""),
    ];

    /// <summary>
    /// /key provider [key ...] [model]: keys and the model in any order, keys also comma-separated. A profile that exists
    /// gets the new keys added to the ones it has (each key is tried in turn when one is out of quota) and keeps its
    /// place, its roles and its model; /key used to replace it, so four pasted keys left one (2026-10-09).
    /// </summary>
    private async Task KeyAsync(string? provider, string[] args, CancellationToken ct)
    {
        var tokens = args.SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.Trim('"', '\'')).Where(t => t.Length > 0).ToList();
        var keys = tokens.Where(LooksLikeKey).Distinct(StringComparer.Ordinal).ToList();
        var model = tokens.FirstOrDefault(t => !LooksLikeKey(t));
        provider = provider?.Trim().ToLowerInvariant();
        if (provider is "xai") provider = "grok";
        if (provider is "google") provider = "gemini";
        var existing = provider is null ? null : AgentConfig.Load().Profiles.FirstOrDefault(p => p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase)
            && p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && provider is not ("anthropic" or "ollama" or "custom"));
        if (existing is not null && keys.Count > 0)
        {
            var before = existing.ApiKeys.Count(k => !string.IsNullOrWhiteSpace(k));
            existing.ApiKeys = existing.ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Concat(keys).Distinct(StringComparer.Ordinal).ToArray();
            if (!string.IsNullOrWhiteSpace(model)) existing.Models = [model];
            existing.Enabled = true;
            AgentConfig.Upsert(existing, makeDefault: false);
            var added = existing.ApiKeys.Length - before;
            Green($"  Profile '{existing.Name}': {existing.ApiKeys.Length} key(s) ({(added > 0 ? $"{added} added" : "all already there")}) · model {existing.Models.FirstOrDefault()}");
            return;
        }
        await NewKeyProfileAsync(provider, keys, model, ct);
    }

    // an API key, not a model name: long, one word, with capitals (models are lower case: gemini-2.5-flash-preview-tts, gpt-5.5)
    private static bool LooksLikeKey(string t) => t.Length >= 20 && !t.Contains('/') && t.Any(char.IsUpper) && t.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    private async Task NewKeyProfileAsync(string? provider, List<string> keys, string? model, CancellationToken ct)
    {
        string? key = keys.Count > 0 ? string.Join(",", keys) : null;
        provider = provider?.Trim().ToLowerInvariant();
        if (provider is "xai") provider = "grok";
        if (provider is "google") provider = "gemini";
        if (provider is null || !KeyProviders.Any(k => k.Id == provider))
        {
            Console.WriteLine("  Providers:");
            for (int i = 0; i < KeyProviders.Length; i++) Console.WriteLine($"    {i + 1,2}  {KeyProviders[i].Label}");
            var pick = (await AskAsync("  Provider (number or name): ", ct) ?? "").Trim().ToLowerInvariant();
            provider = int.TryParse(pick, out var n) && n >= 1 && n <= KeyProviders.Length ? KeyProviders[n - 1].Id : pick;
            if (!KeyProviders.Any(k => k.Id == provider)) throw new ArgumentException("unknown provider " + provider);
        }
        var def = KeyProviders.First(k => k.Id == provider);
        var profile = new AiProfile { Name = provider, Provider = provider, Enabled = true, DisableThinking = true, JsonMode = true, Layers = ["any"] };

        if (provider == "custom")
        {
            var url = (await AskAsync("  Base URL (e.g. https://api.example.com or http://localhost:8080/v1): ", ct) ?? "").Trim();
            if (url.Length == 0) throw new ArgumentException("a base URL is required");
            profile.BaseUrl = url;
            profile.Provider = "custom";
            var pname = (await AskAsync("  Profile name [custom]: ", ct) ?? "").Trim();
            if (pname.Length > 0) profile.Name = pname;
        }
        if (provider == "ollama")
        {
            var url = (await AskAsync("  Ollama URL [http://localhost:11434]: ", ct) ?? "").Trim();
            if (url.Length > 0) profile.BaseUrl = url;
            profile.ApiKeys = [];
        }
        else
        {
            key ??= await ReadSecretAsync($"  {def.Label} API key (several: comma-separated): ", ct);
            var given = (key ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.Trim('"', '\'')).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (given.Length == 0) throw new ArgumentException("an API key is required");
            if (provider == "anthropic")
            {
                var anth = OAuthFlows.AnthropicFromSecret(profile.Name, given[0]);
                profile = anth;
            }
            else profile.ApiKeys = given;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            var m = (await AskAsync($"  Model [{def.DefaultModel}]: ", ct) ?? "").Trim();
            model = m.Length > 0 ? m : def.DefaultModel;
        }
        if (model.Length > 0 && !(provider == "anthropic" && profile.Models.Length > 0 && model == def.DefaultModel))
            profile.Models = [model];
        // a voice model: the provider's other voice models follow it, each with its own free quota per key
        if (model.Contains("tts", StringComparison.OrdinalIgnoreCase))
            profile.Models = [.. new[] { model }.Concat(VoiceMaker.DefaultModels(provider)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (profile.Models.Length == 0) throw new ArgumentException("a model name is required");
        AgentConfig.Upsert(profile);
        Green($"  Saved profile '{profile.Name}' ({profile.Provider}) · {profile.ApiKeys.Length} key(s) · model {profile.Models[0]}");
    }

    private void PrintProfiles()
    {
        var opts = AgentConfig.Load();
        if (opts.Profiles.Count == 0) { Dim("  (no profiles; /login or /key)"); return; }
        bool first = true;
        foreach (var p in opts.Profiles)
        {
            var usable = Usable(p);
            var marker = usable && first ? "*" : " ";
            if (usable && first) first = false;
            var auth = OAuthTokenRefresher.UsesOAuth(p)
                ? "login" + (string.IsNullOrEmpty(p.OAuthAccountId) ? "" : ":" + p.OAuthAccountId) + (p.OAuthExpiresAt > 0 && p.OAuthExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? " (token expired; refreshes on use)" : "")
                : p.ApiKeys.Length == 0 ? "no key" : "key " + Mask(p.ApiKeys[0]);
            Console.ForegroundColor = usable ? ConsoleColor.White : ConsoleColor.DarkGray;
            Console.WriteLine($"  {marker} {p.Name,-14} {p.Provider,-12} {string.Join(", ", p.Models),-40} {auth}{(p.Enabled ? "" : " (disabled)")}{(string.IsNullOrEmpty(p.BaseUrl) ? "" : " " + p.BaseUrl)}");
            Console.ResetColor();
        }
        Dim("  * = answers first; the others are fallbacks in this order. /use <name> changes the order.");
    }

    private static string Mask(string key)
    {
        key = key.Trim();
        return key.Length <= 8 ? "****" : key[..4] + "…" + key[^4..];
    }

    private void Use(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { PrintProfiles(); return; }
        var opts = AgentConfig.Load();
        var p = opts.Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (p is null) { Red($"  no profile '{name}'"); return; }
        p.Enabled = true;
        AgentConfig.MoveFirst(opts, p.Name);
        AgentConfig.Save(opts);
        _o.Profile = null;
        Rebuild();
    }

    private void SetModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) { Console.WriteLine("  model: " + ActiveModel()); return; }
        var opts = AgentConfig.Load();
        var active = ActiveProfile();
        var p = active is null ? null : opts.Profiles.FirstOrDefault(x => x.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
        if (p is null) { Red("  no active profile"); return; }
        p.Models = new[] { model }.Concat(p.Models.Where(m => !m.Equals(model, StringComparison.OrdinalIgnoreCase))).ToArray();
        AgentConfig.Save(opts);
        _o.Model = null;
        Rebuild();
    }

    private void Remove(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Red("  /remove <profile name>"); return; }
        var before = AgentConfig.Load().Profiles.Count;
        var after = AgentConfig.Remove(name).Profiles.Count;
        if (after == before) { Red($"  no profile '{name}'"); return; }
        Dim($"  [removed '{name}']");
        if (after > 0) Rebuild(); else Dim("  no profiles left; /login or /key to add one");
    }

    private async Task ListModelsAsync(CancellationToken ct)
    {
        var p = ActiveProfile();
        if (p is null) { Red("  no active profile"); return; }
        await OAuthTokenRefresher.EnsureFreshAsync(p, 0, ct: ct);
        var models = await ModelCatalog.ListAsync(p, ct);
        if (models.Count == 0) { Dim("  (the provider returned no models)"); return; }
        Console.WriteLine($"  {models.Count} model(s) on '{p.Name}' ({p.Provider}):");
        foreach (var m in models) Console.WriteLine("    " + m);
        Dim("  /model <name> selects one. A \"flash\" or \"mini\" model answers in seconds; a \"pro\"/\"high\" one can wait half a minute in the provider's queue before its first token.");
    }

    private void ChangeWorkspace(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) { Console.WriteLine("  " + _workspace); return; }
        var full = VanityPathHelper.NormalizeAndResolve(dir, _workspace);
        if (!Directory.Exists(full)) { Red("  no such directory: " + full); return; }
        _workspace = full;
        Build();
        Dim("  [workspace: " + _workspace + " · conversation cleared]");
    }

    internal async Task PrintUsageStatsAsync(CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // 1. What is LEFT on each login: the provider's own weekly / five-hour pools, the same rows its app shows.
        var profiles = AgentConfig.Load().Profiles.Where(Usable).ToList();
        foreach (var p in profiles)
        {
            var isLogin = OAuthTokenRefresher.UsesOAuth(p);
            if (!isLogin && !p.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase) && !p.Provider.Equals("oneprovider", StringComparison.OrdinalIgnoreCase))
            {
                Dim($"  {p.Name} ({p.Provider}, api key): {AiUsageProbe.CreditsNote(p.Provider)}");
                continue;
            }
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  {p.Name} ({p.Provider}{(string.IsNullOrEmpty(p.OAuthAccountId) ? "" : ", " + p.OAuthAccountId)})");
            Console.ResetColor();
            AiUsageProbe.Usage? u = null;
            try { u = await AiUsageProbe.ProbeAsync(p, 0, includeRaw: false, ct); } catch { }
            if (u is null) { Dim("  · limits unavailable (the provider did not answer the usage call; the token may need /login again)"); continue; }
            Console.WriteLine(string.IsNullOrWhiteSpace(u.plan) ? "" : $" · plan: {u.plan}");
            if (u.balance is not null)
                Console.WriteLine($"    balance: {u.balance.remaining.ToString("0.00", inv)} {u.balance.unit} left" + (u.balance.limit is { } lim ? $" of {lim.ToString("0.00", inv)}" : ""));
            foreach (var pool in u.pools)
            {
                if (pool.label.Length > 0) Console.WriteLine("    " + pool.label);
                if (pool.buckets.Count > 0)
                    foreach (var b in pool.buckets)
                        PrintLimit(b.label, b.remaining, b.reset, b.description);
                else
                {
                    if (pool.weeklyRemaining is { } w) PrintLimit("Weekly limit remaining", w, pool.weeklyReset, null);
                    if (pool.fiveHourRemaining is { } f) PrintLimit("Five hour limit remaining", f, pool.fiveHourReset, null);
                }
            }
        }

        // 2. What this agent spent: the current process, then the project's stored months.
        var (calls, pt, ctok, _) = LlmRouter.GetUsageTotals();
        Console.WriteLine(calls == 0
            ? "  this session: no model calls yet"
            : $"  this session: {calls:N0} call(s) · {pt:N0} in / {ctok:N0} out · {_usage.LiveCached:N0} cached");
        if (calls > 0) Dim(LlmRouter.DescribeUsage());
        var months = _usage.Months();
        if (months.Count == 0) { Dim("  this project: nothing recorded yet"); return; }
        Console.WriteLine("  this project, by month (every session, every model):");
        foreach (var m in months.Take(6))
            Console.WriteLine($"    {m.Month}  {m.Calls,6:N0} calls  {m.Prompt,12:N0} in  {m.Completion,10:N0} out  {m.Cached,10:N0} cached   ~${m.CostUsd.ToString("0.00", inv)}");
        Dim("  cost is a rough list-price estimate per model family; subscription logins are not billed per token. file: " + _usage.Path);
    }

    private static void PrintLimit(string label, double remaining, string? reset, string? description)
    {
        Console.ForegroundColor = remaining <= 10 ? ConsoleColor.Red : remaining <= 30 ? ConsoleColor.Yellow : ConsoleColor.Green;
        Console.Write($"      {label,-28} {remaining,4:0}%");
        Console.ResetColor();
        var when = ResetHint(reset);
        if (!string.IsNullOrEmpty(description)) Dim("   " + description);
        else if (when.Length > 0) Dim("   resets " + when);
        else Console.WriteLine();
    }

    private static string ResetHint(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso) || !DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) return "";
        var left = at - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "now";
        if (left.TotalDays >= 1) return $"in {(int)left.TotalDays} day{((int)left.TotalDays == 1 ? "" : "s")}, {left.Hours} hour{(left.Hours == 1 ? "" : "s")}";
        if (left.TotalHours >= 1) return $"in {(int)left.TotalHours} hour{((int)left.TotalHours == 1 ? "" : "s")}, {left.Minutes} min";
        return $"in {left.Minutes} min";
    }

    private async Task SetSettingAsync(string[] rest, CancellationToken ct)
    {
        var key = rest.ElementAtOrDefault(0)?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            Console.WriteLine("  /set <name> [value]   known names: GoogleClientSecret, GoogleClientSecretAlt, GoogleAppClientSecret");
            foreach (var kv in AgentConfig.Settings) Console.WriteLine($"    {kv.Key} = {Mask(kv.Value)}");
            return;
        }
        if (key.Equals("google", StringComparison.OrdinalIgnoreCase)) key = "GoogleClientSecret";
        var value = rest.Length > 1 ? string.Join(' ', rest.Skip(1)).Trim() : await ReadSecretAsync($"  {key}: ", ct);
        AgentConfig.SetSetting(key, value);
        Dim(string.IsNullOrEmpty(value) ? $"  [{key} removed]" : $"  [{key} stored in {AgentConfig.ConfigFile}]");
    }

    /// <summary>Model settings of the active profile, persisted: temperature, top_p, max_tokens, thinking, timeout, ctx.</summary>
    private void Tune(string[] rest)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var opts = AgentConfig.Load();
        var active = ActiveProfile();
        var p = active is null ? null : opts.Profiles.FirstOrDefault(x => x.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
        if (p is null) { Red("  no active profile"); return; }
        if (rest.Length == 0)
        {
            Console.WriteLine($"  {p.Name} ({p.Provider} / {p.Models.FirstOrDefault()})");
            Console.WriteLine($"    temperature  {(p.Temperature is { } t ? t.ToString(inv) : "provider default")}");
            Console.WriteLine($"    top_p        {(p.TopP is { } tp ? tp.ToString(inv) : "provider default")}");
            Console.WriteLine($"    max_tokens   {(p.MaxTokens > 0 ? p.MaxTokens.ToString(inv) : "default (16000 chat, 32000 Gemini)")}");
            Console.WriteLine($"    thinking     {(p.DisableThinking ? "off" : "on (provider default)")}");
            Console.WriteLine($"    timeout      {p.RequestTimeoutMs / 1000}s");
            Console.WriteLine($"    ctx          {(p.NumCtx > 0 ? p.NumCtx.ToString(inv) + " (Ollama num_ctx)" : "model default")}");
            Dim("  /tune temperature 0.2 · /tune top_p 0.9 · /tune max_tokens 8000 · /tune thinking off · /tune timeout 120 · /tune ctx 32768 · /tune reset");
            Dim("  thinking depth on Gemini/Antigravity is the model suffix: /model gemini-3.8-flash-low|medium|high");
            return;
        }
        var key = rest[0].ToLowerInvariant().Replace("-", "_");
        var val = rest.ElementAtOrDefault(1)?.Trim().ToLowerInvariant() ?? "";
        double D() => double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var d) ? d : throw new ArgumentException($"'{val}' is not a number");
        int I() => int.TryParse(val, out var i) ? i : throw new ArgumentException($"'{val}' is not a whole number");
        bool Off() => val is "off" or "false" or "0" or "no" or "none";
        switch (key)
        {
            case "temperature": case "temp": p.Temperature = val is "" or "default" or "reset" ? null : Math.Clamp(D(), 0, 2); break;
            case "top_p": case "topp": p.TopP = val is "" or "default" or "reset" ? null : Math.Clamp(D(), 0, 1); break;
            case "max_tokens": case "maxtokens": case "max": p.MaxTokens = val is "" or "default" or "reset" ? 0 : Math.Max(256, I()); break;
            case "thinking": case "reasoning": p.DisableThinking = Off(); break;
            case "timeout": p.RequestTimeoutMs = Math.Max(10, I()) * 1000; break;
            case "ctx": case "num_ctx": case "context": p.NumCtx = val is "" or "default" or "reset" ? 0 : Math.Max(1024, I()); break;
            case "reset": p.Temperature = null; p.TopP = null; p.MaxTokens = 0; p.DisableThinking = false; p.NumCtx = 0; break;
            default: Red("  unknown setting; /tune without arguments lists them"); return;
        }
        AgentConfig.Save(opts);
        Rebuild();
        Tune([]);
    }

    private void SetPersona(string? name)
    {
        _library = PromptLibrary.Load(_workspace);
        if (string.IsNullOrWhiteSpace(name)) { PrintPersonas(); return; }
        if (name is "off" or "default" or "none")
        {
            _personaName = null; _persona = null;
        }
        else
        {
            var p = _library.Persona(name);
            if (p is null) { Red($"  no persona '{name}'; /personas lists them, /init creates examples"); return; }
            _personaName = p.Name; _persona = p;
        }
        // The tools and the turn cap follow the persona; the conversation itself is kept.
        _tools = BuildTools(_persona);
        var keep = _loop.History.ToList();
        var maxTurns = _persona is { MaxTurns: > 0 } ? _persona.MaxTurns : _o.MaxTurns;
        _loop = new AgentLoop(new LayerClient(this), _tools, "", ConsoleEvents(), _usage, maxTurns, "main", Guid.NewGuid().ToString("N"), keep);
        Dim(_persona is null ? "  [persona off: the default videographer]" : $"  [persona: {_persona.Name} · tools: {(_persona.Tools.Length > 0 ? string.Join(", ", _persona.Tools) : "all")}]");
    }

    private void PrintPersonas()
    {
        _library = PromptLibrary.Load(_workspace);
        if (_library.Personas.Count == 0) { Dim("  (no personas; /init creates examples in .vanity-studio/personas, or put .md files in " + Path.Combine(AgentConfig.Dir, "personas") + ")"); return; }
        foreach (var p in _library.Personas)
        {
            var active = _persona is not null && p.Name.Equals(_persona.Name, StringComparison.OrdinalIgnoreCase);
            Console.ForegroundColor = active ? ConsoleColor.Green : ConsoleColor.White;
            Console.WriteLine($"  {(active ? "*" : " ")} {p.Name,-14} {p.Description}");
            Console.ResetColor();
            Dim($"      {Rel(p.Source)}" + (p.Tools.Length > 0 ? " · tools: " + string.Join(", ", p.Tools) : "") + (p.Skills.Length > 0 ? " · skills: " + string.Join(", ", p.Skills) : ""));
        }
        Dim("  /persona <name> switches, /persona off returns to the default agent.");
    }

    private void PrintSkills()
    {
        _library = PromptLibrary.Load(_workspace);
        if (_library.Skills.Count == 0) { Dim("  (no skills; /init creates examples in .vanity-studio/skills, or put .md files in " + Path.Combine(AgentConfig.Dir, "skills") + ")"); return; }
        var (active, _) = SkillsFor(_persona, includePinned: true);
        foreach (var s in _library.Skills)
        {
            var on = active.Contains(s);
            Console.ForegroundColor = on ? ConsoleColor.Green : ConsoleColor.White;
            Console.WriteLine($"  {(on ? "*" : " ")} {s.Name,-18} {s.Description}");
            Console.ResetColor();
            Dim($"      {Rel(s.Source)}" + (s.Always ? " · always" : _pinnedSkills.Contains(s.Name) ? " · pinned" : on ? " · from persona" : " · loadable on demand"));
        }
        Dim("  * = in the prompt every turn; the others are listed to the model and loaded with skill_view when needed. /skill <name> pins one.");
    }

    private void PinSkill(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { PrintSkills(); return; }
        _library = PromptLibrary.Load(_workspace);
        var s = _library.Skill(name);
        if (s is null) { Red($"  no skill '{name}'; /skills lists them"); return; }
        if (_pinnedSkills.Remove(s.Name)) Dim($"  [skill {s.Name} unpinned: loadable on demand]");
        else { _pinnedSkills.Add(s.Name); Dim($"  [skill {s.Name} pinned: in the prompt every turn]"); }
    }

    private string Rel(string path)
    {
        try
        {
            if (VanityPathHelper.IsInside(path, _workspace)) return Path.GetRelativePath(_workspace, path).Replace('\\', '/');
            if (VanityPathHelper.IsInside(path, AgentConfig.Dir)) return "~/.vanity-studio/" + Path.GetRelativePath(AgentConfig.Dir, path).Replace('\\', '/');
        }
        catch { }
        return path;
    }

    private async Task MemoryCommandAsync(string[] rest, CancellationToken ct)
    {
        var sub = rest.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "list";
        switch (sub)
        {
            case "add":
                var text = string.Join(' ', rest.Skip(1)).Trim();
                if (text.Length == 0) { Red("  /memory add <text>"); return; }
                var title = text.Length > 60 ? text[..57] + "..." : text;
                await _memory.SaveAsync(new MemoryEntry { AgentId = "operator", Content = text, Metadata = new() { ["title"] = title } }, ct);
                Dim("  [saved]"); break;
            case "delete": case "rm":
                if (!int.TryParse(rest.ElementAtOrDefault(1), out var n) || _memory.ByNumber(n) is not { } e) { Red("  /memory delete <number>"); return; }
                await _memory.DeleteAsync(e.Id, ct);
                Dim($"  [deleted #{n}]"); break;
            default:
                var notes = await _memory.ListAsync(ct);
                if (notes.Count == 0) { Dim("  (no notes yet; the agent saves facts with the memory tool, or /memory add <text>)"); return; }
                foreach (var note in notes)
                {
                    var t = note.Metadata.TryGetValue("title", out var tt) ? tt : note.Content.Split('\n')[0];
                    Console.WriteLine($"  #{_memory.NumberOf(note.Id),-3} {t}  ({note.CreatedAt:yyyy-MM-dd}, {note.AgentId})");
                    Dim("       " + FirstLine(note.Content, 160));
                }
                Dim("  file: " + _memory.Path); break;
        }
    }

    // ── console helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A line from the console that can be abandoned: the login's "paste the URL or wait for the browser"
    /// prompt is cancelled when the browser comes back first. A plain Console.ReadLine on a background task cannot
    /// be cancelled; it kept owning the keyboard and ate the operator's next message (the first "hi" after a Google
    /// login went nowhere). So the keyboard is only read once a key is actually pressed.</summary>
    private static async Task<string?> AskAsync(string prompt, CancellationToken ct)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return await Task.Run(() => Console.ReadLine(), ct);
        while (!ct.IsCancellationRequested)
        {
            bool key;
            try { key = Console.KeyAvailable; }
            catch (InvalidOperationException) { return await Task.Run(() => Console.ReadLine(), ct); }
            if (key) return Console.ReadLine();
            await Task.Delay(60, CancellationToken.None);
        }
        Console.WriteLine();
        return null;
    }

    // ── spinner: something is visible while the model is thinking ────────────────────────────────────────────────

    private static readonly object ConsoleGate = new();
    private Spinner? _spinner;

    private void SpinStart(string label)
    {
        if (Console.IsOutputRedirected) return;
        SpinStop();
        _spinner = new Spinner(label);
    }

    private void SpinStop()
    {
        var s = Interlocked.Exchange(ref _spinner, null);
        s?.Dispose();
    }

    private sealed class Spinner : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly string _label;
        private const string Frames = @"-\|/";

        public Spinner(string label) { _label = label; _task = Task.Run(RunAsync); }

        private async Task RunAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int i = 0;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    lock (ConsoleGate)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"\r  {Frames[i++ % Frames.Length]} {_label} {sw.Elapsed.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}s ");
                        Console.ResetColor();
                    }
                    await Task.Delay(150, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _task.Wait(500); } catch { }
            lock (ConsoleGate) { Console.Write("\r" + new string(' ', _label.Length + 16) + "\r"); }
        }
    }

    private static async Task<string?> ReadSecretAsync(string prompt, CancellationToken ct)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return await Task.Run(() => Console.ReadLine(), ct);
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } continue; }
                if (k.KeyChar == '\0') continue;
                sb.Append(k.KeyChar); Console.Write('*');
            }
            return sb.ToString();
        }, ct);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            while (line.Length > width)
            {
                var cut = line.LastIndexOf(' ', width);
                if (cut < width / 2) cut = width;
                yield return line[..cut];
                line = line[cut..].TrimStart();
            }
            yield return line;
        }
    }

    private static string FirstLine(string s, int max = 100)
    {
        var line = (s ?? "").Replace("\r", "").Split('\n')[0].Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }

    private static void Dim(string s) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine(s); Console.ResetColor(); }
    private static void Cyan(string s) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(s); Console.ResetColor(); }
    private static void Green(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(s); Console.ResetColor(); }
    private static void Red(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(s); Console.ResetColor(); }
}
