using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;
using Steamworks.Data;

namespace Coopfall.Net
{
    /// <summary>
    /// Co-op over Steam's relay network (no port forwarding, works behind any router): WorldBox
    /// already ships Facepunch.Steamworks and starts Steam with its app id. A Steam connection is
    /// wrapped as a byte stream (SteamStream), so the relay protocol and the client's reader/writer
    /// threads stay the same as over TCP. The host's built-in relay also listens on a Steam relay
    /// socket; friends connect with "steam:&lt;host's Steam ID&gt;" as the server address.
    /// Steam callbacks are pumped on the main thread (Pump, every frame).
    /// </summary>
    public static class SteamTransport
    {
        private static HostSocket _host;
        private static readonly List<ClientConnection> _clients = new List<ClientConnection>();
        private static bool _relayInit;

        /// <summary>Steam is running and initialised by the game.</summary>
        public static bool Available
        {
            get { try { return SteamClient.IsValid; } catch { return false; } }
        }

        /// <summary>This player's Steam ID as friends type it ("steam:7656...").</summary>
        public static string MyAddress
        {
            get { try { return Available ? "steam:" + SteamClient.SteamId.Value : null; } catch { return null; } }
        }

        public static bool IsSteamAddress(string host, out ulong id)
        {
            id = 0;
            string h = (host ?? "").Trim();
            if (h.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) h = h.Substring(6).Trim();
            else if (h.Length != 17) return false;
            return ulong.TryParse(h, out id) && id > 76561190000000000UL;
        }

        private static void InitRelay()
        {
            if (_relayInit) return;
            _relayInit = true;
            try
            {
                SteamNetworkingUtils.DebugLevel = NetDebugOutput.Warning;
                SteamNetworkingUtils.OnDebugOutput += (lvl, msg) => Log.Info("steam net: " + lvl + " " + msg);
                SteamNetworkingUtils.InitRelayNetworkAccess();
                if (SteamNetworkingUtils.SendBufferSize < 4 * 1024 * 1024) SteamNetworkingUtils.SendBufferSize = 4 * 1024 * 1024;
            }
            catch (Exception e) { Log.Warn("steam: relay network access: " + e.Message); }
        }

        /// <summary>Main thread, every frame: Steam's connection events and received messages.</summary>
        private static EmbeddedRelay _wantHost;
        private static float _nextHostTry;

        public static void Pump()
        {
            // hosting began before Steam was ready: open the Steam socket once it is
            if (_wantHost != null && _host == null && _wantHost.Running && UnityEngine.Time.unscaledTime >= _nextHostTry)
            {
                _nextHostTry = UnityEngine.Time.unscaledTime + 5f;
                if (Available) StartHost(_wantHost);
            }
            try { _host?.Receive(256); } catch (Exception e) { Log.Warn("steam: host receive: " + e.Message); }
            lock (_clients)
                for (int i = _clients.Count - 1; i >= 0; i--)
                {
                    try { _clients[i].Receive(256); } catch { }
                    if (_clients[i].Stream.Closed) _clients.RemoveAt(i);
                }
        }

        // ================================================================ host

        /// <summary>Host: accept Steam connections into the built-in relay.</summary>
        public static bool StartHost(EmbeddedRelay relay)
        {
            _wantHost = relay;
            if (!Available) { Log.Info("steam: not ready yet; the Steam relay socket opens when it is"); return false; }
            if (_host != null) return true;
            InitRelay();
            try
            {
                _host = SteamNetworkingSockets.CreateRelaySocket<HostSocket>(0);
                _host.Relay = relay;
                Log.Info("steam: friends can connect to " + MyAddress + " (Steam relay, no port forwarding)");
                return true;
            }
            catch (Exception e) { Log.Warn("steam: couldn't open a relay socket: " + e.Message); _host = null; return false; }
        }

        public static void StopHost()
        {
            _wantHost = null;
            if (_host == null) return;
            try
            {
                foreach (SteamStream s in _host.Streams.Values) s.MarkClosed();
                _host.Close();
            }
            catch { }
            _host = null;
        }

        private sealed class HostSocket : SocketManager
        {
            public EmbeddedRelay Relay;
            public readonly Dictionary<uint, SteamStream> Streams = new Dictionary<uint, SteamStream>();

            public override void OnConnecting(Connection connection, ConnectionInfo info)
            {
                Result r = connection.Accept();
                if (r != Result.OK) Log.Warn("steam: couldn't accept " + info.Identity.SteamId + ": " + r);
            }

            public override void OnConnected(Connection connection, ConnectionInfo info)
            {
                base.OnConnected(connection, info);
                var s = new SteamStream(connection);
                Streams[connection.Id] = s;
                Relay?.AddStream(s, "steam:" + info.Identity.SteamId.Value);
            }

