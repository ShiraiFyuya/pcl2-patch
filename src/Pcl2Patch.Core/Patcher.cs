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

        using (var c = Ctx.Open(path))
        {
            snap.AssemblyName = c.Asm.Name.Name;
            snap.Version = c.Asm.Name.Version.ToString();
            snap.Mvid = c.Mod.Mvid.ToString();
            snap.Kind = c.Mod.Kind.ToString();
        }
        return snap;
    }

    public static List<PatchOutcome> Probe(string path, PatchOptions options)
    {
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
        log ??= _ => { };

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
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
