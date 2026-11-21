using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinControl;

internal static class RealtimeBridge
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };
    private static readonly ConcurrentDictionary<int, ClientSession> Clients = new();
    private static int _nextClientId;

    public static int Run(int port)
    {
        port = Math.Clamp(port, 1024, 65535);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Event = "realtime_bridge_started",
            Host = "127.0.0.1",
            Port = port,
            Protocol = "NDJSON over local TCP (loopback only)",
            Elevated = AccessibilityReader.IsElevated(),
            Note = "Envia {\"id\":\"1\",\"tool\":\"desktop_screen_summary\",\"args\":{}} o escucha eventos push. Ctrl+C para salir."
        }, CompactJson));
        Console.Out.Flush();

        string lastSig = "";
        object sigLock = new();

        void BroadcastEvent(AccessibilityReader.UiEvent ev)
        {
            string sig = $"{ev.Event}|{ev.ProcessId}|{ev.WindowTitle}|{ev.ControlType}|{ev.Name}|{ev.Value}";
            lock (sigLock)
            {
                if (sig == lastSig) return;
                lastSig = sig;
            }

            string payload = JsonSerializer.Serialize(new { type = "event", data = ev }, CompactJson);
            foreach (var kv in Clients)
            {
                if (!kv.Value.TrySendLine(payload))
                {
                    if (Clients.TryRemove(kv.Key, out var dead))
                        dead.Dispose();
                }
            }
        }

        var watchTask = Task.Run(() => AccessibilityReader.RunWatchLoop(cts.Token, BroadcastEvent), cts.Token);

        try
        {
            AcceptLoopAsync(listener, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { listener.Stop(); } catch { }
            cts.Cancel();
            foreach (var kv in Clients)
                kv.Value.Dispose();
            Clients.Clear();
            try { watchTask.Wait(1500); } catch { }
        }

        return 0;
    }

    private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            int id = Interlocked.Increment(ref _nextClientId);
            var session = new ClientSession(client);
            Clients[id] = session;

            _ = Task.Run(() => HandleClientAsync(id, session, ct), ct);
        }
    }

    private static async Task HandleClientAsync(int clientId, ClientSession session, CancellationToken ct)
    {
        try
        {
            session.TrySendLine(JsonSerializer.Serialize(new
            {
                type = "hello",
                server = "win-control",
                version = "2.1.0",
                elevated = AccessibilityReader.IsElevated()
            }, CompactJson));

            while (!ct.IsCancellationRequested)
            {
                string? line = await session.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var req = JsonNode.Parse(line)?.AsObject();
                    if (req == null) continue;

                    var reqId = req["id"]?.DeepClone();
                    string tool = req["tool"]?.GetValue<string>() ?? "";
                    var args = req["args"] as JsonObject ?? new JsonObject();

                    var result = McpServer.ExecuteTool(tool, args);
                    var resp = new JsonObject
                    {
                        ["type"] = "response",
                        ["id"] = reqId,
                        ["result"] = result
                    };
                    session.TrySendLine(resp.ToJsonString(CompactJson));
                }
                catch (Exception ex)
                {
                    var err = new JsonObject
                    {
                        ["type"] = "error",
                        ["message"] = ex.Message
                    };
                    session.TrySendLine(err.ToJsonString(CompactJson));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (Clients.TryRemove(clientId, out var removed))
                removed.Dispose();
        }
    }

    private sealed class ClientSession : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly object _writeLock = new();
        private bool _disposed;

        public ClientSession(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken ct) => _reader.ReadLineAsync(ct);

        public bool TrySendLine(string line)
        {
            lock (_writeLock)
            {
                if (_disposed) return false;
                try
                {
                    _writer.WriteLine(line);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock (_writeLock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _writer.Dispose(); } catch { }
                try { _reader.Dispose(); } catch { }
                try { _client.Dispose(); } catch { }
            }
        }
    }
}
