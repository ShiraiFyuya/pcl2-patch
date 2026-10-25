using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Pcl2Patch;

public sealed class PatchOptions
{
    /// <summary>不碰启动器自更新。</summary>
    public bool KeepAutoUpdate;

    /// <summary>不改"整合包版本行点击 -> 安装"。</summary>
    public bool NoRowClick;

    /// <summary>任一补丁失败就整体中止，不写文件。</summary>
    public bool Strict;
}

public enum PatchState { Applied, Already, Skipped, Failed }

public sealed class PatchOutcome
{
    public string Id;
    public string Title;
    public PatchState State;
    public string Detail;
}

// ---------------------------------------------------------------------------
// 设计约定：
//   * 每个补丁都写成 f(Ctx, Options, dryRun)。
//     dryRun=true  只定位 + 校验，不改任何东西（doctor 和"打完后回读校验"复用同一段逻辑）。
//     dryRun=false 在已验证过的位置上做修改。
//   * 定位只用「类型名 / 方法名 / 签名 / IL 结构 / 数据流」，不含任何字节、偏移或序号。
//   * 打完后重新加载文件，把同一批函数再跑一遍 dryRun，全部应返回 Already —— 这就是自检。
// ---------------------------------------------------------------------------
public static class Patches
{
    public static readonly (string Id, string Title)[] Catalog =
    {
        ("download",   "Minecraft 版本下载"),
        ("nav",        "下载入口"),
        ("entries",    "其余下载入口"),
        ("modpack",    "整合包安装分派"),
        ("rowclick",   "整合包版本行点击"),
        ("silent",     "静默自动更新"),
        ("updatemode", "更新方式默认值"),
    };

    public static PatchOutcome[] Run(Ctx c, PatchOptions o, bool dry)
    {
        var list = new List<PatchOutcome>
        {
            Download(c, o, dry),
            Modpack(c, o, dry),
        };

        var ver = c.Asm.Name.Version;
        list.Add(ver >= new Version(2, 99, 0, 1)
            ? DownloadNav(c, o, dry)
            : Skip("nav", "下载入口", "该版本无此改动"));

        list.Add(ver >= new Version(2, 99, 0, 1)
            ? OtherEntries(c, o, dry)
            : Skip("entries", "其余下载入口", "该版本无此改动"));

        list.Add(o.NoRowClick
            ? Skip("rowclick", "整合包版本行点击", "--no-row-click")
            : RowClick(c, o, dry));

        if (o.KeepAutoUpdate)
        {
            list.Add(Skip("silent", "静默自动更新", "--keep-autoupdate"));
            list.Add(Skip("updatemode", "更新方式默认值", "--keep-autoupdate"));
        }
        else
        {
            list.Add(SilentUpdate(c, o, dry));
            list.Add(UpdateMode(c, o, dry));
        }


        return list.ToArray();
    }

    static PatchOutcome Ok(string id, string title, PatchState st, string detail) =>
        new() { Id = id, Title = title, State = st, Detail = detail };

    static PatchOutcome Skip(string id, string title, string why) =>
        new() { Id = id, Title = title, State = PatchState.Skipped, Detail = "跳过（" + why + "）" };

    static PatchOutcome Fail(string id, string title, string why) =>
        new() { Id = id, Title = title, State = PatchState.Failed, Detail = why };

    // =======================================================================
    // 1. Minecraft 版本下载
    //    PageDownloadInstall.LoaderInit 把 LoaderTask 桩换成真正的 DlClientListLoader
    // =======================================================================
    static PatchOutcome Download(Ctx c, PatchOptions o, bool dry)
    {
        const string id = "download", title = "Minecraft 版本下载";
        try
        {
            var m = c.Method("PCL.PageDownloadInstall", "LoaderInit", 0);

            var field = c.Type("PCL.ModDownload").Fields.FirstOrDefault(f => f.Name == "DlClientListLoader")
                ?? throw new PatchException("缺少 ModDownload::DlClientListLoader");

            if (m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference fr && fr.Name == field.Name))
                return Ok(id, title, PatchState.Already, "已是解锁状态");

            var call = m.Body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "PageLoaderInit")
                ?? throw new PatchException("找不到 PageLoaderInit 调用");

