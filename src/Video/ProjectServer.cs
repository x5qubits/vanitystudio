using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VanityStudio.Video;

/// <summary>
/// Opens a finished video's project in the Studio's editor in the operator's browser. The Studio imports a project
/// from an address (<c>#/video/new?importUrl=...</c>), so the project file is served for a few minutes from this
/// machine (127.0.0.1, a random port and a random path), with the CORS and private-network answers a page on the
/// Studio's origin needs to fetch it. The browser may ask once to allow the page to reach this computer.
/// </summary>
public static class ProjectServer
{
    public static string Open(string projectFile, TimeSpan lifetime)
    {
        if (!File.Exists(projectFile)) throw new FileNotFoundException("no project file", projectFile);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var token = Guid.NewGuid().ToString("n");
        var path = $"/{token}/{Uri.EscapeDataString(Path.GetFileName(projectFile))}";
        var url = $"http://127.0.0.1:{port}{path}";
        var bytes = File.ReadAllBytes(projectFile);
        var cts = new CancellationTokenSource(lifetime);
        cts.Token.Register(() => { try { listener.Stop(); } catch { } });
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => Serve(client, path, bytes));
            }
        });
        var edit = MakeVideoTool.StudioUrl() + "#/video/new?importUrl=" + Uri.EscapeDataString(url);
        try { Process.Start(new ProcessStartInfo(edit) { UseShellExecute = true }); } catch { }
        return edit;
    }

    private static void Serve(TcpClient client, string path, byte[] body)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 10000;
                var buf = new byte[16384];
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return;
                var head = Encoding.ASCII.GetString(buf, 0, n);
                var first = head.Split("\r\n")[0].Split(' ');
                var method = first.Length > 0 ? first[0] : "";
                var target = first.Length > 1 ? first[1].Split('?')[0] : "";
                var origin = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))?[7..].Trim() ?? "*";
                var cors = $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Methods: GET, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\n" +
                           "Access-Control-Allow-Private-Network: true\r\nVary: Origin\r\nCache-Control: no-store\r\nConnection: close\r\n";
                string reply;
                byte[] payload = [];
                if (method == "OPTIONS") reply = "HTTP/1.1 204 No Content\r\n" + cors + "Content-Length: 0\r\n\r\n";
                else if (method == "GET" && target == path) { payload = body; reply = "HTTP/1.1 200 OK\r\n" + cors + $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n"; }
                else reply = "HTTP/1.1 404 Not Found\r\n" + cors + "Content-Length: 0\r\n\r\n";
                var h = Encoding.ASCII.GetBytes(reply);
                stream.Write(h, 0, h.Length);
                if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch { }
        }
    }
}
