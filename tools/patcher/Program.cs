// Patches the retail PEAK managed assemblies so they can be AOT-compiled with IL2CPP and run on iOS
// with no Steam, no Epic Online Services and no contact with Landfall / platform servers.
//
//   PeakPatcher patch    <retailManagedDir> <PeakShim.dll> <PeakPlatform.dll> <outDir>
//   PeakPatcher analyze  <managedDir>            (APIs the port cares about, per assembly)
//   PeakPatcher pinvokes <managedDir>            (every P/Invoke, by native library)
//   PeakPatcher verify   <patchedDir>            (fails if anything desktop-only is left)
//   + dumpil / types / memberrefs / calls / stackcheck / stubmethods (investigation helpers)
//
// All shipped assemblies:
//  * Steamworks (steam_api64, sdkencryptedappticket64) P/Invokes become stubs returning zero / false / null,
//    so any Steam call that slips through does nothing instead of throwing.
//  * every other P/Invoke (EOS, Photon Voice native audio/opus, PortningsbolagetMemory, ...) becomes a stub that
//    throws DllNotFoundException.
//  * Process.Start & co. go to PeakShim.Platform (logs and does nothing).
//  * Cursor.lockState / visible go to PeakShim.VCursor (the touch layer reads it).
//  * PlayerPrefs -> PeakShim.Prefs (Documents/PEAK-settings.txt), Microphone -> PeakShim.Mic (iOS on/off option).
//  * Application.Quit, Screen.SetResolution / fullScreen, targetFrameRate / vSyncCount -> PeakShim.Platform.
// Input-reading assemblies: every UnityEngine.Input member goes to PeakShim.VInput (virtual mouse/keys from touch).
// Managers: PlatformManager creates PeakPlatform.IOSPlatform instead of SteamRuntimeManager.
// Platforms: PlatformUtility.LoadServerSettings never downloads the server settings (reports "not loaded").
// User: UserManager does not log in to Epic Online Services (local user, no sanctions).
// SteamManagerAssembly: SteamManager never initialises Steam (Initialized stays false).
// com.rlabrecque.steamworks.net: "is Steam running" checks pass; SteamUser.GetSteamID / SteamFriends.GetPersonaName
//    report the local iOS player (PeakShim.Identity).
// Assembly-CSharp:
//  * the game's own "NoSteam" developer mode is on (except for achievements, which the iOS platform stores).
//  * CloudAPI version check / lobby verification (azurewebsites) are off; the daily island comes from the
//    game's offline fallback (by date).
//  * NetworkingUtilities.ConnectToNetwork uses Photon's offline mode unless a Wi-Fi session is set up.
// Zorro.Core.Runtime ReflectionUtility: attribute scans only walk the game's assemblies (PeakShim.GameAssemblies),
//    keeping the first scene load inside the iOS launch watchdog.
// Unity.Burst: Burst is reported disabled, so [BurstCompile] code runs as managed (IL2CPP) code.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

static partial class Program
{
    const string ShimName = "PeakShim";
    const string PlatformAsmName = "PeakPlatform";

    // Native libraries implemented inside the iOS player (PeakNative.mm), keyed without "lib" prefix / extension.
    static readonly HashSet<string> InternalLibs = new HashSet<string>();

    // Libraries whose stubs return default values instead of throwing.
    static readonly HashSet<string> DefaultStubLibs = new HashSet<string> { "steam_api64", "sdkencryptedappticket64" };

    // Assemblies whose UnityEngine.Input reads are redirected to the virtual input.
    static readonly HashSet<string> InputRedirect = new HashSet<string>
    {
        "Assembly-CSharp", "Assembly-CSharp-firstpass", "Zorro.Core.Runtime", "Zorro.UI.Runtime", "Zorro.ControllerSupport",
        "Zorro.Settings.Runtime", "pworld", "PhotonVoice", "UnityEngine.UI", "Unity.TextMeshPro", "Tayx.Graphy",
        "Unity.RenderPipelines.Core.Runtime",
    };

