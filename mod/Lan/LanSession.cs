using System;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Peak;
using Peak.Network;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zorro.Core;
using Zorro.UI.Modal;

namespace PeakIOS.Lan
{
    /// <summary>
    /// Local (same Wi-Fi) multiplayer through the game's own PLAY and JOIN (room code) menus.
    ///
    /// PLAY starts a small Photon server inside the app (LanServer) and points Photon at it instead of creating a
    /// Steam lobby; once connected the airport loads as on PC and the game creates its room there. The room name -
    /// the code in the pause menu and on the banner - carries the host's Wi-Fi address. JOIN decodes that address
    /// from the typed code, points Photon at the host and loads the airport, which joins the room (the game's own
    /// invite path). No Photon Cloud, Steam or any other server is involved (PeakShim.Net still refuses anything
    /// that is not a local network address).
    ///
    /// Code: 6 characters like the PC game's. [0] and [1] any letter, [2..5] base-32 of (last two bytes of the
    /// host's IPv4 address &lt;&lt; 4 | check). The first two bytes are the joining phone's own, so host and guests must
    /// share a /16 network (every home Wi-Fi and iPhone hotspot does).
    /// </summary>
    static class LanSession
    {
        const string GameApp = "peak-ios-lan-game";    // stand-ins for the Photon app IDs: rooms are kept per app,
        const string VoiceApp = "peak-ios-lan-voice";  // so game and voice must differ (never the game's real IDs)
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I
        const float ConnectTimeout = 12f;

        static LanServer s_Server;
        static LanBanner s_Runner;
        static bool s_Active, s_Hosting, s_Connecting, s_LeftMenu, s_PendingOffline;
        static string s_HostAddress = "", s_JoinAddress = "", s_Code = "";

        public static bool Hosting => s_Active && s_Hosting;
        public static string Code => s_Code;
        public static string HostAddress => s_HostAddress;

        public static void Init(GameObject host)
        {
            PeakShim.Lan.OnHost = BeforeHost;
            PeakShim.Lan.OnJoin = BeforeJoin;
            PeakShim.Lan.OnRoomName = RoomName;
            s_Runner = host.AddComponent<LanBanner>();
        }

        static bool IsMenu(string scene) => scene == "Title" || scene == "MainMenu" || scene == "Pretitle";

        // ---- host (PLAY) -----------------------------------------------------------------------------
        static bool BeforeHost()
        {
            if (NetworkingUtilities.EnteringRoom || s_Connecting) return true;
            End(null);
            s_HostAddress = LocalAddress();
            try
            {
                if (s_Server == null) s_Server = new LanServer();
                if (!s_Server.Running) s_Server.Start(LanServer.DefaultPort);
            }
            catch (Exception e)
            {
                Log.Error($"LAN server could not start ({e.Message})");
                ShowError("Can't host", "The game server on this iPhone did not start (" + e.Message + "). Try again, or restart the app. Play Solo still works.");
                return true;
            }
            s_Server.GameAppId = GameApp;
            s_Hosting = true;
            s_Code = MakeCode(s_HostAddress) ?? "";
            Begin("127.0.0.1");
            TouchLocalNetwork(s_HostAddress);
            Log.Info($"LAN hosting on {s_HostAddress}:{LanServer.DefaultPort}, code {(s_Code == "" ? "(none: no Wi-Fi)" : s_Code)}");
            s_Runner.StartCoroutine(Connect(() =>
            {
                GameHandler.GetService<ConnectionService>().StateMachine.SwitchState<HostState>().RoomName = s_Code;
                if (Quicksave.ShouldUseSaveData)
                {
                    Quicksave.LoadSavedGameScene();
                    return;
                }
                var loading = RetrievableResourceSingleton<LoadingScreenHandler>.Instance;
                loading.Load(LoadingScreen.LoadingScreenType.Basic, null, loading.LoadSceneProcess("Airport", networked: false, yieldForCharacterSpawn: true));
            }));
            return true;
        }

        static string RoomName(string generated)
        {
            if (!Hosting) return generated;
            if (s_Code == "") s_Code = generated;
            Log.Info($"LAN room code {s_Code}" + (s_Code == generated ? " (no Wi-Fi address: only this phone can play)" : ""));
            return s_Code;
        }

        // ---- join (JOIN + code) ----------------------------------------------------------------------
        static bool BeforeJoin(string room)
        {
            if (s_Connecting) return true;
            string code = (room ?? "").Trim().ToUpperInvariant();
            string own = LocalAddress();
            string address = DecodeCode(code, own);
            if (address == null)
            {
                Log.Warn($"LAN join: '{code}' is not a Wi-Fi game code (own address {own})");
                ShowError("Can't join " + code,
                    own == "?" ? "This iPhone is not on Wi-Fi. Join the same Wi-Fi as the host, then try again."
                               : "That code doesn't look right. Type the 6 characters the host sees at the top of the screen (or in the pause menu).");
                return true;
            }
            End(null);
            s_Hosting = false;
            s_Code = code;
            Begin(address);
            TouchLocalNetwork(address);
            Log.Info($"LAN joining {code} at {address}:{LanServer.DefaultPort}");
            s_Runner.StartCoroutine(Connect(() =>
            {
                var st = GameHandler.GetService<ConnectionService>().StateMachine.SwitchState<JoinSpecificRoomState>();
                st.RoomName = code;
                st.RegionToJoin = "";   // no region swap: the game then simply joins the room by name
                if (Modal.IsOpen) Modal.CloseModal();
                var loading = RetrievableResourceSingleton<LoadingScreenHandler>.Instance;
                loading.Load(LoadingScreen.LoadingScreenType.Basic, null, loading.LoadSceneProcess("Airport", networked: false, yieldForCharacterSpawn: true));
            }));
            return true;
        }

