using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Pcl2Patch;

/// <summary>对目标文件的基本描述。</summary>
public sealed class FileSnapshot
{
    public string Path;
    public long Size;
    public string Sha256;
    public string AssemblyName;
    public string Version;
    public string Mvid;
    public string Kind;

    public string OneLine =>
        $"{AssemblyName} {Version} · {Size / 1024.0 / 1024.0:F1} MB · {Mvid}";
}

/// <summary>一次补丁运行的完整结果。</summary>
public sealed class PatchRun
{
    public FileSnapshot Snapshot;
    public List<PatchOutcome> Probe = new();
    public List<PatchOutcome> Applied = new();
    public List<PatchOutcome> Verified = new();

    public bool Success;
    public bool NothingToDo;
    public string TargetPath;
    public string BackupPath;
    public string Message;
}

/// <summary>
/// CLI 和 GUI 共用的补丁流程：
/// 副本上工作 -> 探测 -> 应用 -> 写入暂存文件 -> 回读自检 -> 备份 -> 替换。
/// 任何一步失败都不会写坏原文件。
/// </summary>
public static class Patcher
{
    public static FileSnapshot Inspect(string path)
    {
        var info = new FileInfo(path);
        var snap = new FileSnapshot
        {
            Path = path,
            Size = info.Length,
            Sha256 = Hash(path),
        };

        try
        {
            using var c = Ctx.Open(path);
            snap.AssemblyName = c.Asm.Name.Name;
            snap.Version = c.Asm.Name.Version.ToString();
            snap.Mvid = c.Mod.Mvid.ToString();
            snap.Kind = c.Mod.Kind.ToString();
            return snap;
        }
        catch (Exception)
        {
            // 不是托管程序集：再试 .NET 单文件 bundle（PCL-CE）
            var data = File.ReadAllBytes(path);
            if (CePatcher.TryOpen(data, out var ce))
            {
                snap.AssemblyName = "Plain Craft Launcher 2";
                snap.Version = ce.Version;
                snap.Mvid = "PCL-CE";
                snap.Kind = "CE 单文件";
                return snap;
            }
            throw;
        }
    }

    /// <summary>目标是否为 PCL-CE 单文件包。</summary>
    public static bool IsCe(string path)
    {
        try
        {
            using var c = Ctx.Open(path);
            return false;
        }
        catch (Exception)
        {
            return CePatcher.TryOpen(File.ReadAllBytes(path), out _);
        }
    }

    public static List<PatchOutcome> Probe(string path, PatchOptions options)
    {
        if (IsCe(path))
        {
            var data = File.ReadAllBytes(path);
            if (!CePatcher.TryOpen(data, out var ce))
                return new List<PatchOutcome> { new PatchOutcome { Id = CePatcher.PatchId, Title = CePatcher.PatchTitle, State = PatchState.Failed, Detail = "无法解析 CE 单文件包" } };
            return new List<PatchOutcome> { CePatcher.Probe(data, ce) };
        }

        using var c = Ctx.Open(path);
        return Patches.Run(c, options, dry: true).ToList();
    }

