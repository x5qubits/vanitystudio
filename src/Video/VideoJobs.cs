using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using VanityStudio.Infra;
using VanityStudio.Llm;
using static VanityStudio.Video.VideoText;

namespace VanityStudio.Video;

/// <summary>One video job, as saved in <c>jobs/&lt;id&gt;.json</c>.</summary>
public sealed record VideoJob
{
    public const string Queued = "queued", Rendering = "rendering", Done = "done", Failed = "failed", Cancelled = "cancelled";
    public const string Prepare = "prepare", Ship = "ship", Track = "track", Collect = "collect", Report = "report", Closed = "closed";

    public long Id { get; init; }
    /// <summary>"script" (a make_video script the Studio's director compiles) or "doc" (a Studio project doc, edit_video).</summary>
    public string Kind { get; init; } = "script";
    /// <summary>A doc job's file (relative to the project): the files it names are found from there.</summary>
    public string? DocPath { get; init; }
    public string Title { get; init; } = "";
    public string Request { get; init; } = "";
    public string ScriptJson { get; init; } = "{}";
    public string AssetsJson { get; init; } = "{}";
    public string Status { get; init; } = Queued;
    public string Stage { get; init; } = Prepare;
    public string? Error { get; init; }
    public double Pct { get; init; }
    public string? RenderJob { get; init; }
    public string? RenderStage { get; init; }
    public int? Position { get; init; }
    public int Attempts { get; init; }
    public long? RemixOf { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; init; }
    public DateTime? NotBefore { get; init; }
    public string? OutDir { get; init; }
    public string? Video { get; init; }
    public string? Sheet { get; init; }
    public string? Poster { get; init; }
    public string? Project { get; init; }
    public string? ReportPath { get; init; }
    public string? ReportJson { get; init; }
    public double Duration { get; init; }
    public long Bytes { get; init; }

    /// <summary>Still being worked: not reported yet and not cancelled.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Open => Stage != Closed && Status != Cancelled;
    [System.Text.Json.Serialization.JsonIgnore] public bool Finished => Status is Done or Failed or Cancelled;
}

/// <summary>The jobs of one project: a JSON file per job, written atomically, plus a folder per job for its voice lines.</summary>
public sealed class VideoJobStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public VideoJobStore(string dir) { _dir = dir; }
    public string Dir => _dir;

    public long Insert(VideoJob job)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var id = Ids().DefaultIfEmpty(0).Max() + 1;
            Write(job with { Id = id });
            return id;
        }
    }

    public VideoJob? Get(long id)
    {
        lock (_gate)
        {
            var f = Path.Combine(_dir, id + ".json");
            try { return File.Exists(f) ? JsonSerializer.Deserialize<VideoJob>(File.ReadAllText(f), Json) : null; }
            catch { return null; }
        }
    }

    public void Save(VideoJob job) { lock (_gate) Write(job); }

    /// <summary>Saves unless the job was cancelled meanwhile; false then.</summary>
    public bool SaveUnlessCancelled(VideoJob job)
    {
        lock (_gate)
        {
            if (Get(job.Id) is { Status: VideoJob.Cancelled }) return false;
            Write(job);
            return true;
        }
    }

    /// <summary>Newest first.</summary>
    public List<VideoJob> All()
    {
        lock (_gate) return Ids().OrderByDescending(i => i).Select(Get).Where(j => j is not null).Cast<VideoJob>().ToList();
    }

    public string WorkDir(long id)
    {
        var d = Path.Combine(_dir, id.ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    private IEnumerable<long> Ids() =>
        Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.json").Select(f => long.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : 0).Where(n => n > 0)
            : [];

    private void Write(VideoJob job)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, job.Id + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(job, Json), new UTF8Encoding(false));
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, true); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(50); }
        }
    }
}

/// <summary>
/// The job runner (contract: the Studio's docs/video-jobs.md). make_video validates a script and queues a job; this
/// works every open job in stages, saving the job after each step so nothing waits on a model while a video renders
/// and a restart picks every job up where it stood:
/// <list type="number">
/// <item><b>prepare</b>: with <c>voice: true</c> every scene line is spoken (<see cref="VoiceMaker"/>) and measured; every
/// <c>make</c> becomes an AI still (<see cref="ImageMaker"/>, in the video's shape) or clip (<see cref="ClipMaker"/>),
/// saved under media/made/. A make that fails becomes a note, and its scene uses the block's fallback.</item>
/// <item><b>ship</b>: every file goes into the renderer's job (<see cref="StudioOps"/> put, as <c>job:&lt;name&gt;</c>), the
/// script is rewritten to those names and the voice objects, and start queues it.</item>
/// <item><b>track</b>: the renderer's status for every rendering job, every two seconds.</item>
/// <item><b>collect</b>: the MP4, the contact sheet, the banner, the project and the report are copied to
/// videos/&lt;id&gt;-&lt;title&gt;/, then ack lets the renderer delete its folder.</item>
/// <item><b>report</b>: the report is printed and handed to the conversation once.</item>
/// </list>
/// </summary>
public sealed class VideoJobs : IDisposable
{
    public const int MaxAttempts = 3;
    private const double ShapeTolerance = 0.19;

    private readonly StudioProject _project;
    private readonly Func<AiOptions> _ai;
    private readonly VideoJobStore _store;
    private readonly ConcurrentDictionary<long, Task> _working = new();
    private readonly SemaphoreSlim _prepareGate = new(2, 2);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _notices = new();
    private readonly ConcurrentDictionary<long, string> _lastLine = new();
    private Task? _loop;
    // spoken lines of this process (text → file): the same line in two jobs is paid for once
    private static readonly ConcurrentDictionary<string, (string path, string mime, double seconds)> _speech = new();

