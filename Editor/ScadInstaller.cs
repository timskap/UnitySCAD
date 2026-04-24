using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SCADPlugin.Editor
{
    public static class ScadInstaller
    {
        // Targets a development snapshot rather than the 2021.01 stable because
        // only snapshots ship the Manifold CSG backend, which is typically
        // 10–50× faster than 2021.01's CGAL on boolean operations. Bump these
        // together when rolling to a newer snapshot — the Linux AppImage carries
        // a build-number suffix (`ai*****`) that changes per snapshot.
        public const string SnapshotVersion = "2025.06.22";
        const string LinuxBuildSuffix = "ai25955";

        static Task _task;
        static double _progress;
        static string _stage;
        static CancellationTokenSource _cts;

        public static bool IsInstalling => _task != null && !_task.IsCompleted;
        public static double Progress => _progress;
        public static string Stage => _stage;

        public static string InstallRoot
        {
            get
            {
#if UNITY_EDITOR_OSX
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Library/Application Support/SCADPlugin/OpenSCAD");
#elif UNITY_EDITOR_WIN
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SCADPlugin", "OpenSCAD");
#else
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    ".local/share/SCADPlugin/OpenSCAD");
#endif
            }
        }

        public static string ResolveBundledExecutable()
        {
            if (!Directory.Exists(InstallRoot)) return null;
#if UNITY_EDITOR_OSX
            var appExe = Path.Combine(InstallRoot, "OpenSCAD.app/Contents/MacOS/OpenSCAD");
            return File.Exists(appExe) ? appExe : null;
#elif UNITY_EDITOR_WIN
            foreach (var exe in Directory.EnumerateFiles(InstallRoot, "openscad.exe", SearchOption.AllDirectories))
                return exe;
            return null;
#else
            foreach (var img in Directory.EnumerateFiles(InstallRoot, "*.AppImage", SearchOption.AllDirectories))
                return img;
            return null;
#endif
        }

        public static void BeginInstall()
        {
            if (IsInstalling) return;
            _cts = new CancellationTokenSource();
            _progress = 0;
            _stage = "Starting...";
            _task = Task.Run(() => InstallAsync(_cts.Token));
            EditorApplication.update += Tick;
        }

        public static void Uninstall()
        {
            if (IsInstalling) return;
            if (!Directory.Exists(InstallRoot)) return;
            try { Directory.Delete(InstallRoot, true); }
            catch (Exception ex) { Debug.LogWarning("[SCADPlugin] Uninstall failed: " + ex.Message); }
            if (ScadImporterSettings.ExecutablePath?.StartsWith(InstallRoot) == true)
                ScadImporterSettings.ExecutablePath = string.Empty;
        }

        static void Tick()
        {
            if (_task == null) { EditorApplication.update -= Tick; return; }

            if (_task.IsCompleted)
            {
                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();

                if (_task.IsFaulted)
                {
                    var ex = _task.Exception?.GetBaseException();
                    Debug.LogError("[SCADPlugin] Install failed: " + ex);
                    EditorUtility.DisplayDialog("OpenSCAD install failed", ex?.Message ?? "Unknown error", "OK");
                }
                else if (_task.IsCanceled)
                {
                    Debug.Log("[SCADPlugin] Install cancelled.");
                }
                else
                {
                    var exe = ResolveBundledExecutable();
                    if (!string.IsNullOrEmpty(exe))
                    {
                        ScadImporterSettings.ExecutablePath = exe;
                        Debug.Log("[SCADPlugin] OpenSCAD installed at " + exe);
                        EditorUtility.DisplayDialog(
                            "OpenSCAD installed",
                            "Installed to:\n" + InstallRoot + "\n\nExecutable:\n" + exe,
                            "OK");
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(
                            "Install incomplete",
                            "Executable was not found after install at:\n" + InstallRoot,
                            "OK");
                    }
                }
                _task = null;
                _cts = null;
            }
            else
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                        "SCAD Plugin — Installing OpenSCAD",
                        _stage ?? "Working...",
                        (float)_progress))
                {
                    _cts?.Cancel();
                }
            }
        }

        static async Task InstallAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(InstallRoot);

            const string root = "https://files.openscad.org/snapshots";

#if UNITY_EDITOR_OSX
            var url = $"{root}/OpenSCAD-{SnapshotVersion}.dmg";
            var dmg = Path.Combine(Path.GetTempPath(), $"openscad_{Guid.NewGuid():N}.dmg");
            try
            {
                await DownloadAsync(url, dmg, ct);
                InstallMac(dmg);
            }
            finally
            {
                try { File.Delete(dmg); } catch { }
            }
#elif UNITY_EDITOR_WIN
            var url = $"{root}/OpenSCAD-{SnapshotVersion}-x86-64.zip";
            var zip = Path.Combine(Path.GetTempPath(), $"openscad_{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(url, zip, ct);
                _stage = "Extracting archive...";
                _progress = 0;
                ZipFile.ExtractToDirectory(zip, InstallRoot, overwriteFiles: true);
            }
            finally
            {
                try { File.Delete(zip); } catch { }
            }
#else
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
            var fileName = $"OpenSCAD-{SnapshotVersion}.{LinuxBuildSuffix}-{arch}.AppImage";
            var url = $"{root}/{fileName}";
            var dest = Path.Combine(InstallRoot, fileName);
            await DownloadAsync(url, dest, ct);
            _stage = "Setting execute permission...";
            _progress = 0;
            RunProcess("chmod", new[] { "+x", dest });
#endif
        }

        static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
        {
            _stage = "Downloading " + Path.GetFileName(url);
            _progress = 0;

            using var handler = new HttpClientHandler { AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var dst = File.Create(destPath);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                received += read;
                if (total.HasValue && total.Value > 0)
                    _progress = (double)received / total.Value;
            }
        }

#if UNITY_EDITOR_OSX
        static void InstallMac(string dmgPath)
        {
            _stage = "Mounting DMG...";
            _progress = 0;

            var mountPoint = Path.Combine(Path.GetTempPath(), $"scadplugin_mount_{Guid.NewGuid():N}");
            Directory.CreateDirectory(mountPoint);

            RunProcess("hdiutil", new[] { "attach", "-nobrowse", "-readonly", "-mountpoint", mountPoint, dmgPath });
            try
            {
                string src = null;
                foreach (var entry in Directory.EnumerateDirectories(mountPoint, "*.app"))
                {
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith("OpenSCAD", StringComparison.OrdinalIgnoreCase))
                    {
                        src = entry;
                        break;
                    }
                }
                if (src == null)
                    throw new Exception("No OpenSCAD .app bundle found inside mounted DMG at " + mountPoint);

                var dst = Path.Combine(InstallRoot, "OpenSCAD.app");
                if (Directory.Exists(dst)) Directory.Delete(dst, true);

                _stage = $"Copying {Path.GetFileName(src)}...";
                RunProcess("cp", new[] { "-R", src, dst });

                _stage = "Removing quarantine attribute...";
                RunProcess("xattr", new[] { "-dr", "com.apple.quarantine", dst }, ignoreExitCode: true);
            }
            finally
            {
                RunProcess("hdiutil", new[] { "detach", mountPoint }, ignoreExitCode: true);
                try { Directory.Delete(mountPoint); } catch { }
            }
        }
#endif

        static void RunProcess(string file, string[] args, bool ignoreExitCode = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new Exception("Failed to start " + file);
            var stderr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (!ignoreExitCode && proc.ExitCode != 0)
                throw new Exception($"{file} exited {proc.ExitCode}: {stderr.Trim()}");
        }
    }
}
