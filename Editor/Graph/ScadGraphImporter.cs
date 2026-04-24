using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace SCADPlugin.Editor.Graph
{
    [ScriptedImporter(version: 1, ext: "scadgraph")]
    public class ScadGraphImporter : ScriptedImporter
    {
        public float scale = 1f;
        public bool weldVertices = true;
        public bool recalculateNormals = true;
        public int compileTimeoutSeconds = 120;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var graph = LoadOrCreate(ctx.assetPath);

            // The graph SO is always emitted as a sub-asset so the editor
            // can bind to it directly via AssetDatabase.LoadAssetAtPath.
            graph.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset("graph", graph);

            var compiler = new ScadGraphCompiler(graph);
            var source = compiler.Compile();

            var sourceText = new TextAsset(source) { name = "Generated.scad" };
            ctx.AddObjectToAsset("source", sourceText);

            if (compiler.Diagnostics.Count > 0)
            {
                foreach (var d in compiler.Diagnostics)
                    ctx.LogImportWarning(d);
            }

            var exe = ScadImporterSettings.ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                ctx.LogImportError(
                    "OpenSCAD executable not configured (Preferences → SCAD Plugin). " +
                    "Graph saved, but no mesh produced.");
                var empty = new Mesh { name = graph.name };
                ctx.AddObjectToAsset("mesh", empty);
                ctx.SetMainObject(empty);
                return;
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"scadgraph_{Guid.NewGuid():N}.scad");
            try
            {
                File.WriteAllText(tempPath, source);

                var task = ScadLiveCompiler.CompileAsync(
                    scadPath: tempPath,
                    parameters: Array.Empty<ScadParameter>(),
                    colorLiterals: null,
                    maxParallelCompiles: 0,
                    timeoutMs: Mathf.Max(5, compileTimeoutSeconds) * 1000,
                    exePath: exe,
                    scale: scale,
                    weldVertices: weldVertices,
                    ct: CancellationToken.None);

                task.Wait();
                var result = task.Result;

                if (!result.success)
                {
                    ctx.LogImportError("OpenSCAD compile failed: " + result.error);
                    var empty = new Mesh { name = graph.name };
                    ctx.AddObjectToAsset("mesh", empty);
                    ctx.SetMainObject(empty);
                    return;
                }

                var mesh = BuildMesh(result.parts, graph.name);
                if (recalculateNormals) mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                ctx.AddObjectToAsset("mesh", mesh);
                ctx.SetMainObject(mesh);
            }
            catch (Exception ex)
            {
                ctx.LogImportError("ScadGraphImporter failed: " + ex.GetBaseException().Message);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        static ScadGraph LoadOrCreate(string assetPath)
        {
            var graph = ScriptableObject.CreateInstance<ScadGraph>();
            string json = null;
            try { json = File.ReadAllText(assetPath); } catch { }

            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            {
                // Empty file: synthesise a minimal default graph so the
                // asset is immediately usable.
                var output = new Nodes.OutputNode();
                graph.AddNode(output, new Vector2(200, 0));
                graph.outputNodeId = output.id;
            }
            else
            {
                try { EditorJsonUtility.FromJsonOverwrite(json, graph); }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to parse .scadgraph '{assetPath}': {ex.Message}");
                }
            }

            graph.nodes ??= new List<ScadNode>();
            graph.connections ??= new List<ScadConnection>();
            graph.exposedParameters ??= new List<ScadExposedParameter>();
            return graph;
        }

        static Mesh BuildMesh(List<ScadLiveCompiler.RawPart> parts, string name)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var subs = new List<int[]>(parts.Count);
            int offset = 0;
            foreach (var p in parts)
            {
                verts.AddRange(p.verts);
                if (p.normals != null && p.normals.Count == p.verts.Count)
                    normals.AddRange(p.normals);
                else
                    for (int i = 0; i < p.verts.Count; i++) normals.Add(Vector3.up);
                var shifted = new int[p.tris.Count];
                for (int i = 0; i < p.tris.Count; i++) shifted[i] = p.tris[i] + offset;
                subs.Add(shifted);
                offset += p.verts.Count;
            }
            var mesh = new Mesh { name = name };
            if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.subMeshCount = Mathf.Max(1, subs.Count);
            for (int i = 0; i < subs.Count; i++) mesh.SetTriangles(subs[i], i);
            return mesh;
        }
    }

    // Menu entry: creates a blank .scadgraph file. The importer synthesises
    // a default graph on first import.
    internal static class ScadGraphCreateMenu
    {
        [MenuItem("Assets/Create/SCAD/Graph", false, 81)]
        static void Create()
        {
            ProjectWindowUtil.CreateAssetWithContent(
                "NewScadGraph.scadgraph",
                "{}",
                icon: null);
        }
    }

    // Double-click on a .scadgraph opens our editor window rather than
    // Unity's default JSON text-editor fallback.
    internal static class ScadGraphOpenHandler
    {
        [UnityEditor.Callbacks.OnOpenAsset(1)]
        static bool OnOpen(int instanceID, int line)
        {
            var path = AssetDatabase.GetAssetPath(instanceID);
            if (string.IsNullOrEmpty(path)) return false;
            if (!path.EndsWith(".scadgraph", StringComparison.OrdinalIgnoreCase)) return false;
            UI.ScadGraphEditorWindow.OpenForAsset(path);
            return true;
        }
    }
}
