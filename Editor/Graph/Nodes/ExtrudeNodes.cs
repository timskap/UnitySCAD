using System;

namespace SCADPlugin.Editor.Graph.Nodes
{
    [ScadNode("Linear Extrude", "Extrusion")]
    [Serializable]
    public class LinearExtrudeNode : ScadNode
    {
        public bool center;
        public int slices = 1;
        public int fn;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("height", "Height", ScadPortType.Number, "10"));
            inputs.Add(ScadPort.In("twist", "Twist (deg)", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("scale", "Scale", ScadPortType.Number, "1"));
            inputs.Add(ScadPort.In("child", "Shape", ScadPortType.Shape));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var fnPart = fn > 0 ? $", $fn={fn}" : string.Empty;
            return $"linear_extrude(height={Read("height", c)}, twist={Read("twist", c)}, scale={Read("scale", c)}, center={ScadLiteral.Bool(center)}, slices={slices}{fnPart}) {{ {ReadSolid("child", c)}; }}";
        }
    }

    [ScadNode("Rotate Extrude", "Extrusion")]
    [Serializable]
    public class RotateExtrudeNode : ScadNode
    {
        public int fn;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("angle", "Angle (deg)", ScadPortType.Number, "360"));
            inputs.Add(ScadPort.In("child", "Shape", ScadPortType.Shape));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var fnPart = fn > 0 ? $", $fn={fn}" : string.Empty;
            return $"rotate_extrude(angle={Read("angle", c)}{fnPart}) {{ {ReadSolid("child", c)}; }}";
        }
    }

    [ScadNode("Projection", "Extrusion",
        Tooltip = "Project a 3D solid onto the XY plane as a 2D Shape.")]
    [Serializable]
    public class ProjectionNode : ScadNode
    {
        public bool cut;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("child", "Solid", ScadPortType.Solid));
            outputs.Add(ScadPort.Out("out", "Shape", ScadPortType.Shape));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"projection(cut={ScadLiteral.Bool(cut)}) {{ {ReadSolid("child", c)}; }}";
    }
}
