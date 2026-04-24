using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCADPlugin.Editor.Graph.Nodes;
using UnityEditor;
using UnityEngine;

namespace SCADPlugin.Editor.Graph.Import
{
    // Public facade: one-line SCAD-text → ScadGraph. Used by the editor
    // window's import button and by the .scad Asset menu item.
    public static class ScadSourceToGraph
    {
        public class Result
        {
            public ScadGraph graph;
            public readonly List<string> warnings = new List<string>();
            public readonly List<string> errors   = new List<string>();
        }

        public static Result Build(string source)
        {
            var r = new Result();

            // Extract `module` and `function` definitions. The preamble
            // still carries the raw text (so the generated SCAD remains
            // compilable even if in-graph expansion fails), but parsed
            // module ASTs are also produced so the builder can inline
            // calls to user-defined modules into a rich node graph.
            var pre = ScadSourcePreprocessor.Extract(source ?? string.Empty);

            var lexer = new Lexer(pre.cleaned);
            var tokens = lexer.Tokenize();
            r.errors.AddRange(lexer.errors);

            var parser = new Parser(tokens);
            var program = parser.ParseProgram();
            r.errors.AddRange(parser.errors);

            var graph = ScriptableObject.CreateInstance<ScadGraph>();
            var output = new OutputNode();
            graph.AddNode(output, Vector2.zero);
            graph.outputNodeId = output.id;
            r.graph = graph;

            var builder = new GraphBuilder(
                graph, r.warnings, pre.userModules, pre.preamble, pre.parsedModules);
            builder.Build(program);
            builder.AutoLayout();

            return r;
        }

        // ---- File / menu integration --------------------------------------

        [MenuItem("Assets/SCAD/Convert .scad to Graph", false, 84)]
        static void ConvertScadAssetToGraph()
        {
            var obj = Selection.activeObject;
            var path = obj != null ? AssetDatabase.GetAssetPath(obj) : null;
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(".scad", StringComparison.OrdinalIgnoreCase))
                return;

            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("SCAD Import", "Read failed: " + ex.Message, "OK");
                return;
            }

            var result = Build(source);
            ReportDiagnostics(result);

