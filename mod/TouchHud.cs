using System;
using System.Collections.Generic;
using PeakShim;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using TouchPhase = UnityEngine.TouchPhase;

namespace PeakIOS
{
    /// <summary>
    /// Gameplay touch controls (shown while the game has the cursor locked, i.e. not in a menu):
    /// floating stick on the left (analog, fed to CharacterInput.GetMovementInput by the patcher), drag anywhere
    /// else to look (virtual mouse, so the game's sensitivity setting applies, times our own look speed),
    /// semi-transparent buttons for the game's actions. Buttons press the keyboard key the player's binding of
    /// that action uses, so rebinding keeps working. Item buttons show only while an item is held.
    /// The eye button hides every button (stick and look keep working).
    /// </summary>
    public static class TouchHud
    {
        enum Kind { Hold, Toggle, Tap }

        sealed class Button
        {
            public string Label;
            public Kind Kind;
            public string Action;          // Input System action whose keyboard binding is pressed, or
            public Key Fallback;           //   this key when the action has none
            public int Mouse = -1;         // mouse button (0 left, 1 right, 2 middle)
            public Action OnTap;
            public Func<bool> Shown;       // context: null = always (unless hidden)
            public float X, Y, Size;       // centre (GUI pixels, origin top-left) and diameter, set by Layout
            public bool AlwaysShown;
            public bool Held;
            public Rect Rect => new Rect(X - Size / 2, Y - Size / 2, Size, Size);
        }

        static readonly List<Button> s_Buttons = new List<Button>();
        static readonly Dictionary<int, Button> s_ButtonFingers = new Dictionary<int, Button>();
        static readonly HashSet<int> s_Ignored = new HashSet<int>();
        static readonly Dictionary<string, Key> s_KeyCache = new Dictionary<string, Key>();
        static int s_StickFinger = -1, s_LookFinger = -1;
        static Vector2 s_StickOrigin, s_StickPos, s_LastLook;
        static bool s_Sprint, s_Hidden, s_Active;
        static Button s_SprintButton;
        static Texture2D s_Circle;
        static GUIStyle s_Label;
        static int s_LayoutW, s_LayoutH, s_KeyCacheFrame = -1000;
        static Rect s_LayoutSafe;

        public static bool Hidden => s_Hidden;
        /// Look speed multiplier (settings: peakios_look_speed, 0.2 .. 3).
        public static float LookSpeed = 1f;
        /// Button size multiplier (peakios_hud_scale, 0.6 .. 1.6) and opacity (peakios_hud_alpha, 0.1 .. 1).
        public static float Scale = 1f, Opacity = 1f;

        static bool HoldingItem()
        {
            try { var c = Character.localCharacter; return c != null && c.data != null && c.data.currentItem != null; }
            catch (Exception) { return false; }
        }

        static void Init()
        {
            if (s_Buttons.Count > 0) return;
            s_Hidden = Prefs.GetInt("peakios_hud_hidden", 0) != 0;
            LookSpeed = Mathf.Clamp(Prefs.GetFloat("peakios_look_speed", 1f), 0.2f, 3f);
            Scale = Mathf.Clamp(Prefs.GetFloat("peakios_hud_scale", 1f), 0.6f, 1.6f);
            Opacity = Mathf.Clamp(Prefs.GetFloat("peakios_hud_alpha", 1f), 0.1f, 1f);
            Add("Jump", "Jump", Key.Space);
            Add("Grab", mouse: 0);                       // use item / hold to climb
            Add("Use 2", mouse: 1).Shown = HoldingItem;  // secondary use (eat, aim, ...)
            Add("Interact", "Interact", Key.E);
            Add("Drop", "Drop", Key.Q).Shown = HoldingItem;
            Add("Crouch", "Crouch", Key.LeftCtrl);
            Add("Emote", "Emote", Key.R);
            Add("Ping", mouse: 2);
            Add("1", "Hotbar1", Key.Digit1, Kind.Tap);
            Add("2", "Hotbar2", Key.Digit2, Kind.Tap);
            Add("3", "Hotbar3", Key.Digit3, Kind.Tap);
            Add("Bag", "SelectBackpack", Key.Digit4, Kind.Tap);
            Add("Empty", "UnselectSlot", Key.Tab, Kind.Tap).Shown = HoldingItem;
            s_SprintButton = Add("Sprint", kind: Kind.Toggle);
            Add("Menu", "Pause", Key.Escape, Kind.Tap).AlwaysShown = true;
            Add("Hide", kind: Kind.Tap, onTap: () =>
            {
                s_Hidden = !s_Hidden;
                Prefs.SetInt("peakios_hud_hidden", s_Hidden ? 1 : 0);
            }).AlwaysShown = true;
        }

