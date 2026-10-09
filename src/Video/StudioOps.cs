using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VanityStudio.Infra;

#nullable disable

namespace VanityStudio.Video
{
    /// <summary>
    /// The renderer: videos are made through the live Vanity Studio (photovideoeditor.com/app). The job runner
    /// (<see cref="VideoJobs"/>) hands it a video SCRIPT and its media as a durable job; it queues it and runs it in a
    /// private headless Chrome on a job page served ON the Studio's origin, where the Studio's own
    /// <c>js/video/director.js</c> compiles the script into a normal Studio doc and renders it. The MP4, the contact
    /// sheet, the report and the editable project stay in the job folder until the runner has pulled them. Nothing is
    /// installed: no Node, no local copy of the Studio, and the Studio itself is never changed.
    ///
    /// The job page and the job's media never touch the network: Chrome's request interception (CDP Fetch) answers
    /// <c>&lt;studio&gt;/__vanity_job.html</c> with the job page and
    /// <c>&lt;studio&gt;/__vanity_job/&lt;job&gt;/media/&lt;name&gt;</c> with the file from this machine, so the page
    /// sees same-origin URLs (no CORS, no mixed content, no local server).
    ///
    /// Actions:
    ///   put    { job, name, b64 }                       → { name, size }: a media file into the job (an existing one is overwritten)
    ///   start  { job, script_json, name?, studio_url? } → { job, state, position }: saves the script and queues the job;
    ///                                                     every "job:&lt;name&gt;" in it must have been put. Sent again for a
    ///                                                     queued, running or done job it only answers; a failed or cancelled
    ///                                                     job starts over with the script it brings (the media stay).
    ///   status { jobs: [ids] } or { job }               → { jobs: [ { job, state, pct, stage, position, error, seconds,
    ///                                                     files: { video, sheet, poster, project, report }, bytes, duration } ] }
    ///   get    { job, path, offset, max }               → a slice of a job file (the runner pulls the MP4 back)
    ///   cancel { job }                                  → { job, cancelled }: a queued job leaves the queue, a running one stops
    ///   ack    { job }                                  → { job, deleted }: the runner has everything; the folder goes
    ///
    /// Durable: studio-jobs/&lt;job&gt;/job.json holds the job's state (written atomically on every change) beside its
    /// script.json; a restart puts the jobs that were running back in the queue (<see cref="Resume"/>). Jobs run in the
    /// order they came, <see cref="MaxParallel"/> at a time, each in its own Chrome; a folder stays until its ack or for
    /// 7 days. The host sets <see cref="OwnProcess"/> so Chrome dies with it.
    /// </summary>
    public static class StudioOps
    {
        public const string DefaultStudioUrl = "https://photovideoeditor.com/app/";
        /// <summary>Ties a spawned browser to the host's lifetime (the host wires its job object here).</summary>
        public static Action<Process> OwnProcess;
        /// <summary>Where jobs live: one folder per job with job.json, script.json, media/ and out/.</summary>
        public static string Root = Path.Combine(AgentConfig.Dir, "studio-jobs");
        /// <summary>Optional explicit browser (else the BrowserPath setting or CHROME_PATH, then Chrome, then Edge).</summary>
        public static string BrowserPath;
        /// <summary>How many videos render at the same time, each in its own Chrome: 2, unless the environment variable
        /// VANITY_VIDEO_PARALLEL or the VideoParallel setting in config.json says otherwise (at most 8). Read once; a host
        /// may set it.</summary>
        public static int MaxParallel = ReadMaxParallel();

        private const long MaxPut = 200L * 1024 * 1024;
        private const int KeepDays = 7;
        private static readonly JsonSerializerOptions FileJson = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private static readonly JsonSerializerOptions CompactJson = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // The jobs this process knows, the waiting line and the running ones. Every change happens under this lock and
        // is written to that job's job.json before the lock is let go.
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _queue = new List<string>();
        private static readonly Dictionary<string, Slot> _running = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        // a standalone capture (CaptureAsync) holds a browser profile too: never swept as a leftover while it works
        private static readonly ConcurrentDictionary<string, byte> _captures = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // where the workers write: the host's log (set by Resume), else the log of the first start
        private static Action<string> _log;
        private static long _seq;

        public static Dictionary<string, object> Dispatch(Dictionary<string, object> p, Action<string> log)
        {
            log ??= _ => { };
            var action = (Str(p, "action") ?? "").Trim().ToLowerInvariant();
            switch (action)
            {
                case "put": return Put(p);
                case "start": return Start(p, log);
                case "status": return Status(p);
                case "get": return Get(p);
                case "cancel": return Cancel(p, log);
                case "ack": return Ack(p, log);
                case "site": return Site(p, log);
                case "probe": return Probe(p, log);
                case "read": return Site(p, log);
                default: throw new ArgumentException("unknown studio action '" + action + "' (put|start|status|get|cancel|ack|site)");
            }
        }

        /// <summary>Once at the host's start: closes the Studio browsers the last process left, puts every job it was
        /// running back in the queue (in the order the jobs came) and starts the workers. Safe to call again: jobs this
        /// process already knows are left as they are. <paramref name="ownProcess"/> becomes <see cref="OwnProcess"/>
        /// when none is set yet, so a resumed job's Chrome dies with the host too. Never throws.</summary>
        public static void Resume(Action<string> log, Action<Process> ownProcess = null)
        {
            if (log != null) _log = log;
            if (ownProcess != null && OwnProcess == null) OwnProcess = ownProcess;
            var lg = _log ?? (_ => { });
            try
            {
                CleanOldJobs();
                CloseLeftoverBrowsers(lg);
                var found = new List<Job>();
                if (Directory.Exists(Root))
                    foreach (var d in Directory.GetDirectories(Root))
                        if (IsJobId(Path.GetFileName(d)) && LoadJob(d) is { } j) found.Add(j);
                int requeued = 0, waiting;
                lock (_lock)
                {
                    foreach (var j in found)
                    {
                        if (_jobs.ContainsKey(j.Id)) continue;
                        _jobs[j.Id] = j;
                        _seq = Math.Max(_seq, j.Seq);
                    }
                    // a job marked running that no worker of this process runs was cut off by a stop: it starts over
                    foreach (var j in _jobs.Values)
                    {
                        if (j.State == "running" && !_running.ContainsKey(j.Id))
                        {
                            j.State = "queued"; j.Stage = ""; j.Pct = 0; j.Started = null;
                            Save(j);
                            requeued++;
                        }
                        if (j.State == "queued" && !_queue.Contains(j.Id, StringComparer.OrdinalIgnoreCase)) _queue.Add(j.Id);
                    }
                    long SeqOf(string id) => _jobs.TryGetValue(id, out var q) ? q.Seq : 0;
                    _queue.Sort((a, b) => SeqOf(a).CompareTo(SeqOf(b)));
                    SyncPositions();
                    waiting = _queue.Count;
                }
                if (waiting > 0) lg($"[studio] {waiting} video job(s) waiting to run ({requeued} were cut off when Vanity Studio stopped)");
                Pump();
            }
            catch (Exception ex) { lg("[studio] could not resume the video jobs: " + ex.Message); }
        }

        // ── a job: in memory and in studio-jobs\<job>\job.json ─────────────────
        private sealed class Job
        {
            public string Id, State = "queued", Stage = "", Error, Name = "video", StudioUrl, Kind = "script";
            public int Pct, Position;
            public long Seq;
            public DateTime Created;
            public DateTime? Started, Finished;
            public Dictionary<string, string> Files;
            public long Bytes;
            public double Duration, Seconds;
            public bool Gone;   // acked or cleaned away: never written again

            public JsonObject ToJson()
            {
                var o = new JsonObject
                {
                    ["job"] = Id, ["state"] = State, ["stage"] = Stage, ["pct"] = Pct, ["position"] = Position, ["error"] = Error,
                    ["name"] = Name, ["studio_url"] = StudioUrl, ["kind"] = Kind, ["seq"] = Seq,
                    ["created"] = Created.ToString("o", CultureInfo.InvariantCulture),
                    ["started"] = Started?.ToString("o", CultureInfo.InvariantCulture),
                    ["finished"] = Finished?.ToString("o", CultureInfo.InvariantCulture),
                    ["bytes"] = Bytes, ["duration"] = Duration, ["seconds"] = Seconds,
                };
                if (Files != null)
                {
                    var f = new JsonObject();
                    foreach (var kv in Files) f[kv.Key] = kv.Value;
                    o["files"] = f;
                }
                return o;
            }

