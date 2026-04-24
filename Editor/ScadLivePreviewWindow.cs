using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SCADPlugin.Editor
{
    // Minimal live-preview window: drop a .scad asset, edit parameters,
    // background-compiles via OpenSCAD on edit. No tabs, no foldouts, no overlays
    // — the whole job is "show the parameters and the resulting mesh, reliably".
    public class ScadLivePreviewWindow : EditorWindow
    {
        [MenuItem("Window/SCAD Live Preview")]
        public static void Open() => GetWindow<ScadLivePreviewWindow>("SCAD Live Preview");

        [SerializeField] bool useInlineCode = false;
        [SerializeField] string inlineCode = "cube([10, 10, 10]);\n";
        [SerializeField] string _instanceGuid;
        [SerializeField] Object scadAsset;
        [SerializeField] List<ScadParameter> parameters = new List<ScadParameter>();
        [SerializeField] bool liveUpdate = true;
        [SerializeField] float scale = 1f;
        [SerializeField] bool weldVertices = true;
        [SerializeField] bool recalculateNormals = true;
        [SerializeField] bool perColorSubmeshes = true;
        [SerializeField] int previewFnOverride = 12;
        [SerializeField] int maxParallelCompiles = 0;
        [SerializeField] int compileTimeoutSeconds = 120;

        // Orbit camera state — serialized so view survives reloads.
        [SerializeField] float _pitch = 20f;
        [SerializeField] float _yaw = 30f;
        [SerializeField] float _distance = 10f;
        [SerializeField] Vector3 _target;

        // Runtime state.
        Object _lastAsset;
        double _dirtyAt;
        Task<ScadLiveCompiler.Result> _activeTask;
        CancellationTokenSource _activeCts;
        string _status = "Idle";
        Vector2 _scroll;

        // Preview rendering.
        PreviewRenderUtility _pru;
        Mesh _previewMesh;
        Material[] _previewMats;
        readonly List<Object> _pendingDestroy = new List<Object>();

        const float PreviewHeight = 260f;

        // Per-window temp file used as the on-disk source when in Inline mode.
        // The GUID is generated lazily so multiple windows don't collide.
        string InlineTempPath
        {
            get
            {
                if (string.IsNullOrEmpty(_instanceGuid))
                    _instanceGuid = Guid.NewGuid().ToString("N");
                return Path.Combine(Path.GetTempPath(),
                    $"scadplugin_inline_{_instanceGuid}.scad");
            }
        }

        void OnEnable()
        {
            if (parameters == null) parameters = new List<ScadParameter>();
            EditorApplication.update += OnUpdate;
            EnsurePreviewUtility();

            // Domain reload: scadAsset / inlineCode are restored but parameter
            // extraction is not. Re-extract immediately so the panel isn't empty.
            if (useInlineCode)
            {
                if (!string.IsNullOrEmpty(inlineCode)) ReadParametersFromInline();
            }
            else if (scadAsset != null)
            {
                _lastAsset = scadAsset;
                ReadParametersFromAsset();
            }
        }

        void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            try { _activeCts?.Cancel(); } catch { }
            _activeTask = null;
            _activeCts = null;
            FlushPendingDestroys();
            DestroyPreviewAssets(immediate: true);
            if (_pru != null) { _pru.Cleanup(); _pru = null; }
            try { if (File.Exists(InlineTempPath)) File.Delete(InlineTempPath); } catch { }
        }

        void OnUpdate()
        {
            FlushPendingDestroys();

            if (_lastAsset != scadAsset)
            {
                _lastAsset = scadAsset;
                ReadParametersFromAsset();
            }

            if (liveUpdate && _dirtyAt > 0 && _activeTask == null
                && EditorApplication.timeSinceStartup - _dirtyAt >= 0.15)
            {
                _dirtyAt = 0;
                StartCompile();
            }

            if (_activeTask != null && _activeTask.IsCompleted)
            {
                var task = _activeTask;
                _activeTask = null;
                _activeCts?.Dispose();
                _activeCts = null;
                ApplyResult(task);
                Repaint();
            }
        }

        // ---------- GUI ----------

        void OnGUI()
        {
            if (parameters == null) parameters = new List<ScadParameter>();

            // Source mode toggle.
            EditorGUI.BeginChangeCheck();
            var modeIdx = GUILayout.Toolbar(
                useInlineCode ? 1 : 0,
                new[] { "File", "Inline Text" });
            if (EditorGUI.EndChangeCheck())
            {
                useInlineCode = modeIdx == 1;
                if (useInlineCode) ReadParametersFromInline();
                else ReadParametersFromAsset();
            }

            if (!useInlineCode)
            {
                // Asset picker.
                EditorGUI.BeginChangeCheck();
                var picked = (Object)EditorGUILayout.ObjectField(
                    "SCAD Asset", scadAsset, typeof(Object), false);
                if (EditorGUI.EndChangeCheck())
                {
                    if (picked != null)
                    {
                        var path = AssetDatabase.GetAssetPath(picked);
                        if (string.IsNullOrEmpty(path) ||
                            !path.EndsWith(".scad", StringComparison.OrdinalIgnoreCase))
                        {
                            EditorUtility.DisplayDialog(
                                "Not a .scad file",
                                "Drop a .scad asset.",
                                "OK");
                            picked = scadAsset;
                        }
                    }
                    scadAsset = picked;
                    _lastAsset = scadAsset;
                    ReadParametersFromAsset();
                }
            }
            else
            {
                EditorGUILayout.LabelField("SCAD Source", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                var style = new GUIStyle(EditorStyles.textArea) { font = EditorStyles.standardFont };
                inlineCode = EditorGUILayout.TextArea(
                    inlineCode ?? string.Empty, style,
                    GUILayout.MinHeight(120), GUILayout.MaxHeight(220));
                if (EditorGUI.EndChangeCheck()) ReadParametersFromInline();
            }

            // Settings (flat — no foldouts).
            liveUpdate = EditorGUILayout.Toggle("Live Update", liveUpdate);
            previewFnOverride = EditorGUILayout.IntField(
                new GUIContent("Preview $fn", "Force $fn during preview for speed. 0 = file's own value."),
                previewFnOverride);
            perColorSubmeshes = EditorGUILayout.Toggle("Per-Color Submeshes", perColorSubmeshes);
            scale = EditorGUILayout.FloatField("Scale", scale);
            weldVertices = EditorGUILayout.Toggle("Weld Vertices", weldVertices);
            recalculateNormals = EditorGUILayout.Toggle("Smooth Normals", recalculateNormals);
            maxParallelCompiles = EditorGUILayout.IntField(
                new GUIContent("Max Parallel Compiles", "0 = logical core count."), maxParallelCompiles);

            // Preview viewport.
            DrawPreview();

            // Parameters.
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"Parameters ({parameters.Count})", EditorStyles.boldLabel);

            if (parameters.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    useInlineCode
                        ? "No top-level variables in the inline source. Add e.g. `width = 20; // [5:100]`."
                        : (scadAsset == null
                            ? "Drop a .scad asset above."
                            : "No top-level variables in the selected .scad."),
                    MessageType.Info);
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120));
                EditorGUI.BeginChangeCheck();
                string currentGroup = null;
                for (int i = 0; i < parameters.Count; i++)
                {
                    var p = parameters[i];
                    if (p == null) continue;
                    if (!string.Equals(p.group, currentGroup))
                    {
                        currentGroup = p.group;
                        if (!string.IsNullOrEmpty(currentGroup))
                            EditorGUILayout.LabelField(currentGroup, EditorStyles.boldLabel);
                    }
                    DrawParameter(p);
                }
                if (EditorGUI.EndChangeCheck()) MarkDirty();
                EditorGUILayout.EndScrollView();

                if (GUILayout.Button("Reset Parameters to Defaults"))
                {
                    foreach (var p in parameters)
                        if (p != null) p.value = p.defaultValue;
                    MarkDirty();
                }
            }

            // Action buttons.
            EditorGUILayout.Space();
            bool canCompile = useInlineCode
                ? !string.IsNullOrWhiteSpace(inlineCode)
                : scadAsset != null;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!canCompile || _activeTask != null))
                {
                    if (GUILayout.Button("Compile Now"))
                    {
                        _dirtyAt = 0;
                        StartCompile();
                    }
                    if (useInlineCode)
                    {
                        if (GUILayout.Button("Save as .scad Asset…")) SaveInlineAsAsset();
                    }
                    else
                    {
                        if (GUILayout.Button("Commit to Asset")) CommitToAsset();
                    }
                }
                using (new EditorGUI.DisabledScope(_activeTask == null))
                {
                    if (GUILayout.Button("Cancel")) CancelActive();
                }
            }

            EditorGUILayout.LabelField("Status: " + _status, EditorStyles.miniLabel);
        }

        // Direct, side-effect-free parameter row drawing. No SerializedObject —
        // each row reads/writes ScadParameter.value directly. This keeps the
        // control count identical between Layout and Repaint passes.
        void DrawParameter(ScadParameter p)
        {
            var label = new GUIContent(
                ObjectNames.NicifyVariableName(p.name),
                string.IsNullOrEmpty(p.description) ? p.name : p.description);
            var ci = CultureInfo.InvariantCulture;

            switch (p.type)
            {
                case ScadParameterType.Boolean:
                {
                    bool cur = p.value == "true";
                    bool next = EditorGUILayout.Toggle(label, cur);
                    if (next != cur) p.value = next ? "true" : "false";
                    break;
                }
                case ScadParameterType.Integer:
                {
                    int.TryParse(p.value ?? "0", NumberStyles.Integer, ci, out var cur);
                    int next = p.hasRange
                        ? EditorGUILayout.IntSlider(label, cur, (int)p.min, (int)p.max)
                        : EditorGUILayout.IntField(label, cur);
                    if (next != cur) p.value = next.ToString(ci);
                    break;
                }
                case ScadParameterType.Number:
                {
                    double.TryParse(p.value ?? "0", NumberStyles.Float, ci, out var curD);
                    float cur = (float)curD;
                    float next = p.hasRange
                        ? EditorGUILayout.Slider(label, cur, (float)p.min, (float)p.max)
                        : EditorGUILayout.FloatField(label, cur);
                    if (!Mathf.Approximately(next, cur))
                        p.value = next.ToString("R", ci);
                    break;
                }
                case ScadParameterType.String:
                {
                    var raw = p.value ?? "";
                    var shown = (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                        ? raw.Substring(1, raw.Length - 2) : raw;
                    if (IsHexColor(shown))
                    {
                        var cur = HexToColor(shown);
                        var next = EditorGUILayout.ColorField(label, cur);
                        if (next != cur) p.value = "\"" + ColorToHex(next) + "\"";
                    }
                    else
                    {
                        var next = EditorGUILayout.TextField(label, shown);
                        if (next != shown) p.value = "\"" + next + "\"";
                    }
                    break;
                }
                case ScadParameterType.ColorVector:
                {
                    var cur = VectorToColor(p.value);
                    var next = EditorGUILayout.ColorField(label, cur);
                    if (next != cur) p.value = ColorToVector(next);
                    break;
                }
                case ScadParameterType.NumberDropdown:
                case ScadParameterType.StringDropdown:
                {
                    if (p.choices == null || p.choices.Count == 0) break;
                    var labels = p.choices.Select(c => c.label).ToArray();
                    int idx = 0;
                    for (int i = 0; i < p.choices.Count; i++)
                        if (p.choices[i].value == p.value) { idx = i; break; }
                    int next = EditorGUILayout.Popup(label, idx, labels);
                    if (next != idx) p.value = p.choices[next].value;
                    break;
                }
            }
        }

        // ---------- Preview rendering ----------

        void DrawPreview()
        {
            var rect = GUILayoutUtility.GetRect(
                200, PreviewHeight,
                GUILayout.ExpandWidth(true), GUILayout.Height(PreviewHeight));
            if (rect.width < 10 || rect.height < 10) return;

            HandleOrbitInput(rect);

            if (_previewMesh == null)
            {
                EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.2f));
                GUI.Label(rect, "No preview yet.",
                    new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                    { alignment = TextAnchor.MiddleCenter });
                return;
            }

            if (Event.current.type != EventType.Repaint) return;

            EnsurePreviewUtility();
            UpdatePreviewCamera();
            _pru.BeginPreview(rect, GUIStyle.none);
            for (int i = 0; i < _previewMesh.subMeshCount; i++)
            {
                var mat = (_previewMats != null && i < _previewMats.Length) ? _previewMats[i] : null;
                if (mat == null) continue;
                _pru.DrawMesh(_previewMesh, Matrix4x4.identity, mat, i);
            }
            _pru.camera.Render();
            var tex = _pru.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        void HandleOrbitInput(Rect rect)
        {
            var e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;
            switch (e.type)
            {
                case EventType.MouseDrag when e.button == 0:
                    _yaw += e.delta.x * 0.5f;
                    _pitch = Mathf.Clamp(_pitch - e.delta.y * 0.5f, -89f, 89f);
                    e.Use(); Repaint(); break;
                case EventType.ScrollWheel:
                    _distance = Mathf.Max(_distance * (1f + e.delta.y * 0.05f), 0.01f);
                    e.Use(); Repaint(); break;
                case EventType.MouseDown when e.button == 0 && e.clickCount == 2:
                    FrameMesh(); e.Use(); Repaint(); break;
            }
        }

        void EnsurePreviewUtility()
        {
            if (_pru != null) return;
            _pru = new PreviewRenderUtility();
            _pru.cameraFieldOfView = 30f;
            _pru.camera.clearFlags = CameraClearFlags.SolidColor;
            _pru.camera.backgroundColor = new Color(0.18f, 0.18f, 0.2f);
            _pru.lights[0].intensity = 1.2f;
            _pru.lights[0].transform.rotation = Quaternion.Euler(45, -45, 0);
            _pru.lights[1].intensity = 0.6f;
        }

        void UpdatePreviewCamera()
        {
            var rot = Quaternion.Euler(_pitch, _yaw, 0);
            _pru.camera.transform.position = _target + rot * new Vector3(0, 0, -_distance);
            _pru.camera.transform.LookAt(_target);
            _pru.camera.nearClipPlane = Mathf.Max(_distance * 0.01f, 0.01f);
            _pru.camera.farClipPlane = _distance * 10f;
        }

        void FrameMesh()
        {
            if (_previewMesh == null) return;
            var b = _previewMesh.bounds;
            _target = b.center;
            var radius = Mathf.Max(b.extents.magnitude, 0.01f);
            _distance = radius / Mathf.Tan(15f * Mathf.Deg2Rad) * 1.2f;
        }

        // ---------- Asset / parameter sync ----------

        void ReadParametersFromInline()
        {
            if (parameters == null) parameters = new List<ScadParameter>();
            var parsed = ScadParameterParser.Parse(inlineCode ?? string.Empty)
                ?? new List<ScadParameter>();
            var merged = new List<ScadParameter>(parsed.Count);
            foreach (var def in parsed)
            {
                if (def == null) continue;
                var prev = parameters.Find(x => x != null && x.name == def.name);
                if (prev != null && !string.IsNullOrEmpty(prev.value))
                    def.value = prev.value;
                merged.Add(def);
            }
            parameters = merged;
            _status = "Idle";
            MarkDirty();
        }

        void ReadParametersFromAsset()
        {
            if (parameters == null) parameters = new List<ScadParameter>();

            if (scadAsset == null)
            {
                parameters.Clear();
                _status = "Idle";
                return;
            }
            var path = AssetDatabase.GetAssetPath(scadAsset);
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(".scad", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Clear();
                _status = "Asset is not a .scad file";
                return;
            }

            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception ex) { _status = "Read failed: " + ex.Message; return; }

            var parsed = ScadParameterParser.Parse(source) ?? new List<ScadParameter>();
            var merged = new List<ScadParameter>(parsed.Count);
            foreach (var def in parsed)
            {
                if (def == null) continue;
                var prev = parameters.Find(x => x != null && x.name == def.name);
                if (prev != null && !string.IsNullOrEmpty(prev.value))
                    def.value = prev.value;
                merged.Add(def);
            }
            parameters = merged;
            _status = "Idle";
            MarkDirty();
        }

        void MarkDirty()
        {
            _dirtyAt = EditorApplication.timeSinceStartup;
            _status = "Dirty — recompiling soon";
        }

        // ---------- Compile pipeline ----------

        void StartCompile()
        {
            if (_activeTask != null) return;

            string path;
            string source;
            if (useInlineCode)
            {
                if (string.IsNullOrWhiteSpace(inlineCode)) return;
                path = InlineTempPath;
                source = inlineCode ?? string.Empty;
                try { File.WriteAllText(path, source); }
                catch (Exception ex) { _status = "Cannot write temp: " + ex.Message; return; }
            }
            else
            {
                if (scadAsset == null) return;
                path = AssetDatabase.GetAssetPath(scadAsset);
                if (string.IsNullOrEmpty(path)) return;
                try { source = File.ReadAllText(path); }
                catch (Exception ex) { _status = "Read failed: " + ex.Message; return; }
            }

            var exe = ScadImporterSettings.ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                _status = "OpenSCAD not configured. Preferences → SCAD Plugin.";
                return;
            }

            var paramList = new List<ScadParameter>(parameters);
            if (previewFnOverride > 0)
            {
                paramList.Add(new ScadParameter
                {
                    name = "$fn",
                    value = previewFnOverride.ToString(CultureInfo.InvariantCulture),
                    type = ScadParameterType.Integer,
                });
            }
            var snap = paramList.ToArray();

            List<string> colors = perColorSubmeshes
                ? ScadColorExtractor.Extract(source, snap)
                : null;

            _activeCts = new CancellationTokenSource();
            _status = "Compiling…";
            var timeoutMs = Mathf.Max(5, compileTimeoutSeconds) * 1000;
            _activeTask = ScadLiveCompiler.CompileAsync(
                path, snap, colors, maxParallelCompiles, timeoutMs, exe,
                scale, weldVertices, _activeCts.Token);
        }

        void CancelActive()
        {
            try { _activeCts?.Cancel(); } catch { }
            _activeTask = null;
            _activeCts = null;
            _status = "Cancelled";
        }

        void ApplyResult(Task<ScadLiveCompiler.Result> task)
        {
            if (task.IsFaulted)
            {
                _status = "Error: " + (task.Exception?.GetBaseException().Message ?? "unknown");
                return;
            }
            if (task.IsCanceled) { _status = "Cancelled"; return; }

            var r = task.Result;
            if (!r.success)
            {
                if (r.error == "Cancelled") return;
                _status = "Failed: " + r.error;
                return;
            }

            DestroyPreviewAssets();
            _previewMesh = BuildMesh(r.parts);
            _previewMesh.hideFlags = HideFlags.DontSave;
            if (recalculateNormals) _previewMesh.RecalculateNormals();
            _previewMesh.RecalculateBounds();
            _previewMats = BuildMaterials(r.parts);
            foreach (var m in _previewMats)
                if (m != null) m.hideFlags = HideFlags.DontSave;
            FrameMesh();

            _status = $"OK · {r.parts.Count} submesh(es), {r.triangleCount:N0} tris, {_previewMesh.vertexCount:N0} verts";
            Repaint();
        }

        Mesh BuildMesh(List<ScadLiveCompiler.RawPart> parts)
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
            var mesh = new Mesh { name = scadAsset != null ? scadAsset.name : "SCAD" };
            if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) mesh.SetTriangles(subs[i], i);
            return mesh;
        }

        Material[] BuildMaterials(List<ScadLiveCompiler.RawPart> parts)
        {
            var baseMat = ResolveDefaultMaterial();
            var mats = new Material[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                var m = new Material(baseMat) { name = "LivePreview_Mat_" + i };
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", parts[i].color);
                if (m.HasProperty("_Color")) m.SetColor("_Color", parts[i].color);
                mats[i] = m;
            }
            return mats;
        }

        static Material ResolveDefaultMaterial()
        {
            var pipe = GraphicsSettings.defaultRenderPipeline ?? GraphicsSettings.currentRenderPipeline;
            if (pipe != null && pipe.defaultMaterial != null) return pipe.defaultMaterial;
            return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
        }

        // ---------- Cleanup helpers ----------

        void DestroyPreviewAssets(bool immediate = false)
        {
            if (immediate)
            {
                if (_previewMesh != null) DestroyImmediate(_previewMesh);
                if (_previewMats != null)
                    foreach (var m in _previewMats)
                        if (m != null) DestroyImmediate(m);
            }
            else
            {
                if (_previewMesh != null) _pendingDestroy.Add(_previewMesh);
                if (_previewMats != null)
                    foreach (var m in _previewMats)
                        if (m != null) _pendingDestroy.Add(m);
            }
            _previewMesh = null;
            _previewMats = null;
        }

        void FlushPendingDestroys()
        {
            if (_pendingDestroy.Count == 0) return;
            foreach (var o in _pendingDestroy) if (o != null) DestroyImmediate(o);
            _pendingDestroy.Clear();
        }

        // ---------- Commit ----------

        void SaveInlineAsAsset()
        {
            if (string.IsNullOrWhiteSpace(inlineCode)) return;
            var target = EditorUtility.SaveFilePanelInProject(
                "Save SCAD source",
                "LiveSource",
                "scad",
                "Save the inline SCAD as a project asset.");
            if (string.IsNullOrEmpty(target)) return;

            try { File.WriteAllText(target, inlineCode); }
            catch (Exception ex) { _status = "Save failed: " + ex.Message; return; }

            AssetDatabase.ImportAsset(target);
            var asset = AssetDatabase.LoadAssetAtPath<Object>(target);
            if (asset != null)
            {
                if (AssetImporter.GetAtPath(target) is ScadImporter importer)
                {
                    importer.parameters = new List<ScadParameter>(parameters);
                    importer.scale = scale;
                    importer.weldVertices = weldVertices;
                    importer.recalculateNormals = recalculateNormals;
                    importer.perColorSubmeshes = perColorSubmeshes;
                    importer.maxParallelCompiles = maxParallelCompiles;
                    importer.compileTimeoutSeconds = compileTimeoutSeconds;
                    importer.skipCompile = false;
                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                }
                useInlineCode = false;
                scadAsset = asset;
                _lastAsset = asset;
                _status = "Saved to " + target + " (switched to File mode)";
            }
        }

        void CommitToAsset()
        {
            if (scadAsset == null) return;
            var path = AssetDatabase.GetAssetPath(scadAsset);
            var importer = AssetImporter.GetAtPath(path) as ScadImporter;
            if (importer == null) { _status = "Asset has no ScadImporter."; return; }
            importer.parameters = new List<ScadParameter>(parameters);
            importer.scale = scale;
            importer.weldVertices = weldVertices;
            importer.recalculateNormals = recalculateNormals;
            importer.perColorSubmeshes = perColorSubmeshes;
            importer.maxParallelCompiles = maxParallelCompiles;
            importer.compileTimeoutSeconds = compileTimeoutSeconds;
            importer.skipCompile = false;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            _status = "Committed to " + path;
        }

        // ---------- Color helpers ----------

        static bool IsHexColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
            int len = s.Length - 1;
            if (len != 3 && len != 4 && len != 6 && len != 8) return false;
            return ColorUtility.TryParseHtmlString(s, out _);
        }
        static Color HexToColor(string hex) =>
            ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
        static string ColorToHex(Color c) =>
            Mathf.Approximately(c.a, 1f)
                ? "#" + ColorUtility.ToHtmlStringRGB(c)
                : "#" + ColorUtility.ToHtmlStringRGBA(c);

        static Color VectorToColor(string literal)
        {
            if (string.IsNullOrEmpty(literal)) return Color.white;
            var t = literal.Trim();
            if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') return Color.white;
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
        static string ColorToVector(Color c)
        {
            var ci = CultureInfo.InvariantCulture;
            return Mathf.Approximately(c.a, 1f)
                ? $"[{c.r.ToString("F3", ci)}, {c.g.ToString("F3", ci)}, {c.b.ToString("F3", ci)}]"
                : $"[{c.r.ToString("F3", ci)}, {c.g.ToString("F3", ci)}, {c.b.ToString("F3", ci)}, {c.a.ToString("F3", ci)}]";
        }
    }
}
