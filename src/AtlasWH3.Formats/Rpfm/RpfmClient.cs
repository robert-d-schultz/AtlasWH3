using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AtlasWH3.Formats.Rpfm;

/// <summary>A request RPFM's server refused or failed, or a server AtlasWH3 cannot use.</summary>
public sealed class RpfmException(string message) : Exception(message);

/// <summary>
/// A session with RPFM's server (rpfm_server.exe, RPFM 5.3 or newer): JSON-RPC 2.0 over a WebSocket at
/// ws://127.0.0.1:45127/ws (docs/server in github.com/Frodo45127/rpfm). Every pack file and DB table AtlasWH3 reads or
/// writes goes through it. The server is started when nothing listens. It exits by itself once its last session
/// disconnects, so a connection made while it shuts down gets no greeting; that one is dropped and the connection
/// retried on a new server. Requests are sent one at a time: a session runs them serially anyway.
/// </summary>
public sealed class RpfmClient : IDisposable
{
    public const string Host = "127.0.0.1:45127";
    public const string Game = "warhammer_3";
    public static readonly Version MinimumVersion = new(5, 3, 0);

    /// <summary>rpfm_server.exe, to start it when no server is running (the host sets this from its settings). Null when
    /// unknown: then only a running server is used.</summary>
    public static Func<string?> LocateServer { get; set; } = () => null;

    /// <summary>RPFM's own settings file, which sessions start with.</summary>
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FrodoWazEre", "rpfm", "config", "settings.json");

    private static readonly Lock SharedGate = new();
    private static RpfmClient? _shared;

    /// <summary>This process's session, with WH3 selected: connected on first use, disconnected when the process exits.</summary>
    public static RpfmClient Shared
    {
        get
        {
            lock (SharedGate)
            {
                if (_shared is { _closed: false }) return _shared;
                if (_shared is null) AppDomain.CurrentDomain.ProcessExit += (_, _) => _shared?.Dispose();
                return _shared = Connect();
            }
        }
    }

    private readonly WebSocket _ws;
    private readonly Lock _gate = new();
    private long _nextId;
    private bool _closed;
    private string? _gameFolder;
    private int? _serverPid;

    private RpfmClient(WebSocket ws) => _ws = ws;