        static Button Add(string label, string action = null, Key fallback = Key.None, Kind kind = Kind.Hold, int mouse = -1, Action onTap = null)
        {
            var b = new Button { Label = label, Action = action, Fallback = fallback, Mouse = mouse, Kind = kind, OnTap = onTap };
            s_Buttons.Add(b);
            return b;
        }

        static Button Find(string label) => s_Buttons.Find(b => b.Label == label);

        /// The keyboard key the player's binding of an action uses (rebinds included), cached for a second.
        static Key KeyFor(Button b)
        {
            if (b.Action == null) return b.Fallback;
            if (Time.frameCount - s_KeyCacheFrame > 60) { s_KeyCache.Clear(); s_KeyCacheFrame = Time.frameCount; }
            if (s_KeyCache.TryGetValue(b.Action, out var k)) return k;
            k = b.Fallback;
            try
            {
                var a = InputSystem.actions?.FindAction(b.Action);
                var kb = VirtualInput.Kb;
                if (a != null && kb != null)
                    foreach (var bind in a.bindings)
                    {
                        string p = bind.effectivePath;
                        if (string.IsNullOrEmpty(p) || !p.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (kb.TryGetChildControl(p.Substring("<Keyboard>/".Length)) is KeyControl kc) { k = kc.keyCode; break; }
                    }
            }
            catch (Exception) { }
            s_KeyCache[b.Action] = k;
            return k;
        }

        /// Positions in GUI pixels (origin top-left), relative to the safe area, scaled by screen height.
        static void Layout()
        {
            Rect safe = Screen.safeArea;
            int w = Screen.width, h = Screen.height;
            if (w == s_LayoutW && h == s_LayoutH && safe == s_LayoutSafe) return;
            s_LayoutW = w; s_LayoutH = h; s_LayoutSafe = safe;
            float H = h * Scale;
            float left = safe.xMin, right = safe.xMax;
            float top = h - safe.yMax, bottom = h - safe.yMin;
            float big = 0.165f * H, mid = 0.13f * H, small = 0.095f * H;
            void Put(string l, float x, float y, float size) { var b = Find(l); b.X = x; b.Y = y; b.Size = size; }
            // right thumb cluster
            Put("Jump", right - 0.13f * H, bottom - 0.14f * H, big);
            Put("Crouch", right - 0.34f * H, bottom - 0.09f * H, mid);
            Put("Grab", right - 0.13f * H, bottom - 0.38f * H, big);
            Put("Use 2", right - 0.33f * H, bottom - 0.29f * H, mid);
            Put("Interact", right - 0.13f * H, bottom - 0.61f * H, mid);
            Put("Drop", right - 0.30f * H, bottom - 0.50f * H, mid);
            // upper right, below the FPS counter
            Put("Ping", right - 0.10f * H, top + 0.24f * H, small);
            Put("Emote", right - 0.23f * H, top + 0.24f * H, small);
            // item slots, bottom centre
            float cx = (left + right) / 2;
            Put("1", cx - 0.18f * H, bottom - 0.07f * H, small);
            Put("2", cx - 0.06f * H, bottom - 0.07f * H, small);
            Put("3", cx + 0.06f * H, bottom - 0.07f * H, small);
            Put("Bag", cx + 0.18f * H, bottom - 0.07f * H, small);
            Put("Empty", cx + 0.30f * H, bottom - 0.07f * H, small);
            // left: sprint above the stick area, menu + hide in the top-left corner
            Put("Sprint", left + 0.12f * H, bottom - 0.52f * H, mid);
            Put("Menu", left + 0.07f * H, top + 0.07f * H, small);
            Put("Hide", left + 0.19f * H, top + 0.07f * H, small);
        }

        /// Forces a new layout (after the scale setting changed).
        public static void Relayout() => s_LayoutW = -1;

        static bool Visible(Button b)
        {
            if (b.AlwaysShown) return true;
            if (s_Hidden) return false;
            return b.Shown == null || b.Held || b.Shown();
        }

        static Button HitButton(Vector2 guiPos)
        {
            foreach (var b in s_Buttons)
            {
                if (!Visible(b)) continue;
                // generous hit circle: 1.15x the drawn radius
                if ((guiPos - new Vector2(b.X, b.Y)).sqrMagnitude <= (b.Size * 0.575f) * (b.Size * 0.575f)) return b;
            }
            return null;
        }

        /// Called by the touch layer every frame. <paramref name="active"/> = gameplay (cursor locked).
        /// Touches already taken by the debug overlay are not passed in.
        public static void Tick(List<Touch> touches, bool active)
        {
            Init();
            Layout();
            if (active != s_Active)
            {
                // every finger already down when the mode changes is ignored until it lifts
                ReleaseAll();
                foreach (var t in touches) s_Ignored.Add(t.fingerId);
                s_Active = active;
            }
            if (!active) { s_Ignored.RemoveWhere(id => !touches.Exists(t => t.fingerId == id)); return; }

            int h = Screen.height;
            foreach (var t in touches)
            {
                int id = t.fingerId;
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if (s_Ignored.Contains(id)) { if (ended) s_Ignored.Remove(id); continue; }
                var gui = new Vector2(t.position.x, h - t.position.y);

                if (t.phase == TouchPhase.Began)
                {
                    var b = HitButton(gui);
                    if (b != null)
                    {
                        s_ButtonFingers[id] = b;
                        Press(b);
                    }
                    else if (s_StickFinger < 0 && t.position.x < Screen.safeArea.xMin + (Screen.safeArea.width * 0.4f))
                    {
                        s_StickFinger = id;
                        s_StickOrigin = s_StickPos = t.position;
                    }
                    else if (s_LookFinger < 0)
                    {
                        s_LookFinger = id;
                        s_LastLook = t.position;
                    }
                    else s_Ignored.Add(id);
                    continue;
                }

                if (s_ButtonFingers.TryGetValue(id, out var held))
                {
                    if (ended) { held.Held = false; s_ButtonFingers.Remove(id); }
                }
                else if (id == s_StickFinger)
                {
                    s_StickPos = t.position;
                    if (ended) { s_StickFinger = -1; s_Sprint = false; }
                }
                else if (id == s_LookFinger)
                {
                    VirtualInput.PointerDelta += (t.position - s_LastLook) * (1500f * LookSpeed / Mathf.Max(1, Screen.width));
                    s_LastLook = t.position;
                    if (ended) s_LookFinger = -1;
                }
            }

            // stick -> analog movement
            Vector2 stick = Vector2.zero;
            if (s_StickFinger >= 0)
            {
                float r = 0.12f * h;
                Vector2 d = s_StickPos - s_StickOrigin;
                if (d.magnitude > r) s_StickOrigin = s_StickPos - d.normalized * r;   // origin follows the thumb
                stick = Vector2.ClampMagnitude((s_StickPos - s_StickOrigin) / r, 1f);
                if (stick.magnitude < 0.12f) stick = Vector2.zero;
            }
            VInput.VStick = stick;
            if (s_Sprint && stick == Vector2.zero && s_StickFinger < 0) s_Sprint = false;
            s_SprintButton.Held = s_Sprint;

            // held buttons -> keys / mouse buttons
            foreach (var b in s_Buttons)
            {
                if (b == s_SprintButton)
                {
                    if (s_Sprint && stick != Vector2.zero) VirtualInput.HoldKey(Key.LeftShift);
                    continue;
                }
                if (b.Held && b.Kind == Kind.Hold) Apply(b);
            }
        }

        static void Press(Button b)
        {
            switch (b.Kind)
            {
                case Kind.Hold:
                    b.Held = true;
                    break;
                case Kind.Toggle:
                    if (b == s_SprintButton) s_Sprint = !s_Sprint;
                    break;
                case Kind.Tap:
                    b.OnTap?.Invoke();
                    if (b.Action != null || b.Fallback != Key.None)
                    {
                        var k = KeyFor(b);
                        if (k != Key.None) VirtualInput.TapKey(k);
                    }
                    break;
            }
        }

        static void Apply(Button b)
        {
            if (b.Mouse == 0) VirtualInput.LeftButton = true;
            else if (b.Mouse == 1) VirtualInput.RightButton = true;
            else if (b.Mouse == 2) VirtualInput.MiddleButton = true;
            else
            {
                var k = KeyFor(b);
                if (k != Key.None) VirtualInput.HoldKey(k);
            }
        }

        public static void ReleaseAll()
        {
            foreach (var b in s_Buttons) b.Held = false;
            s_ButtonFingers.Clear();
            s_StickFinger = s_LookFinger = -1;
            s_Sprint = false;
            VInput.VStick = Vector2.zero;
        }

        // ---- drawing (IMGUI, called from TouchLayer.OnGUI) -------------------------------------------
        public static void Draw()
        {
            if (!s_Active || s_Buttons.Count == 0) return;
            if (s_Circle == null) s_Circle = MakeCircle(128);
            if (s_Label == null)
            {
                s_Label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                s_Label.normal.textColor = Color.white;
            }
            int h = Screen.height;
            foreach (var b in s_Buttons)
            {
                if (!Visible(b)) continue;
                bool on = b.Held || (b == s_SprintButton && s_Sprint);
                GUI.color = new Color(1, 1, 1, (on ? 0.55f : 0.22f) * Opacity);
                GUI.DrawTexture(b.Rect, s_Circle);
                GUI.color = new Color(1, 1, 1, (on ? 1f : 0.75f) * Mathf.Max(Opacity, 0.35f));
                s_Label.fontSize = Mathf.RoundToInt(b.Size * (b.Label.Length > 5 ? 0.17f : 0.21f));
                string text = b.Label == "Hide" && s_Hidden ? "Show" : b.Label;
                GUI.Label(b.Rect, text, s_Label);
            }
            if (s_StickFinger >= 0)
            {
                float r = 0.12f * h;
                var o = new Vector2(s_StickOrigin.x, h - s_StickOrigin.y);
                var p = new Vector2(s_StickPos.x, h - s_StickPos.y);
                Vector2 off = Vector2.ClampMagnitude(p - o, r);
                GUI.color = new Color(1, 1, 1, 0.18f * Opacity);
                GUI.DrawTexture(new Rect(o.x - r, o.y - r, 2 * r, 2 * r), s_Circle);
                GUI.color = new Color(1, 1, 1, 0.45f * Opacity);
                float k = r * 0.45f;
                GUI.DrawTexture(new Rect(o.x + off.x - k, o.y + off.y - k, 2 * k, 2 * k), s_Circle);
            }
            GUI.color = Color.white;
        }

        static Texture2D MakeCircle(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            float c = (n - 1) / 2f, rOut = n / 2f - 1f, rIn = rOut - n * 0.06f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float fill = Mathf.Clamp01(rOut - d + 0.5f);              // antialiased disc
                    float ring = Mathf.Clamp01(d - rIn + 0.5f);               // brighter rim
                    byte a = (byte)(255 * fill * (0.55f + 0.45f * ring));
                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
