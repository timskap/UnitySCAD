using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SCADPlugin.Editor
{
    // Async compile pipeline for the live-preview window. Runs entirely on worker
    // threads: no Unity API calls, no main-thread dependencies. Returns raw mesh
    // data that the caller turns into a Mesh + Materials on the main thread.
    public static class ScadLiveCompiler
    {
        const string FilterVarName = "__scadplugin_target_color__";

        public class RawPart
        {
            public List<Vector3> verts;
            public List<Vector3> normals;
            public List<int> tris;
            public Color color;
        }

        public class Result
        {
            public bool success;
            public List<RawPart> parts = new List<RawPart>();
            public int triangleCount;
            public string error;
        }

        public static Task<Result> CompileAsync(
            string scadPath,
            ScadParameter[] parameters,
            IReadOnlyList<string> colorLiterals,
            int maxParallelCompiles,
            int timeoutMs,
            string exePath,
            float scale,
            bool weldVertices,
            CancellationToken ct)
        {
            return Task.Run(() => CompileCore(
                scadPath, parameters, colorLiterals, maxParallelCompiles,
                timeoutMs, exePath, scale, weldVertices, ct), ct);
        }

        static Result CompileCore(
            string scadPath,
            ScadParameter[] parameters,
            IReadOnlyList<string> colorLiterals,
            int maxParallel,
            int timeoutMs,
            string exePath,
            float scale,
            bool weldVertices,
            CancellationToken ct)
        {
            var result = new Result();

            bool multiColor = colorLiterals != null && colorLiterals.Count > 0;

            if (!multiColor)
            {
                ct.ThrowIfCancellationRequested();
                var r = ScadCompiler.CompileWithPreamble(
                    scadPath, parameters, null, null, timeoutMs, exePath, ct);
                if (!r.success)
                {
                    result.error = r.emptyOutput ? "Empty top-level object." : r.log;
                    return result;
                }
                var part = LoadRaw(r.stlPath, scale, weldVertices);
                part.color = Color.white;
                result.parts.Add(part);
                result.triangleCount = part.tris.Count / 3;
                result.success = true;
                return result;
            }

            const string preamble =
                "module color(c, alpha=1) { if (c == " + FilterVarName + ") children(); }";

            var partials = new ScadCompiler.Result[colorLiterals.Count];
            int desired = maxParallel > 0 ? maxParallel : Environment.ProcessorCount;
            int parallelism = Math.Max(1, Math.Min(desired, colorLiterals.Count));

            using (var sem = new SemaphoreSlim(parallelism))
            {
                var tasks = new Task[colorLiterals.Count];
                for (int i = 0; i < colorLiterals.Count; i++)
                {
                    int idx = i;
                    var literal = colorLiterals[idx];
                    tasks[idx] = Task.Run(() =>
                    {
                        try { sem.Wait(ct); } catch { return; }
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            var defines = new Dictionary<string, string>
                            {
                                { FilterVarName, literal },
                            };
                            partials[idx] = ScadCompiler.CompileWithPreamble(
                                scadPath, parameters, preamble, defines, timeoutMs, exePath, ct);
                        }
                        catch (Exception ex)
                        {
                            partials[idx] = new ScadCompiler.Result { success = false, log = ex.ToString() };
                        }
                        finally { sem.Release(); }
                    }, ct);
                }

                try { Task.WaitAll(tasks, ct); }
                catch (OperationCanceledException)
                {
                    result.error = "Cancelled";
                    return result;
                }
            }

            for (int i = 0; i < colorLiterals.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var r = partials[i];
                if (r == null || r.emptyOutput) continue;
                if (!r.success) continue;

                var raw = LoadRaw(r.stlPath, scale, weldVertices);
                if (raw.verts.Count == 0) continue;
                raw.color = ParseColorLiteral(colorLiterals[i]);
                result.parts.Add(raw);
                result.triangleCount += raw.tris.Count / 3;
            }

            result.success = result.parts.Count > 0;
            if (!result.success) result.error = "No geometry produced by any color pass.";
            return result;
        }

        static RawPart LoadRaw(string stlPath, float scale, bool weld)
        {
            StlMeshLoader.LoadRaw(stlPath, scale, weld,
                out var verts, out var normals, out var tris, out _);
            return new RawPart { verts = verts, normals = normals, tris = tris };
        }

        static Color ParseColorLiteral(string literal)
        {
            if (string.IsNullOrEmpty(literal)) return Color.white;
            var t = literal.Trim();

            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
            {
                var inner = t.Substring(1, t.Length - 2);
                if (ColorUtility.TryParseHtmlString(inner, out var c)) return c;
                return Color.white;
            }

            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                var parts = t.Substring(1, t.Length - 2).Split(',');
                var ci = CultureInfo.InvariantCulture;
                var ns = NumberStyles.Float;
                float r = 1, g = 1, b = 1, a = 1;
                if (parts.Length >= 1) float.TryParse(parts[0].Trim(), ns, ci, out r);
                if (parts.Length >= 2) float.TryParse(parts[1].Trim(), ns, ci, out g);
                if (parts.Length >= 3) float.TryParse(parts[2].Trim(), ns, ci, out b);
                if (parts.Length >= 4) float.TryParse(parts[3].Trim(), ns, ci, out a);
                return new Color(r, g, b, a);
            }

            return Color.white;
        }
    }
}
