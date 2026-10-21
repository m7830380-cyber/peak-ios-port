using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace PeakShim
{
    /// <summary>
    /// The game's reflection scans (console commands, type parsers, mod settings) walk every method of every type in
    /// every loaded assembly. Under IL2CPP that takes ~20 s on an A16 during the first scene load, inside
    /// didFinishLaunching, and iOS kills the app (0x8BADF00D scene-create watchdog). The attributes they look for
    /// live in Zorro.Core.Runtime / Assembly-CSharp, so only assemblies that reference Zorro.Core.Runtime (or are it)
    /// can contain them; the patcher sends AppDomain.GetAssemblies() in those scans here.
    /// </summary>
    public static class GameAssemblies
    {
        const string Zorro = "Zorro.Core.Runtime";
        static Assembly[] s_cached;

        public static Assembly[] Get(AppDomain domain)
        {
            if (s_cached != null) return s_cached;
            var sw = Stopwatch.StartNew();
            var all = domain.GetAssemblies();
            var list = new List<Assembly>();
            foreach (var a in all)
            {
                try
                {
                    var name = a.GetName().Name;
                    if (name == Zorro || name == "Assembly-CSharp") { list.Add(a); continue; }
                    if (IsFramework(name)) continue;
                    foreach (var r in a.GetReferencedAssemblies())
                        if (r.Name == Zorro) { list.Add(a); break; }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PEAK-iOS] assembly filter: " + e.Message);
                    list.Add(a);
                }
            }
            s_cached = list.ToArray();
            Debug.Log($"[PEAK-iOS] reflection scans limited to {s_cached.Length} of {all.Length} assemblies ({sw.ElapsedMilliseconds} ms)");
            return s_cached;
        }

        static bool IsFramework(string n) =>
            n == "mscorlib" || n == "netstandard" || n.StartsWith("System") || n.StartsWith("Mono.") ||
            n.StartsWith("UnityEngine") || n.StartsWith("Unity.") || n.StartsWith("Newtonsoft") || n.StartsWith("Sirenix");
    }
}