    /// <summary>A new session with WH3 selected (its schema loaded, no dependencies: AtlasWH3 opens the packs it reads).</summary>
    public static RpfmClient Connect()
    {
        var started = false;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                var ws = Open(tcp);
                tcp.Client.ReceiveTimeout = 5000;
                if ((string?)JsonNode.Parse(Receive(ws))?["method"] != "session.connected")
                    throw new IOException("no session.connected greeting");
                tcp.Client.ReceiveTimeout = 0;
                var client = new RpfmClient(ws);
                client.Start();
                return client;
            }
            catch (Exception e) when (IsRefused(e))
            {
                tcp.Dispose();
                if (!started)
                {
                    StartServer();
                    started = true;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or WebSocketException)
            {
                // a server shutting down drops new connections: start another one if nothing answers next time
                tcp.Dispose();
                started = false;
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(60))
                throw new RpfmException($"RPFM's server did not answer at {Host} (RPFM {MinimumVersion} or newer is needed)");
            Thread.Sleep(250);
        }
    }

    /// <summary>The WebSocket handshake on a plain TCP connection, then a WebSocket over a stream that completes its
    /// async calls synchronously: a reply never waits for a thread-pool thread, which the build's parallel steps keep
    /// busy (ClientWebSocket's completions waited up to tens of milliseconds there).</summary>
    private static WebSocket Open(TcpClient tcp)
    {
        tcp.Connect("127.0.0.1", 45127);
        var stream = tcp.GetStream();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        stream.Write(Encoding.ASCII.GetBytes($"GET /ws HTTP/1.1\r\nHost: {Host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                             $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"));
        // the response headers, byte by byte: the greeting frame follows right after them
        var headers = new List<byte>();
        while (headers.Count < 4 || headers[^4] != '\r' || headers[^3] != '\n' || headers[^2] != '\r' || headers[^1] != '\n')
        {
            var b = stream.ReadByte();
            if (b < 0 || headers.Count > 8192) throw new IOException("RPFM's server closed the connection during the handshake");
            headers.Add((byte)b);
        }
        var status = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n")[0];
        if (!status.Contains(" 101 ")) throw new IOException($"RPFM's server refused the WebSocket: {status}");
        return WebSocket.CreateFromStream(new SyncStream(stream), new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
    }

    /// <summary>A stream whose async calls run synchronously on the caller's thread.</summary>
    private sealed class SyncStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.FromResult(inner.Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => new(inner.Read(buffer.Span));
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            inner.Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            inner.Write(buffer.Span);
            return default;
        }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token)
        {
            inner.Flush();
            return Task.CompletedTask;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static bool IsRefused(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return true;
        return false;
    }

    private static void StartServer()
    {
        var exe = LocateServer();
        if (string.IsNullOrWhiteSpace(exe))
            throw new RpfmException("RPFM's server is not running, and AtlasWH3 does not know where rpfm_server.exe is: set RPFM's folder in Settings");
        if (!File.Exists(exe)) throw new RpfmException($"rpfm_server.exe not found: {exe}");
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true });
    }

    private void Start()
    {
        var server = Get("version")!;
        var version = (string?)server["version"];
        if (!Version.TryParse(version?.Split('-')[0], out var v) || v < MinimumVersion)
            throw new RpfmException($"RPFM {version} is running; AtlasWH3 needs RPFM {MinimumVersion} or newer");
        _serverPid = (int?)server["pid"];
        var status = Call("session.set_game", new { game = Game, rebuild_dependencies = false })!;
        if (status["schema_loaded"]?.GetValue<bool>() != true)
            throw new RpfmException("RPFM has no schema for Warhammer III: open RPFM and update its schemas");
    }

    /// <summary>Points the session at <paramref name="gameFolder"/> (the folder holding data\), for the vanilla packs: the
    /// user's RPFM settings with that game path.</summary>
    public void UseGameFolder(string gameFolder)
    {
        gameFolder = Path.GetFullPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar);
        lock (_gate)
        {
            if (string.Equals(_gameFolder, gameFolder, StringComparison.OrdinalIgnoreCase)) return;
            var settings = File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject() : [];
            if (settings["string"] is not JsonObject strings) settings["string"] = strings = [];
            strings[Game] = gameFolder.Replace('\\', '/');
            Call("session.configure", new { settings });
            _gameFolder = gameFolder;
        }
    }

    /// <summary>The result of <paramref name="method"/>. A method that runs as a job is waited for, and its result returned.</summary>
    public JsonNode? Call(string method, object? parameters = null)
    {
        var result = JsonNode.Parse(Request(method, parameters))!["result"];
        if (result is not JsonObject { Count: 1 } o || o["job"] is not JsonValue job) return result;
        while (true)
        {
            var status = JsonNode.Parse(Request("job.wait", new { job = job.GetValue<long>(), timeout_secs = 600 }))!["result"]!;
            switch ((string?)status["state"])
            {
                case "finished": return status["result"];
                case "failed": throw new RpfmException($"{method}: {status["error"]?["message"]}");
                case "cancelled": throw new RpfmException($"{method}: cancelled");
            }
        }
    }

    /// <summary>The bytes of a file of an open pack (file.read, raw), decoded straight from the reply.</summary>
    public byte[] ReadFile(string packKey, string path)
    {
        using var reply = JsonDocument.Parse(Request("file.read", new { file = new { source = new { pack = packKey }, path }, format = "raw" }));
        return reply.RootElement.GetProperty("result").GetProperty("contents").GetProperty("base64").GetBytesFromBase64();
    }

    /// <summary>The whole reply to one request (notifications in between are skipped); an error reply throws.</summary>
    private byte[] Request(string method, object? parameters)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var id = ++_nextId;
            var message = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });
            _ws.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
            while (true)
            {
                var reply = Receive(_ws);
                using var doc = JsonDocument.Parse(reply);
                if (!doc.RootElement.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number || replyId.GetInt64() != id)
                    continue;
                if (doc.RootElement.TryGetProperty("error", out var error))
                    throw new RpfmException($"{method}: {error.GetProperty("message").GetString()}");
                return reply;
            }
        }
    }

    private static byte[] Receive(WebSocket ws)
    {
        var buffer = new ArrayBufferWriter<byte>(1 << 16);
        while (true)
        {
            var pending = ws.ReceiveAsync(buffer.GetMemory(1 << 16), CancellationToken.None);
            var r = pending.IsCompleted ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
            if (r.MessageType == WebSocketMessageType.Close) throw new IOException("RPFM's server closed the connection");
            buffer.Advance(r.Count);
            if (r.EndOfMessage) return buffer.WrittenSpan.ToArray();
        }
    }

    /// <summary>One of the server's REST endpoints (version, sessions).</summary>
    private static JsonNode? Get(string endpoint)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        return JsonNode.Parse(http.GetStringAsync($"http://{Host}/{endpoint}").GetAwaiter().GetResult());
    }

    /// <summary>Ends the session (session.disconnect), so the server drops it at once. The last session leaving shuts the
    /// server down; then this waits for it to exit, so the next connection starts a new server instead of joining one
    /// that drops it mid-request.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            bool last;
            try { last = Get("sessions") is JsonArray { Count: <= 1 }; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { last = false; }
            try { Request("session.disconnect", null); }
            catch (Exception e) when (e is IOException or WebSocketException or RpfmException) { }
            _closed = true;
            _ws.Dispose();
            if (last && _serverPid is { } pid)
                try { using var server = Process.GetProcessById(pid); server.WaitForExit(TimeSpan.FromSeconds(10)); }
                catch (ArgumentException) { }   // exited already
        }
    }
}
