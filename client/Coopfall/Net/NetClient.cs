using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coopfall.Net
{
    /// <summary>
    /// TCP client speaking newline-delimited JSON to the WorldfallRooms relay.
    /// - connect happens on a background thread (never blocks the game);
    /// - a reader thread parses lines into JObjects;
    /// - a writer thread drains two queues: FAST (avatars, powers, chat) always before
    ///   BULK (world snapshot chunks), so a multi-MB upload never delays gameplay packets;
    /// - the writer sends a ping every few seconds so a long world load on the main
    ///   thread can't make the server time us out.
    /// Received packets are dispatched on the Unity main thread via Pump().
    /// </summary>
    public class NetClient
    {
        private TcpClient _tcp;
        private NetworkStream _stream;
        private volatile bool _running;
        private readonly object _qLock = new object();
        private readonly Queue<byte[]> _fast = new Queue<byte[]>();
        private readonly Queue<byte[]> _bulk = new Queue<byte[]>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ConcurrentQueue<JObject> _incoming = new ConcurrentQueue<JObject>();
        private readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();
        private long _bulkBytesQueued;

        private static readonly JsonSerializer Ser = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        public volatile bool Connected;
        public volatile bool Connecting;
        public Action<JObject> OnPacket;          // main thread
        public Action OnConnected;               // main thread
        public Action<string> OnDisconnected;    // main thread, with reason

        public long BulkBytesQueued { get { return Interlocked.Read(ref _bulkBytesQueued); } }

        public void ConnectAsync(string host, int port)
        {
            if (Connecting || Connected) return;
            Connecting = true;
            var t = new Thread(() =>
            {
                try
                {
                    var tcp = new TcpClient();
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(8000))
                    {
                        try { tcp.Close(); } catch { }
                        throw new Exception("no answer from " + host + ":" + port + " (server not running, wrong IP, or port not forwarded)");
                    }
                    tcp.EndConnect(ar);
                    tcp.NoDelay = true;
                    tcp.SendBufferSize = 256 * 1024;
                    tcp.ReceiveBufferSize = 256 * 1024;
                    _tcp = tcp;
                    _stream = tcp.GetStream();
                    lock (_qLock) { _fast.Clear(); _bulk.Clear(); Interlocked.Exchange(ref _bulkBytesQueued, 0); }
                    _running = true;
                    Connected = true;
                    new Thread(ReadLoop) { IsBackground = true, Name = "Coopfall-Read" }.Start();
                    new Thread(WriteLoop) { IsBackground = true, Name = "Coopfall-Write" }.Start();
                    _events.Enqueue("+");
                }
                catch (Exception e)
                {
                    string msg = e.Message;
                    if (e is SocketException se) msg = se.SocketErrorCode == SocketError.ConnectionRefused
                        ? "connection refused - is the server running on that port?"
                        : se.Message;
                    _events.Enqueue("-" + msg);
                }
                finally { Connecting = false; }
            });
            t.IsBackground = true;
            t.Name = "Coopfall-Connect";
            t.Start();
        }

        public void Disconnect(string reason = "disconnected")
        {
            bool was = Connected;
            _running = false;
            Connected = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _wake.Set();
            if (was) _events.Enqueue("-" + reason);
        }

        /// <summary>Queue a small, latency-sensitive message.</summary>
        public void Send(string type, object body = null)
        {
            if (!Connected) return;
            JObject o = body == null ? new JObject() : (body as JObject ?? JObject.FromObject(body, Ser));
            o["t"] = type;
            Enqueue(o.ToString(Formatting.None), false);
        }

        /// <summary>Queue a pre-built JSON line on the bulk (low priority) lane.</summary>
        public void SendBulkLine(string json) { Enqueue(json, true); }

        /// <summary>Queue a pre-built JSON line (must not contain raw newlines).</summary>
        public void SendRaw(string json, bool bulk) { Enqueue(json, bulk); }

        private void Enqueue(string json, bool bulk)
        {
            if (!Connected) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
            lock (_qLock)
            {
                if (bulk) { _bulk.Enqueue(bytes); Interlocked.Add(ref _bulkBytesQueued, bytes.Length); }
                else
                {
                    _fast.Enqueue(bytes);
                    while (_fast.Count > 2000) _fast.Dequeue(); // never grow without bound
                }
            }
            _wake.Set();
        }

        /// <summary>Main thread: dispatch connection events and queued packets.</summary>
        public void Pump()
        {
            while (_events.TryDequeue(out string ev))
            {
                try
                {
                    if (ev == "+") OnConnected?.Invoke();
                    else
                    {
                        while (_incoming.TryDequeue(out JObject late)) { try { OnPacket?.Invoke(late); } catch { } }
                        OnDisconnected?.Invoke(ev.Substring(1));
                    }
                }
                catch (Exception e) { Log.Error("net event handler: " + e); }
            }
            int budget = 4000;
            while (budget-- > 0 && _incoming.TryDequeue(out JObject pkt))
            {
                try { OnPacket?.Invoke(pkt); }
                catch (Exception e) { Log.Error("packet '" + (string)pkt["t"] + "' handler: " + e); }
            }
        }

        private void WriteLoop()
        {
            var lastSend = DateTime.UtcNow;
            try
            {
                while (_running)
                {
                    byte[] next = null;
                    bool wasBulk = false;
                    lock (_qLock)
                    {
                        if (_fast.Count > 0) next = _fast.Dequeue();
                        else if (_bulk.Count > 0) { next = _bulk.Dequeue(); wasBulk = true; }
                    }
                    if (next == null)
                    {
                        if ((DateTime.UtcNow - lastSend).TotalSeconds >= 5)
                        {
                            long ts = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                            next = Encoding.UTF8.GetBytes("{\"t\":\"ping\",\"ts\":" + ts + "}\n");
                        }
                        else { _wake.WaitOne(250); continue; }
                    }
                    _stream.Write(next, 0, next.Length);
                    lastSend = DateTime.UtcNow;
                    if (wasBulk) Interlocked.Add(ref _bulkBytesQueued, -next.Length);
                }
            }
            catch (Exception e)
            {
                if (_running) { Log.Warn("write failed: " + e.Message); Disconnect("connection lost"); }
            }
        }

        private void ReadLoop()
        {
            byte[] buf = new byte[256 * 1024];
            var line = new MemoryStream(128 * 1024);
            try
            {
                while (_running)
                {
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    int start = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        line.Write(buf, start, i - start);
                        start = i + 1;
                        ParseLine(line);
                        line.SetLength(0);
                    }
                    if (start < n) line.Write(buf, start, n - start);
                    if (line.Length > 8 * 1024 * 1024) throw new IOException("line too long");
                }
            }
            catch (Exception e)
            {
                if (_running) Log.Warn("read loop ended: " + e.Message);
            }
            if (_running) Disconnect("server closed the connection");
        }

        private void ParseLine(MemoryStream line)
        {
            if (line.Length == 0) return;
            string s = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim();
            if (s.Length == 0) return;
            try { _incoming.Enqueue(JObject.Parse(s)); }
            catch (Exception e) { Log.Warn("bad packet from server (" + e.Message + "): " + (s.Length > 120 ? s.Substring(0, 120) : s)); }
        }
    }
}
