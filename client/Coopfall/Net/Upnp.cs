using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Coopfall.Net
{
    /// <summary>
    /// Asks the home router (UPnP IGD) to forward a TCP port to this PC, so friends can reach a
    /// relay hosted here over the internet. Runs on a background thread; Status says how it went.
    /// It can't help when the router has UPnP off or the internet provider shares one public
    /// address between customers (carrier-grade NAT): then the port has to be forwarded by hand.
    /// </summary>
    public sealed class Upnp
    {
        private readonly int _port;
        private string _control, _service, _localIp;
        private volatile bool _mapped;

        /// <summary>What happened, for the menu ("finding your router...", "open: 1.2.3.4:25598", ...).</summary>
        public volatile string Status = "not tried";
        public volatile string ExternalIp;
        public bool Open => _mapped;

        public Upnp(int port) { _port = port; }

        public void OpenAsync() => new Thread(() =>
        {
            try { Run(); }
            catch (Exception e) { Status = "failed: " + e.Message; Log.Warn("upnp: " + e.Message); }
        }) { IsBackground = true, Name = "Coopfall UPnP" }.Start();

        private void Run()
        {
            Status = "finding your router...";
            string location = Discover();
            if (location == null) { Status = "no UPnP router answered (turn UPnP on in the router, or forward port " + _port + " TCP by hand)"; Log.Warn("upnp: no router answered"); return; }
            string desc;
            using (var wc = new WebClient()) desc = wc.DownloadString(location);
            Match m = Regex.Match(desc, "<serviceType>(urn:schemas-upnp-org:service:WAN(?:IP|PPP)Connection:\\d)</serviceType>.*?<controlURL>([^<]+)</controlURL>", RegexOptions.Singleline);
            if (!m.Success) { Status = "the router doesn't offer port forwarding over UPnP"; return; }
            _service = m.Groups[1].Value;
            var baseUri = new Uri(location);
            Match bm = Regex.Match(desc, "<URLBase>([^<]+)</URLBase>");
            if (bm.Success) baseUri = new Uri(bm.Groups[1].Value);
            _control = new Uri(baseUri, m.Groups[2].Value).ToString();
            _localIp = LocalIpToward(baseUri.Host);
            Status = "asking the router to open port " + _port + "...";
            Soap("AddPortMapping",
                "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + _port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
                "<NewInternalPort>" + _port + "</NewInternalPort><NewInternalClient>" + _localIp + "</NewInternalClient><NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>Coopfall relay</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>");
            _mapped = true;
            try
            {
                string r = Soap("GetExternalIPAddress", "");
                Match ip = Regex.Match(r, "<NewExternalIPAddress>([^<]*)</NewExternalIPAddress>");
                ExternalIp = ip.Success ? ip.Groups[1].Value : null;
            }
            catch { }
            bool cgnat = ExternalIp != null && IsPrivate(ExternalIp);
            Status = cgnat
                ? "port opened on the router, but its address " + ExternalIp + " is not public (your provider shares it): friends outside can't reach you"
                : "open: friends connect to " + (ExternalIp ?? "your public IP") + ":" + _port;
            Log.Info("upnp: " + Status + " (this PC " + _localIp + ")");
        }

        /// <summary>Removes the forwarding again (when the relay stops).</summary>
        public void Close()
        {
            if (!_mapped) return;
            _mapped = false;
            try { Soap("DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + _port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>"); Log.Info("upnp: port " + _port + " closed again"); }
            catch (Exception e) { Log.Warn("upnp: couldn't close port " + _port + ": " + e.Message); }
        }

        /// <summary>Asks the router on the internet connection only (the standard UPnP search; it stays on the local network).</summary>
        private static string Discover()
        {
            const string req = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
            byte[] b = Encoding.ASCII.GetBytes(req);
            var to = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            var sockets = new System.Collections.Generic.List<UdpClient>();
            try
            {
                // only the connection this PC uses for the internet (where its default route goes)
                IPAddress local = ActiveLocalAddress();
                if (local == null) return null;
                var sock = new UdpClient(new IPEndPoint(local, 0)) { MulticastLoopback = false };
                for (int i = 0; i < 2; i++) sock.Send(b, b.Length, to);
                sockets.Add(sock);
                DateTime until = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < until)
                {
                    foreach (UdpClient u in sockets)
                    {
                        if (u.Available <= 0) continue;
                        IPEndPoint from = null;
                        byte[] r;
                        try { r = u.Receive(ref from); } catch { continue; }
                        Match m = Regex.Match(Encoding.ASCII.GetString(r), "^LOCATION:\\s*(\\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        if (m.Success) return m.Groups[1].Value.Trim();
                    }
                    Thread.Sleep(50);
                }
            }
            finally { foreach (UdpClient u in sockets) { try { u.Close(); } catch { } } }
            return null;
        }

        private string Soap(string action, string args)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:" + action + " xmlns:u=\"" + _service + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
            using (var wc = new WebClient())
            {
                wc.Headers.Add("Content-Type", "text/xml; charset=\"utf-8\"");
                wc.Headers.Add("SOAPACTION", "\"" + _service + "#" + action + "\"");
                try { return wc.UploadString(_control, body); }
                catch (WebException e)
                {
                    string detail = "";
                    try { using (var s = new System.IO.StreamReader(e.Response.GetResponseStream())) detail = Regex.Match(s.ReadToEnd(), "<errorDescription>([^<]*)").Groups[1].Value; } catch { }
                    throw new Exception("the router refused " + action + (detail.Length > 0 ? " (" + detail + ")" : ""));
                }
            }
        }

        /// <summary>This PC's address on the connection Windows uses for the internet. Connecting a UDP
        /// socket only picks the route; nothing is sent.</summary>
        private static IPAddress ActiveLocalAddress()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 53);
                    return ((IPEndPoint)s.LocalEndPoint).Address;
                }
            }
            catch { return null; }
        }

        private static string LocalIpToward(string host)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect(host, 1900);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }

        private static bool IsPrivate(string ip)
        {
            if (!IPAddress.TryParse(ip, out IPAddress a)) return false;
            byte[] b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] < 128) || b[0] == 0;
        }
    }
}