    /// <summary>A line for the operator about a job (progress, a made asset, the final report).</summary>
    public event Action<VideoJob, string>? OnEvent;
    /// <summary>The renderer's own log lines (browser, capture, render progress).</summary>
    public Action<string>? OnRenderLog;

    public VideoJobs(StudioProject project, Func<AiOptions> ai)
    {
        _project = project;
        _ai = ai;
        _store = new VideoJobStore(project.JobsDir);
    }

    public VideoJobStore Store => _store;
    public StudioProject Project => _project;

    public void Start()
    {
        if (_loop is not null) return;
        StudioOps.OwnProcess ??= ChildJob.Own;
        StudioOps.Resume(line => { Log.Info(line); OnRenderLog?.Invoke(line); });
        _loop = Task.Run(LoopAsync);
    }

    public void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>Reports of finished jobs not yet handed to the conversation.</summary>
    public List<string> TakeNotices()
    {
        var list = new List<string>();
        while (_notices.TryDequeue(out var n)) list.Add(n);
        return list;
    }

    public bool AnyOpen => _store.All().Any(j => j.Open);

    /// <summary>Waits until every job of this project is reported (one-shot runs and `render` wait here).</summary>
    public async Task WaitIdleAsync(CancellationToken ct, IReadOnlyCollection<long>? only = null)
    {
        while (!ct.IsCancellationRequested)
        {
            var open = _store.All().Where(j => j.Open && (only is null || only.Contains(j.Id))).ToList();
            if (open.Count == 0 && (only is null || only.All(id => !_working.ContainsKey(id)))) return;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    // ── queueing ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Queues a validated script. <paramref name="reuse"/>: the assets of an earlier job (a remix), reused for
    /// every line and make that did not change.</summary>
    public long Queue(JsonObject script, string request, long? remixOf = null, JsonObject? reuse = null)
    {
        var assets = new JsonObject();
        if (reuse is not null) assets["reuse"] = reuse;
        var id = _store.Insert(new VideoJob
        {
            Title = (Str(script["title"]) ?? "Video").Trim(), Request = request, ScriptJson = script.ToJsonString(),
            AssetsJson = assets.ToJsonString(), RemixOf = remixOf, CreatedAt = DateTime.UtcNow,
        });
        Wake();
        return id;
    }

    /// <summary>Queues a Studio doc (edit_video render): the doc as it is now is what renders.</summary>
    public long QueueDoc(string docRel, string title)
    {
        var full = Path.Combine(_project.Root, docRel);
        var doc = MakeVideoTool.ParseScript(File.ReadAllText(full)) as JsonObject ?? throw new InvalidOperationException(docRel + " is not a JSON object");
        var id = _store.Insert(new VideoJob
        {
            Kind = "doc", DocPath = docRel, Title = title, Request = "render " + docRel, ScriptJson = doc.ToJsonString(),
            Stage = VideoJob.Ship, CreatedAt = DateTime.UtcNow,
        });
        Wake();
        return id;
    }

    /// <summary>Cancels a job: the renderer stops it too. Returns the line for the operator.</summary>
    public string Cancel(long id)
    {
        var job = _store.Get(id);
        if (job is null) return $"There is no video job #{id} here.";
        if (job.Finished || !job.Open) return $"Video job #{id} is already {job.Status}; there is nothing to cancel.";
        _store.Save(job with { Status = VideoJob.Cancelled, Stage = VideoJob.Closed, FinishedAt = DateTime.UtcNow });
        var told = "";
        if (!string.IsNullOrEmpty(job.RenderJob))
        {
            try { StudioOps.Dispatch(new Dictionary<string, object> { { "action", "cancel" }, { "job", job.RenderJob } }, l => Log.Info(l)); told = " and its render was stopped"; }
            catch (Exception ex) { told = $" (the renderer could not be told: {ex.Message})"; }
        }
        return $"Video job #{id} \"{job.Title}\" is cancelled{told}.";
    }

    // ── the loop ───────────────────────────────────────────────────────────────────────────────────────────────

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { Tick(ct); }
            catch (Exception ex) { Log.Warn("[video] tick failed: " + ex.Message); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var open = _store.All().Where(j => j.Open).OrderBy(j => j.Id).ToList();
        var tracks = open.Where(j => j.Stage == VideoJob.Track && !_working.ContainsKey(j.Id)).ToList();
        if (tracks.Count > 0) TrackAll(tracks);
        foreach (var j in open)
        {
            if (j.Stage == VideoJob.Track || _working.ContainsKey(j.Id)) continue;
            if (j.NotBefore is { } nb && nb > now) continue;
            var id = j.Id;
            var task = Task.Run(async () =>
            {
                try { await WorkAsync(id, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn($"[video] #{id}: {ex.Message}"); }
                finally { _working.TryRemove(id, out _); Wake(); }
            });
            _working[id] = task;
        }
    }

    private sealed class JobCancelledException : Exception { }

    private void Say(VideoJob job, string line)
    {
        Log.Info($"[video] #{job.Id} {line}");
        try { OnEvent?.Invoke(job, line); } catch { }
    }

    private VideoJob Persist(VideoJob job)
    {
        if (!_store.SaveUnlessCancelled(job)) throw new JobCancelledException();
        return job;
    }

    // one job: prepare → ship, then (after track) collect → report, step by step while it can go on
    private async Task WorkAsync(long id, CancellationToken ct)
    {
        for (int step = 0; step < 8; step++)
        {
            var job = _store.Get(id);
            if (job is null || !job.Open || job.Stage == VideoJob.Track) return;
            VideoJob next;
            try
            {
                next = job.Stage switch
                {
                    VideoJob.Prepare => await PrepareAsync(job, ct).ConfigureAwait(false),
                    VideoJob.Ship => Ship(job),
                    VideoJob.Collect => Collect(job),
                    VideoJob.Report => Report(job),
                    _ => job,
                };
            }
            catch (JobCancelledException) { return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[video] #{id} {job.Stage}: {ex}");
                next = Retry(_store.Get(id) ?? job, ex.Message);
                if (next.Status == VideoJob.Failed) Say(next, $"failed at {job.Stage}: {ex.Message}");
                else Say(next, $"{job.Stage} went wrong ({ex.Message}); trying again in 15 s");
            }
            if (!_store.SaveUnlessCancelled(next)) return;
            if (next.Stage is VideoJob.Closed or VideoJob.Track || next.NotBefore is not null) return;
        }
    }

    private static VideoJob Retry(VideoJob job, string error)
    {
        var attempts = job.Attempts + 1;
        return attempts >= MaxAttempts
            ? Fail(job with { Attempts = attempts }, error)
            : job with { Attempts = attempts, Error = error, NotBefore = DateTime.UtcNow.AddSeconds(15) };
    }

    private static VideoJob Fail(VideoJob job, string error) => job with
    {
        Status = VideoJob.Failed, Stage = VideoJob.Report, Error = error, FinishedAt = DateTime.UtcNow, NotBefore = null,
    };

    // ── 1. prepare: the voice lines and the AI assets the script asks for ──────────────────────────────────────
    private async Task<VideoJob> PrepareAsync(VideoJob job, CancellationToken ct)
    {
        await _prepareGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var script = ParseObject(job.ScriptJson);
            var assets = ParseObject(job.AssetsJson);
            var reuse = assets["reuse"] as JsonObject;
            var scenes = script["scenes"] as JsonArray ?? new JsonArray();
            var format = Str(script["format"]) ?? "reel";
            var dir = _store.WorkDir(job.Id);

            if (Bool(script["voice"]) == true)
            {
                var voice = assets["voice"] as JsonObject ?? new JsonObject();
                assets["voice"] = voice;
                var lines = scenes.Select(s => Str((s as JsonObject)?["line"])?.Trim() ?? "").ToList();
                int total = lines.Count(l => l.Length > 0), spoken = 0;
                for (int i = 0; i < scenes.Count; i++)
                {
                    var line = Regex.Replace(lines[i], @"\s+", " ").Trim();
                    if (line.Length == 0) continue;
                    spoken++;
                    if (voice[i.ToString()] is JsonObject had && Str(had["text"]) == line && (had["error"] is not null || File.Exists(Str(had["path"]) ?? ""))) continue;
                    JsonObject rec;
                    var old = (reuse?["voice"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(v => Str(v["text"]) == line && File.Exists(Str(v["path"]) ?? ""));
                    if (old is not null)
                    {
                        var copy = Path.Combine(dir, $"voice-{i + 1}{Path.GetExtension(Str(old["path"]))}");
                        File.Copy(Str(old["path"])!, copy, true);
                        rec = new JsonObject { ["text"] = line, ["path"] = copy, ["mime"] = Str(old["mime"]), ["seconds"] = Num(old["seconds"]) ?? 0 };
                        Say(job, $"voice {spoken}/{total}: reused from job #{job.RemixOf}");
                    }
                    else
                    {
                        try
                        {
                            var (path, mime, seconds) = await SpeakAsync(line, dir, $"voice-{i + 1}", ct).ConfigureAwait(false);
                            rec = new JsonObject { ["text"] = line, ["path"] = path, ["mime"] = mime, ["seconds"] = Math.Round(seconds, 3) };
                            Say(job, $"voice {spoken}/{total} spoken ({N(seconds)} s)");
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { rec = new JsonObject { ["text"] = line, ["error"] = ex.Message }; Say(job, $"voice {spoken}/{total} could not be spoken: {ex.Message}"); }
                    }
                    voice[i.ToString()] = rec;
                    job = Persist(job with { AssetsJson = assets.ToJsonString() });
                }
            }

            var made = assets["make"] as JsonObject ?? new JsonObject();
            assets["make"] = made;
            for (int i = 0; i < scenes.Count; i++)
            {
                if ((scenes[i] as JsonObject)?["files"] is not JsonObject files) continue;
                foreach (var (slot, value) in files.ToList())
                {
                    if (value is not JsonObject make || Str(make["make"]) is not { } kind) continue;
                    var key = $"{i}/{slot}";
                    var prompt = Str(make["prompt"]) ?? "";
                    var from = Str(make["from"]);
                    if (made[key] is JsonObject had && Str(had["prompt"]) == prompt && Str(had["from"]) == from
                        && (had["error"] is not null || File.Exists(Path.Combine(_project.Root, Str(had["path"]) ?? "\0")))) continue;
                    var old = (reuse?["make"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => Str(m["kind"]) == kind && Str(m["prompt"]) == prompt
                        && Str(m["from"]) == from && File.Exists(Path.Combine(_project.Root, Str(m["path"]) ?? "\0")));
                    if (old is not null) { made[key] = old.DeepClone(); Say(job, $"{kind} for scene {i + 1}: reused {Str(old["path"])}"); }
                    else made[key] = await MakeAsync(job, i, slot, kind, prompt, from, format, ct).ConfigureAwait(false);
                    job = Persist(job with { AssetsJson = assets.ToJsonString() });
                }
            }
            return job with { AssetsJson = assets.ToJsonString(), Stage = VideoJob.Ship, Error = null, NotBefore = null };
        }
        finally { _prepareGate.Release(); }
    }

    private async Task<(string path, string mime, double seconds)> SpeakAsync(string text, string dir, string stem, CancellationToken ct)
    {
        if (_speech.TryGetValue(text, out var had) && File.Exists(had.path))
        {
            var copy = Path.Combine(dir, stem + Path.GetExtension(had.path));
            if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(had.path), StringComparison.OrdinalIgnoreCase)) File.Copy(had.path, copy, true);
            return (copy, had.mime, had.seconds);
        }
        var r = await VoiceMaker.SpeakAsync(_ai(), text, ct, line => Log.Warn("[voice] " + line)).ConfigureAwait(false);
        var path = Path.Combine(dir, stem + ExtFor("", r.Mime));
        await File.WriteAllBytesAsync(path, r.Bytes, ct).ConfigureAwait(false);
        var seconds = MediaSeconds(path);
        _speech[text] = (path, r.Mime, seconds);
        return (path, r.Mime, seconds);
    }

    /// <summary>One AI asset → media/made/, recorded as {kind, prompt, from, path} or, when it cannot be made, {error}.</summary>
    private async Task<JsonObject> MakeAsync(VideoJob job, int scene, string slot, string kind, string prompt, string? from, string format, CancellationToken ct)
    {
        var rec = new JsonObject { ["kind"] = kind, ["prompt"] = prompt, ["from"] = from };
        (byte[] bytes, string mime)? picture = null;
        if (!string.IsNullOrWhiteSpace(from))
        {
            var (f, err) = _project.Resolve(from);
            if (f is null) { rec["error"] = err; return rec; }
            if (kind == "clip")
            {
                // a video model redraws every pixel of its first frame: a small picture comes out smeared
                var (pw, ph) = ImageSize(f.Path);
                if (pw > 0 && ph > 0 && (Math.Max(pw, ph) < 1000 || Math.Min(pw, ph) < 560))
                {
                    rec["error"] = $"{f.Name} is only {pw}x{ph} px, and a video model needs a sharp picture of at least 1000 px on its long side";
                    Say(job, $"clip for scene {scene + 1} not made: {rec["error"]}");
                    return rec;
                }
            }
            picture = (await File.ReadAllBytesAsync(f.Path, ct).ConfigureAwait(false), f.Mime);
        }
        if (kind == "clip" && picture is null) { rec["error"] = "a clip is made from a picture, and none was given"; return rec; }
        // one AI make at a time in this process: the takes of one ad prepare side by side and asked Google's image model
        // for their pictures in the same second, and the second request was throttled (429) and its scenes fell back to
        // text cards (2026-10-09). The same request (kind, prompt, picture, shape) is made once and shared: takes ask
        // for the same pictures.
        var key = $"{kind}|{format}|{from}|{prompt}";
        await _makeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_made.TryGetValue(key, out var had) && File.Exists(Path.Combine(_project.Root, had)))
            {
                rec["path"] = had;
                Say(job, $"{kind} for scene {scene + 1}: the same one another job made, {had}");
                return rec;
            }
            var made = await MakeNowAsync(job, scene, slot, kind, prompt, picture, format, rec, ct).ConfigureAwait(false);
            if (Str(made["path"]) is { } p) _made[key] = p;
            return made;
        }
        finally { _makeGate.Release(); }
    }

    private static readonly SemaphoreSlim _makeGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, string> _made = new();

    private async Task<JsonObject> MakeNowAsync(VideoJob job, int scene, string slot, string kind, string prompt, (byte[] bytes, string mime)? picture,
        string format, JsonObject rec, CancellationToken ct)
    {
        Say(job, $"making a {kind} for scene {scene + 1}: {Short(prompt)}");
        byte[] bytes; string ext;
        try
        {
            if (kind == "clip")
            {
                bytes = await Throttled(() => ClipMaker.RenderAsync(_ai(), picture!.Value, prompt, ct, s => Log.Info($"[video] #{job.Id} clip: {s}")), job, ct).ConfigureAwait(false);
                ext = ".mp4";
            }
            else
            {
                bytes = await StillAsync(job, prompt, picture?.bytes, format, ct).ConfigureAwait(false);
                ext = ExtFor("", SniffImageMime(bytes));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { rec["error"] = ex.Message; Say(job, $"{kind} for scene {scene + 1} could not be made: {ex.Message}"); return rec; }
        if (bytes is not { Length: > 0 }) { rec["error"] = "the provider returned nothing"; return rec; }
        Directory.CreateDirectory(_project.MadeDir);
        var name = $"{kind}-{job.Id}-s{scene + 1}-{Slug(slot, "file")}-{Guid.NewGuid().ToString("n")[..4]}{ext}";
        var path = Path.Combine(_project.MadeDir, name);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        rec["path"] = Path.GetRelativePath(_project.Root, path).Replace('\\', '/');
        Say(job, $"{kind} for scene {scene + 1} saved as {rec["path"]}");
        return rec;
    }

    /// <summary>A still in the video's shape. The shape is said in the prompt as well as asked as the size; what comes
    /// back is measured, and a wrong shape is asked once more from the same profile, then from each other image
    /// profile in turn (at most 4 pictures). When none draws it, the closest one is cropped to the frame's shape.</summary>
    private async Task<byte[]> StillAsync(VideoJob job, string prompt, byte[]? reference, string format, CancellationToken ct)
    {
        var (fw, fh) = FormatSize(format);
        double want = (double)fw / fh;
        var candidates = ImageMaker.Candidates(_ai());
        if (candidates.Count == 0) throw new InvalidOperationException("no AI profile can make pictures (OpenAI, the Antigravity login or Alibaba)");
        if (reference is not null) prompt = "Use the attached picture as the reference: keep its subject, and " + prompt;
        const string noText = " No text, no letters, no logos, no signage and no watermarks anywhere in the picture.";
        (byte[] b, double off)? best = null;
        Exception? last = null;
        int drawn = 0;
        var plan = new List<(AiProfile p, bool again)> { (candidates[0], false), (candidates[0], true) };
        plan.AddRange(candidates.Skip(1).Select(c => (c, true)));
        foreach (var (p, again) in plan)
        {
            if (drawn >= 4) break;
            if (again && best is null && last is not null && p == candidates[0]) continue;   // that profile failed outright: go to the next
            try
            {
                drawn++;
                var bytes = await Throttled(() => ImageMaker.DrawAsync(p, ShapeWords(format, again) + " " + prompt + noText, fw, fh, ct, reference), job, ct).ConfigureAwait(false);
                var (w, h) = ImageSize(bytes);
                var off = w > 0 && h > 0 ? Math.Abs(Math.Log((double)w / h / want)) : 0;
                if (off > ShapeTolerance) Log.Info($"[video] #{job.Id} still: {p.Name} drew {w}x{h}, not {AspectOf(format)}");
                if (best is null || off < best.Value.off) best = (bytes, off);
                if (off <= ShapeTolerance) return bytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; Log.Warn($"[video] #{job.Id} still on {p.Name}: {ex.Message}"); }
        }
        if (best is null) throw last ?? new InvalidOperationException("no picture was drawn");
        return CropTo(best.Value.b, want);
    }

    /// <summary>A provider call that is tried again when the provider is only busy: a 429 / RESOURCE_EXHAUSTED or a 503
    /// waits the reset the answer names ("reset after 7s"), else 5, 15 then 30 s; three more tries at most. Anything
    /// else fails at once.</summary>
    private async Task<T> Throttled<T>(Func<Task<T>> call, VideoJob job, CancellationToken ct)
    {
        int[] waits = [5, 15, 30];
        for (int attempt = 0; ; attempt++)
        {
            try { return await call().ConfigureAwait(false); }
            catch (Exception ex) when (attempt < waits.Length && !ct.IsCancellationRequested
                && Regex.IsMatch(ex.Message, @"\b(429|503)\b|RESOURCE_EXHAUSTED|UNAVAILABLE|rate.?limit|Throttling", RegexOptions.IgnoreCase))
            {
                var named = Regex.Match(ex.Message, @"reset after (\d+(?:\.\d+)?)\s*s", RegexOptions.IgnoreCase);
                var wait = named.Success ? Math.Min(120, double.Parse(named.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 2) : waits[attempt];
                Say(job, $"the provider is busy; trying again in {N(wait)} s");
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
            }
        }
    }

    // the shape in words, at the start of the prompt; a second try says it more plainly
    private static string ShapeWords(string format, bool again) => (format switch
    {
        "square" => "A square 1:1 photograph.",
        "portrait" => "A vertical 4:5 photograph, taller than wide.",
        "landscape" => "A horizontal 16:9 photograph, wider than tall.",
        _ => "A vertical 9:16 photograph, taller than wide, composed for a phone screen.",
    }) + (again ? " The whole picture has exactly this shape, not another one." : "");

    internal static byte[] CropTo(byte[] bytes, double want)
    {
        try
        {
            using var img = Image.Load(bytes);
            double have = (double)img.Width / img.Height;
            if (Math.Abs(Math.Log(have / want)) <= ShapeTolerance) return bytes;
            int w = img.Width, h = img.Height;
            if (have > want) w = (int)Math.Round(h * want); else h = (int)Math.Round(w / want);
            img.Mutate(x => x.Crop(new Rectangle((img.Width - w) / 2, (img.Height - h) / 2, w, h)));
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }
        catch { return bytes; }
    }

    // ── 2. ship: the files, the script that names them, start ──────────────────────────────────────────────────
    private VideoJob Ship(VideoJob job)
    {
        var renderJob = job.RenderJob ?? $"v{job.Id}-{Guid.NewGuid().ToString("n")[..6]}";
        job = Persist(job with { RenderJob = renderJob });
        if (job.Kind == "doc") return ShipDoc(job, renderJob);
        var (script, files, notes) = BuildShipment(job);
        var assets = ParseObject(job.AssetsJson);
        assets["notes"] = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        foreach (var (name, path) in files)
            StudioOps.Dispatch(new Dictionary<string, object> { { "action", "put" }, { "job", renderJob }, { "name", name }, { "b64", Convert.ToBase64String(File.ReadAllBytes(path)) } }, l => Log.Info(l));
        var started = StudioOps.Dispatch(new Dictionary<string, object>
        {
            { "action", "start" }, { "job", renderJob }, { "script_json", script.ToJsonString() }, { "name", Slug(job.Title, "video") }, { "studio_url", MakeVideoTool.StudioUrl() },
        }, line => { Log.Info(line); OnRenderLog?.Invoke(line); });
        var state = Convert.ToString(started.GetValueOrDefault("state")) ?? "queued";
        Say(job, $"sent to the Studio renderer ({files.Count} file(s)){(state == "queued" && started.GetValueOrDefault("position") is int pos && pos > 0 ? $", number {pos} in line" : "")}");
        return job with
        {
            AssetsJson = assets.ToJsonString(), Status = VideoJob.Rendering, Stage = VideoJob.Track, Pct = 0, Error = null, NotBefore = null,
            RenderStage = state, Position = started.GetValueOrDefault("position") is int p ? p : null,
        };
    }

    private VideoJob ShipDoc(VideoJob job, string renderJob)
    {
        var doc = ParseObject(job.ScriptJson);
        var docDir = Path.GetDirectoryName(Path.Combine(_project.Root, job.DocPath ?? "")) ?? _project.Root;
        var files = new Dictionary<string, string>();
        var forRenderer = StudioDoc.ForRenderer(doc, docDir, _project, files);
        foreach (var (name, path) in files)
            StudioOps.Dispatch(new Dictionary<string, object> { { "action", "put" }, { "job", renderJob }, { "name", name }, { "b64", Convert.ToBase64String(File.ReadAllBytes(path)) } }, l => Log.Info(l));
        var started = StudioOps.Dispatch(new Dictionary<string, object>
        {
            { "action", "start" }, { "job", renderJob }, { "doc_json", forRenderer.ToJsonString() }, { "name", Slug(job.Title, "video") }, { "studio_url", MakeVideoTool.StudioUrl() },
        }, line => { Log.Info(line); OnRenderLog?.Invoke(line); });
        var state = Convert.ToString(started.GetValueOrDefault("state")) ?? "queued";
        Say(job, $"sent to the Studio renderer ({files.Count} file(s))");
        return job with
        {
            Status = VideoJob.Rendering, Stage = VideoJob.Track, Pct = 0, Error = null, NotBefore = null,
            RenderStage = state, Position = started.GetValueOrDefault("position") is int p ? p : null,
        };
    }

    /// <summary>The script as the renderer gets it: every file reference replaced by <c>job:&lt;name&gt;</c> (each source
    /// shipped once), a voice object on every spoken scene, and the notes on what could not be made. A slot whose file is
    /// gone (a failed make, a file deleted since the submit) is left out: the block then uses its fallback.</summary>
    internal (JsonObject script, List<(string name, string path)> files, List<string> notes) BuildShipment(VideoJob job)
    {
        var script = ParseObject(job.ScriptJson);
        var assets = ParseObject(job.AssetsJson);
        var notes = new List<string>();
        foreach (var n in script["notes"] as JsonArray ?? new JsonArray()) if (Str(n) is { Length: > 0 } s) notes.Add(s);
        var files = new List<(string name, string path)>();
        var bySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Put(string path, string stem)
        {
            if (bySource.TryGetValue(path, out var had)) return had;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var clean = Regex.Replace(stem, @"[^A-Za-z0-9._-]+", "-");
            var name = clean + ext;
            for (int k = 2; !used.Add(name); k++) name = clean + "-" + k + ext;
            bySource[path] = name;
            files.Add((name, path));
            return name;
        }
        var voiced = Bool(script["voice"]) == true;
        var voice = assets["voice"] as JsonObject;
        var made = assets["make"] as JsonObject;
        var scenes = script["scenes"] as JsonArray ?? new JsonArray();
        for (int i = 0; i < scenes.Count; i++)
        {
            if (scenes[i] is not JsonObject sc) continue;
            var label = $"scene {i + 1} ({Str(sc["block"])})";
            if (sc["files"] is JsonObject slots)
            {
                foreach (var (slot, value) in slots.ToList())
                {
                    if (Str(value) is { } reference)
                    {
                        var (f, err) = _project.Resolve(reference);
                        if (f is null) { slots.Remove(slot); notes.Add($"{label}: {slot}: {err} The scene uses its fallback."); continue; }
                        slots[slot] = "job:" + Put(f.Path, $"s{i + 1}-{slot}");
                    }
                    else if (value is JsonObject make)
                    {
                        var rec = made?[$"{i}/{slot}"] as JsonObject;
                        var rel = Str(rec?["path"]);
                        var full = rel is null ? null : Path.Combine(_project.Root, rel);
                        if (full is not null && File.Exists(full)) slots[slot] = "job:" + Put(full, $"s{i + 1}-{slot}");
                        else
                        {
                            slots.Remove(slot);
                            var what = Str(make["make"]) == "clip" ? "clip" : "picture";
                            notes.Add($"{label}: the {what} for {slot} (\"{Short(Str(make["prompt"]) ?? "")}\") could not be made: " +
                                      $"{Str(rec?["error"]) ?? "it was never made"}. The scene uses its fallback.");
                        }
                    }
                }
                if (slots.Count == 0) sc.Remove("files");
            }
            if (voiced && (Str(sc["line"])?.Trim().Length ?? 0) > 0)
            {
                var rec = voice?[i.ToString()] as JsonObject;
                var path = Str(rec?["path"]);
                if (path is not null && File.Exists(path))
                    sc["voice"] = new JsonObject { ["src"] = "job:" + Put(path, $"voice-{i + 1}"), ["seconds"] = Num(rec!["seconds"]) ?? 0 };
                else notes.Add($"{label}: its line could not be spoken ({Str(rec?["error"]) ?? "no voice was rendered"}).");
            }
        }
        if (script["brand"] is JsonObject brand && Str(brand["logo"]) is { } logoRef)
        {
            var (f, err) = _project.Resolve(logoRef);
            if (f is null) { brand.Remove("logo"); notes.Add($"brand logo: {err} The video uses the brand's name instead."); }
            else brand["logo"] = "job:" + Put(f.Path, "logo");
        }
        script.Remove("takes");
        script.Remove("remix_of");
        if (notes.Count > 0) script["notes"] = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        return (script, files, notes);
    }

    // ── 3. track: the renderer's status for every rendering job ────────────────────────────────────────────────
    private void TrackAll(List<VideoJob> rows)
    {
        Dictionary<string, object> res;
        try { res = StudioOps.Dispatch(new Dictionary<string, object> { { "action", "status" }, { "jobs", rows.Select(r => (object)(r.RenderJob ?? "")).ToList() } }, l => Log.Info(l)); }
        catch (Exception ex) { Log.Warn("[video] status: " + ex.Message); return; }
        var entries = ((System.Collections.IEnumerable)res["jobs"]).Cast<Dictionary<string, object>>().ToList();
        var wake = false;
        foreach (var r in rows)
        {
            var e = entries.FirstOrDefault(x => string.Equals(Convert.ToString(x.GetValueOrDefault("job")), r.RenderJob, StringComparison.Ordinal));
            var state = Convert.ToString(e?.GetValueOrDefault("state")) ?? "unknown";
            VideoJob next;
            switch (state)
            {
                case "queued":
                case "running":
                    var pct = Convert.ToDouble(e!.GetValueOrDefault("pct") ?? 0, System.Globalization.CultureInfo.InvariantCulture) / 100.0;
                    var stage = state == "queued" ? "queued" : Convert.ToString(e.GetValueOrDefault("stage")) is { Length: > 0 } s ? s : "running";
                    next = r with { Pct = pct, RenderStage = stage, Position = state == "queued" ? Convert.ToInt32(e.GetValueOrDefault("position") ?? 0) : null, Error = null };
                    var line = state == "queued" ? $"waiting for the renderer (number {next.Position} in line)" : $"{stage} {Math.Round(pct * 100 / 10) * 10:0}%";
                    if (!_lastLine.TryGetValue(r.Id, out var prev) || prev != line) { _lastLine[r.Id] = line; Say(next, line); }
                    break;
                case "done":
                    var assets = ParseObject(r.AssetsJson);
                    var outFiles = e!.GetValueOrDefault("files") as Dictionary<string, object>;
                    var o = new JsonObject();
                    if (outFiles is not null) foreach (var kv in outFiles) o[kv.Key] = Convert.ToString(kv.Value);
                    assets["out"] = o;
                    next = r with
                    {
                        AssetsJson = assets.ToJsonString(), Stage = VideoJob.Collect, Pct = 1, RenderStage = "done", Position = null,
                        Duration = Convert.ToDouble(e.GetValueOrDefault("duration") ?? 0, System.Globalization.CultureInfo.InvariantCulture),
                        Bytes = Convert.ToInt64(e.GetValueOrDefault("bytes") ?? 0L, System.Globalization.CultureInfo.InvariantCulture),
                    };
                    wake = true;
                    break;
                case "failed":
                    next = Fail(r, "the Studio could not make it: " + (Convert.ToString(e!.GetValueOrDefault("error")) is { Length: > 0 } er ? er : "it stopped without saying why"));
                    wake = true;
                    break;
                case "cancelled":
                    next = Fail(r, "the render was cancelled");
                    wake = true;
                    break;
                default:
                    // the renderer lost the job's folder: ship it again, three times at most
                    var attempts = r.Attempts + 1;
                    next = attempts >= MaxAttempts
                        ? Fail(r with { Attempts = attempts }, $"the renderer lost the job {attempts} times")
                        : r with { Attempts = attempts, Status = VideoJob.Queued, Stage = VideoJob.Ship, RenderJob = null, RenderStage = null, Position = null, Error = "the renderer had lost the job; it is sent again" };
                    wake = true;
                    break;
            }
            _store.SaveUnlessCancelled(next);
        }
        if (wake) Wake();
    }

    // ── 4. collect: the files into videos/<id>-<title>/, then ack ──────────────────────────────────────────────
    private VideoJob Collect(VideoJob job)
    {
        var assets = ParseObject(job.AssetsJson);
        if (assets["out"] is not JsonObject outFiles || Str(outFiles["video"]) is not { Length: > 0 } videoRel)
            return Fail(job, "the renderer finished the video but did not say where the file is");
        var slug = Slug(job.Title, "video");
        var outDir = Path.Combine(_project.VideosDir, $"{job.Id}-{slug}");
        Directory.CreateDirectory(outDir);
        var src = Path.Combine(StudioOps.Root, job.RenderJob ?? "");
        string? Take(string kind, string target)
        {
            if (Str(outFiles[kind]) is not { Length: > 0 } rel) return null;
            var from = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(from)) { Log.Warn($"[video] #{job.Id} {kind}: {from} is missing"); return null; }
            var to = Path.Combine(outDir, target);
            File.Copy(from, to, true);
            return Path.GetRelativePath(_project.Root, to).Replace('\\', '/');
        }
        var video = Take("video", slug + (Path.GetExtension(videoRel) is { Length: > 1 } e ? e.ToLowerInvariant() : ".mp4"))
                    ?? throw new InvalidOperationException("the rendered video is missing from the renderer's folder");
        var sheet = Take("sheet", slug + "-sheet.jpg");
        var poster = Take("poster", slug + "-banner.png");
        var project = Take("project", slug + ".vstudio.json");
        var report = Take("report", "report.json");
        string? reportJson = null;
        if (report is not null) try { reportJson = JsonNode.Parse(File.ReadAllText(Path.Combine(_project.Root, report)))?.ToJsonString(); } catch { }
        // the script as it was written, so `vanity-studio render` can make it again
        try { File.WriteAllText(Path.Combine(outDir, "script.json"), JsonNode.Parse(job.ScriptJson)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); } catch { }
        try { StudioOps.Dispatch(new Dictionary<string, object> { { "action", "ack" }, { "job", job.RenderJob ?? "" } }, l => Log.Info(l)); }
        catch (Exception ex) { Log.Warn($"[video] #{job.Id} ack: {ex.Message}"); }
        return job with
        {
            Status = VideoJob.Done, Stage = VideoJob.Report, Pct = 1, Error = null, FinishedAt = DateTime.UtcNow, NotBefore = null,
            OutDir = Path.GetRelativePath(_project.Root, outDir).Replace('\\', '/'), Video = video, Sheet = sheet, Poster = poster, Project = project,
            ReportPath = report, ReportJson = reportJson,
        };
    }

    // ── 5. report: printed, and handed to the conversation once ────────────────────────────────────────────────
    private VideoJob Report(VideoJob job)
    {
        var (handIn, raw) = BuildReport(job);
        Say(job, raw);
        _notices.Enqueue(handIn);
        return job with { Stage = VideoJob.Closed };
    }

    /// <summary>The hand-in for the conversation and the report printed for the operator.</summary>
    public (string handIn, string raw) BuildReport(VideoJob job)
    {
        var script = ParseObject(job.ScriptJson);
        var assets = ParseObject(job.AssetsJson);
        var report = ParseObject(job.ReportJson ?? "{}");
        var notes = new List<string>();
        foreach (var n in (report["notes"] as JsonArray ?? new JsonArray()).Concat(assets["notes"] as JsonArray ?? new JsonArray()))
            if (Text(n) is { Length: > 0 } s && !notes.Contains(s)) notes.Add(s);
        var head = $"[Result of video job #{job.Id}, queued with make_video: this is not a message typed by the operator]\n";
        if (job.Status != VideoJob.Done)
        {
            var why = string.IsNullOrWhiteSpace(job.Error) ? "it stopped without a reason" : job.Error.Trim().TrimEnd('.');
            var noteText = notes.Count > 0 ? "\nNotes:\n- " + string.Join("\n- ", notes) : "";
            return (head + $"The video \"{job.Title}\" was not made: {why}.{noteText}",
                    $"✗ The video \"{job.Title}\" (job #{job.Id}) could not be made: {why}.{noteText}");
        }
        var format = Str(script["format"]) ?? "reel";
        var (fw, fh) = FormatSize(format);
        var duration = Num(report["duration"]) ?? job.Duration;
        var sb = new StringBuilder();
        if (job.Kind == "doc") sb.Append($"Video: \"{job.Title}\", {N(duration)} s, rendered from the Studio project {job.DocPath}.\n");
        else sb.Append($"Video: \"{job.Title}\", {N(duration)} s, {format} ({fw}x{fh}).\n");
        sb.Append($"The video: {job.Video}\n");
        if (job.Poster is not null) sb.Append($"The last frame as a picture (a static banner, PNG): {job.Poster}\n");
        if (job.Sheet is not null) sb.Append($"Contact sheet (the middle of every scene): {job.Sheet}\n");
        if (job.Project is not null) sb.Append($"Project file (edit_video open source={job.Id} to change anything in it; /edit {job.Id} opens it in the Studio in the browser): {job.Project}\n");
        if (job.Kind != "doc") sb.Append("Scenes:\n");
        var reported = (report["scenes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var written = job.Kind == "doc" ? new List<JsonObject>() : (script["scenes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var count = Math.Max(reported.Count, written.Count);
        for (int i = 0; i < count; i++)
        {
            var r = i < reported.Count ? reported[i] : null;
            var w = i < written.Count ? written[i] : null;
            var block = Str(r?["block"]) ?? Str(w?["block"]) ?? "?";
            var line = Str(r?["line"]) ?? Str(w?["line"]) ?? "";
            var start = Num(r?["start"]); var dur = Num(r?["dur"]);
            var when = start is { } s0 && dur is { } d0 ? $", {N(s0)}-{N(s0 + d0)} s" : "";
            var sceneNotes = (r?["notes"] as JsonArray ?? new JsonArray()).Select(Text).Where(x => x is { Length: > 0 }).ToList();
            sb.Append($"{i + 1}. {block}{when}" + (line.Length > 0 ? $": \"{line}\"" : "") + (sceneNotes.Count > 0 ? " (" + string.Join("; ", sceneNotes) + ")" : "") + "\n");
        }
        if (notes.Count > 0) sb.Append("Notes:\n- " + string.Join("\n- ", notes) + "\n");
        var checks = (report["checks"] as JsonArray ?? new JsonArray()).Concat(report["warnings"] as JsonArray ?? new JsonArray())
            .Select(Text).Where(x => x is { Length: > 0 }).Distinct().Take(8).ToList();
        if (checks.Count > 0) sb.Append("Checks:\n- " + string.Join("\n- ", checks) + "\n");
        var facts = sb.ToString().TrimEnd();
        return (head + facts, $"✓ The video \"{job.Title}\" is ready (job #{job.Id}).\n" + facts);
    }

    /// <summary>One line per job for /jobs and make_video status.</summary>
    public static string Line(VideoJob j)
    {
        var head = $"#{j.Id} \"{j.Title}\": ";
        var problem = string.IsNullOrWhiteSpace(j.Error) ? "" : $" (last problem: {j.Error})";
        return j.Status switch
        {
            VideoJob.Cancelled => head + "cancelled",
            VideoJob.Failed => head + "failed: " + (j.Error ?? "no reason given"),
            VideoJob.Done => head + $"done, {N(j.Duration)} s: {j.Video}",
            VideoJob.Rendering when j.Stage == VideoJob.Collect => head + "rendered, the files are being collected",
            VideoJob.Rendering when j.RenderStage == "queued" => head + "waiting for the renderer" + (j.Position is int p ? $", number {p} in line" : "") + problem,
            VideoJob.Rendering => head + $"rendering ({j.RenderStage ?? "running"}, {Math.Round(j.Pct * 100)}%)" + problem,
            _ => head + (j.Stage == VideoJob.Prepare ? "preparing its voice lines and pictures" : "being sent to the renderer") + problem,
        };
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }
}
