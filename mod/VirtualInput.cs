using System;
using System.Collections.Generic;
using PeakShim;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace PeakIOS
{
    /// <summary>
    /// PEAK reads gameplay and menu input through Input System actions (keyboard / mouse / gamepad)
    /// and keybinds through the legacy Input API (patched to PeakShim.VInput). This class owns virtual Keyboard /
    /// Mouse / Gamepad devices and, once per frame right before the Input System processes events, pushes the
    /// state the touch layer asked for into both. Real MFi / Xbox / PlayStation controllers are separate Input
    /// System gamepads and keep working on their own.
    ///
    /// The real touchscreen device is disabled for the Input System, so UI only ever sees the virtual mouse
    /// (the touch layer reads fingers through the legacy Input.touches, which is not affected).
    /// </summary>
    public static class VirtualInput
    {
        public static Keyboard Kb { get; private set; }
        public static Mouse Mouse { get; private set; }
        public static Gamepad Pad { get; private set; }

        /// Called once per frame before the game sees input (fills the request state below).
        public static event Action Tick;

        // ---- requests for the coming frame (set in Tick) ---------------------------------------------
        static readonly HashSet<Key> s_Keys = new HashSet<Key>();
        public static Vector2 Stick;               // analog movement, -1..1 (virtual gamepad left stick)
        public static Vector2 PointerPos = new Vector2(-1000, -1000);   // screen pixels, bottom-left origin
        public static Vector2 PointerDelta;        // pointer pixels this frame (look)
        public static float Scroll;                // wheel "notches" this frame (+ = up)
        public static bool LeftButton, RightButton, MiddleButton;
        public static string Text = "";            // typed text for the legacy inputString this frame

        public static void HoldKey(Key k) => s_Keys.Add(k);
        /// Hold a legacy-API key (a GlobalInputHandler keybind) this frame.
        public static void HoldLegacyKey(KeyCode k) { if (k != KeyCode.None) s_ExtraLegacy.Add(k); }
        /// Press an Input System key for two frames (one-shot buttons).
        public static void TapKey(Key k) => s_Taps[k] = 2;

        static readonly HashSet<KeyCode> s_ExtraLegacy = new HashSet<KeyCode>();
        static readonly Dictionary<Key, int> s_Taps = new Dictionary<Key, int>();

        static int s_LastFrame = -1;
        static int s_Errors;

        public static void Init()
        {
            try
            {
                Kb = InputSystem.AddDevice<Keyboard>("iOSTouchKeyboard");
                Mouse = InputSystem.AddDevice<Mouse>("iOSTouchMouse");
                Pad = InputSystem.AddDevice<Gamepad>("iOSTouchGamepad");
                foreach (var d in InputSystem.devices) DisableTouchscreen(d);
                InputSystem.onDeviceChange += (d, change) =>
                {
                    if (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected) DisableTouchscreen(d);
                    if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed)
                        Log.Info($"input device {change}: {d.name} ({d.layout})");
                };
                InputSystem.onBeforeUpdate += OnBeforeUpdate;
                Log.Info($"virtual input devices added; update mode={InputSystem.settings.updateMode}");
            }
            catch (Exception e)
            {
                Log.Error("virtual devices: " + e);
            }
        }

        static void DisableTouchscreen(InputDevice d)
        {
            if (!(d is Touchscreen) || !d.enabled) return;
            InputSystem.DisableDevice(d);
            Log.Info("Input System touchscreen disabled (touch is turned into the virtual mouse)");
        }

        static void OnBeforeUpdate()
        {
            var t = InputState.currentUpdateType;
            if (t != InputUpdateType.Dynamic && t != InputUpdateType.Fixed) return;
            if (Time.frameCount == s_LastFrame) return;
            s_LastFrame = Time.frameCount;
            try
            {
                s_Keys.Clear();
                s_ExtraLegacy.Clear();
                Stick = Vector2.zero;
                PointerDelta = Vector2.zero;
                Scroll = 0;
                LeftButton = RightButton = MiddleButton = false;
                Text = "";
                Tick?.Invoke();
                Push();
            }
            catch (Exception e)
            {
                if (s_Errors++ < 20) Log.Error("virtual input tick: " + e);
            }
            finally
            {
                VInput.BeginFrame();
            }
        }

        static readonly List<KeyCode> s_LegacyKeys = new List<KeyCode>();

        static void Push()
        {
            if (Kb == null) return;
            if (s_Taps.Count > 0)
                foreach (var k in new List<Key>(s_Taps.Keys))
                {
                    s_Keys.Add(k);
                    if (--s_Taps[k] <= 0) s_Taps.Remove(k);
                }
            var ms = new MouseState { position = PointerPos, delta = PointerDelta, scroll = new Vector2(0, Scroll * 120f) };
            if (LeftButton) ms = ms.WithButton(MouseButton.Left, true);
            if (RightButton) ms = ms.WithButton(MouseButton.Right, true);
            if (MiddleButton) ms = ms.WithButton(MouseButton.Middle, true);
            var keys = new Key[s_Keys.Count];
            s_Keys.CopyTo(keys);
            InputSystem.QueueStateEvent(Kb, new KeyboardState(keys));
            InputSystem.QueueStateEvent(Mouse, ms);
            InputSystem.QueueStateEvent(Pad, new GamepadState { leftStick = Stick });

            // legacy API (keybinds, typing): same state
            VInput.VMousePosition = PointerPos;
            VInput.VMouseDelta += PointerDelta;
            VInput.VScroll += new Vector2(0, Scroll);
            VInput.SetMouseButton(0, LeftButton);
            VInput.SetMouseButton(1, RightButton);
            VInput.SetMouseButton(2, MiddleButton);
            VInput.VInputString += Text;
            foreach (var kc in s_LegacyKeys) VInput.SetKey(kc, false);
            s_LegacyKeys.Clear();
            foreach (var k in keys)
            {
                var kc = ToKeyCode(k);
                if (kc == KeyCode.None) continue;
                VInput.SetKey(kc, true);
                s_LegacyKeys.Add(kc);
            }
            foreach (var kc in s_ExtraLegacy)
            {
                VInput.SetKey(kc, true);
                s_LegacyKeys.Add(kc);
            }
        }

        public static KeyCode ToKeyCode(Key k)
        {
            switch (k)
            {
                case Key.Enter: return KeyCode.Return;
                case Key.Escape: return KeyCode.Escape;
                case Key.Backspace: return KeyCode.Backspace;
                case Key.Space: return KeyCode.Space;
                case Key.Tab: return KeyCode.Tab;
                case Key.LeftShift: return KeyCode.LeftShift;
                case Key.LeftCtrl: return KeyCode.LeftControl;
                case Key.UpArrow: return KeyCode.UpArrow;
                case Key.DownArrow: return KeyCode.DownArrow;
                case Key.LeftArrow: return KeyCode.LeftArrow;
                case Key.RightArrow: return KeyCode.RightArrow;
            }
            if (k >= Key.A && k <= Key.Z) return KeyCode.A + (k - Key.A);
            if (k >= Key.Digit1 && k <= Key.Digit9) return KeyCode.Alpha1 + (k - Key.Digit1);
            if (k == Key.Digit0) return KeyCode.Alpha0;
            if (k >= Key.F1 && k <= Key.F12) return KeyCode.F1 + (k - Key.F1);
            return KeyCode.None;
        }
    }
}
