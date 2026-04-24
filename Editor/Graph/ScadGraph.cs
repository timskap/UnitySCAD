using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCADPlugin.Editor.Graph
{
    // Graph as a ScriptableObject. Stored inside a `.scadgraph` asset as
    // JSON (serialised via EditorJsonUtility so [SerializeReference] node
    // polymorphism survives the round trip).
    public class ScadGraph : ScriptableObject
    {
        [SerializeReference] public List<ScadNode> nodes = new List<ScadNode>();
        public List<ScadConnection> connections = new List<ScadConnection>();
        public List<ScadExposedParameter> exposedParameters = new List<ScadExposedParameter>();

        // Explicit pointer to the single output node. The compiler starts
        // here and walks backwards; nodes not reachable from the output are
        // dead-stripped from emission.
        public string outputNodeId;

        // Free-form SCAD text prepended to the generated source before
        // exposed parameters. Used to carry user-defined module/function
        // definitions (and special variable assignments like `$fn`)
        // captured by the SCAD source importer.
        [TextArea(4, 20)] public string preamble = string.Empty;

        public int schemaVersion = 1;

        public ScadNode FindNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || nodes == null) return null;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] != null && nodes[i].id == nodeId) return nodes[i];
            return null;
        }

        public void AddNode(ScadNode node, Vector2 position)
        {
            if (node == null) return;
            node.position = position;
            nodes.Add(node);
        }

        public void RemoveNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            nodes.RemoveAll(n => n == null || n.id == nodeId);
            connections.RemoveAll(c =>
                c == null || c.fromNodeId == nodeId || c.toNodeId == nodeId);
            if (outputNodeId == nodeId) outputNodeId = null;
        }

        public void Connect(string fromNode, string fromPort, string toNode, string toPort)
        {
            // One input port drives at most one source — replace any prior
            // connection into the same input.
            connections.RemoveAll(c =>
                c != null && c.toNodeId == toNode && c.toPortId == toPort);
            connections.Add(new ScadConnection
            {
                fromNodeId = fromNode,
                fromPortId = fromPort,
                toNodeId = toNode,
                toPortId = toPort,
            });
        }

        public void Disconnect(string toNode, string toPort)
        {
            connections.RemoveAll(c =>
                c != null && c.toNodeId == toNode && c.toPortId == toPort);
        }

        public ScadConnection FindIncoming(string toNode, string toPort)
        {
            if (connections == null) return null;
            for (int i = 0; i < connections.Count; i++)
            {
                var c = connections[i];
                if (c != null && c.toNodeId == toNode && c.toPortId == toPort) return c;
            }
            return null;
        }

        public static ScadGraph CreateDefault()
        {
            var g = CreateInstance<ScadGraph>();
            var output = new Nodes.OutputNode();
            g.AddNode(output, new Vector2(200, 0));
            g.outputNodeId = output.id;
            return g;
        }
    }
}
