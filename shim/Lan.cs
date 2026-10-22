using System;
using UnityEngine;

namespace PeakShim
{
    /// <summary>
    /// Hooks for local (same Wi-Fi) multiplayer. The patcher calls these from the game's menus; the iOS mod
    /// (PeakIOS.Lan.LanSession) fills in the delegates.
    ///  * MainMenuMainPage.StartMultiplayerLobby (PLAY) starts with "if (BeforeHost()) return;" (in-app server,
    ///    Photon pointed at it, the airport loads as host) instead of creating a Steam lobby.
    ///  * MainMenuMainPage.JoinClicked reads PhotonNetwork.OfflineMode through <see cref="MenuOffline"/>, so the
    ///    join page opens although the menu is offline.
    ///  * MainMenuJoinRoomPage.JoinRoom(code) starts with "if (BeforeJoin(code)) return;" (Photon pointed at the
    ///    host the code names, the airport loads and joins).
    ///  * NetworkConnector.HandleConnectionState names the hosted room with <see cref="RoomName"/> (a code that
    ///    carries the host's address).
    /// </summary>
    public static class Lan
    {
        /// true = handled here (the game must not go on)
        public static Func<bool> OnHost;
        public static Func<string, bool> OnJoin;
        public static Func<string, string> OnRoomName;

        public static bool BeforeHost()
        {
            try { return OnHost != null && OnHost(); }
            catch (Exception e) { Debug.LogError("[PEAK-iOS] LAN host: " + e); return true; }
        }

        public static bool BeforeJoin(string room)
        {
            try { return OnJoin != null && OnJoin(room); }
            catch (Exception e) { Debug.LogError("[PEAK-iOS] LAN join: " + e); return true; }
        }

        public static bool MenuOffline(bool offline) => OnJoin == null && offline;

        public static string RoomName(string generated)
        {
            try { return OnRoomName?.Invoke(generated) ?? generated; }
            catch (Exception e) { Debug.LogError("[PEAK-iOS] LAN room name: " + e); return generated; }
        }
    }
}
