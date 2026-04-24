using System;

namespace SCADPlugin.Editor.Graph.Nodes
{
    [ScadNode("Combine Vector3", "Vector")]
    [Serializable]
    public class CombineVector3Node : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("x", "X", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("y", "Y", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("z", "Z", ScadPortType.Number, "0"));
            outputs.Add(ScadPort.Out("out", "Vector", ScadPortType.Vector3));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"[{Read("x", c)}, {Read("y", c)}, {Read("z", c)}]";
    }

    [ScadNode("Combine Vector2", "Vector")]
    [Serializable]
    public class CombineVector2Node : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("x", "X", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("y", "Y", ScadPortType.Number, "0"));
            outputs.Add(ScadPort.Out("out", "Vector", ScadPortType.Vector2));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"[{Read("x", c)}, {Read("y", c)}]";
    }

    [ScadNode("Split Vector3", "Vector")]
    [Serializable]
    public class SplitVector3Node : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("in", "Vector", ScadPortType.Vector3, "[0, 0, 0]"));
            outputs.Add(ScadPort.Out("x", "X", ScadPortType.Number));
            outputs.Add(ScadPort.Out("y", "Y", ScadPortType.Number));
            outputs.Add(ScadPort.Out("z", "Z", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var v = Read("in", c);
            return outputPortId switch
            {
                "x" => $"({v}).x",
                "y" => $"({v}).y",
                "z" => $"({v}).z",
                _   => "0",
            };
        }
    }

    [ScadNode("Vector Length", "Vector")]
    [Serializable]
    public class VectorLengthNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("in", "Vector", ScadPortType.Vector3, "[0, 0, 0]"));
            outputs.Add(ScadPort.Out("out", "Length", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"norm({Read("in", c)})";
    }

    [ScadNode("Normalize", "Vector")]
    [Serializable]
    public class NormalizeNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("in", "Vector", ScadPortType.Vector3, "[1, 0, 0]"));
            outputs.Add(ScadPort.Out("out", "Vector", ScadPortType.Vector3));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var v = Read("in", c);
            return $"({v} / norm({v}))";
        }
    }

    [ScadNode("Dot", "Vector")]
    [Serializable]
    public class DotNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("a", "A", ScadPortType.Vector3, "[0, 0, 0]"));
            inputs.Add(ScadPort.In("b", "B", ScadPortType.Vector3, "[0, 0, 0]"));
            outputs.Add(ScadPort.Out("out", "Dot", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var a = Read("a", c);
            var b = Read("b", c);
            return $"({a}.x * {b}.x + {a}.y * {b}.y + {a}.z * {b}.z)";
        }
    }

    [ScadNode("Cross", "Vector")]
    [Serializable]
    public class CrossNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("a", "A", ScadPortType.Vector3, "[0, 0, 0]"));
            inputs.Add(ScadPort.In("b", "B", ScadPortType.Vector3, "[0, 0, 0]"));
            outputs.Add(ScadPort.Out("out", "Cross", ScadPortType.Vector3));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"cross({Read("a", c)}, {Read("b", c)})";
    }
}
