using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace SCADPlugin.Editor
{
    public static class ScadImporterSettings
    {
        const string KeyPath = "SCADPlugin.OpenScadPath";

        public static string ExecutablePath
        {
            get => EditorPrefs.GetString(KeyPath, string.Empty);
            set => EditorPrefs.SetString(KeyPath, value ?? string.Empty);
        }

        public static string ResolveExecutablePath()
        {
            var user = ExecutablePath;
            if (!string.IsNullOrEmpty(user) && File.Exists(user)) return user;

            var bundled = ScadInstaller.ResolveBundledExecutable();
            if (!string.IsNullOrEmpty(bundled)) return bundled;

            foreach (var candidate in DefaultCandidates())
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static IEnumerable<string> DefaultCandidates()
        {
#if UNITY_EDITOR_OSX
            yield return "/Applications/OpenSCAD.app/Contents/MacOS/OpenSCAD";
            yield return "/opt/homebrew/bin/openscad";
            yield return "/usr/local/bin/openscad";
#elif UNITY_EDITOR_WIN
            yield return @"C:\Program Files\OpenSCAD\openscad.exe";
            yield return @"C:\Program Files (x86)\OpenSCAD\openscad.exe";
#else
            yield return "/usr/bin/openscad";
            yield return "/usr/local/bin/openscad";
#endif
        }
    }
}
