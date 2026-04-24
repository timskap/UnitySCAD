using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SCADPlugin.Editor.Graph
{
    // Turns a ScadGraph into an OpenSCAD source string. The algorithm is a
    // reverse walk from the output node: each node's Emit returns the SCAD
    // expression for one of its outputs; the compiler recurses into the
    // nodes that feed this node's inputs. Results are memoised per
    // (nodeId, portId) so a node with multiple consumers only emits once.
    public class ScadGraphCompiler
    {
        public readonly ScadGraph graph;
        readonly Dictionary<string, string> _emitCache = new Dictionary<string, string>();
        readonly HashSet<string> _visiting = new HashSet<string>();
        readonly List<string> _diagnostics = new List<string>();
        public IReadOnlyList<string> Diagnostics => _diagnostics;

        public ScadGraphCompiler(ScadGraph g)
        {
            graph = g ?? throw new ArgumentNullException(nameof(g));
        }

        public string Compile()
        {
            _emitCache.Clear();
            _visiting.Clear();
            _diagnostics.Clear();

            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated from ScadGraph. Do not edit by hand.");

            if (!string.IsNullOrWhiteSpace(graph.preamble))
            {
                sb.AppendLine();
                sb.AppendLine("// Preamble (user-defined modules / functions / special variables)");
                sb.AppendLine(graph.preamble.TrimEnd());
            }

            EmitExposedParameters(sb);

            if (string.IsNullOrEmpty(graph.outputNodeId))
            {
                _diagnostics.Add("Graph has no output node.");
                sb.AppendLine("// No output node.");
                return sb.ToString();
            }

            var output = graph.FindNode(graph.outputNodeId);
            if (output == null)
            {
                _diagnostics.Add($"Output node '{graph.outputNodeId}' not found.");
                sb.AppendLine("// Output node missing.");
                return sb.ToString();
            }
            if (output.inputs.Count == 0)
            {
                _diagnostics.Add("Output node has no input port.");
                sb.AppendLine("// Output node has no input.");
                return sb.ToString();
            }

            var geometry = ReadInput(output, output.inputs[0].id);
            sb.AppendLine();
            sb.Append(geometry);
            if (!geometry.TrimEnd().EndsWith(";")) sb.Append(";");
            sb.AppendLine();
            return sb.ToString();
        }

        void EmitExposedParameters(StringBuilder sb)
        {
            if (graph.exposedParameters == null || graph.exposedParameters.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine("// Exposed parameters");
            foreach (var p in graph.exposedParameters)
            {
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                var v = string.IsNullOrEmpty(p.defaultLiteral)
                    ? ScadLiteral.DefaultFor(p.type)
                    : p.defaultLiteral;
                if (p.hasRange && p.type == ScadPortType.Number)
                {
                    sb.AppendLine(
                        $"{SanitizeIdentifier(p.id)} = {v}; // [{ScadLiteral.Number(p.min)}:{ScadLiteral.Number(p.max)}]");
                }
                else
                {
                    sb.AppendLine($"{SanitizeIdentifier(p.id)} = {v};");
                }
            }
        }

        // Resolve an input port to a SCAD expression: follow any incoming
        // connection, otherwise use the port's default literal.
        public string ReadInput(ScadNode node, string inputPortId)
        {
            var port = node.InputById(inputPortId);
            if (port == null)
            {
                _diagnostics.Add($"Node '{node.id}' is missing expected input port '{inputPortId}'.");
                return ScadLiteral.DefaultFor(ScadPortType.Any);
            }

            var conn = FindIncoming(node.id, inputPortId);
            if (conn == null)
                return string.IsNullOrEmpty(port.defaultLiteral)
                    ? ScadLiteral.DefaultFor(port.type)
                    : port.defaultLiteral;

            var src = graph.FindNode(conn.fromNodeId);
            if (src == null)
            {
                _diagnostics.Add($"Dangling connection: source node '{conn.fromNodeId}' not found.");
                return ScadLiteral.DefaultFor(port.type);
            }
            return EmitPort(src, conn.fromPortId);
        }

        public string EmitPort(ScadNode node, string outputPortId)
        {
            var key = node.id + "::" + outputPortId;
            if (_emitCache.TryGetValue(key, out var cached)) return cached;
            if (!_visiting.Add(node.id))
            {
                _diagnostics.Add($"Cycle detected at node '{node.id}'.");
                return ScadLiteral.DefaultFor(ScadPortType.Any);
            }
            var result = node.Emit(outputPortId, this);
            _visiting.Remove(node.id);
            _emitCache[key] = result;
            return result;
        }

        public ScadConnection FindIncoming(string toNodeId, string toPortId) =>
            graph.FindIncoming(toNodeId, toPortId);

        internal static string SanitizeIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
                if (i > 0) ok |= (c >= '0' && c <= '9');
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }

        // Utility for geometry nodes — wraps child statements into a
        // grouped block. If there is exactly one child and it already ends
        // in `;`, the wrapper is unnecessary and we return it unchanged.
        public static string WrapChildren(IEnumerable<string> children)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var c in children)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                n++;
                sb.Append(c.TrimEnd());
                if (!c.TrimEnd().EndsWith(";")) sb.Append(";");
                sb.Append(' ');
            }
            if (n == 0) return "";
            return sb.ToString().TrimEnd();
        }
    }
}
