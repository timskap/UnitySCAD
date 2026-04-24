using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SCADPlugin.Editor.Graph;
using SCADPlugin.Editor.Graph.Import;
using SCADPlugin.Editor.Graph.Nodes;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SCADPlugin.Editor.Graph.UI
{
    public class ScadGraphEditorWindow : EditorWindow
    {
        string _assetPath;
        ScadGraph _graph;
        ScadGraphView _view;
        VisualElement _inspector;
        Label _statusLabel;
        bool _dirty;

        public static void OpenForAsset(string assetPath)
        {
            var w = GetWindow<ScadGraphEditorWindow>();
            w.titleContent = new GUIContent(Path.GetFileNameWithoutExtension(assetPath));
            w.minSize = new Vector2(800, 480);
            w.LoadFromAsset(assetPath);
            w.Focus();
        }

        void CreateGUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            BuildToolbar(rootVisualElement);

            var split = new TwoPaneSplitView(
                fixedPaneIndex: 1,
                fixedPaneStartDimension: 260,
                orientation: TwoPaneSplitViewOrientation.Horizontal)
            {
                viewDataKey = "scad-graph-editor-split",
            };
            split.style.flexGrow = 1;
            rootVisualElement.Add(split);

            _view = new ScadGraphView(this);
            _view.style.flexGrow = 1;
            _view.OnGraphMutated += MarkDirty;
            _view.OnSelectionChanged += HandleSelectionChanged;
            split.Add(_view);

            _inspector = BuildInspector();
            split.Add(_inspector);

            BuildStatusBar(rootVisualElement);

            if (!string.IsNullOrEmpty(_assetPath))
                PopulateFromGraph();
        }

        void BuildToolbar(VisualElement root)
        {
            var tb = new Toolbar();
            tb.style.flexShrink = 0;

            var saveBtn = new ToolbarButton(Save) { text = "Save", tooltip = "Save the graph and reimport the asset (Ctrl+S)." };
            tb.Add(saveBtn);

            var compileBtn = new ToolbarButton(() => { Save(); AssetDatabase.Refresh(); })
            { text = "Compile", tooltip = "Save and trigger OpenSCAD compilation via the importer." };
            tb.Add(compileBtn);

            var frameBtn = new ToolbarButton(() => _view?.FrameAll()) { text = "Frame All" };
            tb.Add(frameBtn);

            var importMenu = new ToolbarMenu { text = "Import" };
            importMenu.menu.AppendAction("From SCAD source...", _ => ImportFromSource());
            importMenu.menu.AppendAction("From .scad file...", _ => ImportFromFile());
            tb.Add(importMenu);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            tb.Add(spacer);

            var pathLabel = new Label();
            pathLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            pathLabel.style.marginRight = 6;
            pathLabel.schedule.Execute(() => pathLabel.text = _assetPath ?? "(unsaved)").Every(250);
            tb.Add(pathLabel);

            root.Add(tb);

            rootVisualElement.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.ctrlKey && evt.keyCode == KeyCode.S) { Save(); evt.StopPropagation(); }
            });
        }

        VisualElement BuildInspector()
        {
            var root = new VisualElement();
            root.style.paddingLeft = 6;
            root.style.paddingRight = 6;
            root.style.paddingTop = 6;

            var title = new Label("Inspector");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 6;
            root.Add(title);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            var hint = new HelpBox("Select a node to edit its properties.", HelpBoxMessageType.Info);
            scroll.Add(hint);
            scroll.name = "inspector-content";

            return root;
        }

        void BuildStatusBar(VisualElement root)
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingTop = 2;
            bar.style.paddingBottom = 2;
            bar.style.paddingLeft = 6;
            bar.style.paddingRight = 6;
            bar.style.borderTopWidth = 1;
            bar.style.borderTopColor = new Color(0, 0, 0, 0.18f);

            _statusLabel = new Label("Ready");
            _statusLabel.style.flexGrow = 1;
            _statusLabel.style.fontSize = 11;
            bar.Add(_statusLabel);
            root.Add(bar);
        }

        void LoadFromAsset(string path)
        {
            _assetPath = path;
            if (_view != null) PopulateFromGraph();
        }

        void PopulateFromGraph()
        {
            _graph = LoadGraphFromDisk(_assetPath);
            _view?.LoadGraph(_graph);
            _dirty = false;
            UpdateStatus("Loaded " + Path.GetFileName(_assetPath));
        }

        static ScadGraph LoadGraphFromDisk(string path)
        {
            var g = ScriptableObject.CreateInstance<ScadGraph>();
            string json = null;
            try { json = File.ReadAllText(path); } catch { }

            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            {
                var output = new Nodes.OutputNode();
                g.AddNode(output, new Vector2(200, 0));
                g.outputNodeId = output.id;
                return g;
            }
            try { EditorJsonUtility.FromJsonOverwrite(json, g); }
            catch (Exception ex) { Debug.LogError("Parse .scadgraph: " + ex.Message); }
            g.nodes ??= new List<ScadNode>();
            g.connections ??= new List<ScadConnection>();
            g.exposedParameters ??= new List<ScadExposedParameter>();
            return g;
        }

        void MarkDirty()
        {
            _dirty = true;
            UpdateStatus("Modified");
        }

        void Save()
        {
            if (_graph == null || string.IsNullOrEmpty(_assetPath)) return;
            try
            {
                var json = EditorJsonUtility.ToJson(_graph, prettyPrint: true);
                File.WriteAllText(_assetPath, json);
                AssetDatabase.ImportAsset(_assetPath);
                _dirty = false;
                UpdateStatus("Saved " + Path.GetFileName(_assetPath));
            }
            catch (Exception ex)
            {
                UpdateStatus("Save failed: " + ex.Message);
            }
        }

        void UpdateStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.text = text;
        }

        void HandleSelectionChanged(ScadNode node)
        {
            var scroll = _inspector?.Q<ScrollView>("inspector-content");
            if (scroll == null) return;
            scroll.Clear();

            if (node == null)
            {
                scroll.Add(new HelpBox("Select a node to edit its properties.", HelpBoxMessageType.Info));
                return;
            }

            var header = new Label(PrettyName(node.GetType()));
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 4;
            scroll.Add(header);

            ScadNodeInspector.Build(node, scroll, MarkDirty);

            var portsLabel = new Label("Defaults for unconnected inputs");
            portsLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            portsLabel.style.marginTop = 10;
            portsLabel.style.marginBottom = 2;
            scroll.Add(portsLabel);

            foreach (var p in node.inputs)
            {
                if (p == null) continue;
                var f = new TextField(p.label) { value = p.defaultLiteral ?? "" };
                f.labelElement.style.minWidth = 80;
                f.RegisterValueChangedCallback(evt =>
                {
                    p.defaultLiteral = evt.newValue;
                    MarkDirty();
                });
                scroll.Add(f);
            }
        }

        static string PrettyName(Type t)
        {
            var attr = t.GetCustomAttribute<ScadNodeAttribute>();
            return attr != null ? attr.DisplayName : t.Name;
        }

        void OnDisable()
        {
            if (_dirty && !string.IsNullOrEmpty(_assetPath))
                Save();
        }

        // ---- SCAD source import --------------------------------------------

        void ImportFromSource()
        {
            ScadPasteImportDialog.Show(this, source =>
            {
                if (string.IsNullOrWhiteSpace(source)) return;
                ApplyImport(ScadSourceToGraph.Build(source), "pasted source");
            });
        }

        void ImportFromFile()
        {
            var path = EditorUtility.OpenFilePanel("Import SCAD file", "Assets", "scad");
            if (string.IsNullOrEmpty(path)) return;
            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception ex)
            {
                UpdateStatus("Read failed: " + ex.Message);
                return;
            }
            ApplyImport(ScadSourceToGraph.Build(source), Path.GetFileName(path));
        }

        void ApplyImport(ScadSourceToGraph.Result result, string sourceLabel)
        {
            if (result == null || result.graph == null)
            {
                UpdateStatus("Import failed.");
                return;
            }

            // Replace the current in-memory graph with the imported one.
            // The user is prompted only if they had unsaved changes.
            if (_dirty)
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Import SCAD",
                    "The current graph has unsaved changes. Overwrite?",
                    "Overwrite", "Cancel");
                if (!overwrite) return;
            }

            _graph = result.graph;
            _view.LoadGraph(_graph);
            MarkDirty();

            ScadSourceToGraph.ReportDiagnostics(result);
            var msg = $"Imported from {sourceLabel}";
            if (result.errors.Count > 0) msg += $" — {result.errors.Count} error(s)";
            if (result.warnings.Count > 0) msg += $", {result.warnings.Count} warning(s)";
            UpdateStatus(msg);
        }
    }

    // Modal paste dialog used by the toolbar's "Import → From SCAD source…"
    // entry. Kept in-file because it's only ever used from the editor window.
    internal class ScadPasteImportDialog : EditorWindow
    {
        Action<string> _onImport;
        TextField _text;

        public static void Show(EditorWindow owner, Action<string> onImport)
        {
            var w = CreateInstance<ScadPasteImportDialog>();
            w.titleContent = new GUIContent("Import SCAD source");
            w.minSize = new Vector2(540, 360);
            w._onImport = onImport;
            w.ShowUtility();
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.flexDirection = FlexDirection.Column;

            var hint = new Label(
                "Paste SCAD source below. Recognised modules become typed nodes; " +
                "unknown modules are preserved as Custom Statement nodes. " +
                "Top-level `x = …;` assignments become exposed parameters.");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 6;
            root.Add(hint);

            var codeScroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            codeScroll.style.flexGrow = 1;
            codeScroll.style.minHeight = 120;
            codeScroll.horizontalScrollerVisibility = ScrollerVisibility.Auto;
            codeScroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            codeScroll.contentContainer.style.flexGrow = 1;

            _text = new TextField { multiline = true, value = string.Empty };
            _text.style.flexGrow = 1;
            _text.style.whiteSpace = WhiteSpace.NoWrap;
            var input = _text.Q(className: TextField.inputUssClassName);
            if (input != null)
            {
                input.style.unityFont = EditorStyles.standardFont;
                input.style.whiteSpace = WhiteSpace.NoWrap;
                input.style.flexGrow = 1;
            }
            codeScroll.Add(_text);
            root.Add(codeScroll);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 6;

            var cancel = new Button(Close) { text = "Cancel" };
            cancel.style.flexGrow = 1;
            cancel.style.flexBasis = 0;
            cancel.style.marginRight = 4;
            row.Add(cancel);

            var import = new Button(() =>
            {
                _onImport?.Invoke(_text.value);
                Close();
            })
            { text = "Import" };
            import.style.flexGrow = 1;
            import.style.flexBasis = 0;
            import.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(import);

            root.Add(row);
        }
    }

    // ---------------------------------------------------------------------
    // GraphView
    // ---------------------------------------------------------------------

    public class ScadGraphView : GraphView
    {
        readonly ScadGraphEditorWindow _window;
        ScadGraph _graph;
        ScadNodeSearchProvider _searchProvider;
        readonly Dictionary<string, ScadNodeView> _nodeViews = new Dictionary<string, ScadNodeView>();
        bool _suppressChange;

        public event Action OnGraphMutated;
        public event Action<ScadNode> OnSelectionChanged;

        public ScadGraphView(ScadGraphEditorWindow window)
        {
            _window = window;
            style.flexGrow = 1;

            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            this.AddManipulator(new FreehandSelector());

            var bg = new GridBackground();
            Insert(0, bg);
            bg.StretchToParentSize();

            graphViewChanged = OnGraphChanged;

            _searchProvider = ScriptableObject.CreateInstance<ScadNodeSearchProvider>();
            _searchProvider.Configure(this, window);
            nodeCreationRequest = ctx =>
                SearchWindow.Open(new SearchWindowContext(ctx.screenMousePosition), _searchProvider);
        }

        public void LoadGraph(ScadGraph graph)
        {
            _graph = graph;
            _suppressChange = true;
            try
            {
                DeleteElements(graphElements.ToList());
                _nodeViews.Clear();

                if (_graph == null) return;

                foreach (var node in _graph.nodes)
                {
                    if (node == null) continue;
                    var v = CreateNodeView(node);
                    AddElement(v);
                    _nodeViews[node.id] = v;
                }
                foreach (var conn in _graph.connections)
                {
                    if (conn == null) continue;
                    if (!_nodeViews.TryGetValue(conn.fromNodeId, out var fromView)) continue;
                    if (!_nodeViews.TryGetValue(conn.toNodeId, out var toView)) continue;
                    var fromPort = fromView.GetPort(conn.fromPortId, Direction.Output);
                    var toPort = toView.GetPort(conn.toPortId, Direction.Input);
                    if (fromPort == null || toPort == null) continue;
                    var edge = fromPort.ConnectTo(toPort);
                    AddElement(edge);
                }
            }
            finally
            {
                _suppressChange = false;
            }

            schedule.Execute(() => FrameAll()).ExecuteLater(50);
        }

        ScadNodeView CreateNodeView(ScadNode node)
        {
            var view = new ScadNodeView(node);
            view.OnPositionChanged += pos =>
            {
                node.position = pos;
                OnGraphMutated?.Invoke();
            };
            return view;
        }

        public void CreateNode(ScadNode node, Vector2 worldPosition)
        {
            if (_graph == null) return;
            _graph.AddNode(node, worldPosition);
            var view = CreateNodeView(node);
            AddElement(view);
            _nodeViews[node.id] = view;
            OnGraphMutated?.Invoke();
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var result = new List<Port>();
            var startData = startPort.userData as ScadPort;
            ports.ForEach(p =>
            {
                if (p == startPort) return;
                if (p.node == startPort.node) return;
                if (p.direction == startPort.direction) return;
                var pData = p.userData as ScadPort;
                if (startData == null || pData == null) return;
                bool ok = startPort.direction == Direction.Output
                    ? ScadLiteral.Compatible(startData.type, pData.type)
                    : ScadLiteral.Compatible(pData.type, startData.type);
                if (ok) result.Add(p);
            });
            return result;
        }

        GraphViewChange OnGraphChanged(GraphViewChange change)
        {
            if (_suppressChange || _graph == null) return change;

            if (change.elementsToRemove != null)
            {
                foreach (var el in change.elementsToRemove)
                {
                    switch (el)
                    {
                        case ScadNodeView nv:
                            _graph.RemoveNode(nv.node.id);
                            _nodeViews.Remove(nv.node.id);
                            break;
                        case Edge edge:
                            if (edge.input?.userData is ScadPort inPort &&
                                edge.input.node is ScadNodeView inView)
                                _graph.Disconnect(inView.node.id, inPort.id);
                            break;
                    }
                }
            }

            if (change.edgesToCreate != null)
            {
                foreach (var edge in change.edgesToCreate)
                {
                    if (edge.output?.userData is ScadPort fromPort &&
                        edge.output.node is ScadNodeView fromView &&
                        edge.input?.userData is ScadPort toPort &&
                        edge.input.node is ScadNodeView toView)
                    {
                        _graph.Connect(fromView.node.id, fromPort.id, toView.node.id, toPort.id);
                    }
                }
            }

            if (change.movedElements != null)
            {
                foreach (var el in change.movedElements)
                {
                    if (el is ScadNodeView nv)
                        nv.node.position = nv.GetPosition().position;
                }
            }

            OnGraphMutated?.Invoke();
            return change;
        }

        public override void AddToSelection(ISelectable selectable)
        {
            base.AddToSelection(selectable);
            if (selectable is ScadNodeView nv) OnSelectionChanged?.Invoke(nv.node);
        }

        public override void RemoveFromSelection(ISelectable selectable)
        {
            base.RemoveFromSelection(selectable);
            OnSelectionChanged?.Invoke(selection.OfType<ScadNodeView>().FirstOrDefault()?.node);
        }

        public override void ClearSelection()
        {
            base.ClearSelection();
            OnSelectionChanged?.Invoke(null);
        }
    }

    // ---------------------------------------------------------------------
    // Node view
    // ---------------------------------------------------------------------

    public class ScadNodeView : UnityEditor.Experimental.GraphView.Node
    {
        public readonly ScadNode node;
        readonly Dictionary<string, Port> _ports = new Dictionary<string, Port>();

        public event Action<Vector2> OnPositionChanged;

        public ScadNodeView(ScadNode n)
        {
            node = n;
            var attr = n.GetType().GetCustomAttribute<ScadNodeAttribute>();
            if (n is UserModuleCallNode umc)
                title = string.IsNullOrEmpty(umc.moduleName) ? "Module Call" : $"call: {umc.moduleName}";
            else
                title = attr != null ? attr.DisplayName : n.GetType().Name;
            if (attr != null && !string.IsNullOrEmpty(attr.Tooltip))
                tooltip = attr.Tooltip;

            foreach (var p in n.inputs)
            {
                var port = BuildPort(p, Direction.Input, Port.Capacity.Single);
                inputContainer.Add(port);
                _ports[p.id] = port;
            }
            foreach (var p in n.outputs)
            {
                var port = BuildPort(p, Direction.Output, Port.Capacity.Multi);
                outputContainer.Add(port);
                _ports[p.id] = port;
            }

            SetPosition(new Rect(n.position, new Vector2(200, 120)));
            RefreshExpandedState();
            RefreshPorts();

            this.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var newPos = GetPosition().position;
                if ((newPos - n.position).sqrMagnitude > 0.01f)
                    OnPositionChanged?.Invoke(newPos);
            });
        }

        public Port GetPort(string portId, Direction direction)
        {
            if (!_ports.TryGetValue(portId, out var p)) return null;
            return p.direction == direction ? p : null;
        }

        static Port BuildPort(ScadPort data, Direction direction, Port.Capacity capacity)
        {
            var port = Port.Create<Edge>(
                Orientation.Horizontal, direction, capacity,
                MapType(data.type));
            port.portName = data.label;
            port.portColor = ColorFor(data.type);
            port.userData = data;
            return port;
        }

        static Type MapType(ScadPortType t) => t switch
        {
            ScadPortType.Number => typeof(float),
            ScadPortType.Vector2 => typeof(Vector2),
            ScadPortType.Vector3 => typeof(Vector3),
            ScadPortType.Boolean => typeof(bool),
            ScadPortType.String => typeof(string),
            ScadPortType.Color => typeof(Color),
            ScadPortType.Solid => typeof(ScadNodeView),
            ScadPortType.Shape => typeof(Rect),
            _ => typeof(object),
        };

        static Color ColorFor(ScadPortType t) => t switch
        {
            ScadPortType.Number => new Color(0.78f, 0.82f, 0.98f),
            ScadPortType.Vector2 => new Color(0.66f, 0.88f, 0.78f),
            ScadPortType.Vector3 => new Color(0.38f, 0.82f, 0.72f),
            ScadPortType.Boolean => new Color(0.96f, 0.72f, 0.42f),
            ScadPortType.String => new Color(0.92f, 0.82f, 0.58f),
            ScadPortType.Color => new Color(0.96f, 0.58f, 0.62f),
            ScadPortType.Solid => new Color(0.62f, 0.78f, 0.96f),
            ScadPortType.Shape => new Color(0.86f, 0.62f, 0.96f),
            _ => new Color(0.78f, 0.78f, 0.78f),
        };
    }

    // ---------------------------------------------------------------------
    // Search provider
    // ---------------------------------------------------------------------

    public class ScadNodeSearchProvider : ScriptableObject, ISearchWindowProvider
    {
        ScadGraphView _view;
        ScadGraphEditorWindow _window;

        public void Configure(ScadGraphView view, ScadGraphEditorWindow window)
        {
            _view = view;
            _window = window;
        }

        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
        {
            var list = new List<SearchTreeEntry>
            {
                new SearchTreeGroupEntry(new GUIContent("Create Node"), 0),
            };

            var entries = new List<(string path, Type type, string name, string tooltip)>();
            foreach (var t in TypeCache.GetTypesWithAttribute<ScadNodeAttribute>())
            {
                if (t.IsAbstract) continue;
                var attr = (ScadNodeAttribute)Attribute.GetCustomAttribute(t, typeof(ScadNodeAttribute));
                if (attr == null || attr.Hidden) continue;
                var path = attr.Category + "/" + attr.DisplayName;
                entries.Add((path, t, attr.DisplayName, attr.Tooltip));
            }
            entries.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.Ordinal));

            var last = new List<string>();
            foreach (var (path, type, name, tooltip) in entries)
            {
                var parts = path.Split('/');
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (i >= last.Count || last[i] != parts[i])
                    {
                        while (last.Count > i) last.RemoveAt(last.Count - 1);
                        list.Add(new SearchTreeGroupEntry(new GUIContent(parts[i]), i + 1));
                        last.Add(parts[i]);
                    }
                }
                while (last.Count > parts.Length - 1) last.RemoveAt(last.Count - 1);

                list.Add(new SearchTreeEntry(new GUIContent(name))
                {
                    level = parts.Length,
                    userData = type,
                });
            }

            return list;
        }

        public bool OnSelectEntry(SearchTreeEntry entry, SearchWindowContext context)
        {
            if (entry.userData is not Type type) return false;
            var instance = (ScadNode)Activator.CreateInstance(type);

            var windowRoot = _window.rootVisualElement;
            var localMouse = windowRoot.ChangeCoordinatesTo(
                windowRoot.parent,
                context.screenMousePosition - _window.position.position);
            var graphPos = _view.contentViewContainer.WorldToLocal(localMouse);

            _view.CreateNode(instance, graphPos);
            return true;
        }
    }

    // ---------------------------------------------------------------------
    // Per-node property inspector (reflection-based)
    // ---------------------------------------------------------------------

    internal static class ScadNodeInspector
    {
        static readonly HashSet<string> Ignore = new HashSet<string>
        {
            "id", "position", "inputs", "outputs",
        };

        public static void Build(ScadNode node, VisualElement container, Action onChange)
        {
            if (node == null) return;
            var type = node.GetType();
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Ignore.Contains(f.Name)) continue;
                var ctrl = BuildField(node, f, onChange);
                if (ctrl != null) container.Add(ctrl);
            }
        }

        static VisualElement BuildField(ScadNode node, FieldInfo f, Action onChange)
        {
            var label = Nicify(f.Name);
            if (f.FieldType == typeof(float))
            {
                var fld = new FloatField(label) { value = (float)f.GetValue(node) };
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            if (f.FieldType == typeof(int))
            {
                var fld = new IntegerField(label) { value = (int)f.GetValue(node) };
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            if (f.FieldType == typeof(bool))
            {
                var fld = new Toggle(label) { value = (bool)f.GetValue(node) };
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            if (f.FieldType == typeof(string))
            {
                var fld = new TextField(label) { value = (string)f.GetValue(node) ?? "" };
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            if (f.FieldType == typeof(Vector3))
            {
                var fld = new Vector3Field(label) { value = (Vector3)f.GetValue(node) };
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            if (f.FieldType == typeof(Vector2))
            {
                var fld = new Vector2Field(label) { value = (Vector2)f.GetValue(node) };
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            if (f.FieldType == typeof(Color))
            {
                var fld = new ColorField(label) { value = (Color)f.GetValue(node) };
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            if (f.FieldType.IsEnum)
            {
                var fld = new EnumField(label, (Enum)f.GetValue(node));
                fld.RegisterValueChangedCallback(evt => { f.SetValue(node, evt.newValue); onChange?.Invoke(); });
                return fld;
            }
            return null;
        }

        static string Nicify(string name) => ObjectNames.NicifyVariableName(name);
    }
}