            public override void OnDisconnected(Connection connection, ConnectionInfo info)
            {
                base.OnDisconnected(connection, info);
                if (Streams.TryGetValue(connection.Id, out SteamStream s)) { s.MarkClosed(); Streams.Remove(connection.Id); }
                try { connection.Close(); } catch { }
            }

            public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                if (Streams.TryGetValue(connection.Id, out SteamStream s)) s.Push(data, size);
            }
        }

        // ================================================================ client

        /// <summary>Guest: connect to a host's relay over Steam (blocking; call from a background thread).</summary>
        public static Stream Connect(ulong steamId, int timeoutMs)
        {
            if (!Available) throw new Exception("Steam isn't running");
            InitRelay();
            ClientConnection c = null;
            var opened = new ManualResetEvent(false);
            Exception fail = null;
            // Facepunch must be called on the main thread: hand the connect over and wait
            MainThread.Run(() =>
            {
                try
                {
                    c = SteamNetworkingSockets.ConnectRelay<ClientConnection>(steamId, 0);
                    lock (_clients) _clients.Add(c);
                }
                catch (Exception e) { fail = e; }
                opened.Set();
            });
            if (!opened.WaitOne(timeoutMs)) throw new Exception("the game didn't start the Steam connection");
            if (fail != null) throw new Exception("couldn't connect over Steam: " + fail.Message);
            if (!c.Ready.WaitOne(timeoutMs) || !c.Connected)
            {
                MainThread.Run(() => { try { c.Close(); } catch { } });
                throw new Exception("no answer from steam:" + steamId + " (the host isn't hosting, or Steam can't reach them)");
            }
            return c.Stream;
        }

        private sealed class ClientConnection : ConnectionManager
        {
            public readonly ManualResetEvent Ready = new ManualResetEvent(false);
            private SteamStream _stream;
            public SteamStream Stream => _stream ?? (_stream = new SteamStream(Connection));

            public override void OnConnected(ConnectionInfo info)
            {
                base.OnConnected(info);
                _stream = new SteamStream(Connection);
                Ready.Set();
            }

            public override void OnDisconnected(ConnectionInfo info)
            {
                base.OnDisconnected(info);
                Stream.MarkClosed();
                Ready.Set();
            }

            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) => Stream.Push(data, size);
        }
    }

    /// <summary>A reliable Steam connection as a stream of bytes (writes split into messages; reads in arrival order).</summary>
    public sealed class SteamStream : Stream
    {
        private const int MaxMessage = 256 * 1024;
        private readonly Connection _conn;
        private readonly BlockingCollection<byte[]> _in = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        private byte[] _cur;
        private int _pos;
        private volatile bool _closed;

        public SteamStream(Connection c) { _conn = c; }

        public bool Closed => _closed;

        internal void Push(IntPtr data, int size)
        {
            if (_closed || size <= 0) return;
            var b = new byte[size];
            Marshal.Copy(data, b, 0, size);
            try { _in.Add(b); } catch { }
        }

        internal void MarkClosed()
        {
            if (_closed) return;
            _closed = true;
            try { _in.CompleteAdding(); } catch { }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_cur == null || _pos >= _cur.Length)
            {
                try { _cur = _in.Take(); }
                catch (InvalidOperationException) { return 0; }   // closed
                _pos = 0;
            }
            int n = Math.Min(count, _cur.Length - _pos);
            Buffer.BlockCopy(_cur, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                if (_closed) throw new IOException("the Steam connection closed");
                int n = Math.Min(count, MaxMessage);
                Result r = _conn.SendMessage(buffer, offset, n, SendType.Reliable);
                if (r == Result.LimitExceeded) { Thread.Sleep(5); continue; }   // send buffer full: wait for it to drain
                if (r != Result.OK) { MarkClosed(); throw new IOException("Steam send failed: " + r); }
                offset += n;
                count -= n;
            }
        }

        public override void Close()
        {
            if (!_closed) { MarkClosed(); MainThread.Run(() => { try { _conn.Close(); } catch { } }); }
            base.Close();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Work handed to the Unity main thread (run in WorldBoxMod.Update via Drain).</summary>
    public static class MainThread
    {
        private static readonly ConcurrentQueue<Action> _q = new ConcurrentQueue<Action>();
        public static void Run(Action a) => _q.Enqueue(a);
        public static void Drain()
        {
            while (_q.TryDequeue(out Action a))
                try { a(); } catch (Exception e) { Log.Warn("main-thread task: " + e.Message); }
        }
    }
}
