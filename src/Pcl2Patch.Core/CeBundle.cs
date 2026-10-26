using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Pcl2Patch;

/// <summary>
/// .NET 单文件 bundle（Bundle 版本 6+，PCL-CE 使用）的只读解析。
/// 布局：apphost → 各文件原始字节 → 头部 → 清单条目（位于文件末尾，因此这样定位）。
/// </summary>
public sealed class CeBundle
{
    public sealed class Entry
    {
        public long Offset;
        public long Size;
        public long CompressedSize;
        public byte Type;
        public string Name;

        public bool Compressed => CompressedSize > 0;
    }

    public uint Major;
    public uint Minor;
    public string BundleId;
    public readonly List<Entry> Entries = new List<Entry>();

    public Entry Find(string name)
    {
        foreach (var e in Entries)
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    public byte[] ReadEntry(byte[] bundleBytes, Entry entry)
    {
        if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > bundleBytes.Length)
            throw new PatchException($"bundle 条目越界：{entry.Name}");

        var raw = new byte[entry.Size];
        Buffer.BlockCopy(bundleBytes, (int)entry.Offset, raw, 0, (int)entry.Size);
        if (!entry.Compressed) return raw;

        // v6 起支持 Deflate 压缩（PCL-CE 目前未压缩，这里做兼容）
        using var input = new MemoryStream(raw);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}

public static class CeBundleReader
{
    /// <summary>尝试把 <paramref name="data"/> 当作 .NET 单文件 bundle 解析。</summary>
    public static CeBundle TryRead(byte[] data)
    {
        if (data == null || data.Length < 64) return null;

        // 头部在文件末尾的清单之前，从尾部向前找可完整解析的位置
        var scanStart = Math.Max(0, data.Length - 4 * 1024 * 1024);
        for (var pos = data.Length - 12; pos >= scanStart; pos--)
        {
            CeBundle bundle;
            if (TryReadHeaderAt(data, pos, out bundle) && bundle.Entries.Count > 0)
                return bundle;
        }
        return null;
    }

    static bool TryReadHeaderAt(byte[] data, int offset, out CeBundle bundle)
    {
        bundle = null;
        if (offset < 0 || offset + 12 > data.Length) return false;

        var major = BitConverter.ToUInt32(data, offset);
        var minor = BitConverter.ToUInt32(data, offset + 4);
        if (major < 1 || major > 16 || minor != 0) return false;

        var count = BitConverter.ToInt32(data, offset + 8);
        if (count < 1 || count > 20000) return false;

        var pos = offset + 12;
        if (!TryReadString(data, ref pos, out var bundleId)) return false;
        if (bundleId == null || bundleId.Length < 4 || bundleId.Length > 64) return false;

        if (major >= 2)
        {
            if (pos + 40 > data.Length) return false;
            pos += 40; // depsJson / runtimeconfig 位置与大小、flags
        }

        var entries = new List<CeBundle.Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var entryLen = major >= 6 ? 25 : 17;
            if (pos + entryLen > data.Length) return false;

            var entry = new CeBundle.Entry
            {
                Offset = BitConverter.ToInt64(data, pos),
                Size = BitConverter.ToInt64(data, pos + 8),
            };
            var p = pos + 16;
            if (major >= 6)
            {
                entry.CompressedSize = BitConverter.ToInt64(data, p);
                p += 8;
            }
            entry.Type = data[p];
            p += 1;

            if (!TryReadString(data, ref p, out var name)) return false;
            if (string.IsNullOrEmpty(name) || name.Length > 512) return false;
            if (entry.Offset < 0 || entry.Size < 0) return false;
            if (entry.Offset + entry.Size > data.Length) return false;

            entry.Name = name;
            entries.Add(entry);
            pos = p;
        }

        // 解析必须恰好停在文件末尾，才算找到真正的头部
        if (pos != data.Length) return false;

        bundle = new CeBundle { Major = major, Minor = minor, BundleId = bundleId };
        bundle.Entries.AddRange(entries);
        return true;
    }

    static bool TryReadString(byte[] data, ref int pos, out string value)
    {
        value = null;
        if (pos >= data.Length) return false;

        var length = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= data.Length || shift > 28) return false;
            var b = data[pos++];
            length |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        if (length < 0 || pos + length > data.Length) return false;

        value = Encoding.UTF8.GetString(data, pos, length);
        pos += length;
        return true;
    }
}
