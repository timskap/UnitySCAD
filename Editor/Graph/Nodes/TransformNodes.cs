using System;

namespace SCADPlugin.Editor.Graph.Nodes
{
    [ScadNode("Translate", "Transform")]
    [Serializable]
    public class TranslateNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("offset", "Offset", ScadPortType.Vector3, "[0, 0, 0]"));
            inputs.Add(ScadPort.In("child", "Child", ScadPortType.Solid));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"translate({Read("offset", c)}) {{ {ReadSolid("child", c)}; }}";
    }

    [ScadNode("Rotate", "Transform")]
    [Serializable]
    public class RotateNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("angles", "Euler (deg)", ScadPortType.Vector3, "[0, 0, 0]"));
            inputs.Add(ScadPort.In("child", "Child", ScadPortType.Solid));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"rotate({Read("angles", c)}) {{ {ReadSolid("child", c)}; }}";
    }

    [ScadNode("Scale", "Transform")]
    [Serializable]
    public class ScaleNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("factor", "Factor", ScadPortType.Vector3, "[1, 1, 1]"));
            inputs.Add(ScadPort.In("child", "Child", ScadPortType.Solid));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"scale({Read("factor", c)}) {{ {ReadSolid("child", c)}; }}";
    }

    [ScadNode("Mirror", "Transform")]
    [Serializable]
    public class MirrorNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("normal", "Normal", ScadPortType.Vector3, "[1, 0, 0]"));
            inputs.Add(ScadPort.In("child", "Child", ScadPortType.Solid));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"mirror({Read("normal", c)}) {{ {ReadSolid("child", c)}; }}";
    }

    [ScadNode("Color", "Transform")]
    [Serializable]
    public class ColorNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("color", "Color", ScadPortType.Color, "[1, 1, 1, 1]"));
            inputs.Add(ScadPort.In("child", "Child", ScadPortType.Solid));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"color({Read("color", c)}) {{ {ReadSolid("child", c)}; }}";
    }

    [ScadNode("Offset", "Transform",
        Tooltip = "2D offset (inflate/deflate a Shape).")]
    [Serializable]
    public class OffsetNode : ScadNode
    {
        public bool useDelta;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("amount", "Amount", ScadPortType.Number, "1"));
            inputs.Add(ScadPort.In("child", "Child", ScadPortType.Shape));
            outputs.Add(ScadPort.Out("out", "Shape", ScadPortType.Shape));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var kw = useDelta ? "delta" : "r";
            return $"offset({kw}={Read("amount", c)}) {{ {ReadSolid("child", c)}; }}";
        }
    }

    [ScadNode("Resize", "Transform")]
    [Serializable]
    public class ResizeNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("newsize", "New Size", ScadPortType.Vector3, "[10, 10, 10]"));
            inputs.Add(ScadPort.In("child", "Child", ScadPortType.Solid));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"resize({Read("newsize", c)}) {{ {ReadSolid("child", c)}; }}";
    }
}
