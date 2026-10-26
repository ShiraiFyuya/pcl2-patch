using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Pcl2Patch;

/// <summary>
/// PCL-CE（Plain Craft Launcher Community Edition）支持。
///
/// CE 把"地区限制"做成了运行期策略：<c>PCL.Core.App.Region.RegionPolicy.Evaluate</c>
/// 根据 Windows 区域信号判定是否为中国大陆，<c>RegionAssessment.IsRestricted</c>
/// 在「中国大陆 / 无法判定」时返回 true，下载、整合包安装、版本修复等入口都通过它拦截。
///
/// CE 的发布物是 .NET 单文件 bundle，不能直接当程序集读写。这里按 bundle 清单
/// 找到内嵌的 <c>PCL.Core.dll</c>，在原字节上把 <c>get_IsRestricted</c> 的
/// 方法体改成「恒返回 false」（tiny header + ldc.i4.0 + ret），长度只减不增，
/// 因此不需要重写 bundle，直接在原偏移写回即可。
/// </summary>
public static class CePatcher
{
    public const string PatchId = "ce-region";
    public const string PatchTitle = "地区限制";

    static readonly string[] CoreNames = { "PCL.Core.dll" };
    const string MainName = "Plain Craft Launcher 2.dll";

    public sealed class Target
    {
        public CeBundle Bundle;
        public CeBundle.Entry Core;
        public CeBundle.Entry Main;
        public string Version;
        public string MainVersion;
    }

    public static bool TryOpen(byte[] data, out Target target)
    {
        target = null;
        var bundle = CeBundleReader.TryRead(data);
        if (bundle == null) return false;

        var main = bundle.Find(MainName);
        if (main == null) return false;

        CeBundle.Entry core = null;
        foreach (var name in CoreNames)
        {
            core = bundle.Find(name);
            if (core != null) break;
        }
        if (core == null) return false;

        var version = string.Empty;
        try
        {
            version = ReadAssemblyVersion(bundle.ReadEntry(data, main));
        }
        catch
        {
            // 版本号读不到不影响补丁本身
        }

        target = new Target { Bundle = bundle, Core = core, Main = main, Version = version };
        return true;
    }

    static string ReadAssemblyVersion(byte[] assembly)
    {
        using var module = ModuleDefinition.ReadModule(new MemoryStream(assembly));
        return module.Assembly.Name.Version.ToString();
    }

    public static PatchOutcome Probe(byte[] data, Target target)
    {
        try
        {
            var core = target.Bundle.ReadEntry(data, target.Core);
            using var module = ModuleDefinition.ReadModule(new MemoryStream(core));
            var getter = FindGetter(module);
            if (getter == null)
                return new PatchOutcome { Id = PatchId, Title = PatchTitle, State = PatchState.Skipped, Detail = "该版本没有地区限制" };

            if (IsPatched(getter))
                return Ok(PatchState.Already, "已是放行状态（无需处理）");

            return Ok(PatchState.Applied, "可定位（IsRestricted 恒为 false）");
        }
        catch (PatchException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>返回打过补丁之后的完整 exe 字节；失败时抛 <see cref="PatchException"/>。</summary>
    public static byte[] Apply(byte[] data, Target target, out string note)
    {
        var core = target.Bundle.ReadEntry(data, target.Core);
        MethodDefinition getter;
        using (var module = ModuleDefinition.ReadModule(new MemoryStream(core)))
        {
            getter = FindGetter(module) ?? throw new PatchException("找不到 RegionAssessment.IsRestricted");
            if (IsPatched(getter))
            {
                note = "已是放行状态";
                return data;
            }

            var offset = RvaToFileOffset(core, getter.RVA);
            if (offset < 0 || offset + 3 > core.Length)
                throw new PatchException("IsRestricted 方法位置无效");

            // tiny header（长度 2、无局部变量）+ ldc.i4.0 + ret
            core[offset] = 0x0A;
            core[offset + 1] = 0x16;
            core[offset + 2] = 0x2A;
        }

        // 自检：重新读取，确认方法体已是 ldc.i4.0; ret
        using (var check = ModuleDefinition.ReadModule(new MemoryStream(core)))
        {
            var patchedGetter = FindGetter(check) ?? throw new PatchException("自检失败：方法丢失");
            if (!IsPatched(patchedGetter))
                throw new PatchException("自检失败：方法体未生效");
        }

        var result = (byte[])data.Clone();
        Buffer.BlockCopy(core, 0, result, (int)target.Core.Offset, core.Length);
        note = $"RegionAssessment.IsRestricted -> false（{core.Length:N0} 字节中的 3 字节）";
        return result;
    }

    static MethodDefinition FindGetter(ModuleDefinition module)
    {
        var type = module.GetType("PCL.Core.App.Region.RegionAssessment");
        if (type == null)
        {
            // 兼容改名：按属性找
            foreach (var t in module.Types)
            {
                var p = t.Properties.FirstOrDefault(x => x.Name == "IsRestricted" && x.GetMethod != null);
                if (p != null) return p.GetMethod;
            }
            return null;
        }

        var prop = type.Properties.FirstOrDefault(x => x.Name == "IsRestricted");
        return prop?.GetMethod;
    }

    static bool IsPatched(MethodDefinition method)
    {
        var body = method.Body;
        if (body == null || body.Instructions.Count != 2) return false;
        return body.Instructions[0].OpCode == OpCodes.Ldc_I4_0
               && body.Instructions[1].OpCode == OpCodes.Ret;
    }

    /// <summary>PE 里的 RVA -> 文件内偏移。</summary>
    static int RvaToFileOffset(byte[] pe, int rva)
    {
        var peOffset = BitConverter.ToInt32(pe, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > pe.Length) return -1;

        var sections = BitConverter.ToInt16(pe, peOffset + 6);
        var optionalSize = BitConverter.ToInt16(pe, peOffset + 20);
        var table = peOffset + 24 + optionalSize;

        for (var i = 0; i < sections; i++)
        {
            var s = table + i * 40;
            if (s + 40 > pe.Length) break;

            var virtualAddress = BitConverter.ToUInt32(pe, s + 12);
            var rawSize = BitConverter.ToUInt32(pe, s + 16);
            var rawPointer = BitConverter.ToUInt32(pe, s + 20);
            if ((uint)rva >= virtualAddress && (uint)rva < virtualAddress + Math.Max(rawSize, 1u))
                return (int)(rawPointer + (uint)rva - virtualAddress);
        }
        return -1;
    }

    static PatchOutcome Ok(PatchState state, string detail) =>
        new PatchOutcome { Id = PatchId, Title = PatchTitle, State = state, Detail = detail };

    static PatchOutcome Fail(string detail) =>
        new PatchOutcome { Id = PatchId, Title = PatchTitle, State = PatchState.Failed, Detail = detail };
}
