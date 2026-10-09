using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VanityStudio.Infra;

namespace VanityStudio.Video;

/// <summary>
/// The edit links: a small server on this computer (127.0.0.1, port 47812 when it is free) while Vanity Studio runs, so
/// a finished video opens in the Studio's editor from a link the operator clicks or copies.
/// <list type="bullet">
/// <item><c>/edit/&lt;job&gt;</c> sends the browser to the Studio with <c>#/video/new?importUrl=</c> this server's copy of
/// the job's project; the Studio fetches it, imports it as a new project and opens it in the editor.</item>
/// <item><c>/p/&lt;job&gt;.vstudio.json</c> is that project file, readable only by pages of the Studio's own origin (CORS),
/// with the private-network answer Chrome asks a public page before it may fetch from this computer.</item>
/// </list>
/// The browser may ask once to let the Studio reach this computer: allow it. A link works while Vanity Studio is open.
/// </summary>
public sealed class ProjectServer : IDisposable
{
    public const int PreferredPort = 47812;
    private static ProjectServer? _current;
    private static readonly object Gate = new();

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Func<long, string?> _projectOf;
    public int Port { get; }

    private ProjectServer(TcpListener listener, Func<long, string?> projectOf)
    {
        _listener = listener;
        _projectOf = projectOf;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The session's server, started on first use. <paramref name="projectOf"/> gives a job's project file
    /// (full path) or null; a later call (another folder) replaces it.</summary>
    public static ProjectServer For(Func<long, string?> projectOf)
    {
        lock (Gate)
        {
            if (_current is not null) { _current._projectOf = projectOf; return _current; }
            TcpListener l;
            try { l = new TcpListener(IPAddress.Loopback, PreferredPort); l.Start(); }
            catch (SocketException) { l = new TcpListener(IPAddress.Loopback, 0); l.Start(); }   // another session has the port
            return _current = new ProjectServer(l, projectOf);
        }
    }

    public string EditLink(long job) => $"http://127.0.0.1:{Port}/edit/{job}";
    public string ProjectUrl(long job) => $"http://127.0.0.1:{Port}/p/{job}.vstudio.json";
    public string StudioLink(long job) => MakeVideoTool.StudioUrl() + "#/video/new?importUrl=" + Uri.EscapeDataString(ProjectUrl(job));

    /// <summary>Opens the job's project in the Studio in the default browser. Returns the link it opened.</summary>
    public string Open(long job)
    {
        var link = EditLink(job);
        if (Environment.GetEnvironmentVariable("VANITY_STUDIO_NO_BROWSER") is "1" or "true") return link;   // scripts and tests
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); } catch { }
        return link;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 10000;
                var head = ReadHead(stream);
                if (head.Length == 0) return;
                var lines = head.Split("\r\n");
                var first = lines[0].Split(' ');
                var method = first.Length > 0 ? first[0] : "";
                var target = first.Length > 1 ? first[1].Split('?')[0] : "";
                var origin = lines.FirstOrDefault(l => l.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))?[7..].Trim();
                var studio = new Uri(MakeVideoTool.StudioUrl()).GetLeftPart(UriPartial.Authority);
                // only the Studio's pages may read a project from here
                var cors = origin is not null && origin.Equals(studio, StringComparison.OrdinalIgnoreCase)
                    ? $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Methods: GET, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Private-Network: true\r\nVary: Origin\r\n"
                    : "Vary: Origin\r\n";
                const string common = "Cache-Control: no-store\r\nConnection: close\r\n";
                byte[] payload = [];
                string reply;
                if (method == "OPTIONS") reply = "HTTP/1.1 204 No Content\r\n" + cors + common + "Content-Length: 0\r\n\r\n";
                else if (method == "GET" && TryJob(target, "/edit/", "", out var editJob) && _projectOf(editJob) is not null)
                {
                    // the short link: on to the Studio, which imports the project from the long one
                    var to = StudioLink(editJob);
                    payload = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Opening in Vanity Studio</title><a href=\"{WebUtility.HtmlEncode(to)}\">Open in Vanity Studio</a>");
                    reply = "HTTP/1.1 302 Found\r\n" + $"Location: {to}\r\n" + common + $"Content-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\n\r\n";
                }
                else if (method == "GET" && TryJob(target, "/p/", ".vstudio.json", out var job) && _projectOf(job) is { } file && File.Exists(file))
                {
                    payload = File.ReadAllBytes(file);
                    reply = "HTTP/1.1 200 OK\r\n" + cors + common + $"Content-Type: application/json\r\nContent-Length: {payload.Length}\r\n\r\n";
                }
                else
                {
                    payload = Encoding.UTF8.GetBytes("Not here: this link belongs to a video of another Vanity Studio session, or Vanity Studio was closed.");
                    reply = "HTTP/1.1 404 Not Found\r\n" + cors + common + $"Content-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\n\r\n";
                }
                var h = Encoding.ASCII.GetBytes(reply);
                stream.Write(h, 0, h.Length);
                if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch (Exception ex) { Log.Debug("[edit-link] " + ex.Message); }
        }
    }

    private static bool TryJob(string target, string prefix, string suffix, out long job)
    {
        job = 0;
        if (!target.StartsWith(prefix, StringComparison.Ordinal) || !target.EndsWith(suffix, StringComparison.Ordinal)) return false;
        return long.TryParse(target[prefix.Length..^suffix.Length], out job) && job > 0;
    }

    private static string ReadHead(NetworkStream stream)
    {
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (sb.Length < 16384)
        {
            if (stream.Read(buf, 0, 1) <= 0) break;
            sb.Append((char)buf[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}
