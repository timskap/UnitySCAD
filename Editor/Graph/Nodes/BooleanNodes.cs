using System;
using System.Collections.Generic;

namespace SCADPlugin.Editor.Graph.Nodes
{
    // Boolean and composition ops all follow the same pattern: one output
    // Solid, a variable number of Child input ports. Extra ports are added
    // dynamically by the editor as connections are made (see
    // ScadGraphView.EnsureBooleanExtraPorts), but the serialized layout is
    // authoritative.

    [Serializable]
    public abstract class MultiChildNode : ScadNode
    {
        public int childCount = 2;

        protected void RebuildChildPorts()
        {
            // Preserve output; rebuild inputs to match childCount.
            inputs.Clear();
            for (int i = 0; i < childCount; i++)
                inputs.Add(ScadPort.In($"child_{i}", $"Child {i + 1}", ScadPortType.Solid));
        }

        protected override void DefinePorts()
        {
            RebuildChildPorts();
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        protected IEnumerable<string> EmitChildren(ScadGraphCompiler c)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                var port = inputs[i];
                if (port == null) continue;
                var conn = c.FindIncoming(id, port.id);
                if (conn == null) continue;
                yield return ReadSolid(port.id, c);
            }
        }
    }

    [ScadNode("Union", "Boolean")]
    [Serializable]
    public class UnionNode : MultiChildNode
    {
        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"union() {{ {ScadGraphCompiler.WrapChildren(EmitChildren(c))} }}";
    }

    [ScadNode("Difference", "Boolean",
        Tooltip = "First child minus the rest.")]
    [Serializable]
    public class DifferenceNode : MultiChildNode
    {
        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"difference() {{ {ScadGraphCompiler.WrapChildren(EmitChildren(c))} }}";
    }

    [ScadNode("Intersection", "Boolean")]
    [Serializable]
    public class IntersectionNode : MultiChildNode
    {
        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"intersection() {{ {ScadGraphCompiler.WrapChildren(EmitChildren(c))} }}";
    }

    [ScadNode("Hull", "Boolean")]
    [Serializable]
    public class HullNode : MultiChildNode
    {
        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"hull() {{ {ScadGraphCompiler.WrapChildren(EmitChildren(c))} }}";
    }

    [ScadNode("Minkowski", "Boolean")]
    [Serializable]
    public class MinkowskiNode : MultiChildNode
    {
        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"minkowski() {{ {ScadGraphCompiler.WrapChildren(EmitChildren(c))} }}";
    }
}
