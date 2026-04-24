using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SCADPlugin.Editor
{
    // Unity 6 UI Toolkit live-preview window. Parameters, source, and the preview
    // viewport are rendered with UIElements; the viewport itself uses an
    // IMGUIContainer so PreviewRenderUtility can blit into it.
    public class ScadLivePreviewWindow : EditorWindow
    {
        [MenuItem("Window/SCAD Live Preview")]
        public static void Open()
        {
            var w = GetWindow<ScadLivePreviewWindow>("SCAD Live Preview");
            w.minSize = new Vector2(360, 520);
        }

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

        [SerializeField] float _pitch = 20f;
        [SerializeField] float _yaw = 30f;
        [SerializeField] float _distance = 10f;
        [SerializeField] Vector3 _target;

        [SerializeField] bool _settingsFoldoutOpen = false;
        [SerializeField] string _paramSearch = string.Empty;

        Object _lastAsset;
        double _dirtyAt;
        Task<ScadLiveCompiler.Result> _activeTask;
        CancellationTokenSource _activeCts;
        string _status = "Idle";
        StatusKind _statusKind = StatusKind.Idle;

        PreviewRenderUtility _pru;
        Mesh _previewMesh;
        Material[] _previewMats;
        readonly List<Object> _pendingDestroy = new List<Object>();

        // UI references
        RadioButtonGroup _modeGroup;
        VisualElement _sourceSlot;
        ObjectField _assetField;
        TextField _inlineField;
        IMGUIContainer _preview;
        Label _previewHint;
        Label _previewInfoLabel;
        Foldout _paramsFoldout;
        Label _paramsCountLabel;
        ToolbarSearchField _paramsSearchField;
        ScrollView _paramsScroll;
        HelpBox _paramsEmptyHelp;
        Button _resetParamsBtn;
        Button _compileBtn;
        Button _saveCommitBtn;
        Button _cancelBtn;
        Label _statusLabel;
        VisualElement _statusDot;
        ProgressBar _busyBar;

        enum StatusKind { Idle, Busy, Ok, Warn, Error }

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
                _preview?.MarkDirtyRepaint();
            }
        }

        // ---------- UI Toolkit layout ----------

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            ApplyRootStyle(root);
            ApplySharedStyles(root);

            BuildHeader(root);
            BuildSourceAndPreviewSplit(root);
            BuildCompileSettings(root);
            BuildParametersSection(root);
            BuildActionsBar(root);
            BuildStatusBar(root);

            RefreshSourceSlot();
            RebuildParameterRows();
            UpdateActionButtons();
            SetStatus(_status, _statusKind, repaintPreview: false);
        }

        static void ApplyRootStyle(VisualElement root)
        {
            root.style.flexDirection = FlexDirection.Column;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 4;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
        }

        static void ApplySharedStyles(VisualElement root)
        {
            var sheet = ScriptableObject.CreateInstance<StyleSheet>();
            // Runtime StyleSheets cannot be authored from C#, so fall back to
            // per-element inline styling done in the Build* helpers.
            _ = sheet;
        }

        void BuildHeader(VisualElement root)
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.marginBottom = 6;

            var title = new Label("SCAD Live Preview");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13;
            title.style.flexGrow = 1;
            bar.Add(title);

            _modeGroup = new RadioButtonGroup(null, new List<string> { "File", "Inline" })
            {
                value = useInlineCode ? 1 : 0,
            };
            _modeGroup.style.flexDirection = FlexDirection.Row;
            _modeGroup.RegisterValueChangedCallback(evt =>
            {
                useInlineCode = evt.newValue == 1;
                RefreshSourceSlot();
                if (useInlineCode) ReadParametersFromInline();
                else ReadParametersFromAsset();
                UpdateActionButtons();
            });
            bar.Add(_modeGroup);

            root.Add(bar);
        }

        void BuildSourceAndPreviewSplit(VisualElement root)
        {
            var container = new VisualElement();
            container.style.flexGrow = 1;
            container.style.minHeight = 260;
            container.style.marginBottom = 6;
            container.style.flexShrink = 0;

            var split = new TwoPaneSplitView(
                fixedPaneIndex: 0,
                fixedPaneStartDimension: 160,
                orientation: TwoPaneSplitViewOrientation.Vertical)
            {
                viewDataKey = "scad-live-preview-split",
            };
            split.style.flexGrow = 1;

            split.Add(CreateSourceCard());
            split.Add(CreatePreviewCard());

            container.Add(split);
            root.Add(container);
        }

        VisualElement CreateSourceCard()
        {
            var card = MakeCard();
            card.style.marginBottom = 0;
            card.style.flexGrow = 1;
            card.style.flexShrink = 1;
            card.style.overflow = Overflow.Hidden;
            card.style.minHeight = 40;

            _sourceSlot = new VisualElement();
            _sourceSlot.style.flexGrow = 1;
            _sourceSlot.style.flexDirection = FlexDirection.Column;
            card.Add(_sourceSlot);
            return card;
        }

        void RefreshSourceSlot()
        {
            if (_sourceSlot == null) return;
            _sourceSlot.Clear();

            if (!useInlineCode)
            {
                _assetField = new ObjectField("SCAD Asset")
                {
                    objectType = typeof(Object),
                    allowSceneObjects = false,
                    value = scadAsset,
                };
                _assetField.labelElement.style.minWidth = 120;
                _assetField.RegisterValueChangedCallback(evt =>
                {
                    var picked = evt.newValue;
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
                            _assetField.SetValueWithoutNotify(scadAsset);
                            return;
                        }
                    }
                    scadAsset = picked;
                    _lastAsset = scadAsset;
                    ReadParametersFromAsset();
                    UpdateActionButtons();
                });
                _sourceSlot.Add(_assetField);
            }
            else
            {
                var header = new Label("SCAD Source");
                header.style.unityFontStyleAndWeight = FontStyle.Bold;
                header.style.marginBottom = 4;
                header.style.flexShrink = 0;
                _sourceSlot.Add(header);

                var codeScroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
                codeScroll.style.flexGrow = 1;
                codeScroll.style.minHeight = 40;
                codeScroll.horizontalScrollerVisibility = ScrollerVisibility.Auto;
                codeScroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
                codeScroll.contentContainer.style.flexGrow = 1;

                _inlineField = new TextField
                {
                    multiline = true,
                    value = inlineCode ?? string.Empty,
                };
                _inlineField.style.flexGrow = 1;
                _inlineField.style.whiteSpace = WhiteSpace.NoWrap;

                var textInput = _inlineField.Q(className: TextField.inputUssClassName);
                if (textInput != null)
                {
                    textInput.style.unityFont = EditorStyles.standardFont;
                    textInput.style.whiteSpace = WhiteSpace.NoWrap;
                    textInput.style.flexGrow = 1;
                }

                _inlineField.RegisterValueChangedCallback(evt =>
                {
                    inlineCode = evt.newValue ?? string.Empty;
                    ReadParametersFromInline();
                    UpdateActionButtons();
                });

                codeScroll.Add(_inlineField);
                _sourceSlot.Add(codeScroll);
            }
        }

        VisualElement CreatePreviewCard()
        {
            var card = MakeCard();
            card.style.marginBottom = 0;
            card.style.paddingTop = 4;
            card.style.paddingBottom = 4;
            card.style.paddingLeft = 4;
            card.style.paddingRight = 4;
            card.style.flexGrow = 1;
            card.style.flexShrink = 1;
            card.style.minHeight = 80;
            card.style.overflow = Overflow.Hidden;

            var stack = new VisualElement();
            stack.style.position = Position.Relative;
            stack.style.flexGrow = 1;
            stack.style.borderTopLeftRadius = 4;
            stack.style.borderTopRightRadius = 4;
            stack.style.borderBottomLeftRadius = 4;
            stack.style.borderBottomRightRadius = 4;
            stack.style.overflow = Overflow.Hidden;
            stack.style.backgroundColor = new Color(0.18f, 0.18f, 0.2f);

            _preview = new IMGUIContainer(OnPreviewIMGUI);
            _preview.style.flexGrow = 1;
            _preview.style.height = Length.Percent(100);
            _preview.AddManipulator(new OrbitManipulator(this));
            stack.Add(_preview);

            _previewHint = new Label("No preview yet.");
            _previewHint.style.position = Position.Absolute;
            _previewHint.style.left = 0;
            _previewHint.style.right = 0;
            _previewHint.style.top = 0;
            _previewHint.style.bottom = 0;
            _previewHint.style.unityTextAlign = TextAnchor.MiddleCenter;
            _previewHint.style.color = new Color(0.72f, 0.72f, 0.76f);
            stack.Add(_previewHint);

            var overlayTop = new VisualElement();
            overlayTop.style.position = Position.Absolute;
            overlayTop.style.left = 6;
            overlayTop.style.top = 6;
            overlayTop.style.flexDirection = FlexDirection.Row;
            overlayTop.pickingMode = PickingMode.Ignore;

            _previewInfoLabel = new Label("");
            StylePill(_previewInfoLabel);
            _previewInfoLabel.pickingMode = PickingMode.Ignore;
            overlayTop.Add(_previewInfoLabel);
            stack.Add(overlayTop);

            var overlayBottom = new VisualElement();
            overlayBottom.style.position = Position.Absolute;
            overlayBottom.style.right = 6;
            overlayBottom.style.bottom = 6;
            overlayBottom.style.flexDirection = FlexDirection.Row;

            var frameBtn = new Button(() =>
            {
                FrameMesh();
                _preview?.MarkDirtyRepaint();
            }) { text = "Frame", tooltip = "Frame the mesh in view (double-click preview)" };
            StyleOverlayButton(frameBtn);
            overlayBottom.Add(frameBtn);

            var resetBtn = new Button(() =>
            {
                _pitch = 20f; _yaw = 30f;
                FrameMesh();
                _preview?.MarkDirtyRepaint();
            }) { text = "Reset View", tooltip = "Reset orbit angles and distance" };
            StyleOverlayButton(resetBtn);
            overlayBottom.Add(resetBtn);

            stack.Add(overlayBottom);

            card.Add(stack);
            return card;
        }

        void BuildCompileSettings(VisualElement root)
        {
            var foldout = new Foldout { text = "Compile Settings", value = _settingsFoldoutOpen };
            foldout.style.marginBottom = 6;
            foldout.RegisterValueChangedCallback(evt => _settingsFoldoutOpen = evt.newValue);

            var body = new VisualElement();
            body.style.paddingLeft = 4;
            body.style.paddingRight = 4;

            var liveToggle = new Toggle("Live Update") { value = liveUpdate, tooltip = "Recompile automatically after parameter or source changes." };
            SetLabelWidth(liveToggle);
            liveToggle.RegisterValueChangedCallback(evt => { liveUpdate = evt.newValue; });
            body.Add(liveToggle);

            var fnField = new IntegerField("Preview $fn") { value = previewFnOverride, tooltip = "Force $fn during preview for speed. 0 = file's own value." };
            SetLabelWidth(fnField);
            fnField.RegisterValueChangedCallback(evt => { previewFnOverride = evt.newValue; MarkDirty(); });
            body.Add(fnField);

            var perColor = new Toggle("Per-Color Submeshes") { value = perColorSubmeshes };
            SetLabelWidth(perColor);
            perColor.RegisterValueChangedCallback(evt => { perColorSubmeshes = evt.newValue; MarkDirty(); });
            body.Add(perColor);

            var scaleField = new FloatField("Scale") { value = scale };
            SetLabelWidth(scaleField);
            scaleField.RegisterValueChangedCallback(evt => { scale = evt.newValue; MarkDirty(); });
            body.Add(scaleField);

            var weld = new Toggle("Weld Vertices") { value = weldVertices };
            SetLabelWidth(weld);
            weld.RegisterValueChangedCallback(evt => { weldVertices = evt.newValue; MarkDirty(); });
            body.Add(weld);

            var smooth = new Toggle("Smooth Normals") { value = recalculateNormals };
            SetLabelWidth(smooth);
            smooth.RegisterValueChangedCallback(evt => { recalculateNormals = evt.newValue; MarkDirty(); });
            body.Add(smooth);

            var par = new IntegerField("Max Parallel Compiles") { value = maxParallelCompiles, tooltip = "0 = logical core count." };
            SetLabelWidth(par);
            par.RegisterValueChangedCallback(evt => { maxParallelCompiles = evt.newValue; });
            body.Add(par);

            var timeout = new IntegerField("Compile Timeout (s)") { value = compileTimeoutSeconds };
            SetLabelWidth(timeout);
            timeout.RegisterValueChangedCallback(evt => { compileTimeoutSeconds = Mathf.Max(5, evt.newValue); });
            body.Add(timeout);

            foldout.Add(body);
            root.Add(foldout);
        }

        void BuildParametersSection(VisualElement root)
        {
            _paramsFoldout = new Foldout { text = "Parameters", value = true };
            _paramsFoldout.style.marginBottom = 6;

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.marginBottom = 4;

            _paramsCountLabel = new Label("0");
            _paramsCountLabel.style.color = new Color(0.72f, 0.72f, 0.76f);
            _paramsCountLabel.style.marginRight = 8;
            headerRow.Add(_paramsCountLabel);

            _paramsSearchField = new ToolbarSearchField { value = _paramSearch };
            _paramsSearchField.style.flexGrow = 1;
            _paramsSearchField.RegisterValueChangedCallback(evt =>
            {
                _paramSearch = evt.newValue ?? string.Empty;
                FilterParameterRows();
            });
            headerRow.Add(_paramsSearchField);

            _resetParamsBtn = new Button(() =>
            {
                foreach (var p in parameters)
                    if (p != null) p.value = p.defaultValue;
                RebuildParameterRows();
                MarkDirty();
            }) { text = "Reset", tooltip = "Reset all parameter values to their defaults" };
            _resetParamsBtn.style.marginLeft = 6;
            headerRow.Add(_resetParamsBtn);

            _paramsFoldout.Add(headerRow);

            _paramsEmptyHelp = new HelpBox("Drop a .scad asset above.", HelpBoxMessageType.Info);
            _paramsEmptyHelp.style.marginTop = 2;
            _paramsFoldout.Add(_paramsEmptyHelp);

            _paramsScroll = new ScrollView(ScrollViewMode.Vertical);
            _paramsScroll.style.minHeight = 80;
            _paramsScroll.style.maxHeight = 260;
            _paramsFoldout.Add(_paramsScroll);

            root.Add(_paramsFoldout);
        }

        void BuildActionsBar(VisualElement root)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 4;

            _compileBtn = new Button(() =>
            {
                _dirtyAt = 0;
                StartCompile();
            }) { text = "Compile Now" };
            _compileBtn.style.flexGrow = 1;
            _compileBtn.style.marginRight = 4;
            row.Add(_compileBtn);

            _saveCommitBtn = new Button(OnSaveOrCommit) { text = "Save as .scad…" };
            _saveCommitBtn.style.flexGrow = 1;
            _saveCommitBtn.style.marginRight = 4;
            row.Add(_saveCommitBtn);

            _cancelBtn = new Button(CancelActive) { text = "Cancel" };
            _cancelBtn.style.width = 80;
            row.Add(_cancelBtn);

            root.Add(row);
        }

        void BuildStatusBar(VisualElement root)
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingTop = 4;
            bar.style.paddingBottom = 2;
            bar.style.borderTopWidth = 1;
            bar.style.borderTopColor = new Color(0, 0, 0, 0.18f);

            _statusDot = new VisualElement();
            _statusDot.style.width = 8;
            _statusDot.style.height = 8;
            _statusDot.style.borderTopLeftRadius = 4;
            _statusDot.style.borderTopRightRadius = 4;
            _statusDot.style.borderBottomLeftRadius = 4;
            _statusDot.style.borderBottomRightRadius = 4;
            _statusDot.style.marginRight = 6;
            _statusDot.style.backgroundColor = DotColor(StatusKind.Idle);
            bar.Add(_statusDot);

            _statusLabel = new Label(_status);
            _statusLabel.style.flexGrow = 1;
            _statusLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
            _statusLabel.style.fontSize = 11;
            bar.Add(_statusLabel);

            _busyBar = new ProgressBar { title = "" };
            _busyBar.style.width = 100;
            _busyBar.style.display = DisplayStyle.None;
            bar.Add(_busyBar);

            root.Add(bar);
        }

        // ---------- Parameter UI ----------

        void RebuildParameterRows()
        {
            if (_paramsScroll == null) return;
            _paramsScroll.Clear();

            string currentGroup = null;
            int visible = 0;
            foreach (var p in parameters)
            {
                if (p == null) continue;
                if (!string.Equals(p.group, currentGroup))
                {
                    currentGroup = p.group;
                    if (!string.IsNullOrEmpty(currentGroup))
                    {
                        var hdr = new Label(currentGroup);
                        hdr.AddToClassList("scad-group-header");
                        hdr.style.unityFontStyleAndWeight = FontStyle.Bold;
                        hdr.style.marginTop = 6;
                        hdr.style.marginBottom = 2;
                        _paramsScroll.Add(hdr);
                    }
                }
                var row = CreateParameterRow(p);
                if (row != null)
                {
                    row.userData = p;
                    _paramsScroll.Add(row);
                    visible++;
                }
            }

            if (_paramsCountLabel != null)
                _paramsCountLabel.text = parameters.Count == 0 ? "empty" : $"{parameters.Count} parameter{(parameters.Count == 1 ? "" : "s")}";

            UpdateParametersEmptyHint();
            FilterParameterRows();
        }

        void UpdateParametersEmptyHint()
        {
            if (_paramsEmptyHelp == null) return;
            bool empty = parameters.Count == 0;
            _paramsEmptyHelp.text = useInlineCode
                ? "No top-level variables in the inline source. Add e.g. `width = 20; // [5:100]`."
                : (scadAsset == null
                    ? "Drop a .scad asset above."
                    : "No top-level variables in the selected .scad.");
            _paramsEmptyHelp.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            if (_paramsScroll != null)
                _paramsScroll.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
            if (_resetParamsBtn != null)
                _resetParamsBtn.SetEnabled(!empty);
            if (_paramsSearchField != null)
                _paramsSearchField.SetEnabled(!empty);
        }

        void FilterParameterRows()
        {
            if (_paramsScroll == null) return;
            var filter = (_paramSearch ?? string.Empty).Trim();
            bool hasFilter = filter.Length > 0;
            foreach (var child in _paramsScroll.Children())
            {
                if (child.userData is ScadParameter p)
                {
                    bool match = !hasFilter
                        || (p.name != null && p.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (p.description != null && p.description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                    child.style.display = match ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
        }

        VisualElement CreateParameterRow(ScadParameter p)
        {
            var label = ObjectNames.NicifyVariableName(p.name);
            var tooltip = string.IsNullOrEmpty(p.description) ? p.name : p.description;
            var ci = CultureInfo.InvariantCulture;

            switch (p.type)
            {
                case ScadParameterType.Boolean:
                {
                    var f = new Toggle(label) { value = p.value == "true", tooltip = tooltip };
                    SetLabelWidth(f);
                    f.RegisterValueChangedCallback(evt =>
                    {
                        p.value = evt.newValue ? "true" : "false";
                        MarkDirty();
                    });
                    return f;
                }
                case ScadParameterType.Integer:
                {
                    int.TryParse(p.value ?? "0", NumberStyles.Integer, ci, out var cur);
                    if (p.hasRange)
                    {
                        var s = new SliderInt(label, (int)p.min, (int)p.max) { value = cur, tooltip = tooltip, showInputField = true };
                        SetLabelWidth(s);
                        s.RegisterValueChangedCallback(evt =>
                        {
                            p.value = evt.newValue.ToString(ci);
                            MarkDirty();
                        });
                        return s;
                    }
                    var f = new IntegerField(label) { value = cur, tooltip = tooltip };
                    SetLabelWidth(f);
                    f.RegisterValueChangedCallback(evt =>
                    {
                        p.value = evt.newValue.ToString(ci);
                        MarkDirty();
                    });
                    return f;
                }
                case ScadParameterType.Number:
                {
                    double.TryParse(p.value ?? "0", NumberStyles.Float, ci, out var curD);
                    float curF = (float)curD;
                    if (p.hasRange)
                    {
                        var s = new Slider(label, (float)p.min, (float)p.max) { value = curF, tooltip = tooltip, showInputField = true };
                        SetLabelWidth(s);
                        s.RegisterValueChangedCallback(evt =>
                        {
                            p.value = evt.newValue.ToString("R", ci);
                            MarkDirty();
                        });
                        return s;
                    }
                    var f = new FloatField(label) { value = curF, tooltip = tooltip };
                    SetLabelWidth(f);
                    f.RegisterValueChangedCallback(evt =>
                    {
                        p.value = evt.newValue.ToString("R", ci);
                        MarkDirty();
                    });
                    return f;
                }
                case ScadParameterType.String:
                {
                    var raw = p.value ?? "";
                    var shown = (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                        ? raw.Substring(1, raw.Length - 2) : raw;
                    if (IsHexColor(shown))
                    {
                        var f = new ColorField(label) { value = HexToColor(shown), tooltip = tooltip, showAlpha = true };
                        SetLabelWidth(f);
                        f.RegisterValueChangedCallback(evt =>
                        {
                            p.value = "\"" + ColorToHex(evt.newValue) + "\"";
                            MarkDirty();
                        });
                        return f;
                    }
                    var tf = new TextField(label) { value = shown, tooltip = tooltip };
                    SetLabelWidth(tf);
                    tf.RegisterValueChangedCallback(evt =>
                    {
                        p.value = "\"" + evt.newValue + "\"";
                        MarkDirty();
                    });
                    return tf;
                }
                case ScadParameterType.ColorVector:
                {
                    var f = new ColorField(label) { value = VectorToColor(p.value), tooltip = tooltip, showAlpha = true };
                    SetLabelWidth(f);
                    f.RegisterValueChangedCallback(evt =>
                    {
                        p.value = ColorToVector(evt.newValue);
                        MarkDirty();
                    });
                    return f;
                }
                case ScadParameterType.NumberDropdown:
                case ScadParameterType.StringDropdown:
                {
                    if (p.choices == null || p.choices.Count == 0) return null;
                    var labels = p.choices.Select(c => c.label).ToList();
                    int idx = 0;
                    for (int i = 0; i < p.choices.Count; i++)
                        if (p.choices[i].value == p.value) { idx = i; break; }
                    idx = Mathf.Clamp(idx, 0, labels.Count - 1);
                    var pop = new PopupField<string>(label, labels, idx) { tooltip = tooltip };
                    SetLabelWidth(pop);
                    pop.RegisterValueChangedCallback(evt =>
                    {
                        int i = labels.IndexOf(evt.newValue);
                        if (i >= 0)
                        {
                            p.value = p.choices[i].value;
                            MarkDirty();
                        }
                    });
                    return pop;
                }
            }
            return null;
        }

        // ---------- Actions ----------

        void OnSaveOrCommit()
        {
            if (useInlineCode) SaveInlineAsAsset();
            else CommitToAsset();
        }

        void UpdateActionButtons()
        {
            bool canCompile = useInlineCode
                ? !string.IsNullOrWhiteSpace(inlineCode)
                : scadAsset != null;
            bool busy = _activeTask != null;

            if (_compileBtn != null) _compileBtn.SetEnabled(canCompile && !busy);
            if (_cancelBtn != null) _cancelBtn.SetEnabled(busy);
            if (_saveCommitBtn != null)
            {
                _saveCommitBtn.text = useInlineCode ? "Save as .scad…" : "Commit to Asset";
                _saveCommitBtn.tooltip = useInlineCode
                    ? "Save the inline source as a new .scad asset."
                    : "Write current settings + parameters back into the selected .scad importer.";
                _saveCommitBtn.SetEnabled(canCompile && !busy);
            }
        }

        // ---------- Preview rendering (IMGUI inside IMGUIContainer) ----------

        void OnPreviewIMGUI()
        {
            if (_preview == null) return;
            var rect = new Rect(0, 0, _preview.contentRect.width, _preview.contentRect.height);
            if (rect.width < 10 || rect.height < 10) return;

            if (_previewMesh == null)
            {
                if (_previewHint != null) _previewHint.style.display = DisplayStyle.Flex;
                return;
            }
            if (_previewHint != null) _previewHint.style.display = DisplayStyle.None;

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

        internal void OrbitBy(Vector2 delta)
        {
            _yaw += delta.x * 0.5f;
            _pitch = Mathf.Clamp(_pitch - delta.y * 0.5f, -89f, 89f);
            _preview?.MarkDirtyRepaint();
        }

        internal void ZoomBy(float wheelY)
        {
            _distance = Mathf.Max(_distance * (1f + wheelY * 0.05f), 0.01f);
            _preview?.MarkDirtyRepaint();
        }

        internal void OnPreviewDoubleClick()
        {
            FrameMesh();
            _preview?.MarkDirtyRepaint();
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
            RebuildParameterRows();
            SetStatus("Idle", StatusKind.Idle, repaintPreview: false);
            MarkDirty();
        }

        void ReadParametersFromAsset()
        {
            if (parameters == null) parameters = new List<ScadParameter>();

            if (scadAsset == null)
            {
                parameters.Clear();
                RebuildParameterRows();
                SetStatus("Idle", StatusKind.Idle, repaintPreview: false);
                return;
            }
            var path = AssetDatabase.GetAssetPath(scadAsset);
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(".scad", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Clear();
                RebuildParameterRows();
                SetStatus("Asset is not a .scad file", StatusKind.Warn);
                return;
            }

            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception ex) { SetStatus("Read failed: " + ex.Message, StatusKind.Error); return; }

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
            RebuildParameterRows();
            SetStatus("Idle", StatusKind.Idle, repaintPreview: false);
            MarkDirty();
        }

        void MarkDirty()
        {
            _dirtyAt = EditorApplication.timeSinceStartup;
            SetStatus("Dirty — recompiling soon", StatusKind.Busy);
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
                catch (Exception ex) { SetStatus("Cannot write temp: " + ex.Message, StatusKind.Error); return; }
            }
            else
            {
                if (scadAsset == null) return;
                path = AssetDatabase.GetAssetPath(scadAsset);
                if (string.IsNullOrEmpty(path)) return;
                try { source = File.ReadAllText(path); }
                catch (Exception ex) { SetStatus("Read failed: " + ex.Message, StatusKind.Error); return; }
            }

            var exe = ScadImporterSettings.ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                SetStatus("OpenSCAD not configured. Preferences → SCAD Plugin.", StatusKind.Warn);
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
            SetStatus("Compiling…", StatusKind.Busy);
            var timeoutMs = Mathf.Max(5, compileTimeoutSeconds) * 1000;
            _activeTask = ScadLiveCompiler.CompileAsync(
                path, snap, colors, maxParallelCompiles, timeoutMs, exe,
                scale, weldVertices, _activeCts.Token);
            UpdateActionButtons();
        }

        void CancelActive()
        {
            try { _activeCts?.Cancel(); } catch { }
            _activeTask = null;
            _activeCts = null;
            SetStatus("Cancelled", StatusKind.Warn);
            UpdateActionButtons();
        }

        void ApplyResult(Task<ScadLiveCompiler.Result> task)
        {
            UpdateActionButtons();

            if (task.IsFaulted)
            {
                SetStatus("Error: " + (task.Exception?.GetBaseException().Message ?? "unknown"), StatusKind.Error);
                return;
            }
            if (task.IsCanceled) { SetStatus("Cancelled", StatusKind.Warn); return; }

            var r = task.Result;
            if (!r.success)
            {
                if (r.error == "Cancelled") return;
                SetStatus("Failed: " + r.error, StatusKind.Error);
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

            SetStatus(
                $"OK · {r.parts.Count} submesh(es), {r.triangleCount:N0} tris, {_previewMesh.vertexCount:N0} verts",
                StatusKind.Ok);
            if (_previewInfoLabel != null)
            {
                _previewInfoLabel.text = $"{r.parts.Count} · {r.triangleCount:N0} tris";
                _previewInfoLabel.style.display = DisplayStyle.Flex;
            }
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
            catch (Exception ex) { SetStatus("Save failed: " + ex.Message, StatusKind.Error); return; }

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
                if (_modeGroup != null) _modeGroup.SetValueWithoutNotify(0);
                RefreshSourceSlot();
                SetStatus("Saved to " + target + " (switched to File mode)", StatusKind.Ok);
                UpdateActionButtons();
            }
        }

        void CommitToAsset()
        {
            if (scadAsset == null) return;
            var path = AssetDatabase.GetAssetPath(scadAsset);
            var importer = AssetImporter.GetAtPath(path) as ScadImporter;
            if (importer == null) { SetStatus("Asset has no ScadImporter.", StatusKind.Error); return; }
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
            SetStatus("Committed to " + path, StatusKind.Ok);
        }

        // ---------- Status ----------

        void SetStatus(string text, StatusKind kind, bool repaintPreview = true)
        {
            _status = text;
            _statusKind = kind;
            if (_statusLabel != null) _statusLabel.text = text;
            if (_statusDot != null) _statusDot.style.backgroundColor = DotColor(kind);
            if (_busyBar != null)
                _busyBar.style.display = kind == StatusKind.Busy ? DisplayStyle.Flex : DisplayStyle.None;
            if (repaintPreview) _preview?.MarkDirtyRepaint();
        }

        static Color DotColor(StatusKind kind) => kind switch
        {
            StatusKind.Busy => new Color(1f, 0.78f, 0.22f),
            StatusKind.Ok => new Color(0.38f, 0.80f, 0.41f),
            StatusKind.Warn => new Color(0.98f, 0.55f, 0.18f),
            StatusKind.Error => new Color(0.92f, 0.31f, 0.31f),
            _ => new Color(0.55f, 0.56f, 0.60f),
        };

        // ---------- Style helpers ----------

        static VisualElement MakeCard()
        {
            var c = new VisualElement();
            c.style.backgroundColor = new Color(0, 0, 0, 0.08f);
            c.style.borderTopLeftRadius = 4;
            c.style.borderTopRightRadius = 4;
            c.style.borderBottomLeftRadius = 4;
            c.style.borderBottomRightRadius = 4;
            c.style.paddingTop = 6;
            c.style.paddingBottom = 6;
            c.style.paddingLeft = 6;
            c.style.paddingRight = 6;
            c.style.marginBottom = 6;
            return c;
        }

        static void SetLabelWidth(VisualElement field, int width = 140)
        {
            var lbl = field.Q<Label>(className: "unity-base-field__label");
            if (lbl != null)
            {
                lbl.style.minWidth = width;
                lbl.style.maxWidth = width;
            }
        }

        static void StylePill(Label l)
        {
            l.style.backgroundColor = new Color(0, 0, 0, 0.45f);
            l.style.color = new Color(0.9f, 0.9f, 0.92f);
            l.style.paddingLeft = 6;
            l.style.paddingRight = 6;
            l.style.paddingTop = 2;
            l.style.paddingBottom = 2;
            l.style.borderTopLeftRadius = 3;
            l.style.borderTopRightRadius = 3;
            l.style.borderBottomLeftRadius = 3;
            l.style.borderBottomRightRadius = 3;
            l.style.fontSize = 10;
            l.style.display = DisplayStyle.None;
        }

        static void StyleOverlayButton(Button b)
        {
            b.style.marginLeft = 4;
            b.style.height = 22;
            b.style.paddingLeft = 8;
            b.style.paddingRight = 8;
            b.style.fontSize = 11;
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

        // ---------- Orbit manipulator ----------

        sealed class OrbitManipulator : MouseManipulator
        {
            readonly ScadLivePreviewWindow _win;
            bool _dragging;
            Vector2 _last;

            public OrbitManipulator(ScadLivePreviewWindow win)
            {
                _win = win;
                activators.Add(new ManipulatorActivationFilter { button = MouseButton.LeftMouse });
            }

            protected override void RegisterCallbacksOnTarget()
            {
                target.RegisterCallback<MouseDownEvent>(OnDown);
                target.RegisterCallback<MouseMoveEvent>(OnMove);
                target.RegisterCallback<MouseUpEvent>(OnUp);
                target.RegisterCallback<WheelEvent>(OnWheel);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                target.UnregisterCallback<MouseDownEvent>(OnDown);
                target.UnregisterCallback<MouseMoveEvent>(OnMove);
                target.UnregisterCallback<MouseUpEvent>(OnUp);
                target.UnregisterCallback<WheelEvent>(OnWheel);
            }

            void OnDown(MouseDownEvent e)
            {
                if (e.clickCount == 2 && e.button == (int)MouseButton.LeftMouse)
                {
                    _win.OnPreviewDoubleClick();
                    e.StopPropagation();
                    return;
                }
                if (!CanStartManipulation(e)) return;
                _dragging = true;
                _last = e.mousePosition;
                target.CaptureMouse();
                e.StopPropagation();
            }

            void OnMove(MouseMoveEvent e)
            {
                if (!_dragging) return;
                var delta = (Vector2)e.mousePosition - _last;
                _last = e.mousePosition;
                _win.OrbitBy(delta);
                e.StopPropagation();
            }

            void OnUp(MouseUpEvent e)
            {
                if (!_dragging) return;
                _dragging = false;
                if (target.HasMouseCapture()) target.ReleaseMouse();
                e.StopPropagation();
            }

            void OnWheel(WheelEvent e)
            {
                _win.ZoomBy(e.delta.y);
                e.StopPropagation();
            }
        }
    }
}
