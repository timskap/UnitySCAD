using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SCADPlugin.Editor
{
    public static class ScadCompiler
    {
        public class Result
        {
            public bool success;
            public bool emptyOutput;
            public string stlPath;
            public string log;
        }

        public static Result Compile(string scadPath, IEnumerable<ScadParameter> parameters, int timeoutMs = 120000)
        {
            return CompileWithPreamble(scadPath, parameters, null, null, timeoutMs, null, default);
        }

        // Runs OpenSCAD with an optional source preamble prepended to the SCAD file
        // and optional extra -D defines. The preamble enables tricks like overriding
        // the built-in `color` module to filter geometry per-color.
        //
        // exePath lets callers pre-resolve the OpenSCAD executable on the main thread
        // so this method can run on worker threads (EditorPrefs access inside
        // ResolveExecutablePath is main-thread-only).
        public static Result CompileWithPreamble(
            string scadPath,
            IEnumerable<ScadParameter> parameters,
            string preamble,
            IDictionary<string, string> extraDefines,
            int timeoutMs = 120000,
            string exePath = null,
            CancellationToken ct = default)
        {
            var exe = exePath ?? ScadImporterSettings.ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                return new Result
                {
                    success = false,
                    log = "OpenSCAD executable not found. Set its path in Edit → Preferences → SCAD Plugin.",
                };
            }

            // Consult the disk cache before spawning OpenSCAD. A hit skips the
            // process launch, parser, CSG, and STL write entirely — the big wins
            // come from re-running the same parameters (Apply twice) or changing
            // a value that doesn't affect every colored submesh.
            var cacheHash = ScadCompileCache.ComputeHash(
                scadPath, parameters, preamble, extraDefines, exe);
            var cached = ScadCompileCache.Query(cacheHash);
            if (cached.hit)
            {
                if (cached.emptyMarker)
                    return new Result { success = false, emptyOutput = true, log = "(cached empty)" };
                return new Result { success = true, stlPath = cached.stlPath, log = "(cached)" };
            }

            var stlPath = Path.Combine(Path.GetTempPath(), $"scadplugin_{Guid.NewGuid():N}.stl");

            string sourceToCompile;
            string tempSource = null;
            if (!string.IsNullOrEmpty(preamble))
            {
                var original = File.ReadAllText(scadPath);
                tempSource = Path.Combine(Path.GetTempPath(),
                    $"scadplugin_src_{Guid.NewGuid():N}.scad");
                File.WriteAllText(tempSource, preamble + "\n" + original);
                sourceToCompile = tempSource;
            }
            else
            {
                sourceToCompile = Path.GetFullPath(scadPath);
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(stlPath);
            psi.ArgumentList.Add("--export-format");
            psi.ArgumentList.Add("binstl");

            foreach (var p in parameters)
            {
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                var value = string.IsNullOrEmpty(p.value) ? p.defaultValue : p.value;
                if (string.IsNullOrEmpty(value)) continue;
                psi.ArgumentList.Add("-D");
                psi.ArgumentList.Add($"{p.name}={value}");
            }

            if (extraDefines != null)
            {
                foreach (var kv in extraDefines)
                {
                    psi.ArgumentList.Add("-D");
                    psi.ArgumentList.Add($"{kv.Key}={kv.Value}");
                }
            }

            psi.ArgumentList.Add(sourceToCompile);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            try
            {
                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // When the caller cancels, kill the live OpenSCAD process so the
                // editor can immediately start a new compile with fresher params
                // instead of waiting for the stale one to finish.
                using var cancelReg = ct.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                });

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    return new Result { success = false, log = "OpenSCAD timed out." };
                }
                proc.WaitForExit();

                if (ct.IsCancellationRequested)
                    return new Result { success = false, log = "Cancelled." };

                var combined = (stdout.Length > 0 ? stdout.ToString() : "") +
                               (stderr.Length > 0 ? stderr.ToString() : "");

                bool hasFile = File.Exists(stlPath) && new FileInfo(stlPath).Length > 0;
                bool reportsEmpty = combined.Contains("Current top level object is empty",
                    StringComparison.OrdinalIgnoreCase);

                if (!hasFile && reportsEmpty)
                {
                    ScadCompileCache.StoreEmpty(cacheHash);
                    return new Result
                    {
                        success = false,
                        emptyOutput = true,
                        log = combined,
                    };
                }

                if (proc.ExitCode != 0 || !hasFile)
                {
                    return new Result
                    {
                        success = false,
                        log = $"OpenSCAD exited with code {proc.ExitCode}.\n{combined}",
                    };
                }

                var cachedPath = ScadCompileCache.Store(cacheHash, stlPath);
                return new Result { success = true, stlPath = cachedPath, log = combined };
            }
            catch (Exception ex)
            {
                return new Result { success = false, log = ex.ToString() };
            }
            finally
            {
                if (tempSource != null)
                {
                    try { File.Delete(tempSource); } catch { }
                }
            }
        }
    }
}