    // Shipped with the PC game but never used by the iOS build (Xbox PC / GDK).
    // Harmony / MonoMod / Mono.Cecil (mod loading) need Mono.Cecil and runtime code generation, neither of which exists
    // under IL2CPP (the linker rejects them): the game's only use, Plugin.Harmony, is removed (RemoveHarmony).
    static readonly HashSet<string> Dropped = new HashSet<string> { "Unity.Microsoft.GDK.dll", "Unity.Microsoft.GDK.Tools.dll", "XblPCSandbox.dll",
        "0Harmony.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll",
        // Mono.Cecil: the game reads mod DLLs with it (plugin metadata); not part of the iOS build either
        "Mono.Cecil.dll", "Mono.Cecil.Mdb.dll", "Mono.Cecil.Pdb.dll", "Mono.Cecil.Rocks.dll" };
    static readonly HashSet<string> DroppedNames = new HashSet<string>(Dropped.Select(Path.GetFileNameWithoutExtension));

    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "analyze") return Analyze.Run(args[1]);
            if (args.Length >= 2 && args[0] == "pinvokes") return Analyze.PInvokes(args[1]);
            if (args.Length >= 2 && args[0] == "verify") return Verify(args[1]);
            if (args.Length >= 2 && args[0] == "stackcheck") return Analyze.StackCheck(args.Skip(1).ToArray());
            if (args.Length >= 3 && args[0] == "dumpil") return Analyze.DumpIL(args[1], args[2]);
            if (args.Length >= 3 && args[0] == "types") return Analyze.Types(args[1], args[2]);
            if (args.Length >= 4 && args[0] == "stubmethods") return Analyze.StubMethods(args[1], args[2], args[3]);
            if (args.Length >= 3 && args[0] == "memberrefs") return Analyze.MemberRefs(args[1], args[2]);
            if (args.Length >= 3 && args[0] == "calls") return Analyze.Calls(args[1], args[2]);
            if (args.Length >= 5 && args[0] == "patch") return Patch(args[1], args[2], args[3], args[4]);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("FATAL: " + e);
            return 3;
        }
        Console.Error.WriteLine("usage: PeakPatcher patch <managedDir> <PeakShim.dll> <PeakPlatform.dll> <outDir> | analyze|pinvokes|verify <dir>");
        return 1;
    }

    /// The game's own (non-engine, non-BCL) assemblies: every dll in Managed that is not a UnityEngine module,
    /// a BCL facade or mscorlib.
    static List<string> ShippedAssemblies(string dir)
    {
        var list = new List<string>();
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (name == "UnityEngine.dll" || (name.StartsWith("UnityEngine.") && name != "UnityEngine.UI.dll")) continue;
            if (name.StartsWith("System.") || name.StartsWith("Mono.") || name.StartsWith("Microsoft.") ||
                name == "mscorlib.dll" || name == "netstandard.dll" || name == "System.dll" || name == "Accessibility.dll") continue;
            if (Dropped.Contains(name)) continue;
            // Unity Gaming Services (authentication, multiplayer, QoS, telemetry): no game code uses them, but they
            // start themselves at launch and would contact Unity's servers - left out of the iOS build.
            if (name.StartsWith("Unity.Services.")) continue;
            list.Add(name);
        }
        return list;
    }

    static int Patch(string inDir, string shimPath, string platformPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(inDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(shimPath)));
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(platformPath)));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, ReadSymbols = false };
        var shim = AssemblyDefinition.ReadAssembly(shimPath, rp);
        var platform = AssemblyDefinition.ReadAssembly(platformPath, rp);

        int total = 0, failures = 0;
        var shipped = ShippedAssemblies(inDir);
        foreach (var name in shipped)
        {
            var asm = AssemblyDefinition.ReadAssembly(Path.Combine(inDir, name), rp);
            var ctx = new Ctx(asm.MainModule, shim.MainModule, platform.MainModule);
            var counts = new List<string>();
            int Step(string what, int n) { if (n > 0) counts.Add($"{what} {n}"); return n; }

            int n = 0;
            string an = asm.Name.Name;
            // game-specific rewrites first: they replace calls the generic passes would otherwise redirect
            if (an == "Assembly-CSharp") n += Step("game", PatchGame(ctx));
            n += Step("harmony", RemoveHarmony(ctx));
            if (an == "Managers") n += Step("platform", PatchManagers(ctx));
            if (an == "Platforms") n += Step("servers", PatchPlatforms(ctx));
            if (an == "SteamManagerAssembly") n += Step("steam", PatchSteamManager(ctx));
            if (an == "com.rlabrecque.steamworks.net") n += Step("steamworks", PatchSteamworks(ctx));
            if (an == "Unity.Burst") n += Step("burst", PatchBurst(ctx));
            if (an == "Photon3Unity3D") n += Step("netguard", PatchPhotonConnect(ctx));
            if (an == "Unity.ResourceManager") n += Step("bundlecrc", PatchBundleCrc(ctx));
            if (an == "User") n += Step("eos", PatchUser(ctx));
            if (an == "LightVolume.Runtime") n += Step("lightmap", PatchLightmap(ctx));
            if (an == "Zorro.Core.Runtime") n += Step("scan", LimitAssemblyScan(ctx, "Zorro.Core.ReflectionUtility"));
            if (an == "Unity.RenderPipelines.Core.Runtime") n += Step("copypass", PatchCopyPass(ctx));
            n += Step("pinvoke", RetargetPInvokes(ctx));
            n += Step("process", RedirectProcess(ctx));
            n += Step("cursor", RedirectCursor(ctx));
            n += Step("prefs", RedirectAll(ctx, "UnityEngine.PlayerPrefs", "Prefs"));
            n += Step("mic", RedirectAll(ctx, "UnityEngine.Microphone", "Mic"));
            n += Step("screen", RedirectScreen(ctx));
            n += Step("typedref", RemoveTypedReferences(ctx));
            if (InputRedirect.Contains(an)) n += Step("input", RedirectInput(ctx));

            total += n;
            failures += ctx.Failures;
            asm.Write(Path.Combine(outDir, name));
            Console.WriteLine($"{name}: {n} patches{(counts.Count > 0 ? " (" + string.Join(", ", counts) + ")" : "")}{(ctx.Failures > 0 ? $", {ctx.Failures} FAILED" : "")}");
        }
        File.Copy(shimPath, Path.Combine(outDir, ShimName + ".dll"), true);
        File.Copy(platformPath, Path.Combine(outDir, PlatformAsmName + ".dll"), true);
        Console.WriteLine($"{shipped.Count} assemblies, {total} patches, {failures} failures");
        if (failures > 0) return 2;
        return Verify(outDir);
    }

    // ------------------------------------------------------------------------------------------
    sealed class Ctx
    {
        public readonly ModuleDefinition Mod;
        public readonly ModuleDefinition Shim;
        public readonly ModuleDefinition Platform;
        public int Failures;
        public Ctx(ModuleDefinition mod, ModuleDefinition shim, ModuleDefinition platform) { Mod = mod; Shim = shim; Platform = platform; }

        public TypeDefinition ShimType(string name) => Shim.GetType(ShimName + "." + name) ?? throw new Exception("shim type missing: " + name);

        public MethodReference ShimMethod(string type, string name, params string[] paramTypes)
        {
            var m = ShimType(type).Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramTypes.Length &&
                x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(paramTypes));
            if (m == null) throw new Exception($"shim method missing: {type}.{name}({string.Join(",", paramTypes)})");
            return Mod.ImportReference(m);
        }

        public void Fail(string what)
        {
            Console.WriteLine("  !! " + what);
            Failures++;
        }
    }

    static IEnumerable<(MethodDefinition m, Instruction ins)> Calls(ModuleDefinition mod, Func<MethodDefinition, bool> where = null)
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || (where != null && !where(m))) continue;
                foreach (var ins in m.Body.Instructions.ToList())
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj) && ins.Operand is MethodReference)
                        yield return (m, ins);
            }
    }

    static string Sig(MethodReference mr) => string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));

    static MethodReference DnfCtor(ModuleDefinition mod)
    {
        var dnfType = new TypeReference("System", "DllNotFoundException", mod, mod.TypeSystem.CoreLibrary);
        var ctor = new MethodReference(".ctor", mod.TypeSystem.Void, dnfType) { HasThis = true };
        ctor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
        return ctor;
    }

    // ------------------------------------------------------------------------------------------
    static int RetargetPInvokes(Ctx c)
    {
        int n = 0;
        var mod = c.Mod;
        ModuleReference internalRef = null;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.IsPInvokeImpl || m.PInvokeInfo == null) continue;
                string lib = m.PInvokeInfo.Module.Name;
                if (lib == "__Internal") continue;
                string libKey = Path.GetFileNameWithoutExtension(lib).ToLowerInvariant();
                if (lib.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || lib.EndsWith(".so") || lib.EndsWith(".dylib")) { }
                else libKey = lib.ToLowerInvariant();
                if (libKey.StartsWith("lib")) libKey = libKey.Substring(3);
                string entry = m.PInvokeInfo.EntryPoint ?? m.Name;
                if (InternalLibs.Contains(libKey))
                {
                    if (internalRef == null)
                    {
                        internalRef = mod.ModuleReferences.FirstOrDefault(r => r.Name == "__Internal");
                        if (internalRef == null) { internalRef = new ModuleReference("__Internal"); mod.ModuleReferences.Add(internalRef); }
                    }
                    m.PInvokeInfo.Module = internalRef;
                    n++;
                    continue;
                }
                if (DefaultStubLibs.Contains(libKey)) StubPInvokeDefault(m);
                else StubPInvoke(m, $"{lib} ({entry}) is not available on iOS");
                n++;
            }
        // drop now-unused module references (steam_api64, ...)
        var used = new HashSet<ModuleReference>(mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null).Select(m => m.PInvokeInfo.Module));
        foreach (var r in mod.ModuleReferences.ToList())
            if (!used.Contains(r)) mod.ModuleReferences.Remove(r);
        return n;
    }

    static void ClearPInvoke(MethodDefinition m)
    {
        m.PInvokeInfo = null;     // Cecil's setter turns IsPInvokeImpl back on,
        m.IsPInvokeImpl = false;  // so clear the flag afterwards
        m.IsPreserveSig = false;
        m.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
        m.ImplAttributes |= MethodImplAttributes.IL | MethodImplAttributes.Managed;
        // marshalling attributes mean nothing on a managed method
        m.MethodReturnType.MarshalInfo = null;
        foreach (var p in m.Parameters) p.MarshalInfo = null;
    }

    static void StubPInvoke(MethodDefinition m, string message)
    {
        ClearPInvoke(m);
        m.Body = new MethodBody(m);
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, message);
        il.Emit(OpCodes.Newobj, DnfCtor(m.Module));
        il.Emit(OpCodes.Throw);
    }

    /// Stub that clears every out/ref argument and returns default(T).
    static void StubPInvokeDefault(MethodDefinition m)
    {
        ClearPInvoke(m);
        var il = NewBody(m);
        foreach (var p in m.Parameters)
            if (p.ParameterType is ByReferenceType br && p.IsOut)
            {
                il.Emit(OpCodes.Ldarg, p);
                il.Emit(OpCodes.Initobj, br.ElementType);
            }
        EmitDefault(m, il, m.ReturnType);
        il.Emit(OpCodes.Ret);
    }

    static void EmitDefault(MethodDefinition m, ILProcessor il, TypeReference t)
    {
        if (t.FullName == "System.Void") return;
        if (!t.IsValueType && !t.IsGenericParameter) { il.Emit(OpCodes.Ldnull); return; }
        var v = new VariableDefinition(t);
        m.Body.Variables.Add(v);
        m.Body.InitLocals = true;
        il.Emit(OpCodes.Ldloca, v);
        il.Emit(OpCodes.Initobj, t);
        il.Emit(OpCodes.Ldloc, v);
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectProcess(Ctx c)
    {
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "System.Diagnostics.Process" || ins.OpCode == OpCodes.Newobj) continue;
            MethodReference to = null;
            if (mr.Name == "Start" && !mr.HasThis)
            {
                string sig = Sig(mr);
                if (sig == "System.Diagnostics.ProcessStartInfo") to = c.ShimMethod("Platform", "ProcessStart", "System.Diagnostics.ProcessStartInfo");
                else if (sig == "System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String");
                else if (sig == "System.String,System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String", "System.String");
                else { c.Fail($"{m.FullName}: unhandled Process.Start({sig})"); continue; }
            }
            else if (mr.Name == "Start" && mr.HasThis && mr.Parameters.Count == 0)
                to = c.ShimMethod("Platform", "ProcessStartInstance", "System.Diagnostics.Process");
            else if (mr.Name == "GetProcessesByName" && Sig(mr) == "System.String")
                to = c.ShimMethod("Platform", "GetProcessesByName", "System.String");
            if (to == null) continue;
            ins.OpCode = OpCodes.Call;
            ins.Operand = to;
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectCursor(Ctx c)
    {
        int n = 0;
        var vc = c.ShimType("VCursor");
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Cursor") continue;
            if (mr.Name != "get_lockState" && mr.Name != "set_lockState" && mr.Name != "get_visible" && mr.Name != "set_visible") continue;
            var target = vc.Methods.First(x => x.Name == mr.Name);
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    /// Redirects every call to a static engine member to a shim method with the same signature.
    static int RedirectStatic(Ctx c, string type, string name, string shimType, string shimName, Func<MethodDefinition, bool> where = null)
    {
        int n = 0;
        var defs = c.ShimType(shimType).Methods.Where(x => x.Name == shimName).ToList();
        foreach (var (m, ins) in Calls(c.Mod, where))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != type || mr.Name != name || mr.HasThis) continue;
            var def = defs.FirstOrDefault(x => Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (def == null) { c.Fail($"{m.FullName}: no shim {shimType}.{shimName} for {type}.{name}({Sig(mr)})"); continue; }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(def);
            n++;
        }
        return n;
    }

    /// Redirects every static member of an engine class to the shim class member with the same name and signature.
    static int RedirectAll(Ctx c, string type, string shimType)
    {
        int n = 0;
        var st = c.ShimType(shimType);
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != type) continue;
            var t = st.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (t == null) { c.Fail($"{m.FullName}: no {shimType}.{mr.Name}({Sig(mr)})"); continue; }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(t);
            n++;
        }
        return n;
    }

    /// Desktop window / frame-rate calls: the iOS player is always fullscreen; frame pacing is the shim's.
    static int RedirectScreen(Ctx c)
    {
        int n = 0;
        foreach (var screen in new[] { "UnityEngine.Screen", "UnityEngine.Device.Screen" })
        {
            n += RedirectStatic(c, screen, "SetResolution", "Platform", "SetResolution");
            n += RedirectStatic(c, screen, "set_fullScreen", "Platform", "SetFullScreen");
            n += RedirectStatic(c, screen, "get_fullScreen", "Platform", "GetFullScreen");
        }
        n += RedirectStatic(c, "UnityEngine.Application", "Quit", "Platform", "Quit");
        n += RedirectStatic(c, "UnityEngine.Application", "set_targetFrameRate", "Platform", "SetTargetFrameRate");
        n += RedirectStatic(c, "UnityEngine.QualitySettings", "set_vSyncCount", "Platform", "SetVSyncCount");
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectInput(Ctx c)
    {
        int n = 0;
        var vinput = c.ShimType("VInput");
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Input" || mr.Name == ".ctor") continue;   // "new Input()" (pworld) is harmless
            var target = vinput.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic &&
                x.Parameters.Count == mr.Parameters.Count && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (target == null)
            {
                c.Fail($"{m.FullName}: no VInput equivalent for Input.{mr.Name}({Sig(mr)})");
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    // IL2CPP does not model mkrefany (C# __makeref) well - report any.
    static int RemoveTypedReferences(Ctx c)
    {
        foreach (var t in c.Mod.GetTypes())
            foreach (var m in t.Methods)
                if (m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Mkrefany || i.OpCode.Code == Code.Refanyval ||
                                                              i.OpCode.Code == Code.Refanytype || i.OpCode.Code == Code.Arglist))
                    Console.WriteLine("  note: typed reference / arglist in " + m.FullName + " (IL2CPP may reject it)");
        return 0;
    }

    // ------------------------------------------------------------------------------------------
    static MethodDefinition Method(Ctx c, TypeDefinition t, string name, int paramCount = -1)
    {
        var m = t?.Methods.FirstOrDefault(x => x.Name == name && (paramCount < 0 || x.Parameters.Count == paramCount));
        if (m == null) c.Fail($"{t?.FullName ?? "<missing type>"}.{name} missing");
        return m;
    }

    static ILProcessor NewBody(MethodDefinition m)
    {
        m.Body = new MethodBody(m);
        return m.Body.GetILProcessor();
    }

    static int MakeNoOp(Ctx c, TypeDefinition t, string name, int paramCount = -1)
    {
        var m = Method(c, t, name, paramCount);
        if (m == null) return 0;
        if (m.ReturnType.FullName != "System.Void") { c.Fail($"{t.Name}.{name} is not void"); return 0; }
        NewBody(m).Emit(OpCodes.Ret);
        return 1;
    }

    static FieldDefinition Field(Ctx c, TypeDefinition t, string name)
    {
        var f = t?.Fields.FirstOrDefault(x => x.Name == name);
        if (f == null) c.Fail($"{t?.FullName}.{name} field missing");
        return f;
    }

    static MethodReference EngineMethod(ModuleDefinition mod, string asmName, string ns, string type, string name, TypeReference ret, bool hasThis, params TypeReference[] ps)
    {
        var scope = mod.AssemblyReferences.FirstOrDefault(r => r.Name == asmName);
        if (scope == null) { scope = new AssemblyNameReference(asmName, new Version(0, 0, 0, 0)); mod.AssemblyReferences.Add(scope); }
        var tr = new TypeReference(ns, type, mod, scope);
        var mr = new MethodReference(name, ret, tr) { HasThis = hasThis };
        foreach (var p in ps) mr.Parameters.Add(new ParameterDefinition(p));
        return mr;
    }

    /// "Invoke" of a closed generic delegate type taken from a parameter (Action&lt;bool&gt;, Action&lt;ulong,bool&gt;, ...).
    static MethodReference DelegateInvoke(ModuleDefinition mod, TypeReference delegateType)
    {
        var git = delegateType as GenericInstanceType ?? throw new Exception("not a generic delegate: " + delegateType.FullName);
        var def = git.ElementType.Resolve() ?? throw new Exception("cannot resolve " + git.ElementType.FullName);
        var invoke = def.Methods.First(x => x.Name == "Invoke");
        var mr = new MethodReference("Invoke", mod.ImportReference(invoke.ReturnType), git) { HasThis = true };
        foreach (var p in invoke.Parameters) mr.Parameters.Add(new ParameterDefinition(p.ParameterType));
        return mod.ImportReference(mr);
    }

    /// Replaces every "call SteamManager::get_Initialized()" in a method with "true".
    static int SteamInitializedTrue(Ctx c, MethodDefinition m)
    {
        if (m == null) return 0;
        int k = 0;
        foreach (var ins in m.Body.Instructions)
            if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.DeclaringType.Name == "SteamManager" && mr.Name == "get_Initialized")
            {
                ins.OpCode = OpCodes.Ldc_I4_1;
                ins.Operand = null;
                k++;
            }
        if (k == 0) c.Fail($"{m.FullName}: no SteamManager.Initialized check found");
        return k;
    }

    // ------------------------------------------------------------------------------------------
    static int PatchGame(Ctx c)
    {
        var mod = c.Mod;
        int n = 0;

        // --- the developers' "NoSteam" mode (Unity Multiplayer Play Mode tag) is on: no Steam lobbies, rich presence,
        // auth tickets or SteamManager, a local user ID. AchievementManager keeps its normal path: the iOS platform
        // stores the stats and achievements that unlock badges and cosmetics.
        n += NoSteamTag(c, t => t.Name != "AchievementManager");

        // --- no server contact -------------------------------------------------------------------------
        // CloudAPI.CheckVersion (azurewebsites) never answers: NextLevelService then uses its built-in offline
        // fallback, which picks the daily island from the date like the server does.
        var cloud = mod.GetType("CloudAPI");
        n += MakeNoOp(c, cloud, "CheckVersion", 1);
        n += MakeNoOp(c, cloud, "VerifyLobby", 2);

        // NetworkingUtilities.ConnectToNetwork: unless a local (Wi-Fi) session is set up, go to Photon's offline
        // mode instead of the Photon cloud:  if (PeakShim.Net.UseOffline()) { PhotonNetwork.OfflineMode = true; return false; }
        var nu = mod.GetType("Peak.Network.NetworkingUtilities");
        var connect = Method(c, nu, "ConnectToNetwork", 1);
        var setOffline = mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Select(i => i.Operand as MethodReference).FirstOrDefault(r => r != null && r.Name == "set_OfflineMode" && r.DeclaringType.Name == "PhotonNetwork");
        if (connect != null && setOffline != null)
        {
            var il = connect.Body.GetILProcessor();
            var first = connect.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Call, c.ShimMethod("Net", "UseOffline")));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(first, il.Create(OpCodes.Call, setOffline));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            n++;
        }
        else if (setOffline == null) c.Fail("PhotonNetwork.set_OfflineMode not referenced");

        // --- touch stick: CharacterInput.GetMovementInput() returns PeakShim.VInput.TouchMove(<its value>), so the
        // on-screen stick moves the character analog (keyboard / gamepad input wins when it is not zero).
        var move = Method(c, mod.GetType("CharacterInput"), "GetMovementInput", 0);
        if (move != null)
        {
            var il = move.Body.GetILProcessor();
            var touchMove = c.ShimMethod("VInput", "TouchMove", "UnityEngine.Vector2");
            int k = 0;
            foreach (var ret in move.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
            {
                // insert before the ret by turning it into the call (branches to the ret land on the call)
                ret.OpCode = OpCodes.Call;
                ret.Operand = touchMove;
                il.InsertAfter(ret, il.Create(OpCodes.Ret));
                k++;
            }
            if (k == 0) c.Fail("GetMovementInput: no ret");
            n += k;
        }
        return n;
    }

    /// Every "CurrentPlayer.Tags.Contains("NoSteam")" (call get_Tags; ldstr; call Enumerable.Contains) in the types
    /// selected becomes "true".
    static int NoSteamTag(Ctx c, Func<TypeDefinition, bool> where)
    {
        int n = 0;
        foreach (var t in c.Mod.GetTypes())
        {
            var outer = t;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            if (!where(outer)) continue;
            foreach (var m in t.Methods.Where(x => x.HasBody))
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr || (string)ins.Operand != "NoSteam") continue;
                    var tags = ins.Previous;
                    var contains = ins.Next;
                    if (tags?.Operand is MethodReference tr && tr.Name == "get_Tags" && contains?.Operand is MethodReference cr && cr.Name == "Contains")
                    {
                        tags.OpCode = OpCodes.Nop; tags.Operand = null;
                        ins.OpCode = OpCodes.Nop; ins.Operand = null;
                        contains.OpCode = OpCodes.Ldc_I4_1; contains.Operand = null;
                        n++;
                    }
                    else c.Fail($"{m.FullName}: unexpected \"NoSteam\" pattern");
                }
        }
        if (n == 0) c.Fail("no NoSteam checks found");
        return n;
    }

    // Peak.CompressableLightMap.Decompress (LightVolume.Runtime): the baked light volume's BC6H / BC4 slices cannot be
    // read with GetPixels on iOS (and the game then logs an error per voxel). The body becomes
    //   DestroyLastLightMap(); var g = gridRes; Uncompressed = PeakShim.Lightmap.Decompress(rgbMapSlices, alphaChannelSlices, g.x, g.y, g.z);
    static int PatchLightmap(Ctx c)
    {
        var t = c.Mod.GetType("Peak.CompressableLightMap");
        var dec = Method(c, t, "Decompress", 0);
        var destroy = Method(c, t, "DestroyLastLightMap", 0);
        var getGrid = Method(c, t, "get_gridRes", 0);
        var setUnc = Method(c, t, "set_Uncompressed", 1);
        var rgb = Field(c, t, "rgbMapSlices");
        var alpha = Field(c, t, "alphaChannelSlices");
        if (dec == null || destroy == null || getGrid == null || setUnc == null || rgb == null || alpha == null) return 0;
        var int3 = getGrid.ReturnType;
        var int3Def = int3.Resolve();
        FieldReference F(string n) => c.Mod.ImportReference(int3Def.Fields.First(f => f.Name == n));
        dec.Body = new MethodBody(dec) { InitLocals = true };
        var g = new VariableDefinition(int3);
        dec.Body.Variables.Add(g);
        var il = dec.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, destroy);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getGrid); il.Emit(OpCodes.Stloc, g);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, rgb);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, alpha);
        il.Emit(OpCodes.Ldloca, g); il.Emit(OpCodes.Ldfld, F("x"));
        il.Emit(OpCodes.Ldloca, g); il.Emit(OpCodes.Ldfld, F("y"));
        il.Emit(OpCodes.Ldloca, g); il.Emit(OpCodes.Ldfld, F("z"));
        il.Emit(OpCodes.Call, c.ShimMethod("Lightmap", "Decompress", "UnityEngine.Texture2D[]", "UnityEngine.Texture2D[]", "System.Int32", "System.Int32", "System.Int32"));
        il.Emit(OpCodes.Call, setUnc);
        il.Emit(OpCodes.Ret);
        return 1;
    }

    // RenderGraphUtils.CanAddCopyPass(RenderGraph, TextureHandle, TextureHandle) (Core RP) starts with
    //   if (!PeakShim.Gfx.AllowCopyPass()) return false;
    // so AddBlitPass (FullScreenPassRendererFeature's colour copy) can fall back to a plain blit instead of the
    // framebuffer-fetch copy pass.
    static int PatchCopyPass(Ctx c)
    {
        var t = c.Mod.GetType("UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils");
        var m = Method(c, t, "CanAddCopyPass", 3);
        if (m == null) return 0;
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Call, c.ShimMethod("Gfx", "AllowCopyPass")));
        il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        return 1;
    }

    // UserManager (User.dll): no Epic Online Services login. The local user is "ios-local" and there are no
    // sanctions; Teardown has no online service to shut down.
    static int PatchUser(Ctx c)
    {
        var um = c.Mod.GetType("Portningsbolaget.Platforms.UserManager");
        var init = Method(c, um, "Initialize", 0);
        var user = Field(c, um, "s_user");
        if (init == null || user == null) return 0;
        var ctor = user.FieldType.Resolve()?.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 4);
        if (ctor == null) { c.Fail("EpicUser(string x4) missing"); return 0; }
        var il = NewBody(init);
        il.Emit(OpCodes.Ldstr, "ios-local");
        il.Emit(OpCodes.Call, c.ShimMethod("Identity", "get_NickName"));
        il.Emit(OpCodes.Ldstr, "offline");
        il.Emit(OpCodes.Ldstr, "offline");
        il.Emit(OpCodes.Newobj, c.Mod.ImportReference(ctor));
        il.Emit(OpCodes.Stsfld, user);
        il.Emit(OpCodes.Ret);
        return 1 + MakeNoOp(c, um, "Teardown", 0);
    }

    // ------------------------------------------------------------------------------------------
    static bool IsDroppedType(TypeReference t)
    {
        if (t == null) return false;
        if (t is GenericInstanceType git && git.GenericArguments.Any(IsDroppedType)) return true;
        if (t is TypeSpecification ts) return IsDroppedType(ts.ElementType);
        if (t.IsGenericParameter) return false;
        if (t.DeclaringType != null) return IsDroppedType(t.DeclaringType);
        return t.Scope is AssemblyNameReference a && DroppedNames.Contains(a.Name);
    }

    static bool IsDroppedMember(object operand) => operand switch
    {
        TypeReference t => IsDroppedType(t),
        MethodReference m => IsDroppedType(m.DeclaringType) || IsDroppedType(m.ReturnType) || m.Parameters.Any(p => IsDroppedType(p.ParameterType)) ||
                             (m is GenericInstanceMethod gim && gim.GenericArguments.Any(IsDroppedType)),
        FieldReference f => IsDroppedType(f.DeclaringType) || IsDroppedType(f.FieldType),
        _ => false,
    };

    /// Removes every use of Harmony / MonoMod: fields of their types go, a "newobj X; stsfld field" pair in a static
    /// constructor is cut out, and any other method touching them becomes "return default" (mod loading only).
    /// Then the assembly references themselves are dropped; anything left over is a failure.
    static int RemoveHarmony(Ctx c)
    {
        var mod = c.Mod;
        if (!mod.AssemblyReferences.Any(r => DroppedNames.Contains(r.Name))) return 0;
        int n = 0;
        foreach (var t in mod.GetTypes())
        {
            var deadFields = t.Fields.Where(f => IsDroppedType(f.FieldType)).ToList();
            foreach (var m in t.Methods.Where(m => m.HasBody))
            {
                var ins = m.Body.Instructions;
                // static field initialisers: <args...>; newobj Dropped::.ctor; stsfld deadField  -> removed
                if (m.IsConstructor && m.IsStatic)
                    for (int i = 0; i < ins.Count; i++)
                    {
                        if (ins[i].OpCode != OpCodes.Stsfld || !(ins[i].Operand is FieldReference fr) || !IsDroppedType(fr.FieldType)) continue;
                        int start = i - 1;
                        if (start < 0 || ins[start].OpCode != OpCodes.Newobj) { c.Fail($"{m.FullName}: unexpected init of {fr.Name}"); continue; }
                        var ctor = (MethodReference)ins[start].Operand;
                        start -= ctor.Parameters.Count;
                        if (start < 0 || ins.Skip(start).Take(ctor.Parameters.Count).Any(x => x.OpCode != OpCodes.Ldstr)) { c.Fail($"{m.FullName}: complex init of {fr.Name}"); continue; }
                        for (int k = i; k >= start; k--) ins.RemoveAt(k);
                        i = start - 1;
                        n++;
                    }
                bool uses = ins.Any(x => IsDroppedMember(x.Operand)) || m.Body.Variables.Any(v => IsDroppedType(v.VariableType));
                if (!uses) continue;
                if (m.IsConstructor) { c.Fail($"{m.FullName}: uses Harmony/MonoMod in a constructor"); continue; }
                var il = NewBody(m);
                foreach (var p in m.Parameters)
                    if (p.ParameterType is ByReferenceType br && p.IsOut) { il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Initobj, br.ElementType); }
                EmitDefault(m, il, m.ReturnType);
                il.Emit(OpCodes.Ret);
                Console.WriteLine("  harmony: stubbed " + m.FullName);
                n++;
            }
            foreach (var f in deadFields) { t.Fields.Remove(f); n++; }
            foreach (var p in t.Properties.Where(p => IsDroppedType(p.PropertyType)).ToList()) t.Properties.Remove(p);
        }
        // anything still pointing at the dropped assemblies (signatures, attributes, base types) is a failure
        foreach (var t in mod.GetTypes())
        {
            if (IsDroppedType(t.BaseType) || t.Interfaces.Any(i => IsDroppedType(i.InterfaceType))) c.Fail($"{t.FullName}: derives from Harmony/MonoMod");
            foreach (var m in t.Methods)
                if (IsDroppedType(m.ReturnType) || m.Parameters.Any(p => IsDroppedType(p.ParameterType))) c.Fail($"{m.FullName}: Harmony/MonoMod in signature");
            if (t.CustomAttributes.Any(a => IsDroppedType(a.AttributeType))) c.Fail($"{t.FullName}: Harmony attribute");
        }
        foreach (var r in mod.AssemblyReferences.Where(r => DroppedNames.Contains(r.Name)).ToList()) mod.AssemblyReferences.Remove(r);
        return n;
    }

    // PlatformManager: PLATFORM = new SteamRuntimeManager()  ->  new PeakPlatform.IOSPlatform()
    static int PatchManagers(Ctx c)
    {
        var ios = c.Platform.GetType("PeakPlatform.IOSPlatform");
        var ctor = ios?.Methods.FirstOrDefault(x => x.IsConstructor && !x.IsStatic && x.Parameters.Count == 0);
        if (ctor == null) { c.Fail("PeakPlatform.IOSPlatform() missing"); return 0; }
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (ins.OpCode != OpCodes.Newobj || mr.DeclaringType.Name != "SteamRuntimeManager") continue;
            ins.Operand = c.Mod.ImportReference(ctor);
            n++;
        }
        if (n != 1) c.Fail($"PlatformManager: expected 1 new SteamRuntimeManager(), found {n}");
        return n;
    }

    // PlatformUtility.LoadServerSettings(Action<bool> callback): callback?.Invoke(false) - nothing is downloaded.
    static int PatchPlatforms(Ctx c)
    {
        var pu = c.Mod.GetType("Portningsbolaget.Platforms.PlatformUtility");
        var m = Method(c, pu, "LoadServerSettings", 1);
        if (m == null) return 0;
        var il = NewBody(m);
        var done = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, done);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, DelegateInvoke(c.Mod, m.Parameters[0].ParameterType));
        il.Append(done);
        return 1;
    }

    // SteamManager.Awake: if (s_instance != null) { Destroy(gameObject); return; }
    //                    s_instance = this; DontDestroyOnLoad(gameObject); Debug.Log(...)   (m_bInitialized stays false)
    static int PatchSteamManager(Ctx c)
    {
        var mod = c.Mod;
        var steam = mod.GetType("SteamManager");
        var awake = Method(c, steam, "Awake", 0);
        var sInstance = Field(c, steam, "s_instance");
        if (awake == null || sInstance == null) return 0;
        var core = mod.AssemblyReferences.First(r => r.Name == "UnityEngine.CoreModule");
        var objType = new TypeReference("UnityEngine", "Object", mod, core);
        var ddol = new MethodReference("DontDestroyOnLoad", mod.TypeSystem.Void, objType);
        ddol.Parameters.Add(new ParameterDefinition(objType));
        var compType = new TypeReference("UnityEngine", "Component", mod, core);
        var getGo = new MethodReference("get_gameObject", new TypeReference("UnityEngine", "GameObject", mod, core), compType) { HasThis = true };
        var destroy = new MethodReference("Destroy", mod.TypeSystem.Void, objType);
        destroy.Parameters.Add(new ParameterDefinition(objType));
        var opIneq = new MethodReference("op_Inequality", mod.TypeSystem.Boolean, objType);
        opIneq.Parameters.Add(new ParameterDefinition(objType));
        opIneq.Parameters.Add(new ParameterDefinition(objType));
        var log = EngineMethod(mod, "UnityEngine.CoreModule", "UnityEngine", "Debug", "Log", mod.TypeSystem.Void, false, mod.TypeSystem.Object);

        var il = NewBody(awake);
        var cont = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Ldsfld, sInstance); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Call, opIneq);
        il.Emit(OpCodes.Brfalse, cont);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getGo); il.Emit(OpCodes.Call, destroy);
        il.Emit(OpCodes.Ret);
        il.Append(cont);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stsfld, sInstance);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getGo); il.Emit(OpCodes.Call, ddol);
        il.Emit(OpCodes.Ldstr, "[PEAK-iOS] Steamworks disabled on iOS");
        il.Emit(OpCodes.Call, log);
        il.Emit(OpCodes.Ret);
        return 1;
    }

    // Steamworks.NET: the wrappers' "is Steam initialised" checks pass (the native calls are default-returning
    // stubs), and the two identity calls the game uses directly report the local iOS player.
    static int PatchSteamworks(Ctx c)
    {
        var mod = c.Mod;
        int n = 0;
        var interop = mod.GetType("Steamworks.InteropHelp");
        n += MakeNoOp(c, interop, "TestIfAvailableClient", 0);
        n += MakeNoOp(c, interop, "TestIfAvailableGameServer", 0);

        var getId = Method(c, mod.GetType("Steamworks.SteamUser"), "GetSteamID", 0);
        var csid = mod.GetType("Steamworks.CSteamID");
        var csidCtor = csid?.Methods.FirstOrDefault(x => x.IsConstructor && !x.IsStatic && Sig(x) == "System.UInt64");
        if (getId != null && csidCtor != null)
        {
            var il = NewBody(getId);
            il.Emit(OpCodes.Call, c.ShimMethod("Identity", "get_UserID"));
            il.Emit(OpCodes.Newobj, csidCtor);
            il.Emit(OpCodes.Ret);
            n++;
        }
        else c.Fail("SteamUser.GetSteamID / CSteamID(ulong) missing");

        var getName = Method(c, mod.GetType("Steamworks.SteamFriends"), "GetPersonaName", 0);
        if (getName != null)
        {
            var il = NewBody(getName);
            il.Emit(OpCodes.Call, c.ShimMethod("Identity", "get_NickName"));
            il.Emit(OpCodes.Ret);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    // PhotonPeer.Connect(serverAddress, ...): if (!PeakShim.Net.AllowServer(serverAddress)) return false;
    // Every Photon connection (PUN, Realtime, Chat, Voice) goes through these, so nothing but this device or the
    // local network can ever be reached - Photon Cloud with the game's AppId included.
    static int PatchPhotonConnect(Ctx c)
    {
        var peer = c.Mod.GetType("ExitGames.Client.Photon.PhotonPeer");
        var guard = c.ShimMethod("Net", "AllowServer", "System.String");
        int n = 0;
        foreach (var m in peer?.Methods.Where(x => x.Name == "Connect" && x.HasBody && x.ReturnType.FullName == "System.Boolean") ?? Enumerable.Empty<MethodDefinition>())
        {
            if (m.Parameters.Count == 0 || m.Parameters[0].Name != "serverAddress") { c.Fail("unexpected " + m.FullName); continue; }
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Call, guard));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            n++;
        }
        if (n != 2) c.Fail($"PhotonPeer.Connect: expected 2 overloads, guarded {n}");
        return n;
    }

    // ------------------------------------------------------------------------------------------
    // The Addressables bundles (localization) are converted for iOS, so their bytes no longer match the CRCs in
    // the binary catalog and Unity would refuse to load them: AssetBundleRequestOptions.Crc reads as 0 (= no check).
    // ReflectionUtility (console commands at the first scene load) and SettingsHandler scan every type of every
    // assembly; under IL2CPP that blows the iOS launch watchdog on older phones. PeakShim.GameAssemblies keeps
    // only the assemblies that can hold the game's attributes.
    static int LimitAssemblyScan(Ctx c, string typeName)
    {
        var t = c.Mod.GetType(typeName);
        if (t == null) { c.Fail("LimitAssemblyScan: no type " + typeName); return 0; }
        var target = c.Mod.ImportReference(c.ShimMethod("GameAssemblies", "Get", "System.AppDomain"));
        int n = 0;
        foreach (var m in t.Methods.Concat(t.NestedTypes.SelectMany(x => x.Methods)))
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
                if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) && ins.Operand is MethodReference mr &&
                    mr.DeclaringType.FullName == "System.AppDomain" && mr.Name == "GetAssemblies" && mr.Parameters.Count == 0)
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = target;
                    n++;
                }
        }
        if (n == 0) c.Fail("LimitAssemblyScan: no GetAssemblies in " + typeName);
        // Console commands are static methods: GetMethods() (every public instance method, inherited ones too, so
        // ~150 per MonoBehaviour) becomes GetMethods(Public | Static | DeclaredOnly), which finds the same commands.
        var gm = t.Methods.FirstOrDefault(x => x.Name == "GetMethodsWithAttribute" && x.Parameters.Count == 0);
        if (gm != null)
        {
            gm.Body.SimplifyMacros(); // the inserted ldc must not leave short branches out of range
            var il = gm.Body.GetILProcessor();
            foreach (var ins in gm.Body.Instructions.ToList())
                if (ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference mr &&
                    mr.DeclaringType.FullName == "System.Type" && mr.Name == "GetMethods" && mr.Parameters.Count == 0)
                {
                    var def = mr.DeclaringType.Resolve().Methods.First(x => x.Name == "GetMethods" && x.Parameters.Count == 1 &&
                        x.Parameters[0].ParameterType.FullName == "System.Reflection.BindingFlags");
                    var withFlags = c.Mod.ImportReference(def);
                    il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4, 2 | 8 | 16));
                    ins.Operand = withFlags;
                    n++;
                }
            gm.Body.OptimizeMacros();
        }
        return n;
    }

    static int PatchBundleCrc(Ctx c)
    {
        var t = c.Mod.GetType("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions");
        var g = Method(c, t, "get_Crc", 0);
        if (g == null) return 0;
        var il = NewBody(g);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        return 1;
    }

    // ------------------------------------------------------------------------------------------
    // No Burst-compiled native code exists for iOS (lib_burst_generated is a Windows DLL), so make Burst
    // report itself disabled: jobs and function pointers then run their managed (IL2CPP) code.
    static int PatchBurst(Ctx c)
    {
        int n = 0;
        var compiler = c.Mod.GetType("Unity.Burst.BurstCompiler");
        var isEnabled = compiler?.Methods.FirstOrDefault(m => m.Name == "get_IsEnabled" && m.IsStatic);
        if (isEnabled == null) c.Fail("BurstCompiler.IsEnabled missing");
        else { var il = NewBody(isEnabled); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); n++; }
        var opts = c.Mod.GetType("Unity.Burst.BurstCompilerOptions");
        foreach (var name in new[] { "get_EnableBurstCompilation", "get_IsEnabled" })
        {
            var g = opts?.Methods.FirstOrDefault(m => m.Name == name && !m.IsStatic);
            if (g == null) continue;
            var il = NewBody(g); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    /// Fails if a patched assembly still references something that cannot work on iOS or would contact a server.
    static int Verify(string dir)
    {
        int problems = 0;
        void Problem(string s) { Console.WriteLine("  VERIFY: " + s); problems++; }
        string[] forbiddenUrls = { "azurewebsites.net", "portservice", "steampowered.com", "steamcommunity.com" };
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name == ShimName || name == PlatformAsmName || name == "PeakIOS") continue;
            var mod = ModuleDefinition.ReadModule(path);
            foreach (var r in mod.AssemblyReferences)
            {
                if (DroppedNames.Contains(r.Name)) Problem($"{name}: still references dropped assembly {r.Name}");
                // everything referenced must ship in the plugins folder or come with Unity (engine modules, BCL)
                bool engine = r.Name.StartsWith("UnityEngine") || r.Name.StartsWith("System") || r.Name == "mscorlib" ||
                              r.Name == "netstandard" || (r.Name.StartsWith("Mono.") && !r.Name.StartsWith("Mono.Cecil")) || r.Name.StartsWith("Microsoft.") || r.Name == "Accessibility";
                if (!engine && !File.Exists(Path.Combine(dir, r.Name + ".dll")) && !(r.Name == "PeakIOS"))
                    Problem($"{name}: references {r.Name}, which is not in the build");
            }
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (m.IsPInvokeImpl && m.PInvokeInfo != null && m.PInvokeInfo.Module.Name != "__Internal")
                        Problem($"{name}: P/Invoke to {m.PInvokeInfo.Module.Name} in {m.FullName}");
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && forbiddenUrls.Any(u => s.Contains(u)) && IsReachableServerCall(m))
                            Problem($"{name}: server URL \"{s}\" still used in {m.FullName}");
                        if (!(ins.Operand is MethodReference mr)) continue;
                        string dt = mr.DeclaringType.FullName;
                        if (dt == "System.Diagnostics.Process" && (mr.Name == "Start" || mr.Name == "GetProcessesByName"))
                            Problem($"{name}: process launch left in {m.FullName}");
                        if (dt == "UnityEngine.Cursor" && (mr.Name == "set_lockState" || mr.Name == "set_visible"))
                            Problem($"{name}: Cursor.{mr.Name} left in {m.FullName}");
                        if (dt == "UnityEngine.Input" && mr.Name != ".ctor" && InputRedirect.Contains(name))
                            Problem($"{name}: Input.{mr.Name} left in {m.FullName}");
                        if (dt == "UnityEngine.PlayerPrefs" || dt == "UnityEngine.Microphone")
                            Problem($"{name}: {dt}.{mr.Name} left in {m.FullName}");
                        if (ins.OpCode == OpCodes.Newobj && mr.DeclaringType.Name == "SteamRuntimeManager")
                            Problem($"{name}: SteamRuntimeManager still created in {m.FullName}");
                    }
                }
        }
        Console.WriteLine(problems == 0 ? "verify: OK" : $"verify: {problems} problem(s)");
        return problems == 0 ? 0 : 4;
    }

    // Server URLs are allowed in code the patcher has cut off from every caller.
    static bool IsReachableServerCall(MethodDefinition m)
    {
        string t = m.DeclaringType.FullName;
        // CheckVersionHandler.CheckVersion and PlatformUtility.LoadServerSettings are no-ops: their coroutines never run
        if (t.StartsWith("CloudAPI") || t.StartsWith("Portningsbolaget.Platforms.PlatformUtility")) return false;
        return true;
    }
}
