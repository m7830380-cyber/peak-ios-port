using PeakShim;
using UnityEngine;
using UnityEngine.Profiling;

namespace PeakIOS
{
    // FPS counter in the top-right corner. Tap it to open the log viewer (status + last log lines);
    // tap anywhere to close it again.
    public class DebugOverlay : MonoBehaviour
    {
        public static bool ShowingLog { get; private set; }
        public static bool ShowFps = true;
        static Rect s_Corner;     // pixels, origin bottom-left
        float m_Fps, m_Accum, m_Worst, m_WorstShown;
        int m_Frames;
        GUIStyle m_Style, m_Small, m_Head;

        public static bool TryBeginTouch(Vector2 pos)
        {
            if (ShowingLog)
            {
                ShowingLog = false;
                return true;
            }
            if (s_Corner.Contains(pos))
            {
                ShowingLog = true;
                Log.Info("log viewer opened");
                return true;
            }
            return false;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            m_Accum += dt;
            m_Frames++;
            if (dt > m_Worst) m_Worst = dt;
            if (m_Accum >= 0.5f)
            {
                m_Fps = m_Frames / m_Accum;
                m_WorstShown = m_Worst * 1000f;
                m_Accum = 0;
                m_Frames = 0;
                m_Worst = 0;
            }
            Rect safe = Screen.safeArea;
            float s = Screen.height * 0.08f;
            s_Corner = new Rect(safe.xMax - s * 2.4f, Screen.height - s * 1.1f, s * 2.4f, s * 1.1f);
        }

        void OnGUI()
        {
            GUI.depth = -200;
            if (m_Style == null)
            {
                m_Style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight };
                m_Small = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.LowerLeft };
                m_Head = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.UpperLeft, fontStyle = FontStyle.Bold };
                foreach (var st in new[] { m_Style, m_Small, m_Head }) st.normal.textColor = Color.white;
            }
            float w = Screen.width, h = Screen.height;
            if (ShowFps || ShowingLog)
            {
                m_Style.fontSize = Mathf.RoundToInt(h * 0.022f);
                string txt = $"{m_Fps:0} fps";
                if (Log.ErrorCount > 0) txt += $"  {Log.ErrorCount} err";
                var r = new Rect(s_Corner.x, h - s_Corner.yMax + 4, s_Corner.width - 6, s_Corner.height);
                GUI.color = new Color(1, 1, 1, 0.7f);
                GUI.Label(r, txt, m_Style);
                GUI.color = Color.white;
            }
            if (!ShowingLog) return;

            GUI.color = new Color(0, 0, 0, 0.88f);
            GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;
            Rect safe = Screen.safeArea;
            float x = safe.xMin + 10, cw = safe.width - 20;
            m_Head.fontSize = Mathf.RoundToInt(h * 0.021f);
            m_Small.fontSize = Mathf.RoundToInt(h * 0.018f);
            long mem = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            long mono = Profiler.GetMonoUsedSizeLong() / (1024 * 1024);
            string head =
                $"{m_Fps:0} fps (worst {m_WorstShown:0} ms)   memory: unity {mem} MB, managed {mono} MB   errors {Log.ErrorCount}, warnings {Log.WarningCount}\n" +
                $"player {Identity.NickName}   cursor {(TouchLayer.CursorFree ? "free" : "locked")}   blocked connections {Net.Blocked}\n" +
                $"log file: Files > On My iPhone > PEAK > {Log.FileName}   (tap anywhere to close; tap this header to save a screenshot + purple check)";
            float headH = h * 0.16f;
            GUI.Label(new Rect(x, h * 0.02f, cw, headH), head, m_Head);
            string all = string.Join("\n", Log.Snapshot());
            GUI.Label(new Rect(x, h * 0.02f + headH, cw, h * 0.96f - headH), all, m_Small);
        }
    }
}
