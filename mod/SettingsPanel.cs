using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakIOS
{
    /// <summary>
    /// "iOS settings" panel (the "iOS" button in the top-left corner, in menus and in gameplay): touch controls,
    /// frame rate, resolution, microphone. Drawn with IMGUI; touches are handled here directly (TouchLayer forwards
    /// them while it is open), so it does not depend on IMGUI's mouse emulation.
    /// </summary>
    public class SettingsPanel : MonoBehaviour
    {
        public static bool Open;

        abstract class Row
        {
            public string Label;
            public Rect Rect;
            public virtual void Touch(Vector2 gui, bool began) { }
            public abstract string Value { get; }
        }

        sealed class SliderRow : Row
        {
            public Func<float> Get; public Action<float> Set; public float Min, Max, Step = 0.05f; public Func<float, string> Fmt;
            public Rect Track => new Rect(Rect.x + Rect.width * 0.45f, Rect.y, Rect.width * 0.5f, Rect.height);
            public override string Value => Fmt(Get());
            public override void Touch(Vector2 gui, bool began)
            {
                var t = Track;
                if (began && !t.Contains(gui)) return;
                float v = Mathf.Lerp(Min, Max, Mathf.Clamp01((gui.x - t.x) / t.width));
                Set(Mathf.Round(v / Step) * Step);
                s_Dirty = true;
            }
        }

        sealed class ChoiceRow : Row
        {
            public string[] Options; public Func<int> Get; public Action<int> Set;
            public Rect Option(int i)
            {
                float w = Rect.width * 0.5f / Options.Length;
                return new Rect(Rect.x + Rect.width * 0.45f + i * w, Rect.y + Rect.height * 0.12f, w - 6, Rect.height * 0.76f);
            }
            public override string Value => "";
            public override void Touch(Vector2 gui, bool began)
            {
                if (!began) return;
                for (int i = 0; i < Options.Length; i++)
                    if (Option(i).Contains(gui)) { Set(i); s_Dirty = true; }
            }
        }

        static readonly int[] s_Fps = { 30, 60, 120 };

        static readonly List<Row> s_Rows = new List<Row>
        {
            new SliderRow { Label = "Look speed", Min = 0.2f, Max = 3f, Get = () => IosSettings.LookSpeed, Set = v => IosSettings.LookSpeed = v, Fmt = v => v.ToString("0.00") },
            new SliderRow { Label = "Button size", Min = 0.6f, Max = 1.6f, Get = () => IosSettings.HudScale, Set = v => IosSettings.HudScale = v, Fmt = v => Mathf.RoundToInt(v * 100) + "%" },
            new SliderRow { Label = "Button opacity", Min = 0.1f, Max = 1f, Get = () => IosSettings.HudOpacity, Set = v => IosSettings.HudOpacity = v, Fmt = v => Mathf.RoundToInt(v * 100) + "%" },
            new ChoiceRow { Label = "Frame rate cap", Options = new[] { "30", "60", "120" }, Get = () => Array.IndexOf(s_Fps, IosSettings.FpsCap) < 0 ? 1 : Array.IndexOf(s_Fps, IosSettings.FpsCap), Set = i => IosSettings.FpsCap = s_Fps[i] },
            new SliderRow { Label = "Resolution", Min = 0.4f, Max = 1f, Get = () => IosSettings.RenderScale, Set = v => IosSettings.RenderScale = v, Fmt = v => Mathf.RoundToInt(v * 100) + "%" },
            new ChoiceRow { Label = "Microphone (voice)", Options = new[] { "Off", "On" }, Get = () => IosSettings.Microphone ? 1 : 0, Set = i => IosSettings.Microphone = i == 1 },
            new ChoiceRow { Label = "Test: post-processing", Options = new[] { "Off", "On" }, Get = () => RenderDiag.PostFx ? 1 : 0, Set = i => RenderDiag.PostFx = i == 1 },
            new ChoiceRow { Label = "Test: screen effects", Options = new[] { "Off", "On" }, Get = () => RenderDiag.Features ? 1 : 0, Set = i => RenderDiag.Features = i == 1 },
            new ChoiceRow { Label = "Test: HDR", Options = new[] { "Off", "On" }, Get = () => RenderDiag.Hdr ? 1 : 0, Set = i => RenderDiag.Hdr = i == 1 },
            new ChoiceRow { Label = "Test: screen copy", Options = new[] { "Blit", "Tile" }, Get = () => GraphicsSetup.FramebufferFetchCopy ? 1 : 0, Set = i => GraphicsSetup.FramebufferFetchCopy = i == 1 },
        };

        static bool s_Dirty;
        static Rect s_Close;
        static Row s_Active;
        GUIStyle m_Title, m_Text, m_Small, m_Center;

        static void Layout()
        {
            float h = Screen.height;
            Rect safe = Screen.safeArea;
            float x = safe.xMin + safe.width * 0.04f, cw = safe.width * 0.92f, rowH = h * 0.075f, y = h * 0.04f;
            s_Close = new Rect(x + cw - cw * 0.18f, y, cw * 0.18f, rowH);
            y += rowH * 1.3f;
            foreach (var r in s_Rows) { r.Rect = new Rect(x, y, cw, rowH); y += rowH * 1.04f; }
        }

        /// Touch handling while the panel is open (screen coordinates, origin bottom-left).
        public static void HandleTouch(Vector2 pos, TouchPhase phase)
        {
            Layout();
            var gui = new Vector2(pos.x, Screen.height - pos.y);
            if (phase == TouchPhase.Began)
            {
                s_Active = null;
                if (s_Close.Contains(gui)) { Close(); return; }
                foreach (var r in s_Rows)
                    if (r.Rect.Contains(gui)) { s_Active = r; r.Touch(gui, true); }
                return;
            }
            if (s_Active == null) return;
            if (phase == TouchPhase.Moved || phase == TouchPhase.Stationary) s_Active.Touch(gui, false);
            else { s_Active = null; if (s_Dirty) { s_Dirty = false; IosSettings.Save(); } }
        }

        public static void Show()
        {
            Open = true;
            Log.Info("iOS settings opened");
        }

        public static void Close()
        {
            Open = false;
            s_Active = null;
            IosSettings.Save();
            IosSettings.Apply();
        }

        void OnGUI()
        {
            if (!Open || Event.current.type != EventType.Repaint) return;
            GUI.depth = -200;
            Layout();
            float h = Screen.height, w = Screen.width;
            int fs = Mathf.RoundToInt(h * 0.035f);
            if (m_Title == null)
            {
                m_Title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                m_Text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
                m_Small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                m_Center = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            }
            m_Title.fontSize = Mathf.RoundToInt(fs * 1.4f);
            m_Text.fontSize = m_Center.fontSize = fs;
            m_Small.fontSize = Mathf.RoundToInt(fs * 0.8f);

            Fill(new Rect(0, 0, w, h), new Color(0.02f, 0.02f, 0.04f, 0.92f));
            GUI.Label(new Rect(s_Rows[0].Rect.x, s_Close.y, s_Close.x - s_Rows[0].Rect.x, s_Close.height), "iOS settings", m_Title);
            Fill(s_Close, new Color(0.3f, 0.6f, 1f, 0.9f));
            GUI.Label(s_Close, "Close", m_Center);

            foreach (var r in s_Rows)
            {
                GUI.Label(new Rect(r.Rect.x, r.Rect.y, r.Rect.width * 0.44f, r.Rect.height), r.Label + "   " + r.Value, m_Text);
                if (r is SliderRow s)
                {
                    var t = s.Track;
                    float f = Mathf.InverseLerp(s.Min, s.Max, s.Get());
                    Fill(new Rect(t.x, t.center.y - 3, t.width, 6), new Color(1, 1, 1, 0.3f));
                    Fill(new Rect(t.x, t.center.y - 3, t.width * f, 6), new Color(0.3f, 0.6f, 1f, 1f));
                    float k = t.height * 0.5f;
                    Fill(new Rect(t.x + t.width * f - k / 2, t.center.y - k / 2, k, k), Color.white);
                }
                else if (r is ChoiceRow c)
                {
                    int cur = c.Get();
                    for (int i = 0; i < c.Options.Length; i++)
                    {
                        var o = c.Option(i);
                        Fill(o, i == cur ? new Color(0.3f, 0.6f, 1f, 0.95f) : new Color(1, 1, 1, 0.15f));
                        GUI.Label(o, c.Options[i], m_Center);
                    }
                }
            }
            var last = s_Rows[s_Rows.Count - 1].Rect;
            GUI.Label(new Rect(last.x, last.yMax + h * 0.01f, last.width, h - last.yMax),
                "Quality, shadows and effects are in the game's own Settings menu. A lower resolution or frame rate saves " +
                "battery and heat. \"Test\" switches are for tracking down picture problems (turning one Off and back On may need a restart). Log: Files > On My iPhone > PEAK > PEAK-iOS.log (or tap the FPS counter).", m_Small);
            GUI.color = Color.white;
        }

        static void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