    /// <summary>
    /// 打补丁。<paramref name="outputPath"/> 为 null 时原地写入（带备份）。
    /// </summary>
    public static PatchRun Run(string inputPath, string outputPath, PatchOptions options,
        bool backup, Action<string> log)
    {
        var run = new PatchRun { TargetPath = outputPath ?? inputPath };
        if (log == null) log = _ => { };

        if (IsCe(inputPath))
            return RunCe(inputPath, outputPath, backup, log);

        var work = Path.Combine(Path.GetTempPath(), "pcl2-patch-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(inputPath, work, true);

        try
        {
            using (var c = Ctx.Open(work))
            {
                run.Snapshot = new FileSnapshot
                {
                    Path = inputPath,
                    Size = new FileInfo(inputPath).Length,
                    Sha256 = Hash(inputPath),
                    AssemblyName = c.Asm.Name.Name,
                    Version = c.Asm.Name.Version.ToString(),
                    Mvid = c.Mod.Mvid.ToString(),
                    Kind = c.Mod.Kind.ToString(),
                };

                log($"{run.Snapshot.AssemblyName} {run.Snapshot.Version}");
                log($"SHA256 {run.Snapshot.Sha256}");

                run.Probe = Patches.Run(c, options, dry: true).ToList();
                foreach (var o in run.Probe) log(Line(o));

                if (run.Probe.All(x => x.State is PatchState.Already or PatchState.Skipped))
                {
                    run.Success = true;
                    run.NothingToDo = true;
                    run.Message = "这个文件已经处理过，无需重复打补丁。";
                    log(run.Message);
                    return run;
                }

                if (run.Probe.Any(x => x.State == PatchState.Failed) && options.Strict)
                {
                    run.Message = "有补丁无法定位（--strict），未做任何修改。";
                    log(run.Message);
                    return run;
                }

                run.Applied = Patches.Run(c, options, dry: false).ToList();
                foreach (var o in run.Applied) log(Line(o));

                var staged = work + ".out";
                c.Write(staged);
                try { c.Mod.Dispose(); } catch { /* ignore */ }

                using (var v = Ctx.Open(staged))
                {
                    run.Verified = Patches.Run(v, options, dry: true).ToList();
                    var bad = run.Verified.Where(x => x.State == PatchState.Failed).ToList();
                    if (bad.Count > 0)
                    {
                        foreach (var o in bad) log(Line(o));
                        run.Message = "回读自检失败，已放弃写入。";
                        log(run.Message);
                        return run;
                    }
                    try { v.Mod.Dispose(); } catch { /* ignore */ }
                }
                log("回读自检: 通过");

                if (outputPath == null && backup)
                {
                    var bak = inputPath + ".bak";
                    if (!File.Exists(bak))
                    {
                        File.Copy(inputPath, bak);
                        run.BackupPath = bak;
                        log("备份: " + bak);
                    }
                    else
                    {
                        run.BackupPath = bak;
                    }
                }

                File.Copy(staged, run.TargetPath, true);
                try { File.Delete(staged); } catch { /* ignore */ }

                run.Success = true;
                run.Message = "已写入: " + run.TargetPath;
                log(run.Message);
                return run;
            }
        }
        finally
        {
            try { File.Delete(work); } catch { /* ignore */ }
        }
    }

    /// <summary>PCL-CE 单文件包：等长原地改写，不需要重组 bundle。</summary>
    static PatchRun RunCe(string inputPath, string outputPath, bool backup, Action<string> log)
    {
        var run = new PatchRun { TargetPath = outputPath ?? inputPath };
        var data = File.ReadAllBytes(inputPath);

        if (!CePatcher.TryOpen(data, out var ce))
        {
            run.Message = "不是可识别的 PCL-CE 单文件包。";
            log(run.Message);
            return run;
        }

        run.Snapshot = new FileSnapshot
        {
            Path = inputPath,
            Size = data.Length,
            Sha256 = Hash(inputPath),
            AssemblyName = "Plain Craft Launcher 2",
            Version = string.IsNullOrEmpty(ce.Version) ? "?" : ce.Version,
            Mvid = "PCL-CE",
            Kind = "CE 单文件",
        };
        log($"{run.Snapshot.AssemblyName} {run.Snapshot.Version} (PCL-CE)");
        log($"SHA256 {run.Snapshot.Sha256}");

        run.Probe = new List<PatchOutcome> { CePatcher.Probe(data, ce) };
        foreach (var o in run.Probe) log(Line(o));

        if (run.Probe.Any(x => x.State == PatchState.Failed))
        {
            run.Message = "补丁无法定位，未做任何修改。";
            log(run.Message);
            return run;
        }
        if (run.Probe.All(x => x.State is PatchState.Already or PatchState.Skipped))
        {
            run.Success = true;
            run.NothingToDo = true;
            run.Message = "这个文件已经处理过，无需重复打补丁。";
            log(run.Message);
            return run;
        }

        byte[] patched;
        try
        {
            patched = CePatcher.Apply(data, ce, out var note);
            run.Applied = new List<PatchOutcome>
            {
                new PatchOutcome { Id = CePatcher.PatchId, Title = CePatcher.PatchTitle, State = PatchState.Applied, Detail = note },
            };
        }
        catch (PatchException ex)
        {
            run.Message = "写入前自检失败：" + ex.Message;
            log(run.Message);
            return run;
        }
        foreach (var o in run.Applied) log(Line(o));

        var staged = Path.Combine(Path.GetTempPath(), "pcl2-patch-ce-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(staged, patched);

            var verifyData = File.ReadAllBytes(staged);
            if (!CePatcher.TryOpen(verifyData, out var verifiedTarget))
            {
                run.Message = "回读自检失败：单文件包无法解析。";
                log(run.Message);
                return run;
            }
            var verified = CePatcher.Probe(verifyData, verifiedTarget);
            run.Verified = new List<PatchOutcome> { verified };
            if (verified.State != PatchState.Already)
            {
                log(Line(verified));
                run.Message = "回读自检失败，已放弃写入。";
                log(run.Message);
                return run;
            }
            log("回读自检: 通过");

            if (outputPath == null && backup)
            {
                var bak = inputPath + ".bak";
                if (!File.Exists(bak))
                {
                    File.Copy(inputPath, bak);
                    run.BackupPath = bak;
                    log("备份: " + bak);
                }
                else
                {
                    run.BackupPath = bak;
                }
            }

            File.Copy(staged, run.TargetPath, true);
            run.Success = true;
            run.Message = "已写入: " + run.TargetPath;
            log(run.Message);
            return run;
        }
        finally
        {
            try { File.Delete(staged); } catch { /* ignore */ }
        }
    }
    public static string Line(PatchOutcome o)
    {
        var mark = o.State switch
        {
            PatchState.Applied => "[+]",
            PatchState.Already => "[=]",
            PatchState.Skipped => "[-]",
            _ => "[!]",
        };
        return $"{mark} {o.Title,-18} {o.Detail}";
    }

    public static string Hash(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
    }
}
