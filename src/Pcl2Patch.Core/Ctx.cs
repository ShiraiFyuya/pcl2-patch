using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Pcl2Patch;

/// <summary>Thrown when the input assembly does not look like the build we know how to patch.</summary>
public sealed class PatchException : Exception
{
    public PatchException(string message) : base(message) { }
}

/// <summary>Metadata / assembly access with forgiving resolution.</summary>
public sealed class Ctx : IDisposable
{
    public AssemblyDefinition Asm { get; private set; }
    public ModuleDefinition Mod => Asm.MainModule;
    public string InputPath { get; private set; }

    Ctx() { }

    public static Ctx Open(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        foreach (var dir in FrameworkDirs())
            resolver.AddSearchDirectory(dir);

        var parameters = new ReaderParameters
        {
            ReadSymbols = false,
            MetadataResolver = new LooseResolver(resolver),
        };

        return new Ctx
        {
            InputPath = path,
            Asm = AssemblyDefinition.ReadAssembly(path, parameters),
        };
    }

    static IEnumerable<string> FrameworkDirs()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) yield break;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var arch in new[] { "Framework64", "Framework" })
        {
            var root = Path.Combine(win, "Microsoft.NET", arch, "v4.0.30319");
            if (Directory.Exists(root)) yield return root;
            var wpf = Path.Combine(root, "WPF");
            if (Directory.Exists(wpf)) yield return wpf;
        }
    }

    // ---------------------------------------------------------------- lookup
    public TypeDefinition Type(string fullName)
    {
        var t = Mod.GetType(fullName);
        if (t == null) throw new PatchException($"missing type: {fullName}");
        return t;
    }

    public TypeDefinition TypeOrNull(string fullName) => Mod.GetType(fullName);

    public MethodDefinition Method(string typeFullName, string name, int? paramCount = null)
    {
        var t = Type(typeFullName);
        var m = t.Methods.FirstOrDefault(x => x.Name == name && (paramCount == null || x.Parameters.Count == paramCount));
        if (m == null) throw new PatchException($"missing method: {typeFullName}::{name}({(paramCount?.ToString() ?? "*")} params)");
        if (m.Body == null) throw new PatchException($"method has no body: {typeFullName}::{name}");
        return m;
    }

    public MethodReference MemberRef(string name, Func<MethodReference, bool> extra)
    {
        var r = Mod.GetMemberReferences().OfType<MethodReference>().FirstOrDefault(m => m.Name == name && extra(m));
        if (r == null) throw new PatchException($"missing member reference: {name}");
        return r;
    }

    public void Write(string outPath)
    {
        Asm.Write(outPath);
    }

    public void Dispose()
    {
        try { Mod.Dispose(); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------- helpers
    public static Instruction First(MethodDefinition m) => m.Body.Instructions[0];

    public static void InsertBeforeAll(MethodDefinition m, Instruction target, IEnumerable<Instruction> seq)
    {
        var il = m.Body.GetILProcessor();
        foreach (var i in seq) il.InsertBefore(target, i);
    }

    public static void BumpStack(MethodDefinition m, int atLeast)
    {
        if (m.Body.MaxStackSize < atLeast) m.Body.MaxStackSize = atLeast;
    }

    /// <summary>True when an instruction outside [start,end] branches into the range.</summary>
    public static bool HasOutsideBranchInto(MethodDefinition m, Instruction start, Instruction end)
    {
        var inside = new HashSet<Instruction>();
        for (var i = start; i != null; i = i.Next)
        {
            inside.Add(i);
            if (i == end) break;
        }
        foreach (var ins in m.Body.Instructions)
        {
            if (inside.Contains(ins)) continue;
            var target = ins.Operand as Instruction;
            if (target != null && inside.Contains(target)) return true;
            if (ins.Operand is Instruction[] targets && targets.Any(inside.Contains)) return true;
        }
        foreach (var h in m.Body.ExceptionHandlers)
        {
            if (h.TryStart != null && inside.Contains(h.TryStart)) return true;
            if (h.HandlerStart != null && inside.Contains(h.HandlerStart)) return true;
        }
        return false;
    }
}

/// <summary>
/// Cecil resolves referenced assemblies when writing metadata (e.g. enum types behind
/// optional parameters). PCL2 ships several merged assemblies that do not exist on disk,
/// so unresolved references are stubbed out as enums instead of aborting the write.
/// </summary>
public sealed class LooseResolver : IMetadataResolver
{
    static readonly ModuleDefinition StubModule = ModuleDefinition.CreateModule("stubs", ModuleKind.Dll);
    readonly IAssemblyResolver _assemblies;

    public LooseResolver(IAssemblyResolver assemblies) => _assemblies = assemblies;

    TypeDefinition Stub(TypeReference type)
    {
        var ns = type.Namespace ?? string.Empty;
        var full = ns.Length == 0 ? type.Name : ns + "." + type.Name;
        var existing = StubModule.GetType(full);
        if (existing != null) return existing;

        var td = new TypeDefinition(ns, type.Name,
            TypeAttributes.Public | TypeAttributes.Sealed,
            StubModule.ImportReference(typeof(Enum)));
        td.Fields.Add(new FieldDefinition("value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            StubModule.TypeSystem.Int32));
        StubModule.Types.Add(td);
        return td;
    }

    public TypeDefinition Resolve(TypeReference type)
    {
        TypeReference element;
        try { element = type.GetElementType(); }
        catch { return Stub(type); }

        try
        {
            ModuleDefinition module = null;
            switch (element.Scope)
            {
                case AssemblyNameReference anr:
                    module = _assemblies.Resolve(anr)?.MainModule;
                    break;
                case ModuleDefinition md:
                    module = md;
                    break;
            }
            var found = module?.GetType(element.FullName);
            if (found != null) return found;
        }
        catch
        {
            // fall through to the stub
        }
        return Stub(element);
    }

    public FieldDefinition Resolve(FieldReference field)
    {
        var td = Resolve(field.DeclaringType);
        return td?.Fields.FirstOrDefault(f => f.Name == field.Name);
    }

    public MethodDefinition Resolve(MethodReference method)
    {
        var td = Resolve(method.DeclaringType);
        return td?.Methods.FirstOrDefault(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count);
    }
}