            var end = m.Body.Instructions.LastOrDefault(i =>
                    i.Offset < call.Offset
                    && i.OpCode == OpCodes.Newobj
                    && i.Operand is MethodReference cm
                    && cm.Name == ".ctor"
                    && cm.DeclaringType.Name.StartsWith("LoaderTask`2", StringComparison.Ordinal))
                ?? throw new PatchException("PageLoaderInit 前找不到 LoaderTask<,> 构造");

            var start = m.Body.Instructions.LastOrDefault(i => i.Offset < end.Offset && i.OpCode == OpCodes.Ldstr)
                ?? throw new PatchException("找不到任务名常量");

            if (start.Previous == null || start.Previous.OpCode != OpCodes.Ldnull)
                throw new PatchException("参数布局与预期不符（任务名前不是 ldnull）");

            if (Ctx.HasOutsideBranchInto(m, start, end))
                throw new PatchException("待替换区间被外部跳转引用");

            var ctor = (MethodReference)end.Operand;
            if (field.FieldType.FullName != ctor.DeclaringType.FullName)
                throw new PatchException($"字段类型不匹配（{field.FieldType.FullName}）");

            if (dry) return Ok(id, title, PatchState.Applied, $"可定位（替换 {(end.Next != null ? end.Next.Offset : end.Offset + 5) - start.Offset} 字节区间）");

            var il = m.Body.GetILProcessor();
            var range = new List<Instruction>();
            for (var i = start; i != null; i = i.Next)
            {
                range.Add(i);
                if (i == end) break;
            }

            il.InsertBefore(start, Instruction.Create(OpCodes.Ldsfld, field));
            foreach (var ins in range) il.Remove(ins);
            Ctx.BumpStack(m, 2);

