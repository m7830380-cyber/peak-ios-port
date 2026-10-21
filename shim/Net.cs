using System;
using System.Net;
using UnityEngine;

namespace PeakShim
{
    /// <summary>
    /// Network guard. The patcher puts <see cref="AllowServer"/> in front of every Photon connection
    /// (PhotonPeer.Connect), so the iOS app can only ever reach this device or the local network (LAN games):
    /// never Photon Cloud with the game's AppId, never any other internet server.
    /// </summary>
    public static class Net
    {
        public static int Blocked;

        /// Set by the iOS mod while a local (Wi-Fi) game is being hosted or joined: Photon may then go online
        /// (to the in-app server). Otherwise NetworkingUtilities.ConnectToNetwork switches to Photon's offline mode.
        public static bool LanSession;

        public static bool UseOffline()
        {
            if (LanSession) return false;
            Debug.Log("[PEAK-iOS] Photon connect -> offline mode (no Wi-Fi session)");
            return true;
        }

        public static bool AllowServer(string serverAddress)
        {
            string host = HostOf(serverAddress);
            bool ok = IsLocal(host);
            if (!ok)
            {
                Blocked++;
                Debug.LogWarning("[PEAK-iOS] blocked a Photon connection to '" + serverAddress + "' (only local network addresses are allowed)");
            }
            return ok;
        }

        /// "wss://host:port/path", "host:port", "[::1]:5055" -> host
        public static string HostOf(string address)
        {
            string s = (address ?? "").Trim();
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            if (s.StartsWith("["))
            {
                int close = s.IndexOf(']');
                return close > 0 ? s.Substring(1, close - 1) : s;
            }
            int colon = s.LastIndexOf(':');
            if (colon >= 0 && s.IndexOf(':') == colon) s = s.Substring(0, colon);
            return s;
        }

        public static bool IsLocal(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (host == "localhost") return true;
            if (!IPAddress.TryParse(host, out var ip)) return false;   // names would need DNS: never local
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fe80::/10, fec0::/10, fc00::/7
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
    }
}