        // ---- shared ----------------------------------------------------------------------------------
        static void Begin(string address)
        {
            s_JoinAddress = address;
            s_LeftMenu = s_PendingOffline = false;
            s_Active = true;
            var s = PhotonNetwork.PhotonServerSettings.AppSettings;
            s.UseNameServer = false;
            s.Server = address;
            s.Port = LanServer.DefaultPort;
            s.Protocol = ConnectionProtocol.Tcp;
            s.EnableProtocolFallback = false;
            s.AuthMode = AuthModeOption.Auth;
            s.FixedRegion = null;
            s.AppIdRealtime = GameApp;
            s.AppIdVoice = VoiceApp;
            s.AppIdChat = s.AppIdFusion = string.Empty;
        }

        /// Goes online against the server named in Begin, then runs <paramref name="ready"/> (load the airport).
        static IEnumerator Connect(Action ready)
        {
            s_Connecting = true;
            NetworkingUtilities.EnteringRoom = true;
            float started = Time.unscaledTime;
            bool sawConnecting = false;
            string error = null;
            PeakShim.Net.LanSession = true;   // the game's ConnectToNetwork goes online only now
            try
            {
                PhotonNetwork.OfflineMode = false;   // offline, PhotonNetwork.IsConnected is always true
                if (PhotonNetwork.NetworkingClient.IsConnected) PhotonNetwork.Disconnect();
            }
            catch (Exception e) { Log.Warn("LAN: leaving offline mode: " + e.Message); }
            while (PhotonNetwork.NetworkingClient.LoadBalancingPeer.PeerState != PeerStateValue.Disconnected && Time.unscaledTime - started < 3f)
                yield return null;
            try
            {
                if (!NetworkingUtilities.ConnectToNetwork()) error = "Photon would not start connecting";
            }
            catch (Exception e) { error = e.Message; }
            PeakShim.Net.LanSession = false;   // anything later (back in the menu) is offline again
            while (error == null)
            {
                var st = PhotonNetwork.NetworkClientState;
                if (st == ClientState.ConnectedToMasterServer) break;
                if (st != ClientState.Disconnected && st != ClientState.PeerCreated) sawConnecting = true;
                float t = Time.unscaledTime - started;
                if ((sawConnecting && st == ClientState.Disconnected && t > 1f) || t > ConnectTimeout)
                    error = $"state {st} after {t:0.0}s";
                else yield return null;
            }
            s_Connecting = false;
            if (error == null)
            {
                Log.Info($"LAN connected to {(s_Hosting ? "this iPhone's game server" : s_JoinAddress)} in {Time.unscaledTime - started:0.0}s");
                try { ready(); }
                catch (Exception e)
                {
                    Log.Error("LAN: loading the airport failed: " + e);
                    NetworkingUtilities.EnteringRoom = false;
                    End("airport load failed");
                }
                yield break;
            }
            bool hosting = s_Hosting;
            Log.Warn($"LAN: could not reach {(hosting ? "this iPhone's game server" : "the host " + s_JoinAddress)} ({error})");
            NetworkingUtilities.EnteringRoom = false;
            End("could not connect");
            ShowError(hosting ? "Can't host" : "Can't join " + s_Code,
                hosting ? "The game server on this iPhone did not answer. Try again, or restart the app. Play Solo still works."
                        : $"Couldn't reach the host at {s_JoinAddress}.\n\nBoth phones must be on the same Wi-Fi, the host must already be in the airport, " +
                          "and PEAK needs \"Local Network\" allowed in iPhone Settings > Privacy & Security > Local Network (on both phones).",
                backToTitle: !hosting);
        }

        /// Back to offline (solo) play. why == null: quietly, before a new host / join.
        public static void End(string why)
        {
            if (!s_Active && (s_Server == null || !s_Server.Running)) return;
            if (why != null) Log.Info("LAN session over: " + why);
            s_Server?.Stop();
            s_Active = s_Hosting = false;
            s_Code = "";
            PeakShim.Net.LanSession = false;
            try { if (!PhotonNetwork.OfflineMode && PhotonNetwork.IsConnected) PhotonNetwork.Disconnect(); }
            catch (Exception e) { Log.Warn("LAN disconnect: " + e.Message); }
            s_PendingOffline = true;
        }

        /// Called every frame (LanBanner.Update).
        internal static void Tick()
        {
            if (s_PendingOffline && !s_Active)
            {
                if (PhotonNetwork.NetworkingClient.IsConnected || PhotonNetwork.InRoom) return;
                s_PendingOffline = false;
                try
                {
                    if (!PhotonNetwork.OfflineMode)
                    {
                        PhotonNetwork.OfflineMode = true;
                        Log.Info("Photon offline mode on (LAN session ended)");
                    }
                }
                catch (Exception e) { Log.Warn("offline mode: " + e.Message); }
                return;
            }
            if (!s_Active || s_Connecting) return;
            string scene = SceneManager.GetActiveScene().name;
            if (!IsMenu(scene)) { s_LeftMenu = true; return; }
            // back in the menu (left the game, kicked, host gone, join failed): this session is over
            if (s_LeftMenu && !PhotonNetwork.InRoom) End("back in the menu");
        }

        static void ShowError(string title, string body, bool backToTitle = false)
        {
            try
            {
                Modal.OpenModal(new DefaultHeaderModalOption(title, body), new ModalButtonsOption(new[]
                {
                    new ModalButtonsOption.Option("OK", () => { if (backToTitle) SceneManager.LoadScene("Title"); })
                }));
            }
            catch (Exception e) { Log.Warn("modal: " + e.Message); }
        }

        // ---- room code -------------------------------------------------------------------------------
        static string MakeCode(string address)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
            var b = ip.GetAddressBytes();
            int v = (b[2] << 8) | b[3];
            int payload = (v << 4) | Check(v);
            var r = new System.Random();
            var chars = new char[6];
            chars[0] = Alphabet[r.Next(24)];   // letters
            chars[1] = Alphabet[r.Next(24)];
            for (int i = 5; i >= 2; i--) { chars[i] = Alphabet[payload & 31]; payload >>= 5; }
            return new string(chars);
        }

        static string DecodeCode(string code, string ownAddress)
        {
            if (code.Length != 6) return null;
            if (!IPAddress.TryParse(ownAddress, out var own) || own.AddressFamily != AddressFamily.InterNetwork) return null;
            int payload = 0;
            for (int i = 2; i < 6; i++)
            {
                int d = Alphabet.IndexOf(code[i]);
                if (d < 0) return null;
                payload = (payload << 5) | d;
            }
            int v = payload >> 4;
            if ((payload & 15) != Check(v)) return null;
            var b = own.GetAddressBytes();
            return $"{b[0]}.{b[1]}.{v >> 8}.{v & 255}";
        }

        static int Check(int v) => (int)(((uint)v * 2654435761u) >> 28);

        // ---- network helpers -------------------------------------------------------------------------
        /// This phone's address on the Wi-Fi (en0), or "?".
        public static string LocalAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != "en0" || ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
                }
            }
            catch (Exception e) { Log.Warn("LAN interfaces: " + e.Message); }
            try
            {
                // iPhone hotspot host: bridge100 etc.; any private IPv4 address that is up
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address) &&
                            PeakShim.Net.IsLocal(ua.Address.ToString()) && !ua.Address.ToString().StartsWith("169.254."))
                            return ua.Address.ToString();
                }
            }
            catch { }
            return "?";
        }

        /// One small UDP packet to the local network (discard port), so iOS asks for "Local Network" access
        /// now rather than silently blocking the first game connection.
        static void TouchLocalNetwork(string address)
        {
            try
            {
                if (!IPAddress.TryParse(address, out var ip) || IPAddress.IsLoopback(ip) || ip.AddressFamily != AddressFamily.InterNetwork) return;
                var b = ip.GetAddressBytes();
                var target = new IPAddress(new byte[] { b[0], b[1], b[2], (byte)(b[3] == 1 ? 2 : 1) });
                using (var u = new UdpClient(AddressFamily.InterNetwork)) u.Send(new byte[] { 0 }, 1, new IPEndPoint(target, 9));
            }
            catch (Exception e) { Log.Info("LAN local network check: " + e.Message); }
        }
    }

    /// "Wi-Fi game" line for the host in the airport (the code is also in the pause menu), and the per-frame
    /// LAN session check.
    sealed class LanBanner : MonoBehaviour
    {
        GUIStyle m_Style;

        void Update() => LanSession.Tick();

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || SettingsPanel.Open || !LanSession.Hosting || !PhotonNetwork.InRoom) return;
            if (SceneManager.GetActiveScene().name != "Airport") return;
            float h = Screen.height;
            if (m_Style == null) m_Style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true };
            m_Style.fontSize = Mathf.RoundToInt(h * 0.03f);
            int players = PhotonNetwork.CurrentRoom?.PlayerCount ?? 1;
            string text = LanSession.HostAddress == "?"
                ? "<b>No Wi-Fi</b> - only this iPhone can play. Join a Wi-Fi network and press PLAY again to play together."
                : $"Wi-Fi game code  <b>{LanSession.Code}</b>  ·  {players} player{(players == 1 ? "" : "s")}  ·  friends on the same Wi-Fi: JOIN, type the code";
            Rect safe = Screen.safeArea;
            float w = safe.width * 0.62f;
            var r = new Rect(safe.x + (safe.width - w) / 2f, h - safe.yMax + h * 0.012f, w, h * 0.05f);
            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.5f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(r, text, m_Style);
        }
    }
}
