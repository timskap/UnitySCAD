using System;

namespace SCADPlugin.Editor.Graph.Nodes
{
    // ---- 3D primitives ----

    [ScadNode("Cube", "Primitive/3D")]
    [Serializable]
    public class CubeNode : ScadNode
    {
        public bool center;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("size", "Size", ScadPortType.Vector3, "[10, 10, 10]"));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"cube({Read("size", c)}, center={ScadLiteral.Bool(center)})";
    }

    [ScadNode("Sphere", "Primitive/3D")]
    [Serializable]
    public class SphereNode : ScadNode
    {
        public int fn;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("radius", "Radius", ScadPortType.Number, "5"));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            fn > 0
                ? $"sphere(r={Read("radius", c)}, $fn={fn})"
                : $"sphere(r={Read("radius", c)})";
    }

    [ScadNode("Cylinder", "Primitive/3D")]
    [Serializable]
    public class CylinderNode : ScadNode
    {
        public bool center;
        public int fn;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("height", "Height", ScadPortType.Number, "10"));
            inputs.Add(ScadPort.In("radius1", "Radius Bottom", ScadPortType.Number, "5"));
            inputs.Add(ScadPort.In("radius2", "Radius Top", ScadPortType.Number, "5"));
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var h = Read("height", c);
            var r1 = Read("radius1", c);
            var r2 = Read("radius2", c);
            return fn > 0
                ? $"cylinder(h={h}, r1={r1}, r2={r2}, center={ScadLiteral.Bool(center)}, $fn={fn})"
                : $"cylinder(h={h}, r1={r1}, r2={r2}, center={ScadLiteral.Bool(center)})";
        }
    }

    [ScadNode("Polyhedron", "Primitive/3D",
        Tooltip = "Raw points + faces. Expects SCAD-format vector-of-vectors literals.")]
    [Serializable]
    public class PolyhedronNode : ScadNode
    {
        public string pointsLiteral = "[[0,0,0],[10,0,0],[0,10,0],[0,0,10]]";
        public string facesLiteral = "[[0,1,2],[0,1,3],[0,2,3],[1,2,3]]";
        public int convexity = 1;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"polyhedron(points={pointsLiteral}, faces={facesLiteral}, convexity={convexity})";
    }

    // ---- 2D primitives ----

    [ScadNode("Square", "Primitive/2D")]
    [Serializable]
    public class SquareNode : ScadNode
    {
        public bool center;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("size", "Size", ScadPortType.Vector2, "[10, 10]"));
            outputs.Add(ScadPort.Out("out", "Shape", ScadPortType.Shape));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"square({Read("size", c)}, center={ScadLiteral.Bool(center)})";
    }

    [ScadNode("Circle", "Primitive/2D")]
    [Serializable]
    public class CircleNode : ScadNode
    {
        public int fn;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("radius", "Radius", ScadPortType.Number, "5"));
            outputs.Add(ScadPort.Out("out", "Shape", ScadPortType.Shape));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            fn > 0
                ? $"circle(r={Read("radius", c)}, $fn={fn})"
                : $"circle(r={Read("radius", c)})";
    }

    [ScadNode("Polygon", "Primitive/2D",
        Tooltip = "Raw 2D points polygon. SCAD-format list literal.")]
    [Serializable]
    public class PolygonNode : ScadNode
    {
        public string pointsLiteral = "[[0,0],[10,0],[10,10],[0,10]]";

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Shape", ScadPortType.Shape));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"polygon({pointsLiteral})";
    }

    [ScadNode("Text", "Primitive/2D")]
    [Serializable]
    public class TextNode : ScadNode
    {
        public string font = "Liberation Sans";
        public string halign = "left";
        public string valign = "baseline";
        public int fn;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("text", "Text", ScadPortType.String, "\"Hello\""));
            inputs.Add(ScadPort.In("size", "Size", ScadPortType.Number, "10"));
            outputs.Add(ScadPort.Out("out", "Shape", ScadPortType.Shape));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var text = Read("text", c);
            var size = Read("size", c);
            var fnPart = fn > 0 ? $", $fn={fn}" : string.Empty;
            return $"text({text}, size={size}, font=\"{font}\", halign=\"{halign}\", valign=\"{valign}\"{fnPart})";
        }
    }
}
