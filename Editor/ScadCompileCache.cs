using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SCADPlugin.Editor
{
    [InitializeOnLoad]
    public static class ScadCompileCache
    {
        // Cap cache disk usage; evict oldest-accessed entries past this. Picked to
        // handle a dozen multi-color models without manual intervention but bounded
        // enough that the project's Library folder doesn't balloon uncontrollably.
        public const long MaxSizeBytes = 512L * 1024 * 1024;

        // Resolved once on the main thread at domain reload. Worker threads that
        // compile asynchronously must not touch Application.dataPath directly.
        static readonly string s_cacheDir;

        static ScadCompileCache()
        {
            var project = Directory.GetParent(Application.dataPath).FullName;
            s_cacheDir = Path.Combine(project, "Library", "SCADPlugin", "cache");
        }

        public static string CacheDir => s_cacheDir;

        public class Lookup
        {
            public bool hit;
            public bool emptyMarker;
            public string stlPath;
        }

        public static string ComputeHash(
            string scadPath,
            IEnumerable<ScadParameter> parameters,
            string preamble,
            IDictionary<string, string> extraDefines,
            string exePath)
        {
            using var sha = SHA256.Create();
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);

            try
            {
                var srcBytes = File.ReadAllBytes(scadPath);
                w.Write(srcBytes.Length);
                w.Write(srcBytes);
            }
            catch
            {
                w.Write(-1);
            }

            var paramList = (parameters ?? System.Linq.Enumerable.Empty<ScadParameter>())
                .Where(p => p != null && !string.IsNullOrEmpty(p.name))
                .OrderBy(p => p.name, StringComparer.Ordinal)
                .ToArray();
            w.Write(paramList.Length);
            foreach (var p in paramList)
            {
                w.Write(p.name);
                w.Write(string.IsNullOrEmpty(p.value) ? (p.defaultValue ?? "") : p.value);
            }

            w.Write(preamble ?? "");

            if (extraDefines != null)
            {
                var sorted = extraDefines.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();
                w.Write(sorted.Length);
                foreach (var kv in sorted) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
            }
            else w.Write(0);

            // OpenSCAD version signal: path + mtime. Upgrading the binary changes
            // mtime and thus invalidates the cache without needing to read metadata.
            w.Write(exePath ?? "");
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                w.Write(File.GetLastWriteTimeUtc(exePath).ToBinary());
            else
                w.Write(0L);

            w.Flush();
            var digest = sha.ComputeHash(ms.ToArray());
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static Lookup Query(string hash)
        {
            var dir = CacheDir;
            if (!Directory.Exists(dir)) return new Lookup();

            var stl = Path.Combine(dir, hash + ".stl");
            if (File.Exists(stl))
            {
                try { File.SetLastAccessTimeUtc(stl, DateTime.UtcNow); } catch { }
                return new Lookup { hit = true, stlPath = stl };
            }

            var empty = Path.Combine(dir, hash + ".empty");
            if (File.Exists(empty))
            {
                try { File.SetLastAccessTimeUtc(empty, DateTime.UtcNow); } catch { }
                return new Lookup { hit = true, emptyMarker = true };
            }

            return new Lookup();
        }

        // Moves an existing temp STL into the cache, returning the new path.
        // Falls back to returning the original path if the move fails — the
        // compile still succeeds, we just miss the cache on the next run.
        public static string Store(string hash, string tempStlPath)
        {
            var dir = CacheDir;
            Directory.CreateDirectory(dir);
            var dst = Path.Combine(dir, hash + ".stl");

            try
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tempStlPath, dst);
            }
            catch
            {
                return tempStlPath;
            }

            EvictIfOversize();
            return dst;
        }

        public static void StoreEmpty(string hash)
        {
            var dir = CacheDir;
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, hash + ".empty");
            try { File.WriteAllBytes(marker, Array.Empty<byte>()); } catch { }
        }

        public static long TotalSize()
        {
            var dir = CacheDir;
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }

        public static int Clear()
        {
            var dir = CacheDir;
            if (!Directory.Exists(dir)) return 0;
            int count = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { File.Delete(f); count++; } catch { }
            }
            return count;
        }

        static void EvictIfOversize()
        {
            var dir = CacheDir;
            if (!Directory.Exists(dir)) return;

            var files = Directory.EnumerateFiles(dir)
                .Select(f => new FileInfo(f))
                .OrderBy(fi => fi.LastAccessTimeUtc)
                .ToList();

            long total = files.Sum(fi => fi.Length);
            int i = 0;
            while (total > MaxSizeBytes && i < files.Count)
            {
                try { total -= files[i].Length; files[i].Delete(); } catch { }
                i++;
            }
        }
    }
}