            return Ok(id, title, PatchState.Applied, $"{range.Count} 条桩指令 -> ldsfld {field.Name}");
        }
        catch (PatchException ex) { return Fail(id, title, ex.Message); }
    }

    // =======================================================================
    // 2. 整合包安装分派
    // =======================================================================
    const string DispatchName = "DispatchModpackInstall";

    static readonly (string Installer, int Key)[] DispatchTable =
    {
        ("InstallPackCurseForge", 0),
        ("InstallPackHMCL", 1),
        ("InstallPackMMC", 2),
        ("InstallPackMCBBS", 3),
        ("InstallPackModrinth", 4),
        ("InstallPackLauncherPack", 9),
    };

    static PatchOutcome Modpack(Ctx c, PatchOptions o, bool dry)
    {
        const string id = "modpack", title = "整合包安装分派";
        try
        {
            var type = c.Type("PCL.ModModpack");
            var target = c.Method("PCL.ModModpack", "ModpackInstall", 3);

            if (type.Methods.Any(x => x.Name == DispatchName))
                return Ok(id, title, PatchState.Already, "分派方法已存在");

            var throwIns = target.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Throw)
                ?? throw new PatchException("ModpackInstall 里找不到 throw");
            var newobj = throwIns.Previous;
            var message = newobj?.Previous;

            if (newobj == null || newobj.OpCode != OpCodes.Newobj
                || !(newobj.Operand is MethodReference exCtor)
                || exCtor.DeclaringType.FullName != "System.Exception"
                || exCtor.Parameters.Count != 1
                || message == null || message.OpCode != OpCodes.Ldstr)
                throw new PatchException("结尾不是 throw new Exception(\"…\")，结构已变");

            foreach (var entry in DispatchTable)
                if (type.Methods.All(x => x.Name != entry.Installer))
                    throw new PatchException($"缺少安装器 {entry.Installer}");

            if (type.Methods.All(x => x.Name != "InstallPackCompress"))
                throw new PatchException("缺少 InstallPackCompress");

            // 数据流定位局部变量
            var zipCall = target.Body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "OpenZip")
                ?? throw new PatchException("找不到 FileUtils::OpenZip 调用");
            var archive = Il.StoreTarget(target.Body, zipCall.Next)
                ?? throw new PatchException("OpenZip 的返回值没有被存进局部变量");

            var emptyLiteral = target.Body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Length == 0
                    && Il.StoreTarget(target.Body, i.Next)?.VariableType.FullName == "System.String")
                ?? throw new PatchException("找不到 baseFolder 初始化");
            var baseFolder = Il.StoreTarget(target.Body, emptyLiteral.Next);

            var minusOne = target.Body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Ldc_I4_M1 && Il.StoreTarget(target.Body, i.Next) != null)
                ?? throw new PatchException("找不到 num 初始化");
            var num = Il.StoreTarget(target.Body, minusOne.Next);

            var result = target.Body.Variables.FirstOrDefault(v => v.VariableType.Name == "LoaderCombo`1")
                ?? throw new PatchException("找不到 LoaderCombo 结果局部变量");

            if (dry)
                return Ok(id, title, PatchState.Applied,
                    $"可定位（archive={archive.Index}, base={baseFolder.Index}, num={num.Index}, result={result.Index}）");

            var dispatcher = BuildDispatcher(c, type, result.VariableType);
            Rewire(c, target, dispatcher, message, newobj, throwIns, archive, baseFolder, num, result);

            return Ok(id, title, PatchState.Applied,
                $"ModpackInstall -> {DispatchName}（{DispatchTable.Length} 个分支 + 默认）");
        }
        catch (PatchException ex) { return Fail(id, title, ex.Message); }
    }

    static MethodDefinition BuildDispatcher(Ctx c, TypeDefinition type, TypeReference comboType)
    {
        var sample = type.Methods.First(m => m.Name == DispatchTable[0].Installer);
        var pt = sample.Parameters.Select(p => p.ParameterType).ToArray();

        var d = new MethodDefinition(DispatchName,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, comboType);
        d.Parameters.Add(new ParameterDefinition("file", ParameterAttributes.None, pt[0]));
        d.Parameters.Add(new ParameterDefinition("archive", ParameterAttributes.None, pt[1]));
        d.Parameters.Add(new ParameterDefinition("baseFolder", ParameterAttributes.None, pt[2]));
        d.Parameters.Add(new ParameterDefinition("num", ParameterAttributes.None, c.Mod.TypeSystem.Int32));
        d.Parameters.Add(new ParameterDefinition("instanceName", ParameterAttributes.None, pt[3]));
        d.Parameters.Add(new ParameterDefinition("logo", ParameterAttributes.None, pt[4]));
        type.Methods.Add(d);

        d.Body = new MethodBody(d) { MaxStackSize = 6 };
        var il = d.Body.GetILProcessor();

        // 门禁：只允许带实例名（= PCL 内下载）的安装。本地导入永远不放行。
        {
            var allow = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[4]));
            il.Append(Instruction.Create(OpCodes.Call,
                c.MemberRef("IsNullOrEmpty", r => r.DeclaringType.Name == "String")));
            il.Append(Instruction.Create(OpCodes.Brfalse, allow));
            il.Append(Instruction.Create(OpCodes.Ldstr,
                "此构建保留了本地导入限制：只允许安装通过 PCL 下载的整合包。"));
            il.Append(Instruction.Create(OpCodes.Newobj,
                c.MemberRef(".ctor", r => r.DeclaringType.FullName == "System.Exception" && r.Parameters.Count == 1)));
            il.Append(Instruction.Create(OpCodes.Throw));
            il.Append(allow);
        }

        var labels = new Instruction[DispatchTable.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = Instruction.Create(OpCodes.Nop);

        for (int i = 0; i < DispatchTable.Length; i++)
        {
            il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[3]));
            il.Append(Instruction.Create(OpCodes.Ldc_I4, DispatchTable[i].Key));
            il.Append(Instruction.Create(OpCodes.Beq, labels[i]));
        }

        var compress = type.Methods.First(m => m.Name == "InstallPackCompress");
        il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[0]));
        il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Call, compress));
        il.Append(Instruction.Create(OpCodes.Ret));

        for (int i = 0; i < DispatchTable.Length; i++)
        {
            var installer = type.Methods.First(m => m.Name == DispatchTable[i].Installer);
            il.Append(labels[i]);
            il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[1]));
            il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[2]));
            if (installer.Parameters.Count >= 4) il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[4]));
            if (installer.Parameters.Count >= 5) il.Append(Instruction.Create(OpCodes.Ldarg, d.Parameters[5]));
            il.Append(Instruction.Create(OpCodes.Call, installer));
            il.Append(Instruction.Create(OpCodes.Ret));
        }

        return d;
    }

    static void Rewire(Ctx c, MethodDefinition target, MethodDefinition dispatcher,
        Instruction message, Instruction newobj, Instruction throwIns,
        VariableDefinition archive, VariableDefinition baseFolder, VariableDefinition num, VariableDefinition result)
    {
        var tail = Instruction.Create(OpCodes.Ldloc, result);
        var tailRet = Instruction.Create(OpCodes.Ret);

        var seq = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldloc, archive),
            Instruction.Create(OpCodes.Ldloc, baseFolder),
            Instruction.Create(OpCodes.Ldloc, num),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldarg_2),
            Instruction.Create(OpCodes.Call, dispatcher),
            Instruction.Create(OpCodes.Stloc, result),
            Instruction.Create(OpCodes.Leave, tail),
        };

        Ctx.InsertBeforeAll(target, message, seq);

        var catchHandler = target.Body.ExceptionHandlers.FirstOrDefault(h => h.HandlerEnd == message);
        if (catchHandler != null) catchHandler.HandlerEnd = seq[0];

        var finallyHandler = target.Body.ExceptionHandlers.FirstOrDefault(h =>
            h.HandlerType == ExceptionHandlerType.Finally && h.HandlerEnd == null);
        if (finallyHandler != null) finallyHandler.HandlerEnd = tail;

        var il = target.Body.GetILProcessor();
        il.Remove(message);
        il.Remove(newobj);
        il.Remove(throwIns);
        il.Append(tail);
        il.Append(tailRet);
        Ctx.BumpStack(target, 8);
    }

    // =======================================================================
    // 2.4 其余下载入口（2.99.0.1+）
    //   (a) PageLaunchLeft.RefreshButtonsUI：无版本时状态 1(禁用) -> 2(可下载)
    //   (b) PageSelectRight：无可用版本时的「下载游戏」按钮
    //   (c) PageDownloadModify：修改版本页的原版组件卡片
    // =======================================================================
    static PatchOutcome OtherEntries(Ctx c, PatchOptions o, bool dry)
    {
        const string id = "entries", title = "其余下载入口";
        try
        {
            var targets = new List<(MethodDefinition Method, Instruction Value)>();

            var refresh = c.Method("PCL.PageLaunchLeft", "RefreshButtonsUI", 0);
            var stateOne = FindStateAssignment(refresh, 1, 3);
            if (stateOne != null) targets.Add((refresh, stateOne));

            var emptyBtn = FindVisibilityAfterString(c.Type("PCL.PageSelectRight"), "无可用版本");
            if (emptyBtn != null) targets.Add(emptyBtn.Value);

            var card = FindVisibilityOf(c.Type("PCL.PageDownloadModify"), "get_CardMinecraft");
            if (card != null) targets.Add(card.Value);

            var pending = targets.Where(t => Il.I4(t.Value) != 0).ToList();
            if (pending.Count == 0)
                return Ok(id, title, PatchState.Already, "没有需要处理的入口");

            if (dry)
                return Ok(id, title, PatchState.Applied,
                    $"可定位（{string.Join(" / ", pending.Select(t => t.Method.Name))}）");

            foreach (var (_, value) in pending)
            {
                value.OpCode = OpCodes.Ldc_I4_0;
                value.Operand = null;
            }

            return Ok(id, title, PatchState.Applied, $"恢复 {pending.Count} 处下载入口");
        }
        catch (PatchException ex) { return Fail(id, title, ex.Message); }
    }

    /// <summary>找「同一个局部先赋 a 再赋 b」里的那次 a 赋值（用于状态机）。</summary>
    static Instruction FindStateAssignment(MethodDefinition m, int a, int b)
    {
        var values = new Dictionary<VariableDefinition, HashSet<int>>();
        var firsts = new Dictionary<VariableDefinition, Instruction>();

        foreach (var ins in m.Body.Instructions)
        {
            var n = Il.I4(ins);
            if (n == null) continue;
            var target = Il.StoreTarget(m.Body, ins.Next);
            if (target == null) continue;

            if (!values.TryGetValue(target, out var set)) { set = new HashSet<int>(); values[target] = set; }
            set.Add(n.Value);
            if (n.Value == a && !firsts.ContainsKey(target)) firsts[target] = ins;
        }

        foreach (var kv in values)
            if (kv.Value.Contains(a) && kv.Value.Contains(b) && firsts.TryGetValue(kv.Key, out var ins))
                return ins;
        return null;
    }

    /// <summary>在包含某段文案的方法里，找到之后第一个 Visibility 赋值。</summary>
    static (MethodDefinition Method, Instruction Value)? FindVisibilityAfterString(TypeDefinition t, string text)
    {
        foreach (var m in t.Methods.Where(x => x.HasBody))
        {
            Instruction marker = null;
            foreach (var ins in m.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Contains(text)) { marker = ins; break; }
            if (marker == null) continue;

            for (var i = marker.Next; i != null; i = i.Next)
                if (i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "set_Visibility")
                    return (m, i.Previous);
        }
        return null;
    }

    /// <summary>找 getter 之后紧跟的那次 Visibility 赋值。</summary>
    static (MethodDefinition Method, Instruction Value)? FindVisibilityOf(TypeDefinition t, string getter)
    {
        foreach (var m in t.Methods.Where(x => x.HasBody))
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Call || !(ins.Operand is MethodReference g) || g.Name != getter) continue;
                for (var i = ins.Next; i != null; i = i.Next)
                    if (i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "set_Visibility")
                        return (m, i.Previous);
            }
        return null;
    }
    // =======================================================================
    // 2.5 下载入口（2.99.0.1 起把「原版游戏」藏掉了）
    //     2.99.0.1 的 PageDownloadLeft 构造函数在 InitializeComponent 之后追加了：
    //         ItemInstall.Visibility = Collapsed / LabResource.Visibility = Collapsed
    //         PageID = DownloadMod / ItemMod.Checked = True
    //     把它之后到 ret 之间的语句全部去掉，即恢复成 2.99.0.0 的构造行为。
    // =======================================================================
    static PatchOutcome DownloadNav(Ctx c, PatchOptions o, bool dry)
    {
        const string id = "nav", title = "下载入口";
        try
        {
            var ctor = c.Method("PCL.PageDownloadLeft", ".ctor", 0);
            var body = ctor.Body;

            var init = body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "InitializeComponent")
                ?? throw new PatchException("找不到 InitializeComponent 调用");

            var hides = body.Instructions.Count(i =>
                i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "set_Visibility");
            if (hides == 0)
                return Ok(id, title, PatchState.Already, "该文件没有入口隐藏逻辑");

            if (dry)
                return Ok(id, title, PatchState.Applied, $"可定位（清除 {hides} 处 Visibility 赋值）");

            var il = body.GetILProcessor();
            var remove = new List<Instruction>();
            for (var i = init.Next; i != null && i.OpCode != OpCodes.Ret; i = i.Next) remove.Add(i);
            foreach (var ins in remove) il.Remove(ins);

            return Ok(id, title, PatchState.Applied, $"恢复「原版游戏」入口（移除 {remove.Count} 条隐藏语句）");
        }
        catch (PatchException ex) { return Fail(id, title, ex.Message); }
    }
    // =======================================================================
    // 3. 整合包版本行点击 -> 安装
    // =======================================================================
    static PatchOutcome RowClick(Ctx c, PatchOptions o, bool dry)
    {
        const string id = "rowclick", title = "整合包版本行点击";
        try
        {
            var page = c.Type("PCL.PageDownloadResourceDetail");
            var save = c.Method("PCL.PageDownloadResourceDetail", "Save_Click", 2);
            var install = c.Method("PCL.PageDownloadResourceDetail", "Install_Click", 2);

            if (save.Body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference r && r.Name == "Install_Click"))
                return Ok(id, title, PatchState.Already, "已是安装行为");

            var typeField = page.Fields.FirstOrDefault(f => f.Name == "TargetResourceType")
                ?? throw new PatchException("缺少 TargetResourceType 字段");
            var itemType = c.Type("PCL.MyListItem");
            var flag = ModpackFlag(c, typeField);

            if (dry)
                return Ok(id, title, PatchState.Applied, $"可定位（Modpack 标志 = {flag}）");

            var original = save.Body.Instructions[0];
            var skip = Instruction.Create(OpCodes.Pop);

            Ctx.InsertBeforeAll(save, original, new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, typeField),
                Instruction.Create(OpCodes.Ldc_I4, flag),
                Instruction.Create(OpCodes.Bne_Un, original),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Isinst, itemType),
                Instruction.Create(OpCodes.Dup),
                Instruction.Create(OpCodes.Brfalse, skip),
                Instruction.Create(OpCodes.Ldarg_2),
                Instruction.Create(OpCodes.Callvirt, install),
                Instruction.Create(OpCodes.Ret),
                skip,
                Instruction.Create(OpCodes.Pop),
                Instruction.Create(OpCodes.Br, original),
            });
            Ctx.BumpStack(save, 4);

            return Ok(id, title, PatchState.Applied, $"Save_Click -> Install_Click（标志 {flag}）");
        }
        catch (PatchException ex) { return Fail(id, title, ex.Message); }
    }

    /// <summary>
    /// 从现有代码里读出 ResourceTypes.Modpack 的数值，而不是写死：
    /// 优先取"创建版本行列表"那个方法里与 TargetResourceType 的比较值，
    /// 其次取全类型中出现次数最多的单 bit 值。
    /// </summary>
    static int ModpackFlag(Ctx c, FieldDefinition field)
    {
        var page = c.Type("PCL.PageDownloadResourceDetail");

        int? Scan(IEnumerable<Instruction> insns)
        {
            foreach (var i in insns)
            {
                if (i.OpCode != OpCodes.Ldfld || !(i.Operand is FieldReference fr) || fr.Name != field.Name) continue;
                var value = Il.I4(i.Next);
                if (value == null) continue;
                var op = i.Next.Next?.OpCode;
                if (op == OpCodes.Ceq || op == OpCodes.Bne_Un || op == OpCodes.Beq) return value;
            }
            return null;
        }

        var rowMethod = page.Methods.FirstOrDefault(m => m.HasBody
            && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "ToListItem"));
        if (rowMethod != null)
        {
            var v = Scan(rowMethod.Body.Instructions);
            if (v.HasValue) return v.Value;
        }

        var counts = new Dictionary<int, int>();
        foreach (var m in page.Methods.Where(x => x.HasBody))
        {
            foreach (var i in m.Body.Instructions)
            {
                if (i.OpCode != OpCodes.Ldfld || !(i.Operand is FieldReference fr) || fr.Name != field.Name) continue;
                var value = Il.I4(i.Next);
                if (value == null) continue;
                var op = i.Next.Next?.OpCode;
                if (op != OpCodes.Ceq && op != OpCodes.Bne_Un && op != OpCodes.Beq) continue;
                var v = value.Value;
                if (v <= 0 || (v & (v - 1)) != 0) continue;
                counts[v] = counts.TryGetValue(v, out var n) ? n + 1 : 1;
            }
        }
        if (counts.Count > 0) return counts.OrderByDescending(kv => kv.Value).First().Key;

        return 2;
    }
    // =======================================================================
    // 4. 静默自动更新
    // =======================================================================
    static PatchOutcome SilentUpdate(Ctx c, PatchOptions o, bool dry)
    {
        const string id = "silent", title = "静默自动更新";
        try
        {
            var m = c.Method("PCL.ModSecret", "UpdateStart", 2);
            if (m.Parameters[0].ParameterType.FullName != "System.Boolean")
                throw new PatchException("UpdateStart 首参不是 bool");

            var first = m.Body.Instructions[0];
            if (first.OpCode == OpCodes.Ldarg_0 && first.Next?.OpCode == OpCodes.Brfalse && first.Next.Next?.OpCode == OpCodes.Ret)
                return Ok(id, title, PatchState.Already, "已是空实现");

            if (dry) return Ok(id, title, PatchState.Applied, "可定位（方法入口插入 early-return）");

            Ctx.InsertBeforeAll(m, first, new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Brfalse, first),
                Instruction.Create(OpCodes.Ret),
            });

            return Ok(id, title, PatchState.Applied, "UpdateStart(silent: true) 直接返回");
        }
        catch (PatchException ex) { return Fail(id, title, ex.Message); }
    }

    // =======================================================================
    // 5. 更新方式默认值
    // =======================================================================
    static PatchOutcome UpdateMode(Ctx c, PatchOptions o, bool dry)
    {
        const string id = "updatemode", title = "更新方式默认值";
        try
        {
            var cctor = c.Type("PCL.Settings").Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic)
                ?? throw new PatchException("缺少 Settings 静态构造");

            var key = cctor.Body.Instructions.FirstOrDefault(i =>
                    i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == "SystemSystemUpdate")
                ?? throw new PatchException("找不到设置项 SystemSystemUpdate");

            var value = key.Next;
            if (value == null) throw new PatchException("设置项后面没有默认值");

            if (value.OpCode == OpCodes.Ldc_I4_3)
                return Ok(id, title, PatchState.Already, "默认值已是 3（不更新）");

            if (value.OpCode != OpCodes.Ldc_I4_0)
                throw new PatchException($"默认值不是 0（实际 {value.OpCode}），拒绝盲改");

            if (dry) return Ok(id, title, PatchState.Applied, "可定位（0 -> 3）");

            value.OpCode = OpCodes.Ldc_I4_3;
            value.Operand = null;
            return Ok(id, title, PatchState.Applied, "SystemSystemUpdate 默认 0 -> 3");
        }
        catch (PatchException ex) { return Fail(id, title, ex.Message); }
    }


}

