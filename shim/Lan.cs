using System;
using UnityEngine;

namespace PeakShim
{
    /// <summary>
    /// Hooks for local (same Wi-Fi) multiplayer. The patcher calls these from MainMenuHandler; the iOS mod
    /// (PeakIOS.Lan.LanSession) fills in the delegates.
    ///  * Host(saveIndex) starts with <see cref="BeforeHost"/> (start the in-app server, go online against it).
    ///  * JoinRoom(code) starts with "if (BeforeJoin(code)) return;" (point Photon at the host the code names).
    ///  * OnLobbyHosted names the room with <see cref="RoomName"/> (a code that carries the host's address).
    /// </summary>
    public static class Lan
    {
        public static Action OnHost;
        /// true = handled here (the game must not go on with the join)
        public static Func<string, bool> OnJoin;
        public static Func<string, string> OnRoomName;

        public static void BeforeHost()
        {
            try { OnHost?.Invoke(); }
            catch (Exception e) { Debug.LogError("[PEAK-iOS] LAN host: " + e); }
        }

        public static bool BeforeJoin(string room)
        {
            try { return OnJoin != null && OnJoin(room); }
            catch (Exception e) { Debug.LogError("[PEAK-iOS] LAN join: " + e); return true; }
        }

        public static string RoomName(string generated)
        {
            try { return OnRoomName?.Invoke(generated) ?? generated; }
            catch (Exception e) { Debug.LogError("[PEAK-iOS] LAN room name: " + e); return generated; }
        }
    }
}
