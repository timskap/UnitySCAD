using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SCADPlugin.Editor
{
    static class ScadSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Preferences/SCAD Plugin", SettingsScope.User)
            {
                label = "SCAD Plugin",
                guiHandler = _ => DrawGui(),
                keywords = new HashSet<string> { "SCAD", "OpenSCAD" },
            };
        }

        static void DrawGui()
        {
            EditorGUILayout.LabelField("OpenSCAD Executable", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                var current = ScadImporterSettings.ExecutablePath;
                var updated = EditorGUILayout.TextField(current);
                if (updated != current) ScadImporterSettings.ExecutablePath = updated;

                if (GUILayout.Button("Browse", GUILayout.Width(80)))
                {
                    var picked = EditorUtility.OpenFilePanel("Locate OpenSCAD executable", "/", "");
                    if (!string.IsNullOrEmpty(picked)) ScadImporterSettings.ExecutablePath = picked;
                }
            }

            var resolved = ScadImporterSettings.ResolveExecutablePath();
            if (string.IsNullOrEmpty(resolved))
            {
                EditorGUILayout.HelpBox(
                    "OpenSCAD not found. Use the one-click installer below, or set a path manually.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("Using: " + resolved, MessageType.Info);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("One-click Install", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"Downloads the OpenSCAD {ScadInstaller.SnapshotVersion} development snapshot from openscad.org into a per-user cache. Snapshots ship the Manifold CSG backend (typically 10–50× faster than the 2021.01 stable on boolean ops). OpenSCAD is GPL-licensed and is not redistributed by this plugin — the download happens on your machine at your request.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(ScadInstaller.IsInstalling))
            {
                if (GUILayout.Button($"Download and Install OpenSCAD {ScadInstaller.SnapshotVersion}"))
                    ScadInstaller.BeginInstall();
            }

            var bundled = ScadInstaller.ResolveBundledExecutable();
            if (!string.IsNullOrEmpty(bundled))
            {
                EditorGUILayout.HelpBox("Installed at: " + bundled, MessageType.Info);
                using (new EditorGUI.DisabledScope(ScadInstaller.IsInstalling))
                {
                    if (GUILayout.Button("Remove Installed OpenSCAD"))
                    {
                        if (EditorUtility.DisplayDialog(
                                "Remove OpenSCAD?",
                                "This deletes:\n" + ScadInstaller.InstallRoot,
                                "Delete", "Cancel"))
                        {
                            ScadInstaller.Uninstall();
                        }
                    }
                }
            }
            else if (System.IO.Directory.Exists(ScadInstaller.InstallRoot))
            {
                EditorGUILayout.HelpBox("Install folder exists but no executable was detected: " + ScadInstaller.InstallRoot, MessageType.Warning);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Compile Cache", EditorStyles.boldLabel);
            var cacheBytes = ScadCompileCache.TotalSize();
            EditorGUILayout.HelpBox(
                $"On-disk cache of compiled STLs keyed by source + parameters + filter color + OpenSCAD binary. A hit skips the OpenSCAD run entirely.\n\nCurrent size: {FormatBytes(cacheBytes)} (cap {FormatBytes(ScadCompileCache.MaxSizeBytes)}, auto-evicts oldest).\nLocation: {ScadCompileCache.CacheDir}",
                MessageType.None);
            if (GUILayout.Button("Clear Compile Cache"))
            {
                var n = ScadCompileCache.Clear();
                Debug.Log($"[SCADPlugin] Cleared {n} cached compile result(s).");
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Reimport All .scad Assets"))
            {
                var guids = AssetDatabase.FindAssets("t:Object");
                int count = 0;
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".scad", System.StringComparison.OrdinalIgnoreCase))
                    {
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                        count++;
                    }
                }
                Debug.Log($"[SCADPlugin] Reimported {count} .scad asset(s).");
            }
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("F1") + " MB";
            return (bytes / (1024d * 1024 * 1024)).ToString("F2") + " GB";
        }
    }
}
