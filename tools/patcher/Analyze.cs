using Mono.Cecil;
using Mono.Cecil.Cil;

// `PeakPatcher analyze <managedDir>`: lists, per assembly, its references and every member it uses from
// APIs the iOS port has to care about (Input, Process, Registry, Application, Screen, ...).
static partial class Analyze
{
    // `PeakPatcher pinvokes <managedDir>`: every P/Invoke, grouped by assembly and native library.
    public static int PInvokes(string dir)
    {
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("UnityEngine.") && name != "UnityEngine.UI.dll") continue;
            if (name.StartsWith("System.") || name.StartsWith("Mono.") || name == "mscorlib.dll" || name == "netstandard.dll") continue;
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(path); } catch { continue; }
            var libs = asm.MainModule.GetTypes().SelectMany(t => t.Methods)
                .Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null)
                .GroupBy(m => m.PInvokeInfo.Module.Name).OrderBy(g => g.Key);
            foreach (var g in libs)
                Console.WriteLine($"{name,-40} {g.Key,-28} {g.Count(),5}  e.g. {string.Join(", ", g.Take(3).Select(m => m.PInvokeInfo.EntryPoint))}");
        }
        return 0;
    }

    static readonly string[] Interesting =
    {
        "UnityEngine.Input", "UnityEngine.Cursor", "UnityEngine.Application", "UnityEngine.Screen",
        "UnityEngine.QualitySettings", "UnityEngine.TouchScreenKeyboard", "System.Diagnostics.Process",
        "Microsoft.Win32.", "System.Windows.Forms.", "System.Reflection.Emit.", "HarmonyLib.",
        "System.Net.Sockets.", "System.IO.Pipes.", "System.Threading.Thread",
    };

    public static int Run(string dir)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("UnityEngine.") && name != "UnityEngine.UI.dll") continue;
            if (name.StartsWith("System.") || name.StartsWith("Mono.") || name == "mscorlib.dll" || name == "netstandard.dll") continue;
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }); }
            catch (Exception e) { Console.WriteLine($"== {name}: unreadable ({e.Message})"); continue; }
            var mod = asm.MainModule;
            Console.WriteLine($"== {name}  refs: {string.Join(", ", mod.AssemblyReferences.Select(r => r.Name))}");
            var uses = new SortedDictionary<string, int>();
            int pinv = 0;
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (m.IsPInvokeImpl) pinv++;
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (!(ins.Operand is MemberReference mr) || mr.DeclaringType == null) continue;
                        string dt = mr.DeclaringType.FullName;
                        if (!Interesting.Any(p => dt == p || (p.EndsWith(".") && dt.StartsWith(p)))) continue;
                        string key = dt + "::" + mr.Name + (mr is MethodReference meth ? "(" + string.Join(",", meth.Parameters.Select(p => p.ParameterType.Name)) + ")" : "");
                        uses[key] = uses.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                }
            if (pinv > 0) Console.WriteLine($"   pinvokes: {pinv}");
            foreach (var kv in uses) Console.WriteLine($"   {kv.Value,4}  {kv.Key}");
        }
        return 0;
    }

    // `PpgPatcher calls <dll> <substring>`: every call site whose callee's full name contains the substring.
    // `PpgPatcher dumpil <dll> <substring>`: prints the IL of every method whose full name contains the substring.
    public static int DumpIL(string path, string what)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var mod = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }).MainModule;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.FullName.Contains(what)) continue;
                Console.WriteLine($"== {m.FullName}  body={m.HasBody} rva={m.RVA} impl={m.ImplAttributes}");
                if (!m.HasBody) continue;
                Console.WriteLine($"   maxstack={m.Body.MaxStackSize} locals={m.Body.Variables.Count} initlocals={m.Body.InitLocals}");
                foreach (var ins in m.Body.Instructions) Console.WriteLine("   " + ins);
            }
        return 0;
    }

    // `PpgPatcher types <dir> <substring>`: lists the types (and type forwarders) matching the substring in every dll.
    public static int Types(string dir, string what)
    {
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            ModuleDefinition mod;
            try { mod = ModuleDefinition.ReadModule(path); } catch { continue; }
            foreach (var t in mod.GetTypes())
                if (t.FullName.Contains(what)) Console.WriteLine($"{Path.GetFileName(path)}: {t.FullName}");
            foreach (var e in mod.ExportedTypes)
                if (e.FullName.Contains(what)) Console.WriteLine($"{Path.GetFileName(path)}: forwarder {e.FullName} -> {e.Scope}");
        }
        return 0;
    }

    // `PpgPatcher stubmethods <dll> <substring> <out.dll>`: replaces matching method bodies with `throw null` (experiments).
    public static int StubMethods(string path, string what, string outPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
        foreach (var t in asm.MainModule.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || !m.FullName.Contains(what)) continue;
                m.Body.Instructions.Clear(); m.Body.ExceptionHandlers.Clear(); m.Body.Variables.Clear();
                var il = m.Body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldnull); il.Emit(Mono.Cecil.Cil.OpCodes.Throw);
                Console.WriteLine("stubbed " + m.FullName);
            }
        asm.Write(outPath);
        return 0;
    }

    // `PpgPatcher memberrefs <dir> <substring>`: every MemberRef / MethodSpec in every dll matching the substring.
    public static int MemberRefs(string dir, string what)
    {
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            ModuleDefinition mod;
            try { mod = ModuleDefinition.ReadModule(path); } catch { continue; }
            foreach (var r in mod.GetMemberReferences())
                if (r.FullName.Contains(what)) Console.WriteLine($"{Path.GetFileName(path)}: memberref {r.FullName}");
            for (uint i = 1; ; i++)
            {
                var spec = mod.LookupToken(new MetadataToken(TokenType.MethodSpec, i)) as MemberReference;
                if (spec == null) break;
                if (spec.FullName.Contains(what)) Console.WriteLine($"{Path.GetFileName(path)}: methodspec {spec.FullName}");
            }
        }
        return 0;
    }

    public static int Calls(string path, string what)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        var mod = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }).MainModule;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                    if (ins.Operand is MethodReference mr && mr.FullName.Contains(what))
                        Console.WriteLine($"{m.FullName}  ->  {ins.OpCode} {mr.FullName}");
            }
        return 0;
    }

    // `PpgPatcher stackcheck <dll>...`: simulates the IL evaluation stack of every method and reports underflow,
    // mismatched depths at branch targets and non-empty stacks at ret - the kind of IL IL2CPP refuses.
    public static int StackCheck(string[] paths)
    {
        int bad = 0;
        foreach (var path in paths)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var mod = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }).MainModule;
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    string err = CheckMethod(m);
                    if (err != null) { bad++; Console.WriteLine($"{Path.GetFileName(path)}: {m.FullName}: {err}"); }
                }
        }
        Console.WriteLine($"{bad} method(s) with stack problems");
        return bad == 0 ? 0 : 2;
    }

    static string CheckMethod(MethodDefinition m)
    {
        var body = m.Body;
        var depth = new Dictionary<Instruction, int>();
        var work = new Stack<Instruction>();
        void Visit(Instruction i, int d, string from)
        {
            if (depth.TryGetValue(i, out int old)) { if (old != d) throw new Exception($"depth {old} vs {d} at IL_{i.Offset:x4} (from {from})"); return; }
            depth[i] = d; work.Push(i);
        }
        try
        {
            Visit(body.Instructions[0], 0, "entry");
            foreach (var h in body.ExceptionHandlers)
            {
                if (h.HandlerStart != null) Visit(h.HandlerStart, h.HandlerType == ExceptionHandlerType.Catch || h.HandlerType == ExceptionHandlerType.Filter ? 1 : 0, "handler");
                if (h.FilterStart != null) Visit(h.FilterStart, 1, "filter");
            }
            while (work.Count > 0)
            {
                var i = work.Pop();
                int d = depth[i];
                int pop = Pops(i, m), push = Pushes(i);
                if (pop < 0) d = 0; else { if (d < pop) return $"stack underflow at IL_{i.Offset:x4} {i.OpCode} (have {d}, need {pop})"; d -= pop; }
                d += push;
                var fc = i.OpCode.FlowControl;
                if (i.OpCode.Code == Code.Ret && d != 0) return $"{d} item(s) left on stack at ret IL_{i.Offset:x4}";
                if (i.Operand is Instruction target) Visit(target, i.OpCode.Code == Code.Leave || i.OpCode.Code == Code.Leave_S ? 0 : d, $"IL_{i.Offset:x4}");
                if (i.Operand is Instruction[] targets) foreach (var tg in targets) Visit(tg, d, $"IL_{i.Offset:x4}");
                if (fc != FlowControl.Branch && fc != FlowControl.Return && fc != FlowControl.Throw && i.Next != null)
                    Visit(i.Next, d, $"IL_{i.Offset:x4}");
            }
        }
        catch (Exception e) { return e.Message; }
        return null;
    }

    static int Pops(Instruction i, MethodDefinition m)
    {
        var op = i.OpCode;
        if (op.FlowControl == FlowControl.Call && i.Operand is IMethodSignature sig)
        {
            int n = sig.Parameters.Count;
            if (op.Code == Code.Newobj) return n;
            if (sig.HasThis && !sig.ExplicitThis) n++;
            if (op.Code == Code.Calli) n++;
            return n;
        }
        if (op.Code == Code.Ret) return m.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
        switch (op.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
            case StackBehaviour.PopAll: return -1;
        }
        return 0;
    }

    static int Pushes(Instruction i)
    {
        var op = i.OpCode;
        if (op.FlowControl == FlowControl.Call && i.Operand is IMethodSignature sig)
            return op.Code == Code.Newobj || sig.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
        switch (op.StackBehaviourPush)
        {
            case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8: case StackBehaviour.Pushr4:
            case StackBehaviour.Pushr8: case StackBehaviour.Pushref: return 1;
            case StackBehaviour.Push1_push1: return 2;
        }
        return 0;
    }
}

