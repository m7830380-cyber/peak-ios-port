using System.Collections.Generic;
using PeakShim;
using UnityEngine;

namespace PeakIOS
{
    /// <summary>
    /// Fingers -> virtual input. While the game shows a cursor (menus, escape menu, terminals) touch is the mouse:
    /// one finger moves the pointer and holds the left button (tap = click, drag = drag), two fingers scroll.
    /// The FPS counter corner opens the log viewer. While the cursor is locked (gameplay) fingers go to TouchHud.
    /// </summary>
    public class TouchLayer : MonoBehaviour
    {
        readonly HashSet<int> m_Consumed = new HashSet<int>();
        int m_Pointer = -1;
        bool m_Scrolling;
        Vector2 m_LastScrollPos;
        readonly List<Touch> m_Free = new List<Touch>();

        public static bool CursorFree => VCursor.LockState != CursorLockMode.Locked;

        void Update() => GraphicsSetup.Tick();

        /// The "iOS" settings button, top left (GUI pixels, origin top-left), in menus and in gameplay.
        static Rect IosButton
        {
            get
            {
                Rect safe = Screen.safeArea;
                float s = 0.095f * Screen.height;
                return new Rect(safe.xMin + 0.31f * Screen.height - s / 2, Screen.height - safe.yMax + 0.07f * Screen.height - s / 2, s, s);
            }
        }

        static GUIStyle s_IosStyle;

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || DebugOverlay.ShowingLog || SettingsPanel.Open) return;
            GUI.depth = -100;
            TouchHud.Draw();
            if (s_IosStyle == null)
            {
                s_IosStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                s_IosStyle.normal.textColor = Color.white;
            }
            var r = IosButton;
            s_IosStyle.fontSize = Mathf.RoundToInt(r.height * 0.3f);
            GUI.color = new Color(0, 0, 0, 0.35f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1, 1, 1, 0.8f);
            GUI.Label(r, "iOS", s_IosStyle);
            GUI.color = Color.white;
        }

        void OnEnable() => VirtualInput.Tick += Tick;
        void OnDisable() => VirtualInput.Tick -= Tick;

        void Tick()
        {
            int count = Input.touchCount;
            int free = 0;
            Vector2 sum = Vector2.zero;
            Touch primary = default;
            bool havePrimary = false;
            m_Free.Clear();
            for (int i = 0; i < count; i++)
            {
                var t = Input.GetTouch(i);
                if (SettingsPanel.Open)
                {
                    if (!m_Consumed.Contains(t.fingerId) || t.phase != TouchPhase.Began) SettingsPanel.HandleTouch(t.position, t.phase);
                    m_Consumed.Add(t.fingerId);
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) m_Consumed.Remove(t.fingerId);
                    continue;
                }
                if (t.phase == TouchPhase.Began && DebugOverlay.TryBeginTouch(t.position)) m_Consumed.Add(t.fingerId);
                else if (t.phase == TouchPhase.Began && !DebugOverlay.ShowingLog && IosButton.Contains(new Vector2(t.position.x, Screen.height - t.position.y)))
                {
                    SettingsPanel.Show();
                    m_Consumed.Add(t.fingerId);
                }
                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if (m_Consumed.Contains(t.fingerId))
                {
                    if (ended) m_Consumed.Remove(t.fingerId);
                    continue;
                }
                m_Free.Add(t);
                if (ended)
                {
                    if (t.fingerId == m_Pointer) m_Pointer = -1;
                    continue;
                }
                free++;
                sum += t.position;
                if (m_Pointer < 0 && !m_Scrolling) m_Pointer = t.fingerId;
                if (t.fingerId == m_Pointer) { primary = t; havePrimary = true; }
            }
            if (DebugOverlay.ShowingLog || SettingsPanel.Open) { m_Pointer = -1; TouchHud.Tick(m_Free, false); return; }
            TouchHud.Tick(m_Free, !CursorFree);
            if (!CursorFree) { m_Pointer = -1; m_Scrolling = false; return; }

            if (free >= 2)
            {
                // two fingers: scroll by their average vertical movement; the pointer press is dropped
                Vector2 avg = sum / free;
                if (m_Scrolling) VirtualInput.Scroll += (avg.y - m_LastScrollPos.y) / (Screen.height * 0.03f);
                m_LastScrollPos = avg;
                m_Scrolling = true;
                m_Pointer = -1;
                return;
            }
            if (free == 0) m_Scrolling = false;
            if (m_Scrolling || !havePrimary) return;
            VirtualInput.PointerPos = primary.position;
            VirtualInput.LeftButton = true;
        }
    }
}