            var targetPath = Path.ChangeExtension(path, ".scadgraph");
            targetPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);
            var json = EditorJsonUtility.ToJson(result.graph, prettyPrint: true);
            File.WriteAllText(targetPath, json);
            AssetDatabase.ImportAsset(targetPath);

            var importedAsset = AssetDatabase.LoadMainAssetAtPath(targetPath);
            if (importedAsset != null) Selection.activeObject = importedAsset;
            EditorUtility.RevealInFinder(targetPath);
        }

        [MenuItem("Assets/SCAD/Convert .scad to Graph", true)]
        static bool ConvertScadAssetToGraph_Validate()
        {
            var obj = Selection.activeObject;
            var path = obj != null ? AssetDatabase.GetAssetPath(obj) : null;
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".scad", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ReportDiagnostics(Result r)
        {
            foreach (var e in r.errors) Debug.LogError("SCAD import: " + e);
            foreach (var w in r.warnings) Debug.LogWarning("SCAD import: " + w);
        }
    }

    internal class GraphBuilder
    {
        readonly ScadGraph _graph;
        readonly List<string> _warnings;
        readonly Dictionary<string, ScadNode> _parameterNodes = new Dictionary<string, ScadNode>();
        readonly HashSet<string> _declaredParameters = new HashSet<string>();
        readonly HashSet<string> _userModules;
        readonly StringBuilder _preamble = new StringBuilder();
        readonly Dictionary<string, ScadSourcePreprocessor.ParsedModuleDef> _moduleDefs;

        // Binding stack used when inlining module bodies: the top scope
        // maps local names (parameters, for-loop vars, body-local
        // assignments) to the expressions they stand for. EmitExpr walks
        // the stack to resolve IdentExpr references before emitting text.
        readonly Stack<Dictionary<string, Expr>> _envStack = new Stack<Dictionary<string, Expr>>();

        // Cycle guard for module inlining: if a module transitively calls
        // itself we stop expanding and fall back to UserModuleCallNode.
        readonly Stack<string> _inlineStack = new Stack<string>();

        // Positional-argument ordering for the subset of SCAD modules we
        // recognise. Positional args get converted into keyword args here
        // so the per-module Fill* methods only deal with names.
        static readonly Dictionary<string, string[]> Positional = new Dictionary<string, string[]>
        {
            ["cube"]           = new[] { "size", "center" },
            ["sphere"]         = new[] { "r" },
            ["cylinder"]       = new[] { "h", "r1", "r2", "center" },
            ["square"]         = new[] { "size", "center" },
            ["circle"]         = new[] { "r" },
            ["polygon"]        = new[] { "points", "paths", "convexity" },
            ["polyhedron"]     = new[] { "points", "faces", "convexity" },
            ["text"]           = new[] { "text", "size" },
            ["translate"]      = new[] { "v" },
            ["rotate"]         = new[] { "a", "v" },
            ["scale"]          = new[] { "v" },
            ["mirror"]         = new[] { "v" },
            ["color"]          = new[] { "c", "alpha" },
            ["resize"]         = new[] { "newsize" },
            ["offset"]         = new[] { "r", "delta" },
            ["linear_extrude"] = new[] { "height" },
            ["rotate_extrude"] = new[] { "angle" },
            ["projection"]     = new[] { "cut" },
        };

        public GraphBuilder(
            ScadGraph graph,
            List<string> warnings,
            HashSet<string> userModules = null,
            string initialPreamble = null,
            Dictionary<string, ScadSourcePreprocessor.ParsedModuleDef> parsedModules = null)
        {
            _graph = graph;
            _warnings = warnings;
            _userModules = userModules ?? new HashSet<string>();
            _moduleDefs = parsedModules ?? new Dictionary<string, ScadSourcePreprocessor.ParsedModuleDef>();
            if (!string.IsNullOrEmpty(initialPreamble))
                _preamble.Append(initialPreamble);
        }

        public void Build(List<Stmt> program)
        {
            // Pass 1: top-level assignments. `$var = ...;` (special vars)
            // go straight into the preamble; ordinary assignments become
            // exposed parameters the user can wire to or tweak.
            foreach (var s in program)
            {
                if (s is AssignStmt a)
                {
                    if (a.isDollar)
                    {
                        _preamble.Append(a.name).Append(" = ").Append(EmitExpr(a.value)).Append(";\n");
                        continue;
                    }
                    if (_declaredParameters.Contains(a.name))
                    {
                        _warnings.Add($"Parameter '{a.name}' declared more than once; keeping first value.");
                        continue;
                    }
                    _declaredParameters.Add(a.name);
                    _graph.exposedParameters.Add(new ScadExposedParameter
                    {
                        id = a.name,
                        label = a.name,
                        type = InferExprType(a.value),
                        defaultLiteral = EmitExpr(a.value),
                    });
                }
            }

            // Pass 2: geometry. The last top-level module statement is
            // taken as the graph's result; preceding ones become siblings
            // under an implicit Union (matches SCAD's top-level semantics).
            var modules = program.OfType<ModuleStmt>().ToList();
            if (modules.Count == 0)
            {
                _warnings.Add("No geometry statements found; produced an empty graph.");
                return;
            }

            ScadNode resultNode;
            if (modules.Count == 1)
            {
                resultNode = BuildModule(modules[0]);
            }
            else
            {
                var union = new UnionNode { childCount = modules.Count };
                _graph.AddNode(union, Vector2.zero);
                // Refresh child ports now that we set childCount above.
                RebuildMultiChildPorts(union);
                for (int i = 0; i < modules.Count; i++)
                {
                    var child = BuildModule(modules[i]);
                    if (child != null)
                    {
                        var port = union.inputs[i];
                        _graph.Connect(child.id, FirstOutputPortId(child), union.id, port.id);
                    }
                }
                resultNode = union;
            }

            if (resultNode != null)
            {
                var output = _graph.FindNode(_graph.outputNodeId);
                if (output != null)
                {
                    _graph.Connect(resultNode.id, FirstOutputPortId(resultNode),
                                   output.id, output.inputs[0].id);
                }
            }

            _graph.preamble = _preamble.ToString().TrimEnd();
        }

        static void RebuildMultiChildPorts(MultiChildNode n)
        {
            // MultiChildNode.DefinePorts sets 2 children by default; after
            // we change childCount we need to regenerate.
            n.inputs.Clear();
            for (int i = 0; i < n.childCount; i++)
                n.inputs.Add(ScadPort.In($"child_{i}", $"Child {i + 1}", ScadPortType.Solid));
        }

        static string FirstOutputPortId(ScadNode n) =>
            n.outputs != null && n.outputs.Count > 0 ? n.outputs[0].id : "out";

        ScadNode BuildModule(ModuleStmt m)
        {
            // Pseudo-module-calls that the SCAD grammar shares with real
            // calls: `for(...)` and `if(...)` parse the same way as a
            // module invocation with one or more args. Intercept them.
            if (m.name == "for") return BuildFor(m);
            if (m.name == "if")  return BuildIfAsRaw(m);

            // User-defined modules get inlined when we have a parsed body,
            // falling back to UserModuleCallNode (which just emits a
            // preamble-defined call) if expansion isn't possible.
            if (_userModules.Contains(m.name))
            {
                if (_moduleDefs.TryGetValue(m.name, out var def) &&
                    !_inlineStack.Contains(m.name))
                {
                    return InlineModuleCall(def, m);
                }
                var u = new UserModuleCallNode
                {
                    moduleName = m.name,
                    rawArguments = RenderArgs(m.args),
                };
                _graph.AddNode(u, Vector2.zero);
                return u;
            }

            var named = NormalizeArgs(m.name, m.args);

            switch (m.name)
            {
                case "cube":           return FillCube(named, m.children);
                case "sphere":         return FillSphere(named, m.children);
                case "cylinder":       return FillCylinder(named, m.children);
                case "square":         return FillSquare(named, m.children);
                case "circle":         return FillCircle(named, m.children);
                case "polygon":        return FillPolygon(named, m.children);
                case "polyhedron":     return FillPolyhedron(named, m.children);
                case "text":           return FillText(named, m.children);

                case "translate":      return FillUnary<TranslateNode>("offset", named, "v", m.children);
                case "rotate":         return FillUnary<RotateNode>("angles", named, "a", m.children);
                case "scale":          return FillUnary<ScaleNode>("factor", named, "v", m.children);
                case "mirror":         return FillUnary<MirrorNode>("normal", named, "v", m.children);
                case "color":          return FillColor(named, m.children);
                case "resize":         return FillUnary<ResizeNode>("newsize", named, "newsize", m.children);
                case "offset":         return FillOffset(named, m.children);

                case "union":          return FillMulti<UnionNode>(m.children);
                case "difference":     return FillMulti<DifferenceNode>(m.children);
                case "intersection":   return FillMulti<IntersectionNode>(m.children);
                case "hull":           return FillMulti<HullNode>(m.children);
                case "minkowski":      return FillMulti<MinkowskiNode>(m.children);

                case "linear_extrude": return FillLinearExtrude(named, m.children);
                case "rotate_extrude": return FillRotateExtrude(named, m.children);
                case "projection":     return FillProjection(named, m.children);
            }

            // Unknown module → preserve as raw SCAD and wrap children under it
            // by regenerating the source. This keeps the graph compilable
            // even if nodes for exotic modules don't exist yet.
            _warnings.Add($"Unknown module '{m.name}' preserved as Custom Statement.");
            var custom = new CustomStatementNode { statement = RenderModule(m) };
            _graph.AddNode(custom, Vector2.zero);
            return custom;
        }

        static Dictionary<string, Arg> NormalizeArgs(string module, List<Arg> args)
        {
            var named = new Dictionary<string, Arg>();
            Positional.TryGetValue(module, out var order);
            int idx = 0;
            foreach (var a in args)
            {
                if (!string.IsNullOrEmpty(a.name))
                {
                    named[a.name] = a;
                }
                else if (order != null && idx < order.Length)
                {
                    named[order[idx]] = a;
                    idx++;
                }
                // extra positional args beyond the known order are silently dropped
            }
            return named;
        }

        // ---- Per-module fillers --------------------------------------------

        ScadNode FillCube(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new CubeNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("size", out var size)) SetPortDefault(n, "size", size.value);
            if (args.TryGetValue("center", out var c)) n.center = AsBool(c.value, false);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillSphere(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new SphereNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("r", out var r)) SetPortDefault(n, "radius", r.value);
            else if (args.TryGetValue("d", out var d))
                SetPortDefault(n, "radius", new BinaryExpr { op = "/", left = d.value, right = new NumExpr { value = 2 } });
            if (args.TryGetValue("$fn", out var fn)) n.fn = AsInt(fn.value, 0);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillCylinder(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new CylinderNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("h", out var h))   SetPortDefault(n, "height", h.value);
            if (args.TryGetValue("r",  out var r))  { SetPortDefault(n, "radius1", r.value); SetPortDefault(n, "radius2", r.value); }
            if (args.TryGetValue("r1", out var r1)) SetPortDefault(n, "radius1", r1.value);
            if (args.TryGetValue("r2", out var r2)) SetPortDefault(n, "radius2", r2.value);
            if (args.TryGetValue("d",  out var d))
            {
                var half = new BinaryExpr { op = "/", left = d.value, right = new NumExpr { value = 2 } };
                SetPortDefault(n, "radius1", half);
                SetPortDefault(n, "radius2", half);
            }
            if (args.TryGetValue("center", out var ctr)) n.center = AsBool(ctr.value, false);
            if (args.TryGetValue("$fn", out var fn))     n.fn = AsInt(fn.value, 0);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillSquare(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new SquareNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("size", out var size)) SetPortDefault(n, "size", size.value);
            if (args.TryGetValue("center", out var c)) n.center = AsBool(c.value, false);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillCircle(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new CircleNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("r", out var r)) SetPortDefault(n, "radius", r.value);
            if (args.TryGetValue("$fn", out var fn)) n.fn = AsInt(fn.value, 0);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillPolygon(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new PolygonNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("points", out var p)) n.pointsLiteral = Render(p.value);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillPolyhedron(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new PolyhedronNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("points", out var p)) n.pointsLiteral = Render(p.value);
            if (args.TryGetValue("faces",  out var f)) n.facesLiteral  = Render(f.value);
            if (args.TryGetValue("convexity", out var c)) n.convexity = AsInt(c.value, 1);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillText(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new TextNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("text", out var t)) SetPortDefault(n, "text", t.value);
            if (args.TryGetValue("size", out var s)) SetPortDefault(n, "size", s.value);
            if (args.TryGetValue("font", out var f) && f.value is StringExpr fs) n.font = fs.value;
            if (args.TryGetValue("halign", out var h) && h.value is StringExpr hs) n.halign = hs.value;
            if (args.TryGetValue("valign", out var v) && v.value is StringExpr vs) n.valign = vs.value;
            if (args.TryGetValue("$fn", out var fn)) n.fn = AsInt(fn.value, 0);
            WarnUnsupportedChildren(n, children);
            return n;
        }

        ScadNode FillUnary<T>(string portId, Dictionary<string, Arg> args, string argName, List<Stmt> children) where T : ScadNode, new()
        {
            var n = new T();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue(argName, out var a)) SetPortDefault(n, portId, a.value);
            ConnectChildrenToFirstSolidPort(n, children);
            return n;
        }

        ScadNode FillColor(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new ColorNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("c", out var c)) SetPortDefault(n, "color", c.value);
            ConnectChildrenToFirstSolidPort(n, children);
            return n;
        }

        ScadNode FillOffset(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new OffsetNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("r", out var r)) { SetPortDefault(n, "amount", r.value); n.useDelta = false; }
            else if (args.TryGetValue("delta", out var d)) { SetPortDefault(n, "amount", d.value); n.useDelta = true; }
            ConnectChildrenToFirstSolidPort(n, children);
            return n;
        }

        ScadNode FillMulti<T>(List<Stmt> children) where T : MultiChildNode, new()
        {
            var n = new T { childCount = Math.Max(2, children.Count) };
            _graph.AddNode(n, Vector2.zero);
            RebuildMultiChildPorts(n);
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i] as ModuleStmt;
                if (child == null) continue;
                var cn = BuildModule(child);
                if (cn != null && i < n.inputs.Count)
                    _graph.Connect(cn.id, FirstOutputPortId(cn), n.id, n.inputs[i].id);
            }
            return n;
        }

        ScadNode FillLinearExtrude(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new LinearExtrudeNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("height", out var h)) SetPortDefault(n, "height", h.value);
            if (args.TryGetValue("twist",  out var t)) SetPortDefault(n, "twist",  t.value);
            if (args.TryGetValue("scale",  out var s)) SetPortDefault(n, "scale",  s.value);
            if (args.TryGetValue("center", out var c)) n.center = AsBool(c.value, false);
            if (args.TryGetValue("slices", out var sl)) n.slices = AsInt(sl.value, 1);
            if (args.TryGetValue("$fn", out var fn)) n.fn = AsInt(fn.value, 0);
            ConnectChildrenToFirstSolidPort(n, children);
            return n;
        }

        ScadNode FillRotateExtrude(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new RotateExtrudeNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("angle", out var a)) SetPortDefault(n, "angle", a.value);
            if (args.TryGetValue("$fn", out var fn)) n.fn = AsInt(fn.value, 0);
            ConnectChildrenToFirstSolidPort(n, children);
            return n;
        }

        ScadNode FillProjection(Dictionary<string, Arg> args, List<Stmt> children)
        {
            var n = new ProjectionNode();
            _graph.AddNode(n, Vector2.zero);
            if (args.TryGetValue("cut", out var c)) n.cut = AsBool(c.value, false);
            ConnectChildrenToFirstSolidPort(n, children);
            return n;
        }

        void ConnectChildrenToFirstSolidPort(ScadNode parent, List<Stmt> children)
        {
            if (children == null || children.Count == 0) return;

            // When a transform has multiple children, wrap them in an
            // implicit Union so the single "child" port still gets one
            // concrete source.
            ScadNode source;
            var moduleChildren = children.OfType<ModuleStmt>().ToList();
            if (moduleChildren.Count == 0) return;
            if (moduleChildren.Count == 1)
            {
                source = BuildModule(moduleChildren[0]);
            }
            else
            {
                var union = new UnionNode { childCount = moduleChildren.Count };
                _graph.AddNode(union, Vector2.zero);
                RebuildMultiChildPorts(union);
                for (int i = 0; i < moduleChildren.Count; i++)
                {
                    var cn = BuildModule(moduleChildren[i]);
                    if (cn != null)
                        _graph.Connect(cn.id, FirstOutputPortId(cn), union.id, union.inputs[i].id);
                }
                source = union;
            }

            if (source == null) return;

            // Find the first unconnected solid/shape input on the parent.
            foreach (var p in parent.inputs)
            {
                if (p.type != ScadPortType.Solid && p.type != ScadPortType.Shape) continue;
                if (_graph.FindIncoming(parent.id, p.id) != null) continue;
                _graph.Connect(source.id, FirstOutputPortId(source), parent.id, p.id);
                return;
            }
        }

        void WarnUnsupportedChildren(ScadNode parent, List<Stmt> children)
        {
            if (children == null || children.Count == 0) return;
            _warnings.Add(
                $"{parent.GetType().Name} doesn't accept children; dropped {children.Count} child statement(s).");
        }

        // ---- Expression → port default ------------------------------------

        void SetPortDefault(ScadNode node, string portId, Expr expr)
        {
            var port = node.InputById(portId);
            if (port == null)
            {
                _warnings.Add($"Node '{node.GetType().Name}' has no port '{portId}'.");
                return;
            }

            // Resolve local bindings (module parameters, for-loop vars,
            // etc.) before inspecting the expression. After substitution,
            // if the result is a simple reference to a top-level exposed
            // parameter we wire a ParameterNode so live edits still flow
            // through; otherwise the substituted form becomes the port's
            // default literal.
            var resolved = Substitute(expr);
            if (resolved is IdentExpr id && !id.isDollar &&
                _declaredParameters.Contains(id.name))
            {
                var pnode = GetOrCreateParameterNode(id.name);
                _graph.Connect(pnode.id, "out", node.id, port.id);
                return;
            }

            port.defaultLiteral = EmitExpr(resolved);
        }

        ScadNode GetOrCreateParameterNode(string name)
        {
            if (_parameterNodes.TryGetValue(name, out var existing)) return existing;
            var p = new ParameterNode { parameterName = name };
            _graph.AddNode(p, Vector2.zero);
            _parameterNodes[name] = p;
            return p;
        }

        static ScadPortType InferExprType(Expr e) => e switch
        {
            BoolExpr => ScadPortType.Boolean,
            StringExpr => ScadPortType.String,
            VecExpr v when v.items.Count == 2 => ScadPortType.Vector2,
            VecExpr v when v.items.Count == 3 => ScadPortType.Vector3,
            VecExpr v when v.items.Count == 4 => ScadPortType.Color,
            _ => ScadPortType.Number,
        };

        bool AsBool(Expr e, bool fallback)
        {
            var r = ResolveToConstant(Substitute(e));
            if (r is BoolExpr b) return b.value;
            if (r is NumExpr n) return n.value != 0;
            return fallback;
        }

        int AsInt(Expr e, int fallback)
        {
            var r = ResolveToConstant(Substitute(e));
            if (r is NumExpr n) return (int)n.value;
            return fallback;
        }

        // Env-aware text rendering: substitute first, then format. Call
        // sites that used to call the static EmitExpr should go through
        // this when running inside an inlined module body so local names
        // get replaced with the expressions they stand for.
        string Render(Expr e) => EmitExpr(Substitute(e));

        // Walk the binding stack (top-most scope first) and replace any
        // IdentExpr whose name is bound. Non-dollar idents only; dollar
        // variables (`$fn`, `$t`) are SCAD-level special variables and
        // stay symbolic.
        Expr Substitute(Expr e)
        {
            switch (e)
            {
                case null:
                    return null;
                case IdentExpr id when !id.isDollar:
                    foreach (var scope in _envStack)
                        if (scope.TryGetValue(id.name, out var bound))
                            return Substitute(bound);
                    return id;
                case VecExpr v:
                {
                    var items = new List<Expr>(v.items.Count);
                    foreach (var it in v.items) items.Add(Substitute(it));
                    return new VecExpr { items = items };
                }
                case RangeExpr rg:
                    return new RangeExpr
                    {
                        start = Substitute(rg.start),
                        step  = rg.step != null ? Substitute(rg.step) : null,
                        end   = Substitute(rg.end),
                    };
                case UnaryExpr u:
                    return new UnaryExpr { op = u.op, operand = Substitute(u.operand) };
                case BinaryExpr b:
                    return new BinaryExpr { op = b.op, left = Substitute(b.left), right = Substitute(b.right) };
                case CallExpr c:
                {
                    var args = new List<Arg>(c.args.Count);
                    foreach (var a in c.args) args.Add(new Arg { name = a.name, value = Substitute(a.value) });
                    return new CallExpr { name = c.name, args = args };
                }
                default:
                    return e;
            }
        }

        // Best-effort compile-time numeric/boolean folding. Returns a
        // NumExpr/BoolExpr when the expression is fully constant, else
        // null. Used for for-loop range evaluation.
        Expr ResolveToConstant(Expr e)
        {
            if (e is NumExpr || e is BoolExpr || e is StringExpr) return e;
            if (e is UnaryExpr u)
            {
                var inner = ResolveToConstant(Substitute(u.operand));
                if (inner is NumExpr n)
                {
                    return u.op switch
                    {
                        "-" => new NumExpr { value = -n.value },
                        "+" => n,
                        _ => null,
                    };
                }
                return null;
            }
            if (e is BinaryExpr b)
            {
                var l = ResolveToConstant(Substitute(b.left));
                var r = ResolveToConstant(Substitute(b.right));
                if (l is NumExpr ln && r is NumExpr rn)
                {
                    return b.op switch
                    {
                        "+" => new NumExpr { value = ln.value + rn.value },
                        "-" => new NumExpr { value = ln.value - rn.value },
                        "*" => new NumExpr { value = ln.value * rn.value },
                        "/" => rn.value == 0 ? null : new NumExpr { value = ln.value / rn.value },
                        "%" => rn.value == 0 ? null : new NumExpr { value = ln.value % rn.value },
                        _ => null,
                    };
                }
                return null;
            }
            return null;
        }

        // Treat a for-range expression as a list of iteration values.
        // Supports only `[a, b, c, ...]` literal vectors whose items fold
        // to numeric constants. Real SCAD ranges (`[start:step:end]`) are
        // not yet recognised — the builder warns and keeps the raw loop.
        List<Expr> EvaluateRange(Expr e)
        {
            e = Substitute(e);
            if (e is VecExpr v)
            {
                var values = new List<Expr>(v.items.Count);
                foreach (var item in v.items)
                {
                    var c = ResolveToConstant(item);
                    if (c == null) return null;
                    values.Add(c);
                }
                return values;
            }
            if (e is RangeExpr rg)
            {
                var startC = ResolveToConstant(rg.start) as NumExpr;
                var endC   = ResolveToConstant(rg.end)   as NumExpr;
                var stepC  = rg.step != null ? ResolveToConstant(rg.step) as NumExpr : new NumExpr { value = 1 };
                if (startC == null || endC == null || stepC == null || stepC.value == 0) return null;

                var values = new List<Expr>();
                double step = stepC.value;
                // SCAD's range is inclusive at both ends; iterate with a
                // small epsilon to absorb floating-point drift.
                const double eps = 1e-9;
                if (step > 0)
                {
                    for (double v = startC.value; v <= endC.value + eps; v += step)
                        values.Add(new NumExpr { value = v });
                }
                else
                {
                    for (double v = startC.value; v >= endC.value - eps; v += step)
                        values.Add(new NumExpr { value = v });
                }
                // Guard against runaway ranges from accidentally huge
                // numeric inputs.
                if (values.Count > 10000)
                {
                    _warnings.Add("for-loop range expanded to > 10000 iterations; truncating.");
                    values.RemoveRange(10000, values.Count - 10000);
                }
                return values;
            }
            return null;
        }

        ScadNode InlineModuleCall(ScadSourcePreprocessor.ParsedModuleDef def, ModuleStmt call)
        {
            var scope = new Dictionary<string, Expr>();

            // Bind arguments into the scope. Named args win over
            // positional ones; unbound parameters fall back to their
            // default values.
            int positional = 0;
            foreach (var a in call.args)
            {
                if (!string.IsNullOrEmpty(a.name))
                {
                    scope[a.name] = a.value;
                }
                else if (positional < def.parameters.Count)
                {
                    scope[def.parameters[positional].name] = a.value;
                    positional++;
                }
            }
            foreach (var p in def.parameters)
            {
                if (!scope.ContainsKey(p.name) && p.defaultValue != null)
                    scope[p.name] = p.defaultValue;
            }

            // Pre-scan the body for local assignments so expressions
            // further down can resolve them, regardless of textual order.
            foreach (var s in def.body)
            {
                if (s is AssignStmt a && !a.isDollar && !scope.ContainsKey(a.name))
                    scope[a.name] = a.value;
            }

            _envStack.Push(scope);
            _inlineStack.Push(def.name);
            try
            {
                var built = new List<ScadNode>();
                foreach (var s in def.body)
                {
                    if (s is ModuleStmt ms)
                    {
                        var node = BuildModule(ms);
                        if (node != null) built.Add(node);
                    }
                    // AssignStmt entries are already in `scope`; dollar
                    // assignments and anything else is skipped.
                }

                if (built.Count == 0)
                {
                    var empty = new UnionNode { childCount = 2 };
                    _graph.AddNode(empty, Vector2.zero);
                    RebuildMultiChildPorts(empty);
                    return empty;
                }
                if (built.Count == 1) return built[0];

                var union = new UnionNode { childCount = built.Count };
                _graph.AddNode(union, Vector2.zero);
                RebuildMultiChildPorts(union);
                for (int i = 0; i < built.Count; i++)
                    _graph.Connect(built[i].id, FirstOutputPortId(built[i]),
                                   union.id, union.inputs[i].id);
                return union;
            }
            finally
            {
                _inlineStack.Pop();
                _envStack.Pop();
            }
        }

        ScadNode BuildFor(ModuleStmt m)
        {
            // for(i = range [, j = range ...]) body — args carry the
            // iteration bindings, children carry the loop body.
            var bindings = new List<(string name, List<Expr> values)>();
            foreach (var a in m.args)
            {
                if (string.IsNullOrEmpty(a.name))
                    return FallbackForLoop(m, "iteration variable has no name");
                var values = EvaluateRange(a.value);
                if (values == null)
                    return FallbackForLoop(m, $"range for '{a.name}' is not a constant literal list");
                bindings.Add((a.name, values));
            }

            // Cartesian product of all iterators.
            var combos = new List<Dictionary<string, Expr>> { new Dictionary<string, Expr>() };
            foreach (var (name, values) in bindings)
            {
                var next = new List<Dictionary<string, Expr>>(combos.Count * values.Count);
                foreach (var combo in combos)
                {
                    foreach (var v in values)
                    {
                        var nc = new Dictionary<string, Expr>(combo) { [name] = v };
                        next.Add(nc);
                    }
                }
                combos = next;
            }

            var built = new List<ScadNode>();
            foreach (var combo in combos)
            {
                _envStack.Push(combo);
                try
                {
                    foreach (var stmt in m.children)
                    {
                        if (stmt is ModuleStmt ms)
                        {
                            var node = BuildModule(ms);
                            if (node != null) built.Add(node);
                        }
                    }
                }
                finally
                {
                    _envStack.Pop();
                }
            }

            if (built.Count == 0)
            {
                var empty = new UnionNode { childCount = 2 };
                _graph.AddNode(empty, Vector2.zero);
                RebuildMultiChildPorts(empty);
                return empty;
            }
            if (built.Count == 1) return built[0];

            var union = new UnionNode { childCount = built.Count };
            _graph.AddNode(union, Vector2.zero);
            RebuildMultiChildPorts(union);
            for (int i = 0; i < built.Count; i++)
                _graph.Connect(built[i].id, FirstOutputPortId(built[i]),
                               union.id, union.inputs[i].id);
            return union;
        }

        ScadNode FallbackForLoop(ModuleStmt m, string reason)
        {
            _warnings.Add($"for-loop not expanded ({reason}); preserved as Custom Statement.");
            var custom = new CustomStatementNode { statement = RenderModuleWithEnv(m) };
            _graph.AddNode(custom, Vector2.zero);
            return custom;
        }

        ScadNode BuildIfAsRaw(ModuleStmt m)
        {
            _warnings.Add("`if` preserved as Custom Statement (no branch evaluation yet).");
            var custom = new CustomStatementNode { statement = RenderModuleWithEnv(m) };
            _graph.AddNode(custom, Vector2.zero);
            return custom;
        }

        // Env-aware variants of the static renderers, used for fallbacks
        // where we emit raw SCAD and want local bindings resolved into
        // the emitted text.
        string RenderArgsWithEnv(List<Arg> args)
        {
            if (args == null || args.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                if (!string.IsNullOrEmpty(args[i].name)) sb.Append(args[i].name).Append('=');
                sb.Append(Render(args[i].value));
            }
            return sb.ToString();
        }

        string RenderModuleWithEnv(ModuleStmt m)
        {
            var sb = new StringBuilder();
            sb.Append(m.name).Append('(');
            sb.Append(RenderArgsWithEnv(m.args));
            sb.Append(')');
            if (m.children != null && m.children.Count > 0)
            {
                sb.Append(" { ");
                foreach (var child in m.children)
                {
                    if (child is ModuleStmt cm)
                    {
                        var s = RenderModuleWithEnv(cm);
                        sb.Append(s);
                        if (!s.TrimEnd().EndsWith(";")) sb.Append(';');
                        sb.Append(' ');
                    }
                }
                sb.Append('}');
            }
            return sb.ToString();
        }

        // Recursive AST → SCAD text. Also used for preserving unknown
        // modules inside CustomStatementNode.
        static string EmitExpr(Expr e)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (e)
            {
                case NumExpr n:    return n.value.ToString("R", ci);
                case BoolExpr b:   return b.value ? "true" : "false";
                case StringExpr s: return "\"" + (s.value ?? "").Replace("\"", "\\\"") + "\"";
                case UndefExpr:    return "undef";
                case IdentExpr id: return id.isDollar ? id.name : ScadGraphCompiler.SanitizeIdentifier(id.name);
                case VecExpr v:
                {
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < v.items.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(EmitExpr(v.items[i]));
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
                case RangeExpr rg:
                    return rg.step != null
                        ? "[" + EmitExpr(rg.start) + " : " + EmitExpr(rg.step) + " : " + EmitExpr(rg.end) + "]"
                        : "[" + EmitExpr(rg.start) + " : " + EmitExpr(rg.end) + "]";
                case UnaryExpr u:  return "(" + u.op + EmitExpr(u.operand) + ")";
                case BinaryExpr bi: return "(" + EmitExpr(bi.left) + " " + bi.op + " " + EmitExpr(bi.right) + ")";
                case CallExpr c:
                {
                    var sb = new StringBuilder(c.name);
                    sb.Append('(');
                    for (int i = 0; i < c.args.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        if (!string.IsNullOrEmpty(c.args[i].name)) sb.Append(c.args[i].name).Append('=');
                        sb.Append(EmitExpr(c.args[i].value));
                    }
                    sb.Append(')');
                    return sb.ToString();
                }
            }
            return "undef";
        }

        static string RenderArgs(List<Arg> args)
        {
            if (args == null || args.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                if (!string.IsNullOrEmpty(args[i].name)) sb.Append(args[i].name).Append('=');
                sb.Append(EmitExpr(args[i].value));
            }
            return sb.ToString();
        }

        static string RenderModule(ModuleStmt m)
        {
            var sb = new StringBuilder();
            sb.Append(m.name).Append('(');
            for (int i = 0; i < m.args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                if (!string.IsNullOrEmpty(m.args[i].name)) sb.Append(m.args[i].name).Append('=');
                sb.Append(EmitExpr(m.args[i].value));
            }
            sb.Append(')');
            if (m.children != null && m.children.Count > 0)
            {
                sb.Append(" { ");
                foreach (var child in m.children)
                {
                    if (child is ModuleStmt cm)
                    {
                        sb.Append(RenderModule(cm));
                        if (!RenderModule(cm).TrimEnd().EndsWith(";")) sb.Append(';');
                        sb.Append(' ');
                    }
                }
                sb.Append('}');
            }
            return sb.ToString();
        }

        // ---- Layout --------------------------------------------------------

        // Columnar layout: output on the right, feeders placed one column
        // to the left per connection hop. Within a column, nodes are
        // stacked vertically in discovery order.
        public void AutoLayout()
        {
            var output = _graph.FindNode(_graph.outputNodeId);
            if (output == null) return;

            var depth = new Dictionary<string, int>();
            var queue = new Queue<(string id, int d)>();
            queue.Enqueue((output.id, 0));
            depth[output.id] = 0;

            while (queue.Count > 0)
            {
                var (nid, d) = queue.Dequeue();
                var n = _graph.FindNode(nid);
                if (n == null) continue;
                foreach (var p in n.inputs)
                {
                    var conn = _graph.FindIncoming(n.id, p.id);
                    if (conn == null) continue;
                    if (depth.TryGetValue(conn.fromNodeId, out var existing) && existing >= d + 1) continue;
                    depth[conn.fromNodeId] = d + 1;
                    queue.Enqueue((conn.fromNodeId, d + 1));
                }
            }

            const float colWidth = 260f;
            const float rowHeight = 160f;
            var rowCounter = new Dictionary<int, int>();
            foreach (var node in _graph.nodes)
            {
                if (node == null) continue;
                depth.TryGetValue(node.id, out var d);
                rowCounter.TryGetValue(d, out var row);
                rowCounter[d] = row + 1;
                node.position = new Vector2(-d * colWidth, row * rowHeight);
            }

            // Exposed-parameter nodes (unreachable from output via incoming
            // edges) land at depth 0 otherwise; nudge them further left so
            // they don't overlap the output.
            foreach (var n in _parameterNodes.Values)
            {
                if (!depth.ContainsKey(n.id))
                    n.position = new Vector2(-3 * colWidth, 0);
            }
        }
    }
}