            public static Job FromJson(JsonObject o, string id)
            {
                string S(string k) => Text(o[k]);
                double D(string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
                DateTime? T(string k) => DateTime.TryParse(S(k), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;
                var j = new Job
                {
                    Id = id, State = S("state") ?? "queued", Stage = S("stage") ?? "", Error = S("error"),
                    Name = S("name") ?? "video", StudioUrl = S("studio_url"), Kind = S("kind") ?? "script",
                    Pct = (int)D("pct"), Position = (int)D("position"), Seq = (long)D("seq"),
                    Created = T("created") ?? DateTime.UtcNow, Started = T("started"), Finished = T("finished"),
                    Bytes = (long)D("bytes"), Duration = D("duration"), Seconds = D("seconds"),
                };
                if (j.Seq <= 0) j.Seq = j.Created.Ticks;
                if (o["files"] is JsonObject f) j.Files = f.Where(kv => Text(kv.Value) != null).ToDictionary(kv => kv.Key, kv => Text(kv.Value));
                return j;
            }

            public Dictionary<string, object> Status(int position)
            {
                var running = State == "running" && Started.HasValue;
                var d = new Dictionary<string, object>
                {
                    { "job", Id }, { "state", State }, { "pct", Pct }, { "stage", Stage ?? "" }, { "position", position }, { "error", Error },
                    { "seconds", Math.Round(running ? (DateTime.UtcNow - Started.Value).TotalSeconds : Seconds, 1) },
                    { "bytes", Bytes }, { "duration", Duration }, { "name", Name },
                };
                if (State == "done" && Files != null) d["files"] = Files.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                return d;
            }
        }

        // a running job's handle: its token (time limit and cancel) and its worker
        private sealed class Slot
        {
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public volatile bool CancelRequested;
            public Task Task;
        }

        // the job folder's state file: a temp file moved over the old one, so a stop mid-write never leaves half a state
        private static void Save(Job j)
        {
            if (j.Gone) return;
            try
            {
                var dir = Path.Combine(Root, j.Id);
                if (Directory.Exists(dir)) WriteAtomic(Path.Combine(dir, "job.json"), j.ToJson().ToJsonString(FileJson));
            }
            catch (Exception ex) { _log?.Invoke("[studio] could not save the state of video job " + j.Id + ": " + ex.Message); }
        }

        private static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, path, true); return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < 10) { Thread.Sleep(50); }   // a scanner holding the old file for a moment
            }
        }

        private static Job LoadJob(string dir)
        {
            try
            {
                var f = Path.Combine(dir, "job.json");
                if (!File.Exists(f)) return null;
                return JsonNode.Parse(File.ReadAllText(f)) is JsonObject o ? Job.FromJson(o, Path.GetFileName(dir)) : null;
            }
            catch { return null; }
        }

        // under _lock: the job as this process knows it, else as its folder says (null = no such job)
        private static Job Known(string id) => _jobs.TryGetValue(id, out var j) ? j : LoadJob(Path.Combine(Root, id));
        private static int PositionOf(string id) => _queue.FindIndex(x => x.Equals(id, StringComparison.OrdinalIgnoreCase)) + 1;
        private static bool IsLive(string state) => state is "queued" or "running" or "done";
        private static Dictionary<string, object> Brief(Job j) => new Dictionary<string, object>
        {
            { "job", j.Id }, { "state", j.State }, { "position", _jobs.ContainsKey(j.Id) ? PositionOf(j.Id) : j.Position },
        };
        private static long NextSeq() { lock (_lock) { _seq = Math.Max(_seq + 1, DateTime.UtcNow.Ticks); return _seq; } }

        // under _lock: a waiting job's place in line is in its job.json too
        private static void SyncPositions()
        {
            for (int i = 0; i < _queue.Count; i++)
                if (_jobs.TryGetValue(_queue[i], out var j) && j.Position != i + 1) { j.Position = i + 1; Save(j); }
        }

        // ── job files ──────────────────────────────────────────────────────────
        private static bool IsJobId(string job) =>
            !string.IsNullOrEmpty(job) && job.Length <= 64 && job.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '-' || c == '_');

        private static string JobDir(Dictionary<string, object> p, bool create)
        {
            var job = Str(p, "job") ?? "";
            if (!IsJobId(job)) throw new ArgumentException("job must be 1-64 letters, digits, '-' or '_'");
            var dir = Path.Combine(Root, job);
            if (create) { Directory.CreateDirectory(Path.Combine(dir, "media")); Directory.CreateDirectory(Path.Combine(dir, "out")); }
            else if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("no studio job " + job);
            return dir;
        }
        public static string SafeName(string name)
        {
            var n = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
            foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
            n = n.Trim().TrimStart('.');
            if (n.Length == 0) n = "media";
            return n.Length > 120 ? n.Substring(n.Length - 120) : n;
        }

        // the video's file name: out/<name>.mp4 (a video extension the runner wrote is dropped, other dots stay)
        private static string VideoName(string raw)
        {
            var n = SafeName(string.IsNullOrWhiteSpace(raw) ? "video" : raw);
            var ext = Path.GetExtension(n).ToLowerInvariant();
            if (ext is ".mp4" or ".webm" or ".mov" or ".m4v") n = n.Substring(0, n.Length - ext.Length);
            n = n.Trim().TrimEnd('.');
            return n.Length == 0 ? "video" : n;
        }

        private static Dictionary<string, object> Put(Dictionary<string, object> p)
        {
            CleanOldJobs();
            var dir = JobDir(p, true);
            var name = SafeName(Str(p, "name"));
            var b64 = Str(p, "b64") ?? "";
            var bytes = Convert.FromBase64String(b64);
            if (bytes.LongLength > MaxPut) throw new InvalidOperationException("file too large for a studio job (200 MB cap)");
            var path = Path.Combine(dir, "media", name);
            var tmp = path + ".part";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
            return new Dictionary<string, object> { { "name", name }, { "size", bytes.LongLength } };
        }

        private static Dictionary<string, object> Get(Dictionary<string, object> p)
        {
            var dir = JobDir(p, false);
            var sep = Path.DirectorySeparatorChar;
            var rel = (Str(p, "path") ?? "").Replace('/', sep).Replace('\\', sep).Trim(sep);
            var full = Path.GetFullPath(Path.Combine(dir, rel));
            if (!full.StartsWith(Path.GetFullPath(dir).TrimEnd(sep) + sep, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("path escapes the job folder");
            if (!File.Exists(full)) throw new FileNotFoundException("no such job file", rel);
            long offset = Math.Max(0, Long(p, "offset"));
            long max = Long(p, "max");
            if (max <= 0) max = 4L * 1024 * 1024;
            max = Math.Min(max, 8L * 1024 * 1024);
            var fi = new FileInfo(full);
            long take = Math.Max(0, Math.Min(max, fi.Length - offset));
            var buf = new byte[take];
            using (var fs = File.OpenRead(full))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                int at = 0;
                while (at < take) { int n = fs.Read(buf, at, (int)(take - at)); if (n <= 0) break; at += n; }
            }
            return new Dictionary<string, object>
            {
                { "name", fi.Name }, { "total_size", fi.Length }, { "offset", offset }, { "length", take },
                { "done", offset + take >= fi.Length }, { "b64", Convert.ToBase64String(buf) },
            };
        }

        // Folders stay until the runner's ack; one older than a week is a leftover of a job the runner forgot. A job
        // that waits or runs is never touched (a restart resumes it).
        private static void CleanOldJobs()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
                foreach (var d in Directory.GetDirectories(Root))
                {
                    var id = Path.GetFileName(d);
                    Job j;
                    lock (_lock)
                    {
                        if (_running.ContainsKey(id)) continue;
                        j = Known(id);
                    }
                    if (j != null && (j.State == "queued" || j.State == "running")) continue;
                    var last = Directory.GetLastWriteTimeUtc(d);
                    if (j != null) { var t = j.Finished ?? j.Started ?? j.Created; if (t > last) last = t; }
                    if (last >= cutoff) continue;
                    lock (_lock)
                    {
                        if (_running.ContainsKey(id) || _queue.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                        if (_jobs.TryGetValue(id, out var mem)) { mem.Gone = true; _jobs.Remove(id); }
                    }
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }

        // ── start ──────────────────────────────────────────────────────────────
        // The runner may send start again after a restart: a job that is queued, running or done only answers
        // where it stands; a failed or cancelled one starts over with the script it brings (its media stay).
        private static Dictionary<string, object> Start(Dictionary<string, object> p, Action<string> log)
        {
            _log ??= log;
            CleanOldJobs();
            var dir = JobDir(p, true);
            var id = Path.GetFileName(dir);
            lock (_lock) { var known = Known(id); if (known != null && IsLive(known.State)) return Brief(known); }

            // a script (compiled by the Studio's director) or a finished Studio doc (rendered as it is: a project edited by hand)
            var kind = string.IsNullOrWhiteSpace(Str(p, "doc_json")) ? "script" : "doc";
            var text = kind == "doc" ? Str(p, "doc_json") : Str(p, "script_json");
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("script_json (the video script) or doc_json (a Studio doc) is required");
            JsonObject script;
            try { script = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException ex) { throw new ArgumentException(kind + "_json is not valid JSON: " + ex.Message); }
            if (script == null) throw new ArgumentException(kind + "_json must be a JSON object");
            var missing = new List<string>();
            MapJobMedia(script.DeepClone(), "", Path.Combine(dir, "media"), missing);
            if (missing.Count > 0) throw new ArgumentException("media not in the job: " + string.Join(", ", missing.Distinct()) + " (put them first)");
            var studio = NormalizeStudioUrl(Str(p, "studio_url"));
            var name = VideoName(Str(p, "name"));

            Dictionary<string, object> reply;
            lock (_lock)
            {
                var again = Known(id);   // two starts at once: the first one queues it
                if (again != null && IsLive(again.State)) return Brief(again);
                // both files on disk before the job is in line: a start that could not be saved is an error, not a job
                WriteAtomic(Path.Combine(dir, kind == "doc" ? "doc.json" : "script.json"), script.ToJsonString(FileJson));
                var job = new Job { Id = id, Name = name, StudioUrl = studio, Kind = kind, Created = DateTime.UtcNow, Seq = NextSeq(), Position = _queue.Count + 1 };
                WriteAtomic(Path.Combine(dir, "job.json"), job.ToJson().ToJsonString(FileJson));
                _jobs[id] = job;
                _queue.Add(id);
                reply = Brief(job);
            }
            log("[studio] video job " + id + " queued (number " + reply["position"] + " in line)");
            Pump();
            return reply;
        }

        // ── status ─────────────────────────────────────────────────────────────
        private static Dictionary<string, object> Status(Dictionary<string, object> p)
        {
            var ids = new List<string>();
            if (p != null && p.TryGetValue("jobs", out var v) && v != null)
            {
                if (v is string s) ids.AddRange(s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                else if (v is IEnumerable e) foreach (var x in e) if (x != null) ids.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            }
            var one = Str(p, "job");
            if (!string.IsNullOrEmpty(one)) ids.Add(one);
            var list = new List<object>();
            lock (_lock)
            {
                // no ids: every job this machine knows
                if (ids.Count == 0) ids = _jobs.Values.OrderBy(j => j.Seq).Select(j => j.Id).ToList();
                foreach (var id in ids.Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var j = IsJobId(id) ? Known(id) : null;
                    if (j != null) list.Add(j.Status(_jobs.ContainsKey(id) ? PositionOf(id) : j.Position));
                    else list.Add(new Dictionary<string, object>
                    {
                        { "job", id }, { "state", "unknown" }, { "pct", 0 }, { "stage", "" }, { "position", 0 }, { "error", null },
                        { "seconds", 0.0 }, { "bytes", 0L }, { "duration", 0.0 },
                    });
                }
            }
            return new Dictionary<string, object> { { "jobs", list } };
        }

        // ── cancel / ack ───────────────────────────────────────────────────────
        // A queued job leaves the line at once. A running one gets its token cancelled: the worker's finally kills its
        // browser and the job is marked cancelled; the slot goes to the next job in line.
        private static Dictionary<string, object> Cancel(Dictionary<string, object> p, Action<string> log)
        {
            var id = (Str(p, "job") ?? "").Trim();
            bool hit = false;
            string state = "unknown";
            Slot slot = null;
            if (IsJobId(id))
                lock (_lock)
                {
                    if (_jobs.TryGetValue(id, out var j))
                    {
                        if (j.State == "queued")
                        {
                            _queue.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
                            j.State = "cancelled"; j.Error = "cancelled before it ran"; j.Finished = DateTime.UtcNow; j.Position = 0;
                            Save(j);
                            SyncPositions();
                            hit = true;
                        }
                        else if (j.State == "running" && _running.TryGetValue(id, out slot)) { slot.CancelRequested = true; hit = true; }
                        state = j.State;
                    }
                }
            if (slot != null) { try { slot.Cts.Cancel(); } catch (ObjectDisposedException) { } }
            if (hit) log("[studio] video job " + id + " cancelled");
            return new Dictionary<string, object> { { "job", id }, { "cancelled", hit }, { "state", state } };
        }

        private static Dictionary<string, object> Ack(Dictionary<string, object> p, Action<string> log)
        {
            var id = (Str(p, "job") ?? "").Trim();
            if (!IsJobId(id)) throw new ArgumentException("job must be 1-64 letters, digits, '-' or '_'");
            var dir = Path.Combine(Root, id);
            Slot slot;
            lock (_lock)
            {
                _queue.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (_running.TryGetValue(id, out slot)) slot.CancelRequested = true;
                if (_jobs.TryGetValue(id, out var j)) { j.Gone = true; _jobs.Remove(id); }
                SyncPositions();
            }
            if (slot != null)
            {
                // acked while it still runs (the runner gave up on it): stop it, and let its browser close before the folder goes
                try { slot.Cts.Cancel(); } catch (ObjectDisposedException) { }
                try { slot.Task?.Wait(TimeSpan.FromSeconds(30)); } catch { }
            }
            for (int i = 0; i < 10 && Directory.Exists(dir); i++) { try { Directory.Delete(dir, true); } catch { Thread.Sleep(300); } }
            var deleted = !Directory.Exists(dir);
            if (deleted) log("[studio] video job " + id + " collected, its folder is removed");
            return new Dictionary<string, object> { { "job", id }, { "deleted", deleted } };
        }

        // ── the workers ────────────────────────────────────────────────────────
        // Starts jobs from the head of the line while fewer than MaxParallel run. Called after every start, finish,
        // cancel and resume.
        private static void Pump()
        {
            var started = new List<string>();
            int running, waiting;
            lock (_lock)
            {
                while (_running.Count < Math.Max(1, MaxParallel) && _queue.Count > 0)
                {
                    var id = _queue[0];
                    _queue.RemoveAt(0);
                    if (!_jobs.TryGetValue(id, out var job) || job.State != "queued") continue;
                    var slot = new Slot();
                    job.State = "running"; job.Stage = ""; job.Pct = 0; job.Position = 0; job.Error = null;
                    job.Started = DateTime.UtcNow; job.Finished = null; job.Files = null; job.Bytes = 0; job.Duration = 0; job.Seconds = 0;
                    Save(job);
                    _running[id] = slot;
                    slot.Task = Task.Run(() => WorkAsync(job, slot));
                    started.Add(id);
                }
                SyncPositions();
                running = _running.Count;
                waiting = _queue.Count;
            }
            foreach (var id in started) _log?.Invoke($"[studio] video job {id} started ({running} running, {waiting} waiting)");
        }

        private static async Task WorkAsync(Job job, Slot slot)
        {
            var log = _log ?? (_ => { });
            var sw = Stopwatch.StartNew();
            Outcome o;
            try { o = await RunJobAsync(job, slot, log).ConfigureAwait(false); }
            catch (Exception ex) { o = Outcome.Fail(ex.Message); }
            lock (_lock)
            {
                // the final state and the freed slot in one step: a start that comes right after sees a finished job
                job.State = o.State; job.Error = o.Error; job.Stage = ""; job.Finished = DateTime.UtcNow;
                job.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
                if (o.State == "done") { job.Pct = 100; job.Files = o.Files; job.Bytes = o.Bytes; job.Duration = o.Duration; }
                Save(job);
                if (_running.TryGetValue(job.Id, out var s) && s == slot) _running.Remove(job.Id);
            }
            try { slot.Cts.Dispose(); } catch { }
            if (o.State == "done") log($"[studio] video job {job.Id} done: out/{job.Name}.mp4 ({o.Bytes} bytes, {o.Duration:0.0} s of video) in {sw.Elapsed.TotalSeconds:0} s");
            else if (o.State == "cancelled") log($"[studio] video job {job.Id} cancelled after {sw.Elapsed.TotalSeconds:0} s");
            else log($"[studio] video job {job.Id} failed: {Clip(o.Error?.Split('\n')[0], 300)}");
            Pump();
        }

        private sealed class Outcome
        {
            public string State = "failed", Error;
            public Dictionary<string, string> Files;
            public long Bytes;
            public double Duration;
            public static Outcome Fail(string error) => new Outcome { State = "failed", Error = error };
        }

        // a job's time limit, counted from when it starts to run (never while it waits): 300 s + 60 s per second of
        // video when the script says how long it is (a top-level "duration" in seconds), else 1800 s; an hour at most
        private static int TimeLimitS(JsonObject script)
        {
            var d = script["duration"] is JsonValue v && v.TryGetValue<double>(out var x) ? x : 0;
            return d > 0 ? (int)Math.Min(3600, 300 + 60 * d) : 1800;
        }

        private static void SetProgress(Job job, string stage, int pct)
        {
            lock (_lock)
            {
                if (job.Stage == stage && job.Pct == pct) return;
                job.Stage = stage; job.Pct = pct;
                Save(job);
            }
        }

        private static string WithLog(string error, List<string> pageLog)
        {
            lock (pageLog)
                return Clip(pageLog.Count == 0 ? error : error + "\npage log:\n" + string.Join("\n", pageLog), 4000);
        }

        // One run of a job in its own browser: the screens of its screen-demo scenes, then the job page (the Studio's
        // director compiles the script and renders it), then the MP4, the sheet, the report and the project into out\.
        private static async Task<Outcome> RunJobAsync(Job job, Slot slot, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            var dir = Path.Combine(Root, job.Id);
            var mediaDir = Path.Combine(dir, "media");
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(mediaDir);
            Directory.CreateDirectory(outDir);
            // a run starts clean: what an earlier, cut-off attempt left in out\ is not this run's
            foreach (var f in Directory.GetFiles(outDir)) { try { File.Delete(f); } catch { } }
            var isDoc = job.Kind == "doc";
            var scriptPath = Path.Combine(dir, isDoc ? "doc.json" : "script.json");
            if (!File.Exists(scriptPath)) return Outcome.Fail($"the job has no {Path.GetFileName(scriptPath)} (start it again)");
            if (JsonNode.Parse(File.ReadAllText(scriptPath)) is not JsonObject script) return Outcome.Fail($"the job's {Path.GetFileName(scriptPath)} is not a JSON object");
            var studio = NormalizeStudioUrl(job.StudioUrl);
            var mediaBase = studio + "__vanity_job/" + job.Id + "/media/";
            var pageUrl = studio + "__vanity_job.html";

            var limitS = TimeLimitS(script);
            slot.Cts.CancelAfter(TimeSpan.FromSeconds(limitS));
            var ct = slot.Cts.Token;

            var missing = new List<string>();
            MapJobMedia(script.DeepClone(), mediaBase, mediaDir, missing);
            if (missing.Count > 0) return Outcome.Fail("media not in the job: " + string.Join(", ", missing.Distinct()) + " (put them first)");
            var exe = FindBrowser();
            if (exe == null) return Outcome.Fail("no Chrome or Edge on this machine; the Studio renders in one");

            var pageLog = new List<string>();
            Browser browser = null;
            try
            {
                CloseLeftoverBrowsers(log);
                var prof = Path.Combine(Path.GetTempPath(), ProfilePrefix + job.Id);
                try { browser = await Browser.StartAsync(exe, prof, ct).ConfigureAwait(false); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith(Browser.NoAnswer) && !ct.IsCancellationRequested)
                {
                    // one more start: a browser that failed to come up once usually starts the second time
                    log("[studio] video job " + job.Id + ": the browser did not start, trying once more");
                    browser = await Browser.StartAsync(exe, prof, ct).ConfigureAwait(false);
                }

                // the screens first, in this job's own browser: the director gets the picture and where its links are; a
                // step that cannot be shown as written fails the job with what the page has instead
                var stepError = isDoc ? null : await CaptureScreensAsync(job, script, browser, mediaDir, scriptPath, log, ct).ConfigureAwait(false);
                if (stepError != null) return Outcome.Fail(stepError);

                missing.Clear();
                var mapped = MapJobMedia(script.DeepClone(), mediaBase, mediaDir, missing);
                if (missing.Count > 0) return Outcome.Fail("media not in the job: " + string.Join(", ", missing.Distinct()) + " (put them first)");
                var pageJob = isDoc ? new JsonObject { ["op"] = "render", ["doc"] = mapped, ["name"] = job.Name } : new JsonObject { ["script"] = mapped, ["name"] = job.Name };
                // the film grain the script asked for is the page's to apply, not a script field the Studio knows
                if (!isDoc && mapped is JsonObject ms && ms["grain"] is JsonValue gv && gv.TryGetValue<string>(out var grain))
                {
                    ms.Remove("grain");
                    pageJob["grain"] = grain;
                }

                var (cdp, _) = await browser.OpenPageAsync(ct).ConfigureAwait(false);
                using (cdp)
                {
                    var crashed = false;
                    cdp.OnEvent += (method, prm) =>
                    {
                        if (method == "Fetch.requestPaused") _ = Task.Run(() => Intercept(cdp, prm, pageUrl, mediaBase, mediaDir, studio));
                        else if (method == "Runtime.exceptionThrown")
                        {
                            var d = prm.GetProperty("exceptionDetails");
                            var txt = d.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var de) ? de.GetString() : d.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                            lock (pageLog) if (pageLog.Count < 30) pageLog.Add("exception: " + Clip(txt, 400));
                        }
                        else if (method == "Runtime.consoleAPICalled" && prm.TryGetProperty("type", out var ty) && ty.GetString() == "error")
                        {
                            var parts = prm.GetProperty("args").EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : a.TryGetProperty("description", out var ds) ? ds.GetString() : "");
                            lock (pageLog) if (pageLog.Count < 30) pageLog.Add("console: " + Clip(string.Join(" ", parts), 400));
                        }
                        else if (method == "Inspector.targetCrashed") crashed = true;
                    };
                    await cdp.SendAsync("Runtime.enable", null, ct).ConfigureAwait(false);
                    await cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
                    try { await cdp.SendAsync("Inspector.enable", null, ct).ConfigureAwait(false); } catch (InvalidOperationException) { }   // only for the crash event
                    await cdp.SendAsync("Fetch.enable", new { patterns = new[] { new { urlPattern = "*__vanity_*", requestStage = "Request" } } }, ct).ConfigureAwait(false);
                    await cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = "window.__job = " + pageJob.ToJsonString() + ";" }, ct).ConfigureAwait(false);
                    log($"[studio] video job {job.Id}: the director runs on {studio}");
                    await cdp.SendAsync("Page.navigate", new { url = pageUrl }, ct).ConfigureAwait(false);

                    // the director says how far it is; job.json follows it (written only when it moves)
                    int lastTenth = -1;
                    while (true)
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                        if (crashed) return Outcome.Fail(WithLog("the Studio page crashed while it worked (often out of memory)", pageLog));
                        var st = await cdp.EvalAsync("JSON.stringify({ d: window.__done === true, p: Number.isFinite(window.__progress) ? window.__progress : 0, s: typeof window.__stage === 'string' ? window.__stage : '' })", ct).ConfigureAwait(false);
                        if (st.ValueKind != JsonValueKind.String) continue;
                        using var sd = JsonDocument.Parse(st.GetString() ?? "{}");
                        var done = sd.RootElement.TryGetProperty("d", out var dEl) && dEl.ValueKind == JsonValueKind.True;
                        var frac = sd.RootElement.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetDouble() : 0;
                        var stage = sd.RootElement.TryGetProperty("s", out var sEl) ? sEl.GetString() ?? "" : "";
                        var pct = (int)Math.Round(Math.Max(0, Math.Min(1, frac)) * 100);
                        SetProgress(job, Clip(stage, 40), pct);
                        int tenth = pct / 10;
                        if (tenth != lastTenth && tenth > 0) { lastTenth = tenth; log($"[studio] video job {job.Id}: {stage} {tenth * 10}%"); }
                        if (done) break;
                    }

                    var resEl = await cdp.EvalAsync("JSON.stringify(window.__result || null)", ct).ConfigureAwait(false);
                    var resJson = resEl.ValueKind == JsonValueKind.String ? resEl.GetString() : "null";
                    using var rd = JsonDocument.Parse(string.IsNullOrEmpty(resJson) ? "null" : resJson);
                    var r0 = rd.RootElement;
                    async Task AddPageOut()
                    {
                        var outText = await cdp.EvalAsync("document.getElementById('out') ? document.getElementById('out').textContent : ''", ct).ConfigureAwait(false);
                        var t = outText.ValueKind == JsonValueKind.String ? outText.GetString() : "";
                        if (!string.IsNullOrWhiteSpace(t)) lock (pageLog) pageLog.Add(Clip(t.Trim(), 1200));
                    }
                    if (r0.ValueKind != JsonValueKind.Object)
                    {
                        await AddPageOut().ConfigureAwait(false);
                        return Outcome.Fail(WithLog("the Studio page returned no result (is " + studio + " reachable and does it serve js/video/director.js?)", pageLog));
                    }
                    if (!(r0.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True))
                    {
                        await AddPageOut().ConfigureAwait(false);
                        return Outcome.Fail(WithLog(Strings(r0, "errors").FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "the director stopped without saying why", pageLog));
                    }

                    // the MP4: read straight out of the page in 2 MB slices over this same connection. Chrome's own
                    // download (an <a download> on the blob) stalled under load with a .crdownload that never completed
                    // (two of the first gallery jobs, 2026-10-08), and a download needs a download folder, a rename and
                    // a guess at when it is done; a read is done when the last slice is in.
                    SetProgress(job, "collect", 100);
                    long bytes = r0.TryGetProperty("bytes", out var byEl) && byEl.ValueKind == JsonValueKind.Number ? byEl.GetInt64() : 0;
                    var ext = r0.TryGetProperty("ext", out var exEl) && exEl.ValueKind == JsonValueKind.String ? exEl.GetString() ?? "" : "";
                    if (!Regex.IsMatch(ext, @"^\.[A-Za-z0-9]{1,5}$")) ext = ".mp4";
                    var name = job.Name + ext;
                    var final = Path.Combine(outDir, name);
                    // the render is done and every file it needed has loaded: request interception goes off first, a fetch
                    // of the page's own blob: address failed under it ("Failed to fetch", Chrome 155)
                    try { await cdp.SendAsync("Fetch.disable", null, ct).ConfigureAwait(false); } catch (InvalidOperationException) { }
                    var sizeEl = await cdp.EvalAsync("(async () => { window.__vjBlob = (window.__blobs && window.__blobs.get(window.__blobUrl)) || await (await fetch(window.__blobUrl)).blob(); return window.__vjBlob.size; })()", ct).ConfigureAwait(false);
                    long size = sizeEl.ValueKind == JsonValueKind.Number ? sizeEl.GetInt64() : 0;
                    if (size <= 0 || (bytes > 0 && size != bytes))
                    {
                        var why = await cdp.EvalAsync("(async () => { try { const r = await fetch(window.__blobUrl); const b = await r.blob(); return 'status ' + r.status + ', ' + b.size + ' bytes'; } catch (e) { return 'fetch failed: ' + e + ' (' + String(window.__blobUrl).slice(0, 80) + ')'; } })()", ct).ConfigureAwait(false);
                        return Outcome.Fail(WithLog($"the rendered file could not be read from the page ({size} of {bytes} bytes; {(why.ValueKind == JsonValueKind.String ? why.GetString() : "no answer")})", pageLog));
                    }
                    const int Slice = 2 * 1024 * 1024;
                    var tmp = final + ".part";
                    using (var fs = File.Create(tmp))
                    {
                        for (long off = 0; off < size; off += Slice)
                        {
                            var b64 = await cdp.EvalAsync("(async () => { const s = window.__vjBlob.slice(" + off + ", " + Math.Min(size, off + Slice) + "); const u = await new Promise((res, rej) => { const fr = new FileReader(); fr.onload = () => res(fr.result); fr.onerror = () => rej(fr.error); fr.readAsDataURL(s); }); return u.slice(u.indexOf(',') + 1); })()", ct).ConfigureAwait(false);
                            if (b64.ValueKind != JsonValueKind.String) return Outcome.Fail(WithLog($"the rendered file stopped coming from the page at {off} of {size} bytes", pageLog));
                            var chunk = Convert.FromBase64String(b64.GetString() ?? "");
                            fs.Write(chunk, 0, chunk.Length);
                        }
                    }
                    if (new FileInfo(tmp).Length != size) return Outcome.Fail(WithLog("the rendered file came from the page incomplete", pageLog));
                    File.Move(tmp, final, true);
                    var files = new Dictionary<string, string> { { "video", "out/" + name } };

                    // the contact sheet the director drew at the scenes' middles
                    if (r0.TryGetProperty("sheet", out var shEl) && shEl.ValueKind == JsonValueKind.String && shEl.GetString() is { } sdu && sdu.StartsWith("data:") && sdu.Contains(','))
                    {
                        var sheetName = job.Name + "-sheet.jpg";
                        File.WriteAllBytes(Path.Combine(outDir, sheetName), Convert.FromBase64String(sdu.Substring(sdu.IndexOf(',') + 1)));
                        files["sheet"] = "out/" + sheetName;
                    }

                    // the last frame at full size (an ad's static banner): a PNG data URL of several MB left on the page,
                    // read in slices like the video. A frame that does not come is a warning, never a failed job.
                    string posterWarn = null;
                    if (r0.TryGetProperty("poster", out var poEl) && poEl.ValueKind == JsonValueKind.True)
                    {
                        try
                        {
                            var lenEl = await cdp.EvalAsync("typeof window.__poster === 'string' ? window.__poster.length : 0", ct).ConfigureAwait(false);
                            long plen = lenEl.ValueKind == JsonValueKind.Number ? lenEl.GetInt64() : 0;
                            var sbp = new StringBuilder();
                            for (long off = 0; off < plen; off += Slice)
                            {
                                var part = await cdp.EvalAsync("window.__poster.slice(" + off + ", " + Math.Min(plen, off + Slice) + ")", ct).ConfigureAwait(false);
                                if (part.ValueKind != JsonValueKind.String) { sbp.Clear(); break; }
                                sbp.Append(part.GetString());
                            }
                            var pdu = sbp.ToString();
                            if (plen > 0 && pdu.Length == plen && pdu.StartsWith("data:image/png;base64,"))
                            {
                                var posterName = job.Name + "-poster.png";
                                File.WriteAllBytes(Path.Combine(outDir, posterName), Convert.FromBase64String(pdu.Substring(pdu.IndexOf(',') + 1)));
                                files["poster"] = "out/" + posterName;
                            }
                            else posterWarn = "the last frame did not come from the page whole";
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { posterWarn = "the last frame could not be read: " + Clip(ex.Message, 200); }
                    }

                    // the editable project: the compiled doc with the job's media inside it, so it opens on any machine
                    if (r0.TryGetProperty("doc", out var docEl) && docEl.ValueKind == JsonValueKind.Object)
                    {
                        var projName = job.Name + ".vstudio.json";
                        var proj = InlineJobMedia(JsonNode.Parse(docEl.GetRawText()), job.Id, mediaDir);
                        File.WriteAllText(Path.Combine(outDir, projName), proj.ToJsonString(CompactJson), new UTF8Encoding(false));
                        files["project"] = "out/" + projName;
                    }

                    // the report: what each scene shows, what fell back, the checks; plus the warnings and which browser drew it
                    // (a machine without Chrome renders in Edge, and an old build draws 3D and gradients differently)
                    var report = r0.TryGetProperty("report", out var rpEl) && rpEl.ValueKind == JsonValueKind.Object ? JsonNode.Parse(rpEl.GetRawText()).AsObject() : new JsonObject();
                    report["warnings"] = new JsonArray(Strings(r0, "warnings").Concat(posterWarn is null ? Array.Empty<string>() : new[] { posterWarn })
                        .Select(w => (JsonNode)JsonValue.Create(w)).ToArray());
                    report["browser"] = BrowserLabel(exe);
                    File.WriteAllText(Path.Combine(outDir, "report.json"), report.ToJsonString(FileJson), new UTF8Encoding(false));
                    files["report"] = "out/report.json";

                    return new Outcome
                    {
                        State = "done", Files = files, Bytes = new FileInfo(final).Length,
                        Duration = r0.TryGetProperty("duration", out var duEl) && duEl.ValueKind == JsonValueKind.Number ? Math.Round(duEl.GetDouble(), 2) : 0,
                    };
                }
            }
            catch (Exception ex) when (slot.Cts.IsCancellationRequested)
            {
                // the token fired: a cancel, or the time limit
                if (slot.CancelRequested) return new Outcome { State = "cancelled", Error = "cancelled while it ran" };
                return Outcome.Fail(WithLog("timed out after " + Math.Round(sw.Elapsed.TotalSeconds) + " s (this video's limit is " + limitS + " s)" + (ex is OperationCanceledException ? "" : ": " + ex.Message), pageLog));
            }
            catch (Exception ex)
            {
                return Outcome.Fail(WithLog(ex.Message, pageLog));
            }
            finally
            {
                browser?.Dispose();
            }
        }

        // ── screens for screen-demo scenes ─────────────────────────────────────
        // Every screen-demo scene with params.url and no files.screen: the page captured in the job's browser at the
        // format's size where its step is (after params.clicks, around params.target), saved as media\capture-<scene>.png,
        // files.screen set to it, params.links to what can be clicked on it and params.target to the target's words as
        // the page writes them. params.login: the operator's login to the site, from the login browser (--site-login). The saved script
        // keeps all of it, so a resumed job does not capture again. A step that cannot be shown as written (a page that
        // does not load, a click or a target it does not have, a site nobody is logged in to) returns the error that
        // fails the job: a title card or the top of another page under the step's instruction teaches the wrong thing.
        private static async Task<string> CaptureScreensAsync(Job job, JsonObject script, Browser browser, string mediaDir, string scriptPath, Action<string> log, CancellationToken ct)
        {
            if (script["scenes"] is not JsonArray scenes) return null;
            var phone = IsPhoneFormat(Text(script["format"]));
            var logins = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            try
            {
                for (int i = 0; i < scenes.Count; i++)
                {
                    // screen-flow: each of its pages (an address, then what is clicked to reach it) captured one screen
                    // tall into files.screen1..5
                    if (scenes[i] is JsonObject fl && Text(fl["block"]) == "screen-flow")
                    {
                        var fp = fl["params"] as JsonObject;
                        var ff = fl["files"] as JsonObject;
                        if (ff == null) { ff = new JsonObject(); fl["files"] = ff; }
                        var flogin = fp?["login"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;
                        var pages = fp?["pages"] is JsonArray pa ? pa.Select(Text).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() : new List<string>();
                        for (int k = 0; k < pages.Count && k < 5; k++)
                        {
                            var slot = "screen" + (k + 1);
                            if (!string.IsNullOrWhiteSpace(Text(ff[slot]))) continue;
                            var parts = pages[k].Split('>').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                            var purl = parts.Count > 0 ? parts[0] : "";
                            var pclicks = parts.Skip(1).ToList();
                            if (!IsHttpUrl(purl)) return "scene " + (i + 1) + " (screen-flow, page " + (k + 1) + "): \"" + Clip(pages[k], 120) + "\" does not start with an http(s) address";
                            SetProgress(job, "capture", 0);
                            var plimit = 90 + 20 * pclicks.Count;
                            using var pcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            pcts.CancelAfter(TimeSpan.FromSeconds(plimit));
                            try
                            {
                                var cookies = flogin ? LoginCookies(purl, logins, log) : null;
                                var shot = await ShootAsync(browser, purl, phone, false, log, pcts.Token, pclicks, null, cookies).ConfigureAwait(false);
                                var name = "capture-" + i + "-" + (k + 1) + ".png";
                                File.WriteAllBytes(Path.Combine(mediaDir, name), shot.Png);
                                ff[slot] = "job:" + name;
                                changed = true;
                            }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                var why = ex is OperationCanceledException ? "the page did not finish within " + plimit + " s" : ex.Message;
                                var error = "scene " + (i + 1) + " (screen-flow, page " + (k + 1) + ", " + string.Join(" > ", parts) + "): " + Clip(why, 600);
                                log("[studio] video job " + job.Id + ": " + error);
                                return error;
                            }
                        }
                        continue;
                    }
                    if (scenes[i] is not JsonObject sc || Text(sc["block"]) != "screen-demo") continue;
                    var prm = sc["params"] as JsonObject;
                    var url = Text(prm?["url"])?.Trim();
                    if (string.IsNullOrEmpty(url) || !IsHttpUrl(url)) continue;
                    var files = sc["files"] as JsonObject;
                    if (!string.IsNullOrWhiteSpace(Text(files?["screen"]))) continue;
                    var clicks = prm?["clicks"] is JsonArray ca ? ca.Select(Text).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() : new List<string>();
                    var targetWords = Text(prm?["target"])?.Trim();
                    var login = prm?["login"] is JsonValue lv && lv.TryGetValue<bool>(out var lb) && lb;
                    var step = url + (clicks.Count > 0 ? " > " + string.Join(" > ", clicks) : "");
                    SetProgress(job, "capture", 0);
                    var limit = 90 + 20 * clicks.Count;
                    using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    capCts.CancelAfter(TimeSpan.FromSeconds(limit));
                    try
                    {
                        var cookies = login ? LoginCookies(url, logins, log) : null;
                        var shot = await ShootAsync(browser, url, phone, true, log, capCts.Token, clicks, targetWords, cookies).ConfigureAwait(false);
                        var name = "capture-" + i + ".png";
                        File.WriteAllBytes(Path.Combine(mediaDir, name), shot.Png);
                        if (files == null) { files = new JsonObject(); sc["files"] = files; }
                        files["screen"] = "job:" + name;
                        var links = new JsonArray();
                        foreach (var l in shot.Links)
                            links.Add(new JsonObject
                            {
                                ["text"] = (string)l["text"], ["href"] = (string)l["href"],
                                ["box"] = new JsonArray(((List<object>)l["box"]).Select(b => (JsonNode)JsonValue.Create((double)b)).ToArray()),
                            });
                        prm["links"] = links;
                        if (shot.TargetText != null) prm["target"] = shot.TargetText;
                        // the address the clicks ended on: the browser frame's address bar shows it
                        if (!string.IsNullOrEmpty(shot.Url)) prm["page_url"] = shot.Url;
                        changed = true;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        var why = ex is OperationCanceledException ? "the page did not finish within " + limit + " s" : ex.Message;
                        var error = "scene " + (i + 1) + " (screen-demo, " + step + "): " + Clip(why, 600);
                        log("[studio] video job " + job.Id + ": " + error);
                        return error;
                    }
                }
                return null;
            }
            finally { if (changed) WriteAtomic(scriptPath, script.ToJsonString(FileJson)); }
        }

        /// <summary>What a page offers, for the videographer to learn a site before it writes a tutorial: the address
        /// opened in a browser of its own on this machine (the desktop site, or the phone site for a reel or portrait
        /// video), the clicks made in order (by the words on what is clicked), then the page it ends on: its headings,
        /// buttons and links with their words and where they lead, form fields, and the sections an address can open at
        /// (#id), each with the screen it is on. login: the operator's login to the site, from the login browser (--site-login). A click
        /// the page does not have fails with what the page has instead. Returns { ok, page: JSON text of the page }.</summary>
        private static Dictionary<string, object> Site(Dictionary<string, object> p, Action<string> log)
        {
            var url = (Str(p, "url") ?? "").Trim();
            if (!IsHttpUrl(url)) throw new ArgumentException("site needs url: an http(s) address");
            var clicks = p.TryGetValue("clicks", out var cv) && cv is IEnumerable<object> cl
                ? cl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)?.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList() : new List<string>();
            if (clicks.Count > 8) throw new ArgumentException("site takes at most 8 clicks");
            var phone = IsPhoneFormat(Str(p, "format"));
            var login = string.Equals(Str(p, "login"), "true", StringComparison.OrdinalIgnoreCase);
            var content = string.Equals(Str(p, "action"), "read", StringComparison.OrdinalIgnoreCase);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            try { return SiteAsync(url, clicks, phone, login, log, cts.Token, content).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new InvalidOperationException("the site did not finish within 150 s: " + url); }
            catch (StepException ex) { throw new InvalidOperationException(ex.Message); }
        }

        private static async Task<Dictionary<string, object>> SiteAsync(string url, List<string> clicks, bool phone, bool login, Action<string> log, CancellationToken ct, bool content = false)
        {
            var exe = FindBrowser() ?? throw new InvalidOperationException("no Chrome or Edge on this machine; the site is opened in one");
            var id = "site-" + Guid.NewGuid().ToString("n").Substring(0, 8);
            _captures[id] = 0;
            Browser browser = null;
            try
            {
                var cookies = login ? LoginCookies(url, new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase), log) : null;
                CloseLeftoverBrowsers(log);
                browser = await Browser.StartAsync(exe, Path.Combine(Path.GetTempPath(), ProfilePrefix + id), ct).ConfigureAwait(false);
                var (cdp, tab) = await browser.OpenPageAsync(ct).ConfigureAwait(false);
                try
                {
                    using (cdp)
                    {
                        await PreparePageAsync(cdp, phone, cookies, ct).ConfigureAwait(false);
                        log($"[studio] site {url}" + (clicks.Count > 0 ? ", then " + string.Join(" > ", clicks) : ""));
                        await cdp.SendAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);
                        await SettleAsync(cdp, ct).ConfigureAwait(false);
                        foreach (var words in clicks) await ClickAsync(cdp, words, ct).ConfigureAwait(false);
                        var page = await cdp.EvalAsync(content ? ContentJs : OutlineJs, ct).ConfigureAwait(false);
                        if (page.ValueKind != JsonValueKind.String) throw new InvalidOperationException("the page could not be read: " + url);
                        return new Dictionary<string, object> { { "ok", true }, { "page", page.GetString() } };
                    }
                }
                finally { await browser.ClosePageAsync(tab).ConfigureAwait(false); }
            }
            finally
            {
                browser?.Dispose();
                _captures.TryRemove(id, out _);
            }
        }

        // read: what a page SAYS, for a video about what it sells: its title and description, the product data it publishes
        // for search engines (JSON-LD, Open Graph prices), its main text as a visitor sees it, and its pictures large
        // enough to show in a video (address, words, pixel size), the share picture first
        private const string ContentJs = @"JSON.stringify((() => {
            const t = (s) => String(s || '').replace(/\s+/g, ' ').trim();
            const meta = (n) => { const e = document.querySelector('meta[property=""' + n + '""], meta[name=""' + n + '""], meta[itemprop=""' + n + '""]'); return e ? t(e.content) : ''; };
            const ld = [...document.querySelectorAll('script[type=""application/ld+json""]')].map((s) => { try { return JSON.parse(s.textContent); } catch (e) { return null; } }).filter(Boolean);
            const main = document.querySelector('main, [role=main], article, #content, .product, #product') || document.body;
            const seen = new Set();
            const pics = [];
            const add = (src, alt, w, h, where) => { if (!src || /^data:/.test(src) || seen.has(src)) return; seen.add(src); pics.push({ src, alt: t(alt).slice(0, 100), w, h, where }); };
            if (meta('og:image')) add(new URL(meta('og:image'), location.href).href, meta('og:title'), +meta('og:image:width') || 0, +meta('og:image:height') || 0, 'share picture');
            for (const i of document.images) {
                const w = i.naturalWidth, h = i.naturalHeight;
                if (w < 300 || h < 300) continue;
                const big = i.closest('a[href]');
                add(i.currentSrc || i.src, i.alt || i.title, w, h, main.contains(i) ? 'main' : 'page');
                if (big && /\.(jpe?g|png|webp|avif)(\?|$)/i.test(big.href)) add(big.href, i.alt, 0, 0, 'full size of the one above');
            }
            const prices = [...main.querySelectorAll('[itemprop=price], .price, [class*=price], [data-price]')].map((e) => t(e.innerText || e.content)).filter((x) => /\d/.test(x) && x.length < 40);
            // the brand's colours as the page wears them: its theme colour, then the saturated colours of its buttons,
            // links, header and headings, by how much they are used (greys and see-through colours do not count)
            const hues = new Map();
            const tint = (c, w) => { const m = /rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/.exec(c || ''); if (!m || (m[4] !== undefined && +m[4] < 0.6)) return;
                const r = +m[1], g = +m[2], b = +m[3]; if (Math.max(r, g, b) - Math.min(r, g, b) < 45) return;
                const hex = '#' + [r, g, b].map((x) => x.toString(16).padStart(2, '0')).join(''); hues.set(hex, (hues.get(hex) || 0) + w); };
            for (const e of document.querySelectorAll('button, [role=button], a, [class*=btn], [class*=button], header, nav, h1, h2, [class*=price], [class*=badge]')) {
                const r = e.getBoundingClientRect(); if (r.width < 4 || r.height < 4) continue; const cs = getComputedStyle(e);
                tint(cs.backgroundColor, 4); tint(cs.color, 1); tint(cs.borderTopColor, 1); }
            const theme = meta('theme-color');
            const colors = [...new Set([/^#[0-9a-f]{6}$/i.test(theme) ? theme.toLowerCase() : null, ...[...hues.entries()].sort((a, b) => b[1] - a[1]).map((x) => x[0])].filter(Boolean))].slice(0, 5);
            // the logo: a picture in the header or named logo, else the site's large icon
            const logoImg = document.querySelector('header img[src], [class*=logo] img[src], img[class*=logo][src], img[alt*=logo i][src], img[src*=logo i], a[href=""/""] img[src]');
            const icon = document.querySelector('link[rel=""apple-touch-icon""], link[rel~=""icon""][sizes]');
            const logo = logoImg ? (logoImg.currentSrc || logoImg.src) : icon ? icon.href : '';
            return { url: location.href, lang: document.documentElement.lang || '', title: document.title, ogTitle: meta('og:title'), description: meta('description') || meta('og:description'),
                price: meta('product:price:amount') || meta('og:price:amount'), currency: meta('product:price:currency') || meta('og:price:currency'), prices: [...new Set(prices)].slice(0, 8),
                colors, logo,
                headings: [...main.querySelectorAll('h1, h2, h3')].map((h) => t(h.innerText)).filter(Boolean).slice(0, 30),
                data: JSON.stringify(ld).slice(0, 8000), text: t(main.innerText).slice(0, 14000), pictures: pics.slice(0, 40) };
        })())";

        // the page as the videographer reads it: headings, what can be clicked (words, where it leads), form fields and
        // the sections an address can open at, each with the screen it is on (1 = the first screen)
        private const string OutlineJs = @"JSON.stringify((() => {
            const vh = innerHeight, sy = window.scrollY || 0;
            " + ShownJs + @"
            const vis = (e) => { const r = e.getBoundingClientRect(); return r.width > 8 && r.height > 8 && shown(e); };
            const words = (e) => (e.innerText || e.value || e.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ');
            const screen = (e) => Math.floor((e.getBoundingClientRect().top + sy) / vh) + 1;
            const heads = [...document.querySelectorAll('h1, h2, h3')].filter(vis).map((e) => ({ level: +e.tagName[1], text: words(e).slice(0, 100), screen: screen(e) })).filter((x) => x.text).slice(0, 40);
            const seen = new Set();
            const actions = [...document.querySelectorAll('a[href], button, [role=button], input[type=submit]')].filter(vis)
                .map((e) => ({ kind: e.tagName === 'A' ? 'link' : 'button', text: words(e).slice(0, 80), href: e.tagName === 'A' ? e.href : '', screen: screen(e) }))
                .filter((x) => x.text && !seen.has(x.text + '|' + x.href) && seen.add(x.text + '|' + x.href)).slice(0, 80);
            const labelOf = (e) => { const l = e.id ? document.querySelector('label[for=""' + CSS.escape(e.id) + '""]') : null;
                return ((l && l.innerText) || (e.closest('label') && e.closest('label').innerText) || e.getAttribute('aria-label') || e.placeholder || e.name || '').trim().replace(/\s+/g, ' ').slice(0, 60); };
            const fields = [...document.querySelectorAll('input:not([type=hidden]):not([type=submit]):not([type=button]), select, textarea')].filter(vis)
                .map((e) => ({ label: labelOf(e), type: (e.type || e.tagName).toLowerCase(), required: !!e.required, screen: screen(e) })).slice(0, 30);
            const sections = [...document.querySelectorAll('[id]')].filter((e) => /^[A-Za-z][\w-]*$/.test(e.id) && vis(e) && e.getBoundingClientRect().height > vh * 0.25)
                .map((e) => { const hd = e.querySelector('h1, h2, h3'); return { id: e.id, heading: hd ? words(hd).slice(0, 80) : '', screen: screen(e) }; }).slice(0, 25);
            return { url: location.href, title: document.title, screens: Math.round(Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0) / vh * 10) / 10, heads, actions, fields, sections };
        })())";

        // the capture sizes (contract section 3): a landscape or square video shows the desktop site at 1920x1080 CSS px;
        // a reel or portrait video shows the phone site, 412x915 CSS px at a phone's 2.625 pixel ratio (about 1081 px wide)
        private const int DeskW = 1920, DeskH = 1080, PhoneW = 412, PhoneH = 915;
        private const double PhoneDpr = 2.625;
        private static bool IsPhoneFormat(string format) => (format ?? "").Trim().ToLowerInvariant() is "reel" or "portrait";
        private static bool IsHttpUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http");

        private sealed class Shot
        {
            public byte[] Png;
            public int Width, Height;
            public string Title, Url;
            public List<Dictionary<string, object>> Links;
            /// <summary>The words of the button or link the target named, exactly as the page writes them (null without one).</summary>
            public string TargetText;
        }

        /// <summary>A step of a tutorial that cannot be shown as written: a click or a target the page does not have, a
        /// site the operator is not logged in to. Its message names the step and what the page has instead.</summary>
        private sealed class StepException : Exception { public StepException(string m) : base(m) { } }

        /// <summary>A picture of a web page and what can be clicked on it, exactly as a job's screen-demo scene gets it,
        /// in a browser of its own (the harness's entry; jobs capture in their own browser). <paramref name="format"/>
        /// reel or portrait = the phone site, else the desktop site. Returns { ok, browser, image_b64, mime, width,
        /// height, title, url, links: [ { text, href, box: [x, y, w, h] } ], seconds } with each box in fractions of the
        /// picture, or { ok: false, errors }.</summary>
        public static async Task<Dictionary<string, object>> CaptureAsync(string url, string format, Action<string> log, CancellationToken ct)
        {
            log ??= _ => { };
            var sw = Stopwatch.StartNew();
            url = (url ?? "").Trim();
            if (!IsHttpUrl(url)) throw new ArgumentException("capture needs url: an http(s) address");
            var exe = FindBrowser() ?? throw new InvalidOperationException("no Chrome or Edge on this machine; the Studio renders in one");
            var id = "capture-" + Guid.NewGuid().ToString("n").Substring(0, 8);
            _captures[id] = 0;
            Browser browser = null;
            try
            {
                CloseLeftoverBrowsers(log);
                browser = await Browser.StartAsync(exe, Path.Combine(Path.GetTempPath(), ProfilePrefix + id), ct).ConfigureAwait(false);
                var shot = await ShootAsync(browser, url, IsPhoneFormat(format), true, log, ct).ConfigureAwait(false);
                return new Dictionary<string, object>
                {
                    { "ok", true }, { "browser", BrowserLabel(exe) }, { "image_b64", Convert.ToBase64String(shot.Png) }, { "mime", "image/png" },
                    { "width", shot.Width }, { "height", shot.Height }, { "title", shot.Title }, { "url", shot.Url },
                    { "links", shot.Links }, { "seconds", Math.Round(sw.Elapsed.TotalSeconds, 1) },
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new Dictionary<string, object> { { "ok", false }, { "errors", new List<string> { "timed out after " + Math.Round(sw.Elapsed.TotalSeconds) + " s loading " + url } } };
            }
            finally
            {
                browser?.Dispose();
                _captures.TryRemove(id, out _);
            }
        }

        // Opens the page in a tab of its own at the capture size, waits for it to settle, walks it so every section has
        // revealed itself, makes the step's clicks, and takes up to 5 screens of the page where the step is (from a screen
        // above the target, else from the address's #section, else from the top) plus the visible links and buttons with
        // their boxes. A click or a target the page does not have throws StepException: the tutorial of 2026-10-08 named
        // websisco.ro/#preturi and #contact, the capture always took the top of the home page, and three scenes showed its
        // first screens under instructions about prices and a form.
        private static async Task<Shot> ShootAsync(Browser browser, string url, bool phone, bool full, Action<string> log, CancellationToken ct,
            IReadOnlyList<string> clicks = null, string targetWords = null, List<Dictionary<string, object>> cookies = null)
        {
            int w = phone ? PhoneW : DeskW, h = phone ? PhoneH : DeskH;
            double dpr = phone ? PhoneDpr : 1;
            var (cdp, target) = await browser.OpenPageAsync(ct).ConfigureAwait(false);
            try
            {
                using (cdp)
                {
                    await PreparePageAsync(cdp, phone, cookies, ct).ConfigureAwait(false);
                    log($"[studio] capture {url} at {w}x{h}{(phone ? " (phone)" : "")}" + (clicks is { Count: > 0 } ? ", then " + string.Join(" > ", clicks) : ""));
                    await cdp.SendAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);
                    await SettleAsync(cdp, ct).ConfigureAwait(false);
                    foreach (var words in clicks ?? Array.Empty<string>()) await ClickAsync(cdp, words, ct).ConfigureAwait(false);
                    var info = await cdp.EvalAsync("JSON.stringify({ title: document.title, url: location.href, docH: Math.max(document.body ? document.body.scrollHeight : 0, document.documentElement.scrollHeight) })", ct).ConfigureAwait(false);
                    string title = "", finalUrl = url; int docH = h;
                    if (info.ValueKind == JsonValueKind.String) { using var jd = JsonDocument.Parse(info.GetString()); title = jd.RootElement.GetProperty("title").GetString() ?? ""; finalUrl = jd.RootElement.GetProperty("url").GetString() ?? url; docH = jd.RootElement.GetProperty("docH").GetInt32(); }
                    int capH = full ? Math.Max(h, Math.Min(docH, h * 5)) : h;
                    // A step with a target, or an address with a #section, is the one screen a visitor sees scrolled there
                    // (the target 60 % down, the section at the top), measured at that scroll: a part that sticks while
                    // the page scrolls (an order summary) is where that screen shows it. A many-screen picture of the same
                    // step lost the summary's "Cumpără acum" on the screens after the first (2026-10-08).
                    double scrollTo = 0; string targetText = null; bool oneScreen = false;
                    if (!string.IsNullOrWhiteSpace(targetWords))
                    {
                        var found = await FindAsync(cdp, targetWords, ct).ConfigureAwait(false);
                        if (found is null) throw new StepException($"\"{targetWords}\" is not a button or a link on {finalUrl}{await HaveAsync(cdp, ct).ConfigureAwait(false)}");
                        scrollTo = found.Value.y - h * 0.6;
                        oneScreen = true;
                    }
                    else if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var fu) && fu.Fragment.Length > 1)
                    {
                        var at = await cdp.EvalAsync("(() => { const e = document.getElementById(" + JsonSerializer.Serialize(Uri.UnescapeDataString(fu.Fragment.Substring(1))) + "); return e ? e.getBoundingClientRect().top + (window.scrollY || 0) : -1; })()", ct).ConfigureAwait(false);
                        if (at.ValueKind == JsonValueKind.Number && at.GetDouble() >= 0) { scrollTo = at.GetDouble() - h * 0.05; oneScreen = true; }
                    }
                    if (oneScreen) capH = h;
                    var docEl = await cdp.EvalAsync("Math.max(document.body ? document.body.scrollHeight : 0, document.documentElement.scrollHeight)", ct).ConfigureAwait(false);
                    if (docEl.ValueKind == JsonValueKind.Number) docH = (int)docEl.GetDouble();
                    // what can be clicked: the links and buttons a visitor sees, in page order, text + absolute address +
                    // where it is on the page (document coordinates: the box plus the scroll offset), measured scrolled to
                    // where the picture starts, so a fixed header is where the picture's first screen shows it
                    await cdp.EvalAsync("scrollTo(0, " + (int)Math.Round(Math.Max(0, Math.Min(scrollTo, Math.Max(0, docH - h)))) + ")", ct).ConfigureAwait(false);
                    await Task.Delay(350, ct).ConfigureAwait(false);
                    await cdp.EvalAsync(FinishJs, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(targetWords))
                    {
                        // measured again where the picture is taken (a sticky part moves with the scroll)
                        var again = await FindAsync(cdp, targetWords, ct).ConfigureAwait(false);
                        if (again is null) throw new StepException($"\"{targetWords}\" is not on the screen of {finalUrl} where it should be clicked{await HaveAsync(cdp, ct).ConfigureAwait(false)}");
                        targetText = again.Value.text;
                    }
                    // bars and widgets pinned over the page (a cookie bar, a chat button, a pop-up) are taken out, unless
                    // the target is in one; a header pinned to the top stays
                    await cdp.EvalAsync(HideOverlaysJs, ct).ConfigureAwait(false);
                    var scrolled = await cdp.EvalAsync("window.scrollY || 0", ct).ConfigureAwait(false);
                    int top = scrolled.ValueKind == JsonValueKind.Number ? (int)Math.Round(scrolled.GetDouble()) : 0;
                    if (!oneScreen) capH = Math.Max(h, Math.Min(docH - top, capH));
                    var linksEl = await cdp.EvalAsync(@"JSON.stringify((() => { const sx = window.scrollX || 0, sy = window.scrollY || 0;
                        " + ShownJs + @"
                        return [...document.querySelectorAll('a[href], button, [role=button], input[type=submit]')].map(e => ({ e, r: e.getBoundingClientRect() }))
                          .filter(({ e, r }) => r.width > 8 && r.height > 8 && shown(e))
                          .map(({ e, r }) => ({ text: (e.innerText || e.value || e.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 80),
                                                href: typeof e.href === 'string' ? e.href : (e.getAttribute('href') || ''),
                                                x: r.left + sx, y: r.top + sy, w: r.width, h: r.height }))
                          .filter(l => l.text || l.href); })())", ct).ConfigureAwait(false);
                    var png = await StitchedAsync(cdp, h, top, capH, docH, (int)Math.Round(w * dpr), (int)Math.Round(capH * dpr), ct).ConfigureAwait(false);
                    var links = new List<Dictionary<string, object>>();
                    if (linksEl.ValueKind == JsonValueKind.String)
                    {
                        using var jd = JsonDocument.Parse(linksEl.GetString());
                        var seen = new HashSet<string>();
                        foreach (var l in jd.RootElement.EnumerateArray())
                        {
                            var text = l.GetProperty("text").GetString() ?? ""; var href = l.GetProperty("href").GetString() ?? "";
                            // page coordinates → the picture's, which starts at the step's place on the page
                            double x = l.GetProperty("x").GetDouble(), y = l.GetProperty("y").GetDouble() - top, bw = l.GetProperty("w").GetDouble(), bh = l.GetProperty("h").GetDouble();
                            // the part of it inside the picture, in fractions of the picture; one outside it is not on screen
                            double x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(w, x + bw), y1 = Math.Min(capH, y + bh);
                            if (x1 - x0 < 4 || y1 - y0 < 4) continue;
                            if (!seen.Add(text + "|" + href)) continue;
                            links.Add(new Dictionary<string, object>
                            {
                                { "text", text }, { "href", href },
                                { "box", new List<object> { Math.Round(x0 / w, 4), Math.Round(y0 / capH, 4), Math.Round((x1 - x0) / w, 4), Math.Round((y1 - y0) / capH, 4) } },
                            });
                            if (links.Count >= 80) break;
                        }
                    }
                    // the picture's own size (a phone capture is the CSS size times the pixel ratio)
                    int pw = (int)Math.Round(w * dpr), ph = (int)Math.Round(capH * dpr);
                    if (png.Length > 24 && png[12] == 'I' && png[13] == 'H' && png[14] == 'D' && png[15] == 'R')
                    {
                        pw = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
                        ph = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                    }
                    return new Shot { Png = png, Width = pw, Height = ph, Title = title, Url = finalUrl, Links = links, TargetText = targetText };
                }
            }
            finally { await browser.ClosePageAsync(target).ConfigureAwait(false); }
        }

        // the tab as the site should see it: the capture size, a normal browser's agent (a "HeadlessChrome" agent gets
        // bot walls), on a phone a phone's agent and touch so it serves its real mobile layout; and the operator's login
        // when the step needs it
        private static async Task PreparePageAsync(Cdp cdp, bool phone, List<Dictionary<string, object>> cookies, CancellationToken ct)
        {
            int w = phone ? PhoneW : DeskW, h = phone ? PhoneH : DeskH;
            await cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
            await cdp.SendAsync("Runtime.enable", null, ct).ConfigureAwait(false);
            await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new { width = w, height = h, deviceScaleFactor = phone ? PhoneDpr : 1, mobile = phone }, ct).ConfigureAwait(false);
            var uaEl = await cdp.EvalAsync("navigator.userAgent", ct).ConfigureAwait(false);
            var ua = uaEl.ValueKind == JsonValueKind.String ? uaEl.GetString() ?? "" : "";
            var ver = Regex.Match(ua, @"Chrome/([\d.]+)").Groups[1].Value;
            if (phone && ver.Length > 0) ua = "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + ver + " Mobile Safari/537.36";
            else ua = ua.Replace("HeadlessChrome/", "Chrome/");
            if (ua.Length > 0) await cdp.SendAsync("Emulation.setUserAgentOverride", new { userAgent = ua }, ct).ConfigureAwait(false);
            if (phone) await cdp.SendAsync("Emulation.setTouchEmulationEnabled", new { enabled = true, maxTouchPoints = 5 }, ct).ConfigureAwait(false);
            if (cookies is { Count: > 0 })
            {
                await cdp.SendAsync("Network.enable", null, ct).ConfigureAwait(false);
                await cdp.SendAsync("Network.setCookies", new { cookies }, ct).ConfigureAwait(false);
            }
        }

        // the page settles: the document complete, then a moment for fonts, lazy pictures and cookie banners to land;
        // then a walk down the page in viewport steps, because sites reveal their sections as they scroll into view
        // (fade-ups, counters, lazy pictures), and back to the top
        private static async Task SettleAsync(Cdp cdp, CancellationToken ct)
        {
            for (int i = 0; i < 100; i++)
            {
                var st = await cdp.EvalAsync("document.readyState", ct).ConfigureAwait(false);
                if (st.ValueKind == JsonValueKind.String && st.GetString() == "complete") break;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            await Task.Delay(1200, ct).ConfigureAwait(false);
            await cdp.EvalAsync(@"(async () => { const h = Math.min(Math.max(document.body ? document.body.scrollHeight : 0, document.documentElement.scrollHeight), innerHeight * 12);
                for (let y = 0; y < h; y += Math.max(300, Math.round(innerHeight * 0.7))) { scrollTo(0, y); await new Promise(r => setTimeout(r, 160)); }
                scrollTo(0, h); await new Promise(r => setTimeout(r, 300)); scrollTo(0, 0); await new Promise(r => setTimeout(r, 500)); return h; })()", ct).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
            await cdp.EvalAsync(FinishJs, ct).ConfigureAwait(false);
        }

        // The picture of a page window, screen by screen: each screen scrolled into view and taken the way a visitor sees
        // it, then joined. A page taken in one tall shot lays itself out for a window that tall: websisco.ro's hero (sized
        // to the window) lost its headline and its button, and a target measured there pointed at an empty picture
        // (2026-10-08). Fixed and sticky parts (a header, a chat button) show on the first screen only.
        private static async Task<byte[]> StitchedAsync(Cdp cdp, int h, int top, int capH, int docH, int pw, int ph, CancellationToken ct)
        {
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(pw, ph));
            var canvas = surface.Canvas;
            canvas.Clear(SkiaSharp.SKColors.White);
            double ky = (double)ph / capH;
            int filled = 0;
            for (int screen = 0; filled < capH && screen < 12; screen++)
            {
                int want = top + filled;                                  // the page row this screen should start at
                int sy = Math.Max(0, Math.Min(want, docH - h));           // the last screen stops at the page's end
                if (screen > 0)
                {
                    await cdp.EvalAsync("scrollTo(0, " + sy + ")", ct).ConfigureAwait(false);
                    await Task.Delay(350, ct).ConfigureAwait(false);
                    await cdp.EvalAsync(FinishJs, ct).ConfigureAwait(false);
                }
                var at = await cdp.EvalAsync("window.scrollY || 0", ct).ConfigureAwait(false);
                int real = at.ValueKind == JsonValueKind.Number ? (int)Math.Round(at.GetDouble()) : sy;
                var shot = await cdp.SendAsync("Page.captureScreenshot", new { format = "png" }, ct).ConfigureAwait(false);
                var bytes = Convert.FromBase64String(shot.TryGetProperty("data", out var de) ? de.GetString() ?? "" : "");
                using (var bmp = SkiaSharp.SKBitmap.Decode(bytes))
                {
                    if (bmp == null) throw new InvalidOperationException("a screen of the page could not be read");
                    double sk = (double)bmp.Height / h;                   // the screen's own pixels per CSS pixel
                    int skip = Math.Max(0, want - real);                  // rows of this screen the picture already has
                    int take = Math.Min(h - skip, capH - filled);
                    if (take <= 0) break;
                    canvas.DrawBitmap(bmp, new SkiaSharp.SKRect(0, (float)(skip * sk), bmp.Width, (float)((skip + take) * sk)),
                        new SkiaSharp.SKRect(0, (float)(filled * ky), pw, (float)((filled + take) * ky)));
                    filled += take;
                }
                if (screen == 0) await cdp.EvalAsync(HideFixedJs, ct).ConfigureAwait(false);
            }
            using var image = surface.Snapshot();
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        // what is pinned over the page and is not its header (a cookie bar, a chat button, a pop-up and its backdrop) is
        // taken out before the picture: it covered the content right above the step's button (2026-10-08). A bar that
        // holds the target stays; a header pinned to the top (at the top, under a third of the screen) stays.
        private const string HideOverlaysJs = @"(() => { let n = 0;
            for (const e of document.querySelectorAll('body *')) {
                const cs = getComputedStyle(e); if (cs.position !== 'fixed' || cs.display === 'none') continue;
                if (e.hasAttribute('data-vanity-target') || e.querySelector('[data-vanity-target]')) continue;
                const r = e.getBoundingClientRect(); if (r.width < 2 || r.height < 2) continue;
                if (r.top > 4 || r.height > innerHeight * 0.3) { e.style.setProperty('display', 'none', 'important'); n++; }
            }
            return n; })()";

        // after the first screen: what is pinned to the window (a header, a chat button, a cookie bar) is taken out, so it
        // does not show again on every screen of the picture
        private const string HideFixedJs = @"(() => { let n = 0;
            for (const e of document.querySelectorAll('body *')) { const p = getComputedStyle(e).position; if (p === 'fixed' || p === 'sticky') { e.style.setProperty('visibility', 'hidden', 'important'); n++; } }
            return n; })()";

        // shown(e): what a visitor really sees: the element and every parent visible, none of them faded out (a slider's
        // inactive slide keeps its button in the page at opacity 0, and that button is not on screen)
        private const string ShownJs = @"const shown = (e) => { for (let n = e; n && n.nodeType === 1; n = n.parentElement) { const cs = getComputedStyle(n); if (cs.visibility === 'hidden' || cs.display === 'none' || +cs.opacity < 0.1) return false; } return true; };";

        // Entrances still running (fade-ups, a slider's captions, CSS transitions) are taken to their end, so the picture
        // shows the page as a visitor sees it once it has settled: websisco.ro's hero text was still at opacity 0 when
        // its picture was taken, and a target measured there pointed at an empty picture (2026-10-08). Looping
        // animations are left as they are.
        private const string FinishJs = @"(() => { let n = 0;
            for (const a of (document.getAnimations ? document.getAnimations() : [])) {
                try { const t = a.effect && a.effect.getComputedTiming(); if (t && Number.isFinite(t.endTime) && t.endTime > 0 && a.playState !== 'finished') { a.finish(); n++; } } catch (e) { }
            }
            return n; })()";

        // The button or link a step names, by its words, the way the Studio's screen-demo finds its target (exact, then
        // contained, then shared words; the first on the page wins a tie), among what the capture lists as clickable
        // (the same selector and size floor), or with wide = true among everything that can be clicked. Shared words
        // count only when they are real words (3 letters or more) and cover most of what was asked: "Buton care nu
        // exista" once matched "Nu ai CUI-ul la indemana?" on "nu", and a step that was not there rendered.
        private const string PickJs = @"((want, wide) => {
            " + ShownJs + @"
            const norm = (s) => String(s || '').toLocaleLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/\s+/g, ' ').trim();
            const w = norm(want); if (!w) return null;
            const ws = new Set(w.split(' ').filter((x) => x.length >= 3));
            const need = Math.max(1, Math.ceil(ws.size * 0.6));
            const sel = wide ? 'a[href], button, [role=button], [role=link], [role=tab], [role=menuitem], input[type=submit], input[type=button], summary, label' : 'a[href], button, [role=button], input[type=submit]';
            let best = null, bs = 0;
            for (const e of document.querySelectorAll(sel)) {
                const r = e.getBoundingClientRect();
                if (r.width <= 8 || r.height <= 8 || !shown(e)) continue;
                const t = norm(e.innerText || e.value || e.getAttribute('aria-label') || '');
                if (!t) continue;
                let s = t === w ? 100 : t.includes(w) ? 80 - Math.min(30, t.length - w.length) : t.length >= 3 && w.includes(t) ? 60 : 0;
                if (!s) { const sh = new Set(t.split(' ').filter((x) => ws.has(x))).size; s = sh >= need ? 20 + sh * 10 : 0; }
                if (s > bs) { bs = s; best = e; }
            }
            return best;
        })";

        // one click of a step, made in the page a moment after the answer (a click that navigates would otherwise cut
        // the answer off); a link that opens a new tab is opened in this one. What it opens settles before the next.
        private static async Task ClickAsync(Cdp cdp, string words, CancellationToken ct)
        {
            var r = await cdp.EvalAsync("(async () => { const e = " + PickJs + "(" + JsonSerializer.Serialize(words) + ", true); if (!e) return 'none';" +
                " e.scrollIntoView({ block: 'center' }); await new Promise((r) => setTimeout(r, 400));" +
                " const href = e.tagName === 'A' && /^https?:/i.test(e.href) ? e.href : '';" +
                " const blank = (e.getAttribute('target') || '').toLowerCase() === '_blank';" +
                " setTimeout(() => { if (href && blank) location.href = href; else e.click(); }, 50); return 'ok'; })()", ct).ConfigureAwait(false);
            if (r.ValueKind != JsonValueKind.String || r.GetString() != "ok")
            {
                var at = await cdp.EvalAsync("location.href", ct).ConfigureAwait(false);
                throw new StepException($"there is no \"{words}\" to click on {(at.ValueKind == JsonValueKind.String ? at.GetString() : "the page")}{await HaveAsync(cdp, ct).ConfigureAwait(false)}");
            }
            await Task.Delay(900, ct).ConfigureAwait(false);
            await SettleAsync(cdp, ct).ConfigureAwait(false);
        }

        // the target's words as the page writes them (the capture lists them the same way) and where it is on the page
        private static async Task<(string text, double y)?> FindAsync(Cdp cdp, string words, CancellationToken ct)
        {
            // the target is marked, so taking out what is pinned over the page never takes it out with its bar
            var r = await cdp.EvalAsync("(() => { const e = " + PickJs + "(" + JsonSerializer.Serialize(words) + ", false); if (!e) return null;" +
                " document.querySelectorAll('[data-vanity-target]').forEach((m) => m.removeAttribute('data-vanity-target')); e.setAttribute('data-vanity-target', '1');" +
                " const b = e.getBoundingClientRect();" +
                " return JSON.stringify({ t: (e.innerText || e.value || e.getAttribute('aria-label') || '').trim().replace(/\\s+/g, ' ').slice(0, 80), y: b.top + (window.scrollY || 0) }); })()", ct).ConfigureAwait(false);
            if (r.ValueKind != JsonValueKind.String) return null;
            using var jd = JsonDocument.Parse(r.GetString() ?? "{}");
            return (jd.RootElement.GetProperty("t").GetString() ?? words, jd.RootElement.GetProperty("y").GetDouble());
        }

        // what the page offers instead, for a step that is not there: the words of its first buttons and links
        private static async Task<string> HaveAsync(Cdp cdp, CancellationToken ct)
        {
            var r = await cdp.EvalAsync(@"(() => { " + ShownJs + @" return JSON.stringify([...new Set([...document.querySelectorAll('a[href], button, [role=button], input[type=submit]')]
                .filter((e) => { const r = e.getBoundingClientRect(); return r.width > 8 && r.height > 8 && shown(e); })
                .map((e) => (e.innerText || e.value || e.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 50)).filter(Boolean))].slice(0, 30)); })()", ct).ConfigureAwait(false);
            if (r.ValueKind != JsonValueKind.String) return "";
            using var jd = JsonDocument.Parse(r.GetString() ?? "[]");
            var have = jd.RootElement.EnumerateArray().Select(x => "\"" + x.GetString() + "\"").ToList();
            return have.Count == 0 ? " (it has no buttons or links)" : "; it has: " + string.Join(", ", have);
        }

        /// <summary>The operator's login to a site, for a step behind it: (address, log) → the cookies of that site,
        /// ready for Network.setCookies, or null when nobody is logged in to it. By default the login browser's
        /// (<see cref="LoginCookiesFor"/>); a host may replace it.</summary>
        public static Func<string, Action<string>, List<Dictionary<string, object>>> SiteLogin = LoginCookiesFor;

        // the login for a step, once per site per capture run
        private static List<Dictionary<string, object>> LoginCookies(string url, Dictionary<string, List<Dictionary<string, object>>> cache, Action<string> log)
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            if (cache.TryGetValue(host, out var had)) return had;
            if (SiteLogin == null) throw new StepException("this machine has no login browser, so a step behind a login cannot be shown here");
            var cookies = SiteLogin(url, log);
            if (cookies is not { Count: > 0 }) throw new StepException($"the login browser is not logged in to {host}: run `vanity-studio --site-login {url}` (or /site-login in the chat), log in, close that window, then make the video again");
            cache[host] = cookies;
            return cookies;
        }

        // ── the login browser: a Chrome profile of Vanity Studio's own, for steps behind a login ──────────────────
        /// <summary>The login browser's profile folder. The operator logs in to a site there once
        /// (<see cref="OpenLoginWindow"/>); a capture behind that login reads the site's cookies from it.</summary>
        public static string LoginProfile => Path.Combine(AgentConfig.Dir, "browser-login");

        /// <summary>A visible browser window on the login profile at <paramref name="url"/>, for the operator to log in.
        /// The cookies stay in that profile; close the window when done, so a capture can read them.</summary>
        public static Process OpenLoginWindow(string url)
        {
            if (!IsHttpUrl(url)) throw new ArgumentException("the login page needs an http(s) address");
            var exe = FindBrowser() ?? throw new InvalidOperationException("no Chrome or Edge on this machine");
            Directory.CreateDirectory(LoginProfile);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--user-data-dir=" + LoginProfile, "--no-first-run", "--no-default-browser-check", "--new-window", url }) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("the browser did not start");
            DrainOutput(proc);
            return proc;
        }

        /// <summary>Switches that keep a render browser from starting Chrome's background services (push messaging,
        /// sync, component updates, default apps): they only fail in a fresh signed-out profile and print errors.</summary>
        private static readonly string[] QuietArgs =
        {
            "--disable-background-networking", "--disable-sync", "--disable-component-update", "--disable-default-apps",
            "--disable-features=PushMessaging,MediaRouter,OptimizationHints", "--log-level=3",
        };

        // what the browser writes on its own console goes to the diagnostic log, read as it comes so its pipes never fill
        private static void DrainOutput(Process proc)
        {
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Debug("[chrome] " + e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Debug("[chrome] " + e.Data); };
            try { proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); } catch { }
        }

        /// <summary>The cookies of <paramref name="url"/>'s site in the login profile, as Network.setCookies params, or
        /// null when the profile has none for it. A headless browser opens the profile itself (it decrypts its own
        /// cookies) and closes it again; while the login window is still open the profile is locked and this throws.</summary>
        public static List<Dictionary<string, object>> LoginCookiesFor(string url, Action<string> log)
        {
            if (!Directory.Exists(LoginProfile)) return null;
            var exe = FindBrowser();
            if (exe == null) return null;
            var host = new Uri(url).Host.ToLowerInvariant();
            if (ProfileInUse(LoginProfile))
                throw new StepException("the login browser window is still open: close it, then make the video again");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Browser b = null;
            try
            {
                b = Browser.StartAsync(exe, LoginProfile, cts.Token, keep: true).GetAwaiter().GetResult();
                using var cdp = b.ConnectBrowserAsync(cts.Token).GetAwaiter().GetResult();
                var res = cdp.SendAsync("Storage.getCookies", null, cts.Token).GetAwaiter().GetResult();
                var mine = new List<Dictionary<string, object>>();
                if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var c in arr.EnumerateArray())
                    {
                        var dom = (c.TryGetProperty("domain", out var dv) ? dv.GetString() ?? "" : "").TrimStart('.').ToLowerInvariant();
                        if (dom.Length == 0 || !(host == dom || host.EndsWith("." + dom, StringComparison.Ordinal))) continue;
                        var d = new Dictionary<string, object> { { "name", c.GetProperty("name").GetString() }, { "value", c.TryGetProperty("value", out var vv) ? vv.GetString() ?? "" : "" } };
                        if (c.TryGetProperty("domain", out var dd)) d["domain"] = dd.GetString();
                        d["path"] = c.TryGetProperty("path", out var pv) && !string.IsNullOrEmpty(pv.GetString()) ? pv.GetString() : "/";
                        if (c.TryGetProperty("secure", out var sv) && sv.ValueKind is JsonValueKind.True or JsonValueKind.False) d["secure"] = sv.GetBoolean();
                        if (c.TryGetProperty("httpOnly", out var hv) && hv.ValueKind is JsonValueKind.True or JsonValueKind.False) d["httpOnly"] = hv.GetBoolean();
                        if (c.TryGetProperty("sameSite", out var ss) && ss.GetString() is "Strict" or "Lax" or "None") d["sameSite"] = ss.GetString();
                        var session = c.TryGetProperty("session", out var se) && se.ValueKind == JsonValueKind.True;
                        if (!session && c.TryGetProperty("expires", out var ev) && ev.ValueKind == JsonValueKind.Number && ev.GetDouble() > 0) d["expires"] = ev.GetDouble();
                        mine.Add(d);
                    }
                try { cdp.SendAsync("Browser.close", null, cts.Token).GetAwaiter().GetResult(); } catch { }   // a clean close keeps the profile whole
                if (mine.Count > 0) log?.Invoke($"[studio] the login to {host} comes from the login browser ({mine.Count} cookies)");
                return mine.Count > 0 ? mine : null;
            }
            catch (OperationCanceledException) { throw new StepException("the login browser did not answer within 60 s"); }
            finally { b?.Dispose(); }
        }

        // a Chrome profile another browser has open: Windows keeps "lockfile" open, Linux and macOS leave a SingletonLock link
        private static bool ProfileInUse(string profile)
        {
            try
            {
                var lockFile = Path.Combine(profile, "lockfile");
                if (OperatingSystem.IsWindows())
                {
                    if (!File.Exists(lockFile)) return false;
                    try { using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
                    catch (IOException) { return true; }
                }
                return new FileInfo(Path.Combine(profile, "SingletonLock")).LinkTarget != null;
            }
            catch { return false; }
        }

        public static string NormalizeStudioUrl(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) url = Environment.GetEnvironmentVariable("VANITY_STUDIO_URL") ?? DefaultStudioUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                throw new ArgumentException("studio_url must be an http(s) URL");
            var s = u.GetLeftPart(UriPartial.Path);
            if (!s.EndsWith("/")) s = s.Substring(0, s.LastIndexOf('/') + 1);
            return s;
        }

        // job:<name> → same-origin URL; anything else is left as written (http(s) URLs, data URLs, library ids).
        // Rewrites in place: only replaced string values get new nodes.
        private static JsonNode MapJobMedia(JsonNode node, string mediaBase, string mediaDir, List<string> missing)
        {
            JsonNode Swap(JsonNode child)
            {
                if (child is JsonValue v && v.TryGetValue<string>(out var s) && s != null && s.StartsWith("job:", StringComparison.Ordinal))
                {
                    var name = SafeName(s.Substring(4));
                    if (!File.Exists(Path.Combine(mediaDir, name))) missing.Add(name);
                    return JsonValue.Create(mediaBase + Uri.EscapeDataString(name));
                }
                MapJobMedia(child, mediaBase, mediaDir, missing);
                return null;
            }
            if (node is JsonObject o)
                foreach (var key in o.Select(kv => kv.Key).ToList()) { var r = Swap(o[key]); if (r != null) o[key] = r; }
            else if (node is JsonArray a)
                for (int i = 0; i < a.Count; i++) { var r = Swap(a[i]); if (r != null) a[i] = r; }
            return node;
        }

        // The project file opens anywhere: every URL of this job's media in the compiled doc (absolute, relative, or
        // inside a longer string such as a CSS url()) becomes the file itself as a data URL.
        private static JsonNode InlineJobMedia(JsonNode node, string jobId, string mediaDir)
        {
            var marker = "__vanity_job/" + jobId + "/media/";
            var rx = new Regex(@"[^\s""'()]*?__vanity_job/" + Regex.Escape(jobId) + @"/media/([^\s""'()?#]+)(?:\?[^\s""'()#]*)?(?:#[^\s""'()]*)?", RegexOptions.IgnoreCase);
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Inline(string s) => rx.Replace(s, m =>
            {
                var name = SafeName(Uri.UnescapeDataString(m.Groups[1].Value));
                if (!cache.TryGetValue(name, out var du))
                {
                    var file = Path.Combine(mediaDir, name);
                    du = File.Exists(file) ? "data:" + MimeOf(name) + ";base64," + Convert.ToBase64String(File.ReadAllBytes(file)) : null;
                    cache[name] = du;
                }
                return du ?? m.Value;
            });
            JsonNode Walk(JsonNode n)
            {
                if (n is JsonObject o) { foreach (var key in o.Select(kv => kv.Key).ToList()) { var r = Walk(o[key]); if (r != null) o[key] = r; } return null; }
                if (n is JsonArray a) { for (int i = 0; i < a.Count; i++) { var r = Walk(a[i]); if (r != null) a[i] = r; } return null; }
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s != null && s.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return JsonValue.Create(Inline(s));
                return null;
            }
            return Walk(node) ?? node;
        }

        // ── request interception: the job page and the job's media ────────────
        private static async Task Intercept(Cdp cdp, JsonElement prm, string pageUrl, string mediaBase, string mediaDir, string studio)
        {
            var reqId = prm.GetProperty("requestId").GetString();
            var url = prm.GetProperty("request").GetProperty("url").GetString() ?? "";
            var path = url.Split('?')[0];
            try
            {
                if (path.Equals(pageUrl, StringComparison.OrdinalIgnoreCase))
                {
                    await Fulfill(cdp, reqId, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(JobHtml(studio))).ConfigureAwait(false);
                    return;
                }
                if (path.StartsWith(mediaBase, StringComparison.OrdinalIgnoreCase))
                {
                    var name = SafeName(Uri.UnescapeDataString(path.Substring(mediaBase.Length)));
                    var file = Path.Combine(mediaDir, name);
                    if (File.Exists(file)) await Fulfill(cdp, reqId, 200, MimeOf(name), await File.ReadAllBytesAsync(file).ConfigureAwait(false)).ConfigureAwait(false);
                    else await Fulfill(cdp, reqId, 404, "text/plain", Encoding.UTF8.GetBytes("not in the job")).ConfigureAwait(false);
                    return;
                }
                await cdp.SendAsync("Fetch.continueRequest", new { requestId = reqId }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                try { await cdp.SendAsync("Fetch.failRequest", new { requestId = reqId, errorReason = "Failed" }, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }
        private static Task Fulfill(Cdp cdp, string reqId, int status, string mime, byte[] body) =>
            cdp.SendAsync("Fetch.fulfillRequest", new
            {
                requestId = reqId,
                responseCode = status,
                responseHeaders = new[]
                {
                    new { name = "Content-Type", value = mime },
                    new { name = "Content-Length", value = body.Length.ToString() },
                    new { name = "Cache-Control", value = "no-store" },
                },
                body = Convert.ToBase64String(body),
            }, CancellationToken.None);

        public static string MimeOf(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".avif": return "image/avif";
                case ".mp4": case ".m4v": return "video/mp4";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".m4a": case ".aac": return "audio/mp4";
                case ".ogg": case ".oga": return "audio/ogg";
                case ".json": return "application/json";
                case ".glb": return "model/gltf-binary";
                case ".riv": return "application/octet-stream";
                default: return "application/octet-stream";
            }
        }

        // The job page, served on the Studio's own origin. A script job: the Studio's director compiles it, checks it and
        // renders it, and sets window.__progress / __stage / __result / __blobUrl / __done itself. A script with a "grain"
        // other than the look's own ("none" or "subtle"): the director compiles and checks only (its own sheetOnly mode),
        // the grain of the compiled doc is turned down, and the doc is rendered with the Studio's video API. A doc job (a
        // Studio project edited by hand) and a probe (docs, spec, validate, frame, sheet): the Studio's video API
        // (js/api/videoApi.js) does it, the way the Studio's tools/headless.html drives it. The page only catches what
        // could not be reported (a Studio without the module, a crash while loading it).
        private static string JobHtml(string studio) => $$"""
<!doctype html>
<html><head><meta charset="utf-8"><base href="{{WebUtility.HtmlEncode(studio)}}"><title>Vanity video job</title></head>
<body><div id="overlays"></div><pre id="out"></pre>
<script type="module">
const job = window.__job || {};
const out = document.getElementById("out");
// the finished video is kept as the Blob the Studio makes its address for: the runner reads it straight from here (a
// fetch of the page's own blob: address failed in Chrome 155 with "Failed to fetch")
const makeUrl = URL.createObjectURL.bind(URL);
window.__blobs = new Map();
URL.createObjectURL = (o) => { const u = makeUrl(o); try { if (o instanceof Blob && /^video\//.test(o.type || "")) window.__blobs.set(u, o); } catch (e) { } return u; };
const finish = (r) => { window.__result = r; window.__progress = 1; window.__done = true; };
const stage = (s, p) => { window.__stage = s; if (p != null) window.__progress = p; };
const said = (e) => String(e && e.message || e);

// a doc rendered with the Studio's video API: the MP4, the contact sheet (the middle of every scene when the report
// has them), the banner (the last poster scene at rest, else the last frame), and the result as the director gives it
async function renderDoc(api, doc, report, warnings0) {
  const v = api.validate(doc);
  if (!v.ok) return finish({ ok: false, errors: v.errors, warnings: v.warnings, report, doc });
  stage("compile", 0.03);
  const built = await api.build(doc);
  const duration = await api.durationOf(built.tl);
  stage("render", 0.08);
  const r = await api.render(built.tl, { onProgress: (p) => { window.__progress = 0.08 + 0.88 * Math.max(0, Math.min(1, p)); } });
  stage("sheet", 0.97);
  const warnings = [...(warnings0 || []), ...v.warnings];
  const scenes = (report && report.scenes) || [];
  let sheet = null, poster = false;
  try {
    sheet = scenes.length
      ? (await api.sheet(built.tl, { times: scenes.map((s) => +(s.start + Math.min(s.dur - 0.05, Math.max(0.6, s.dur * 0.6))).toFixed(2)), cols: Math.min(4, scenes.length), scale: 0.25 })).dataUrl
      : (await api.sheet(built.tl, { frames: 8, cols: 4, scale: 0.25 })).dataUrl;
  } catch (e) { warnings.push("the contact sheet could not be drawn: " + said(e)); }
  const posterScene = [...scenes].reverse().find((s) => s.banner);
  const bannerAt = posterScene ? posterScene.start + Math.max(posterScene.dur * 0.7, posterScene.dur - 0.9) : Math.max(0, duration - 0.06);
  if (report) report.banner = { at: +bannerAt.toFixed(2), scene: posterScene ? scenes.indexOf(posterScene) + 1 : null };
  try { window.__poster = (await api.frame(built.tl, bannerAt, { scale: 1, type: "image/png" })).dataUrl; poster = true; }
  catch (e) { warnings.push("the last frame could not be drawn: " + said(e)); }
  window.__blobUrl = r.url;
  finish({ ok: true, errors: [], warnings, duration, bytes: r.blob.size, ext: r.ext, sheet, poster, doc, report: report || { scenes: [], notes: [], checks: [] } });
}

try {
  if (job.script && (job.grain == null || job.grain === "film")) {
    const { runJob } = await import("./js/video/director.js");
    await runJob(job);
  } else if (job.script) {
    const { runJob } = await import("./js/video/director.js");
    const { installVideoApi } = await import("./js/api/videoApi.js");
    // compile and check only: the director leaves the compiled doc and its report (and sets __done, taken back here at
    // once: the runner cannot look between this and the next line)
    const pre = await runJob({ ...job, sheetOnly: true, times: [0], cols: 1, scale: 0.05 });
    window.__done = false; window.__result = null;
    if (!pre || !pre.ok || !pre.doc) finish(pre || { ok: false, errors: ["the script did not compile"] });
    else {
      // the grain the look laid over every picture scene, turned down (subtle) or taken out (none)
      const k = job.grain === "subtle" ? 0.35 : 0;
      for (const s of pre.doc.scenes || pre.doc.clips || [])
        if (Array.isArray(s.effects))
          s.effects = s.effects.map((e) => e && e.type === "noise" ? (k > 0 ? { ...e, amount: +(Number(e.amount || 0) * k).toFixed(4) } : null) : e).filter(Boolean);
      await renderDoc(installVideoApi(), pre.doc, pre.report, pre.warnings);
    }
  } else {
    const { installVideoApi } = await import("./js/api/videoApi.js");
    const api = installVideoApi();
    const op = job.op || "render";
    window.__progress = 0;
    if (op === "docs") finish({ ok: true, docs: api.docs() });
    else if (op === "spec") finish({ ok: true, spec: api.spec() });
    else if (op === "render") await renderDoc(api, job.doc, null, []);
    else {
      const v = api.validate(job.doc);
      if (op === "validate" || !v.ok) finish({ ok: v.ok, errors: v.errors, warnings: v.warnings });
      else {
        stage("compile", 0.03);
        const built = await api.build(job.doc);
        const duration = await api.durationOf(built.tl);
        if (op === "frame") {
          const f = await api.frame(built.tl, Math.max(0, Math.min(job.t || 0, duration)), { scale: job.scale || 1, type: "image/png" });
          finish({ ok: true, duration, dataUrl: f.dataUrl, width: f.width, height: f.height, warnings: v.warnings });
        } else {
          const sh = await api.sheet(built.tl, { frames: job.frames || 8, cols: job.cols || 4, scale: job.scale || 0.25, times: job.times || null });
          finish({ ok: true, duration, dataUrl: sh.dataUrl, times: sh.times, warnings: v.warnings });
        }
      }
    }
  }
} catch (e) {
  out.textContent += "ERROR " + (e && e.stack || e) + "\n";
  if (!window.__done) finish({ ok: false, errors: [said(e)] });
}
</script></body></html>
""";

        // ── probe: one look at the Studio's video API in a page of its own ─────────────────────────────────────
        /// <summary>probe { op: docs | spec | validate | frame | sheet, doc_json?, media: {name → file}, t, times, frames,
        /// cols, scale, studio_url } → { result: the page's result as JSON text, page_log }. A doc's files are named
        /// job:&lt;name&gt; and <c>media</c> says which file each name is. For the editor: the API reference, a doc's
        /// problems, one frame or a contact sheet, before anything is rendered.</summary>
        private static Dictionary<string, object> Probe(Dictionary<string, object> p, Action<string> log)
        {
            var op = (Str(p, "op") ?? "validate").Trim().ToLowerInvariant();
            if (op is not ("docs" or "spec" or "validate" or "frame" or "sheet")) throw new ArgumentException("probe op must be docs, spec, validate, frame or sheet");
            var limit = op is "sheet" ? 300 : 180;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(limit));
            try { return ProbeAsync(op, p, log, cts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new InvalidOperationException($"the Studio page did not finish the {op} within {limit} s"); }
        }

        private static async Task<Dictionary<string, object>> ProbeAsync(string op, Dictionary<string, object> p, Action<string> log, CancellationToken ct)
        {
            var exe = FindBrowser() ?? throw new InvalidOperationException("no Chrome or Edge on this machine; the Studio runs in one");
            var studio = NormalizeStudioUrl(Str(p, "studio_url"));
            var id = "probe-" + Guid.NewGuid().ToString("n").Substring(0, 8);
            var dir = Path.Combine(Root, id);
            var mediaDir = Path.Combine(dir, "media");
            var mediaBase = studio + "__vanity_job/" + id + "/media/";
            var pageUrl = studio + "__vanity_job.html";
            _captures[id] = 0;
            Browser browser = null;
            try
            {
                Directory.CreateDirectory(mediaDir);
                if (p.TryGetValue("media", out var mv) && mv is IDictionary<string, string> media)
                    foreach (var kv in media)
                        if (File.Exists(kv.Value)) File.Copy(kv.Value, Path.Combine(mediaDir, SafeName(kv.Key)), true);
                JsonNode doc = null;
                var docText = Str(p, "doc_json");
                if (!string.IsNullOrWhiteSpace(docText))
                {
                    doc = JsonNode.Parse(docText);
                    var missing = new List<string>();
                    doc = MapJobMedia(doc, mediaBase, mediaDir, missing);
                    if (missing.Count > 0) throw new ArgumentException("files the doc names are not there: " + string.Join(", ", missing.Distinct()));
                }
                double D(string k, double def) => p.TryGetValue(k, out var v) && v != null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
                var pageJob = new JsonObject { ["op"] = op, ["doc"] = doc, ["t"] = D("t", 0), ["scale"] = D("scale", op == "frame" ? 1 : 0.25), ["frames"] = (int)D("frames", 8), ["cols"] = (int)D("cols", 4) };
                if (p.TryGetValue("times", out var tv) && tv is IEnumerable<double> times) pageJob["times"] = new JsonArray(times.Select(t => (JsonNode)JsonValue.Create(t)).ToArray());
                CloseLeftoverBrowsers(log);
                browser = await Browser.StartAsync(exe, Path.Combine(Path.GetTempPath(), ProfilePrefix + id), ct).ConfigureAwait(false);
                var (cdp, tab) = await browser.OpenPageAsync(ct).ConfigureAwait(false);
                using (cdp)
                {
                    var pageLog = new List<string>();
                    cdp.OnEvent += (method, prm) =>
                    {
                        if (method == "Fetch.requestPaused") _ = Task.Run(() => Intercept(cdp, prm, pageUrl, mediaBase, mediaDir, studio));
                        else if (method == "Runtime.exceptionThrown")
                        {
                            var d = prm.GetProperty("exceptionDetails");
                            var txt = d.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var de) ? de.GetString() : d.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                            lock (pageLog) if (pageLog.Count < 20) pageLog.Add("exception: " + Clip(txt, 400));
                        }
                    };
                    await cdp.SendAsync("Runtime.enable", null, ct).ConfigureAwait(false);
                    await cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
                    await cdp.SendAsync("Fetch.enable", new { patterns = new[] { new { urlPattern = "*__vanity_*", requestStage = "Request" } } }, ct).ConfigureAwait(false);
                    await cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = "window.__job = " + pageJob.ToJsonString() + ";" }, ct).ConfigureAwait(false);
                    log($"[studio] {op} on {studio}");
                    await cdp.SendAsync("Page.navigate", new { url = pageUrl }, ct).ConfigureAwait(false);
                    while (true)
                    {
                        await Task.Delay(400, ct).ConfigureAwait(false);
                        var d = await cdp.EvalAsync("window.__done === true", ct).ConfigureAwait(false);
                        if (d.ValueKind == JsonValueKind.True) break;
                    }
                    var res = await cdp.EvalAsync("JSON.stringify(window.__result || null)", ct).ConfigureAwait(false);
                    var outText = await cdp.EvalAsync("document.getElementById('out') ? document.getElementById('out').textContent : ''", ct).ConfigureAwait(false);
                    if (outText.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(outText.GetString())) lock (pageLog) pageLog.Add(Clip(outText.GetString().Trim(), 1200));
                    await browser.ClosePageAsync(tab).ConfigureAwait(false);
                    return new Dictionary<string, object> { { "result", res.ValueKind == JsonValueKind.String ? res.GetString() : "null" }, { "page_log", string.Join("\n", pageLog) } };
                }
            }
            finally
            {
                browser?.Dispose();
                _captures.TryRemove(id, out _);
                for (int i = 0; i < 5 && Directory.Exists(dir); i++) { try { Directory.Delete(dir, true); } catch { Thread.Sleep(200); } }
            }
        }

        // ── the browser ────────────────────────────────────────────────────────
        /// <summary>Every Studio browser's profile folder starts with this, in the temp folder: <c>vanity-studio-&lt;job&gt;</c>
        /// for a job, so a leftover is told apart and a running job's browser is never taken for one.</summary>
        public const string ProfilePrefix = "vanity-studio-";

        /// <summary>The headless browser's command line. The host may run elevated, and a Chromium started from an elevated
        /// process re-launches itself de-elevated: the process we started exits at once, the kill in the finally block
        /// sees HasExited and skips, the re-launched browser is outside our job object, and every step leaves a hidden
        /// Chrome (about 9 processes, 120-250 MB) behind: 12 of them after one video (2026-10-07). With
        /// --do-not-de-elevate the process we start IS the browser, so it is killed after the job and dies with the host.</summary>
        public static string[] BrowserArgs(int port, string profile) => new[]
        {
            // SwiftShader WebGL: the shader transitions render the same on every machine, GPU or not
            "--headless=new", "--disable-gpu", "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--no-first-run",
            "--no-default-browser-check", "--mute-audio", "--hide-scrollbars", "--disable-extensions", "--autoplay-policy=no-user-gesture-required",
            "--do-not-de-elevate", "--remote-debugging-port=" + port, "--user-data-dir=" + profile, "about:blank",
        };

        /// <summary>Closes Studio browsers left running and removes their profile folders: every
        /// <see cref="ProfilePrefix"/> folder in the temp folder that is a browser profile (Chrome's DevToolsActivePort or
        /// Local State in it, or the 10-hex name of the previous build's one-step browsers) and whose job is NOT running
        /// in this process; jobs that run side by side keep their browsers. Chrome writes DevToolsActivePort (port, then
        /// the browser websocket path) into its profile, so a leftover is asked to close over DevTools; a folder whose
        /// browser is already gone is just deleted. Returns how many browsers were closed.</summary>
        public static int CloseLeftoverBrowsers(Action<string> log)
        {
            int closed = 0, folders = 0;
            HashSet<string> busy;
            lock (_lock) busy = new HashSet<string>(_running.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var c in _captures.Keys) busy.Add(c);
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), ProfilePrefix + "*"))
                {
                    var tail = Path.GetFileName(dir).Substring(ProfilePrefix.Length);
                    if (!IsJobId(tail) || busy.Contains(tail)) continue;   // a job this process is running right now
                    bool legacy = tail.Length == 10 && tail.All(Uri.IsHexDigit);
                    // not a browser profile: the harness's job root and other folders that only share the name
                    if (!legacy && !File.Exists(Path.Combine(dir, "DevToolsActivePort")) && !File.Exists(Path.Combine(dir, "Local State"))) continue;
                    var (c, removed) = CloseProfile(dir);
                    if (c) closed++;
                    if (removed) folders++;
                }
            }
            catch { }
            if (closed > 0 || folders > 0) log?.Invoke($"[studio] closed {closed} leftover browser(s), removed {folders} profile folder(s)");
            return closed;
        }

        // asks the browser that owns this profile folder to close, then removes the folder
        private static (bool closed, bool removed) CloseProfile(string dir)
        {
            bool closed = false, removed = false;
            if (!Directory.Exists(dir)) return (false, false);
            try
            {
                var portFile = Path.Combine(dir, "DevToolsActivePort");
                if (File.Exists(portFile))
                {
                    var lines = File.ReadAllLines(portFile);
                    if (lines.Length >= 2 && int.TryParse(lines[0].Trim(), out var port) && lines[1].StartsWith("/"))
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                        using var cdp = Cdp.ConnectAsync($"ws://127.0.0.1:{port}{lines[1].Trim()}", cts.Token).GetAwaiter().GetResult();
                        try { cdp.SendAsync("Browser.close", null, cts.Token).GetAwaiter().GetResult(); } catch { }   // the socket drops as it closes
                        closed = true;
                    }
                }
            }
            catch { }   // nothing listens: the browser is gone
            for (int i = 0; i < 10; i++) { try { Directory.Delete(dir, true); removed = true; break; } catch { Thread.Sleep(300); } }
            return (closed, removed);
        }

        /// <summary>"Chrome 130.0.6723.117" or "Edge 129..." from the executable's version resource.</summary>
        public static string BrowserLabel(string exe)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(exe).Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Edge" : "Chrome";
                var v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                return string.IsNullOrEmpty(v) ? name : name + " " + v;
            }
            catch { return Path.GetFileName(exe ?? ""); }
        }

        // One job's headless Chrome on its own profile folder: started, asked for tabs, killed with its folder removed.
        private sealed class Browser : IDisposable
        {
            public const string NoAnswer = "the headless browser did not answer";
            public readonly string Exe, Profile;
            private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            private readonly bool _keep;
            private Process _proc;
            private int _port;

            private Browser(string exe, string profile, bool keep) { Exe = exe; Profile = profile; _keep = keep; }

            /// <param name="keep">true for a profile that must survive (the login browser's): it is neither cleared
            /// before the start nor deleted after.</param>
            public static async Task<Browser> StartAsync(string exe, string profile, CancellationToken ct, bool keep = false)
            {
                var b = new Browser(exe, profile, keep);
                try { await b.LaunchAsync(ct).ConfigureAwait(false); return b; }
                catch { b.Dispose(); throw; }
            }

            /// <summary>A DevTools connection to the browser itself (not a tab): Storage and Browser.close live there.</summary>
            public async Task<Cdp> ConnectBrowserAsync(CancellationToken ct)
            {
                using var r = await _http.GetAsync($"http://127.0.0.1:{_port}/json/version", ct).ConfigureAwait(false);
                r.EnsureSuccessStatusCode();
                using var jd = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return await Cdp.ConnectAsync(jd.RootElement.GetProperty("webSocketDebuggerUrl").GetString(), ct).ConfigureAwait(false);
            }

            private async Task LaunchAsync(CancellationToken ct)
            {
                if (!_keep) CloseProfile(Profile);   // every run gets a fresh profile; one a stopped run left is closed first
                _port = FreePort();
                var psi = new ProcessStartInfo(Exe)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    // the browser's own chatter ("DevTools listening on ...", push-messaging registration errors of a
                    // fresh profile) goes to the log, not into the operator's console
                    RedirectStandardError = true, RedirectStandardOutput = true,
                };
                foreach (var a in BrowserArgs(_port, Profile)) psi.ArgumentList.Add(a == "about:blank" ? "--window-size=" + DeskW + "," + DeskH : a);
                // no background services in a render browser: no push-messaging sign-in, sync or component updates
                foreach (var a in QuietArgs) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("about:blank");
                _proc = Process.Start(psi) ?? throw new InvalidOperationException("the browser did not start");
                DrainOutput(_proc);
                try { OwnProcess?.Invoke(_proc); } catch { }
                // a cold start on a busy machine (antivirus scanning the fresh profile) can take well over 15 s
                for (int i = 0; i < 450; i++)
                {
                    try
                    {
                        using var r = await _http.GetAsync($"http://127.0.0.1:{_port}/json/version", ct).ConfigureAwait(false);
                        if (r.IsSuccessStatusCode) return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                throw new InvalidOperationException(NoAnswer);
            }

            // a new tab and a DevTools connection to it (the tab's id closes it again)
            public async Task<(Cdp cdp, string target)> OpenPageAsync(CancellationToken ct)
            {
                using var r = await _http.PutAsync($"http://127.0.0.1:{_port}/json/new?about:blank", null, ct).ConfigureAwait(false);
                r.EnsureSuccessStatusCode();
                using var jd = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var ws = jd.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                var id = jd.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                return (await Cdp.ConnectAsync(ws, ct).ConfigureAwait(false), id);
            }

            public async Task ClosePageAsync(string target)
            {
                if (string.IsNullOrEmpty(target)) return;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var r = await _http.GetAsync($"http://127.0.0.1:{_port}/json/close/{target}", cts.Token).ConfigureAwait(false);
                }
                catch { }
            }

            public void Dispose()
            {
                // a kept profile was asked to close (Browser.close): let it finish writing before anything is killed
                if (_keep) { try { _proc?.WaitForExit(5000); } catch { } }
                try { if (_proc != null && !_proc.HasExited) { _proc.Kill(true); _proc.WaitForExit(5000); } } catch { }
                try { _proc?.Dispose(); } catch { }
                _http.Dispose();
                if (_keep) return;
                for (int i = 0; i < 10; i++) { try { if (Directory.Exists(Profile)) Directory.Delete(Profile, true); break; } catch { Thread.Sleep(300); } }
            }
        }

        // ── helpers ────────────────────────────────────────────────────────────
        public static string FindBrowser()
        {
            if (!string.IsNullOrEmpty(BrowserPath) && File.Exists(BrowserPath)) return BrowserPath;
            foreach (var given in new[] { AgentConfig.Setting("BrowserPath"), Environment.GetEnvironmentVariable("CHROME_PATH") })
                if (!string.IsNullOrWhiteSpace(given) && File.Exists(given)) return given;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Chrome first: its WebCodecs H.264 encoder is the one the Studio's export is tuned on
            var candidates = OperatingSystem.IsWindows()
                ? new[]
                {
                    Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
                }
                : OperatingSystem.IsMacOS()
                ? new[]
                {
                    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                    "/Applications/Chromium.app/Contents/MacOS/Chromium",
                }
                : new[]
                {
                    "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/opt/google/chrome/chrome",
                    "/usr/bin/microsoft-edge", "/usr/bin/microsoft-edge-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/snap/bin/chromium",
                };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        // MaxParallel: the environment first, then the VideoParallel setting in config.json, else 2
        private static int ReadMaxParallel()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("VANITY_VIDEO_PARALLEL"), out var n) && n > 0) return Math.Min(n, 8);
            try { if (int.TryParse(AgentConfig.Setting("VideoParallel"), out n) && n > 0) return Math.Min(n, 8); } catch { }
            return 2;
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
        private static string Text(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        private static List<string> Strings(JsonElement o, string key) =>
            o.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.ToString()).ToList() : new List<string>();
        private static string Clip(string s, int n) { s = s ?? ""; return s.Length > n ? s.Substring(0, n) + "…" : s; }
        private static string Str(Dictionary<string, object> p, string k) => p != null && p.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        private static long Long(Dictionary<string, object> p, string k)
        {
            if (p == null || !p.TryGetValue(k, out var v) || v == null) return 0;
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is double d) return (long)d;
            if (v is decimal m) return (long)m;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var n) ? n : 0;
        }

        // ── a minimal DevTools-protocol client over one page websocket ─────────
        private sealed class Cdp : IDisposable
        {
            private readonly ClientWebSocket _ws = new ClientWebSocket();
            private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
            private readonly SemaphoreSlim _send = new SemaphoreSlim(1, 1);
            private readonly CancellationTokenSource _life = new CancellationTokenSource();
            private int _id;
            public event Action<string, JsonElement> OnEvent;

            public static async Task<Cdp> ConnectAsync(string url, CancellationToken ct)
            {
                var c = new Cdp();
                c._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await c._ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                _ = Task.Run(c.ReceiveLoop);
                return c;
            }

            private async Task ReceiveLoop()
            {
                var buf = new byte[1 << 20];
                var ms = new MemoryStream();
                try
                {
                    while (_ws.State == WebSocketState.Open && !_life.IsCancellationRequested)
                    {
                        var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _life.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count);
                        if (!r.EndOfMessage) continue;
                        var bytes = ms.ToArray();
                        ms.SetLength(0);
                        JsonElement root;
                        try { using var jd = JsonDocument.Parse(bytes); root = jd.RootElement.Clone(); } catch { continue; }
                        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && _pending.TryRemove(id, out var tcs))
                        {
                            if (root.TryGetProperty("error", out var err)) tcs.TrySetException(new InvalidOperationException("CDP: " + err.ToString()));
                            else tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res : default);
                        }
                        else if (root.TryGetProperty("method", out var m))
                        {
                            var prm = root.TryGetProperty("params", out var pp) ? pp : default;
                            try { OnEvent?.Invoke(m.GetString(), prm); } catch { }
                        }
                    }
                }
                catch { }
                foreach (var kv in _pending) kv.Value.TrySetException(new IOException("the browser connection closed"));
            }

            public async Task<JsonElement> SendAsync(string method, object prms, CancellationToken ct)
            {
                int id = Interlocked.Increment(ref _id);
                var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = tcs;
                var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", prms ?? new object() } });
                await _send.WaitAsync(ct).ConfigureAwait(false);
                try { await _ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
                finally { _send.Release(); }
                using (ct.Register(() => tcs.TrySetCanceled()))
                    return await tcs.Task.ConfigureAwait(false);
            }

            public async Task<JsonElement> EvalAsync(string expr, CancellationToken ct)
            {
                var r = await SendAsync("Runtime.evaluate", new { expression = expr, returnByValue = true, awaitPromise = true }, ct).ConfigureAwait(false);
                return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("result", out var rr) && rr.TryGetProperty("value", out var v) ? v : default;
            }

            public void Dispose()
            {
                try { _life.Cancel(); } catch { }
                try { _ws.Abort(); } catch { }
                _ws.Dispose();
            }
        }
    }
}
