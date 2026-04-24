using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace SCADPlugin.Editor
{
    [ScriptedImporter(9, "scad")]
    public class ScadImporter : ScriptedImporter
    {
        [SerializeField]
        public List<ScadParameter> parameters = new List<ScadParameter>();

        [SerializeField]
        public float scale = 1.0f;

        [SerializeField]
        public bool weldVertices = true;

        [SerializeField]
        public bool recalculateNormals = true;

        [SerializeField]
        public bool skipCompile = true;

        [SerializeField]
        public bool perColorSubmeshes = true;

        [SerializeField]
        public int compileTimeoutSeconds = 120;

        // 0 (or negative) = auto: use logical core count. Explicit positive values
        // cap concurrency — lower this if heavy models push your machine into swap.
        [SerializeField]
        public int maxParallelCompiles = 0;

        const int MaxTriangles = 5_000_000;

        // SCAD identifier for the injected color filter. Chosen to be unlikely to
        // collide with any user-defined variable.
        const string FilterVarName = "__scadplugin_target_color__";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var source = File.ReadAllText(ctx.assetPath);
            var parsed = ScadParameterParser.Parse(source);

            var merged = new List<ScadParameter>(parsed.Count);
            foreach (var def in parsed)
            {
                var previous = parameters.Find(p => p != null && p.name == def.name);
                if (previous != null && !string.IsNullOrEmpty(previous.value))
                    def.value = previous.value;
                merged.Add(def);
            }
            parameters = merged;

            var assetName = Path.GetFileNameWithoutExtension(ctx.assetPath);

            if (skipCompile)
            {
                var placeholder = new Mesh { name = assetName };
                ctx.AddObjectToAsset("mesh", placeholder);
                ctx.SetMainObject(placeholder);
                return;
            }

            var timeoutMs = Mathf.Max(5, compileTimeoutSeconds) * 1000;
            var colors = perColorSubmeshes
                ? ScadColorExtractor.Extract(source, parameters)
                : new List<string>();

            if (colors.Count > 0)
                ImportMultiColor(ctx, assetName, colors, timeoutMs);
            else
                ImportSingle(ctx, assetName, timeoutMs);
        }

        void ImportSingle(AssetImportContext ctx, string assetName, int timeoutMs)
        {
            Debug.Log($"[SCADPlugin] Compiling {ctx.assetPath}...");
            var result = ScadCompiler.Compile(ctx.assetPath, parameters, timeoutMs);

            if (!result.success)
            {
                ctx.LogImportError($"[SCADPlugin] {result.log}");
                var placeholder = new Mesh { name = assetName };
                ctx.AddObjectToAsset("mesh", placeholder);
                ctx.SetMainObject(placeholder);
                return;
            }

            if (!CheckTriangleLimit(ctx, result.stlPath, assetName)) return;

            var mesh = StlMeshLoader.Load(result.stlPath, scale, weldVertices, out var stats);
            mesh.name = assetName;
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            LogWeldStats(assetName, stats);

            ctx.AddObjectToAsset("mesh", mesh);

            var root = new GameObject(assetName);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>().sharedMaterial = BuildSingleTintMaterial(assetName, ctx);

            ctx.AddObjectToAsset("root", root);
            ctx.SetMainObject(root);
        }

        void ImportMultiColor(AssetImportContext ctx, string assetName, List<string> colors, int timeoutMs)
        {
            const string preamble =
                "module color(c, alpha=1) { if (c == " + FilterVarName + ") children(); }";

            // Capture everything the worker threads need from main-thread-only sources
            // (ctx.assetPath and EditorPrefs-backed ResolveExecutablePath) before we
            // spawn any tasks.
            var assetPath = ctx.assetPath;
            var exePath = ScadImporterSettings.ResolveExecutablePath();
            if (string.IsNullOrEmpty(exePath))
            {
                ctx.LogImportError("[SCADPlugin] OpenSCAD executable not found. Set its path in Unity → Settings → Preferences → SCAD Plugin.");
                var placeholder = new Mesh { name = assetName };
                ctx.AddObjectToAsset("mesh", placeholder);
                ctx.SetMainObject(placeholder);
                return;
            }

            // Snapshot the parameters into an immutable array so workers read a stable
            // view without touching any SerializedObject under the hood.
            var paramSnapshot = parameters.ToArray();

            int requested = maxParallelCompiles > 0 ? maxParallelCompiles : System.Environment.ProcessorCount;
            int parallelism = Mathf.Clamp(requested, 1, colors.Count);
            Debug.Log(
                $"[SCADPlugin] Compiling {assetPath} as {colors.Count} colored submeshes " +
                $"({parallelism} in parallel)...");

            var results = new ScadCompiler.Result[colors.Count];
            using (var sem = new SemaphoreSlim(parallelism))
            {
                var tasks = new Task[colors.Count];
                for (int i = 0; i < colors.Count; i++)
                {
                    int idx = i;
                    var colorLiteral = colors[idx];
                    tasks[idx] = Task.Run(() =>
                    {
                        sem.Wait();
                        try
                        {
                            // Extractor already returns SCAD syntax: "..." for strings,
                            // [...] for vectors. Pass verbatim.
                            var defines = new Dictionary<string, string>
                            {
                                { FilterVarName, colorLiteral },
                            };
                            results[idx] = ScadCompiler.CompileWithPreamble(
                                assetPath, paramSnapshot, preamble, defines, timeoutMs, exePath);
                        }
                        catch (System.Exception ex)
                        {
                            results[idx] = new ScadCompiler.Result { success = false, log = ex.ToString() };
                        }
                        finally { sem.Release(); }
                    });
                }
                Task.WaitAll(tasks);
            }

            var parts = new List<(List<Vector3> verts, List<Vector3> normals, List<int> tris, Color color)>();
            int totalTriangles = 0;

            for (int i = 0; i < colors.Count; i++)
            {
                var colorStr = colors[i];
                var r = results[i];

                if (r.emptyOutput)
                {
                    // Filter eliminated all geometry — expected when a color only
                    // appears inside a difference() without a positive role.
                    continue;
                }
                if (!r.success)
                {
                    ctx.LogImportError($"[SCADPlugin] pass {colorStr} failed: {r.log}");
                    continue;
                }

                var triCount = StlMeshLoader.PeekBinaryTriangleCount(r.stlPath);
                if (triCount > MaxTriangles)
                {
                    ctx.LogImportError(
                        $"[SCADPlugin] pass {colorStr}: {triCount:N0} triangles exceeds {MaxTriangles:N0} limit. Skipping.");
                    continue;
                }

                var partialMesh = StlMeshLoader.Load(r.stlPath, scale, weldVertices, out _);
                if (partialMesh.vertexCount == 0) continue;

                totalTriangles += partialMesh.triangles.Length / 3;
                var col = ParseColor(colorStr);
                parts.Add((new List<Vector3>(partialMesh.vertices),
                           new List<Vector3>(partialMesh.normals),
                           new List<int>(partialMesh.triangles),
                           col));
                Object.DestroyImmediate(partialMesh);
            }

            if (parts.Count == 0)
            {
                ctx.LogImportError("[SCADPlugin] No geometry produced by any color pass.");
                var placeholder = new Mesh { name = assetName };
                ctx.AddObjectToAsset("mesh", placeholder);
                ctx.SetMainObject(placeholder);
                return;
            }

            var combined = CombineSubmeshes(parts, assetName);
            if (recalculateNormals) combined.RecalculateNormals();
            combined.RecalculateBounds();

            Debug.Log(
                $"[SCADPlugin] {assetName}: {parts.Count} submeshes, " +
                $"{totalTriangles:N0} tris, {combined.vertexCount:N0} verts.");

            ctx.AddObjectToAsset("mesh", combined);

            var mats = new Material[parts.Count];
            var baseMat = ResolveDefaultMaterial();
            for (int i = 0; i < parts.Count; i++)
            {
                var mat = BuildTintedMaterial(baseMat, parts[i].color, $"{assetName}_{i:D2}");
                ctx.AddObjectToAsset($"mat_{i:D2}", mat);
                mats[i] = mat;
            }

            var root = new GameObject(assetName);
            root.AddComponent<MeshFilter>().sharedMesh = combined;
            root.AddComponent<MeshRenderer>().sharedMaterials = mats;

            ctx.AddObjectToAsset("root", root);
            ctx.SetMainObject(root);
        }

        static Mesh CombineSubmeshes(
            List<(List<Vector3> verts, List<Vector3> normals, List<int> tris, Color color)> parts,
            string meshName)
        {
            var combinedVerts = new List<Vector3>();
            var combinedNormals = new List<Vector3>();
            var submeshTris = new List<int[]>(parts.Count);

            int offset = 0;
            foreach (var part in parts)
            {
                combinedVerts.AddRange(part.verts);
                if (part.normals.Count == part.verts.Count)
                    combinedNormals.AddRange(part.normals);
                else
                    for (int i = 0; i < part.verts.Count; i++) combinedNormals.Add(Vector3.up);

                var shifted = new int[part.tris.Count];
                for (int i = 0; i < part.tris.Count; i++) shifted[i] = part.tris[i] + offset;
                submeshTris.Add(shifted);
                offset += part.verts.Count;
            }

            var mesh = new Mesh { name = meshName };
            if (combinedVerts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(combinedVerts);
            mesh.SetNormals(combinedNormals);
            mesh.subMeshCount = submeshTris.Count;
            for (int i = 0; i < submeshTris.Count; i++)
                mesh.SetTriangles(submeshTris[i], i);
            return mesh;
        }

        // Accepts the SCAD literal forms the extractor produces:
        //   "#C3F9BC"              — hex string literal (including surrounding quotes)
        //   "red"                  — named color literal
        //   [0.55, 0.38, 0.24]     — RGB vector (0–1)
        //   [0.55, 0.38, 0.24, 1]  — RGBA vector
        static Color ParseColor(string literal)
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
                var inside = t.Substring(1, t.Length - 2);
                var parts = inside.Split(',');
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                float r = 1, g = 1, b = 1, a = 1;
                if (parts.Length >= 1) float.TryParse(parts[0].Trim(), ns, ci, out r);
                if (parts.Length >= 2) float.TryParse(parts[1].Trim(), ns, ci, out g);
                if (parts.Length >= 3) float.TryParse(parts[2].Trim(), ns, ci, out b);
                if (parts.Length >= 4) float.TryParse(parts[3].Trim(), ns, ci, out a);
                return new Color(r, g, b, a);
            }

            return Color.white;
        }

        static Material BuildTintedMaterial(Material baseMat, Color tint, string name)
        {
            if (baseMat == null || baseMat.shader == null) return baseMat;
            var mat = new Material(baseMat) { name = name };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            return mat;
        }

        bool CheckTriangleLimit(AssetImportContext ctx, string stlPath, string assetName)
        {
            var triCount = StlMeshLoader.PeekBinaryTriangleCount(stlPath);
            if (triCount > MaxTriangles)
            {
                ctx.LogImportError(
                    $"[SCADPlugin] STL contains {triCount:N0} triangles, which exceeds the {MaxTriangles:N0} limit. " +
                    "Reduce model complexity or lower $fn. Aborting import to prevent editor OOM.");
                var placeholder = new Mesh { name = assetName };
                ctx.AddObjectToAsset("mesh", placeholder);
                ctx.SetMainObject(placeholder);
                return false;
            }
            return true;
        }

        void LogWeldStats(string assetName, StlMeshLoader.LoadStats stats)
        {
            if (!weldVertices || stats.rawVertexCount == 0) return;
            var pct = 100f * (1f - (float)stats.weldedVertexCount / stats.rawVertexCount);
            Debug.Log(
                $"[SCADPlugin] {assetName}: {stats.triangleCount:N0} tris, " +
                $"{stats.rawVertexCount:N0} → {stats.weldedVertexCount:N0} verts ({pct:F1}% welded).");
        }

        static Material ResolveDefaultMaterial()
        {
            var pipeline = GraphicsSettings.defaultRenderPipeline ?? GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultMaterial != null)
                return pipeline.defaultMaterial;
            return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
        }

        Material BuildSingleTintMaterial(string assetName, AssetImportContext ctx)
        {
            var baseMat = ResolveDefaultMaterial();
            if (baseMat == null || baseMat.shader == null) return baseMat;

            Color? tint = null;
            foreach (var p in parameters)
            {
                if (p == null || p.type != ScadParameterType.String) continue;
                var raw = p.value ?? p.defaultValue;
                if (string.IsNullOrEmpty(raw)) continue;
                var unquoted = (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                    ? raw.Substring(1, raw.Length - 2) : raw;
                if (ScadImporterEditor.IsHexColor(unquoted))
                {
                    tint = ScadImporterEditor.HexToColor(unquoted);
                    break;
                }
            }

            if (!tint.HasValue) return baseMat;

            var mat = BuildTintedMaterial(baseMat, tint.Value, assetName + "_Material");
            ctx.AddObjectToAsset("material", mat);
            return mat;
        }
    }
}