internal static class Il
{
    public static int? I4(Instruction i)
    {
        if (i == null) return null;
        var oc = i.OpCode;
        if (oc == OpCodes.Ldc_I4_M1) return -1;
        if (oc == OpCodes.Ldc_I4_0) return 0;
        if (oc == OpCodes.Ldc_I4_1) return 1;
        if (oc == OpCodes.Ldc_I4_2) return 2;
        if (oc == OpCodes.Ldc_I4_3) return 3;
        if (oc == OpCodes.Ldc_I4_4) return 4;
        if (oc == OpCodes.Ldc_I4_5) return 5;
        if (oc == OpCodes.Ldc_I4_6) return 6;
        if (oc == OpCodes.Ldc_I4_7) return 7;
        if (oc == OpCodes.Ldc_I4_8) return 8;
        if (oc == OpCodes.Ldc_I4_S) return (sbyte)i.Operand;
        if (oc == OpCodes.Ldc_I4) return (int)i.Operand;
        return null;
    }

    public static VariableDefinition StoreTarget(MethodBody body, Instruction i)
    {
        if (i == null || body == null) return null;
        var oc = i.OpCode;
        if (oc == OpCodes.Stloc_0) return body.Variables.Count > 0 ? body.Variables[0] : null;
        if (oc == OpCodes.Stloc_1) return body.Variables.Count > 1 ? body.Variables[1] : null;
        if (oc == OpCodes.Stloc_2) return body.Variables.Count > 2 ? body.Variables[2] : null;
        if (oc == OpCodes.Stloc_3) return body.Variables.Count > 3 ? body.Variables[3] : null;
        if (oc == OpCodes.Stloc_S || oc == OpCodes.Stloc) return i.Operand as VariableDefinition;
        return null;
    }
}
