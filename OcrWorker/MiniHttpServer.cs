using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AnywhereOcr;

public sealed record HttpReply(int Status, string ContentType, byte[] Body);

/// <summary>
/// A deliberately tiny HTTP/1.1 server on a raw loopback socket. It avoids HTTP.sys
/// (HttpListener) so no URL ACL is needed and the dependency surface stays minimal.
/// </summary>
public sealed class MiniHttpServer
{
    private readonly TcpListener _listener;
    private readonly Func<string, string, byte[], HttpReply> _handler;

    public MiniHttpServer(int port, Func<string, string, byte[], HttpReply> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _handler = handler;
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;
                using var stream = client.GetStream();

                var buffer = new byte[8192];
                var received = new List<byte>();
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) return;
                    received.AddRange(buffer.AsSpan(0, n).ToArray());
                    headerEnd = FindHeaderEnd(received);
                    if (received.Count > 1 << 20) return;
                }

                string header = Encoding.Latin1.GetString(received.ToArray(), 0, headerEnd);
                var lines = header.Split("\r\n");
                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2) return;
                string method = requestLine[0], target = requestLine[1];

                int contentLength = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    if (lines[i].AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(lines[i].AsSpan(colon + 1).Trim(), out contentLength);
                }

                int bodyStart = headerEnd + 4;
                var body = new byte[contentLength];
                int have = Math.Min(contentLength, received.Count - bodyStart);
                received.CopyTo(bodyStart, body, 0, have);
                while (have < contentLength)
                {
                    int n = stream.Read(body, have, contentLength - have);
                    if (n <= 0) return;
                    have += n;
                }

                HttpReply reply;
                try { reply = _handler(method, target, body); }
                catch (Exception e)
                {
                    reply = new HttpReply(500, "text/plain", Encoding.UTF8.GetBytes($"{e.GetType().Name}: {e.Message}"));
                }

                string reason = reply.Status switch { 200 => "OK", 404 => "Not Found", 500 => "Internal Server Error", _ => "Status" };
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {reply.Status} {reason}\r\n" +
                    $"Content-Type: {reply.ContentType}; charset=utf-8\r\n" +
                    $"Content-Length: {reply.Body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
                stream.Write(reply.Body, 0, reply.Body.Length);
                stream.Flush();
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private static int FindHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }
}
