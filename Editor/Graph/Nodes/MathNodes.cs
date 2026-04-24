using System;

namespace SCADPlugin.Editor.Graph.Nodes
{
    public enum ScadBinaryOp { Add, Subtract, Multiply, Divide, Modulo, Power }
    public enum ScadUnaryFn  { Neg, Abs, Sqrt, Floor, Ceil, Round, Sin, Cos, Tan, Asin, Acos, Atan, Log, Exp }

    [ScadNode("Binary Op", "Math")]
    [Serializable]
    public class MathBinaryNode : ScadNode
    {
        public ScadBinaryOp op = ScadBinaryOp.Add;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("a", "A", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("b", "B", ScadPortType.Number, "0"));
            outputs.Add(ScadPort.Out("out", "Result", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var a = Read("a", c);
            var b = Read("b", c);
            return op switch
            {
                ScadBinaryOp.Add      => $"({a} + {b})",
                ScadBinaryOp.Subtract => $"({a} - {b})",
                ScadBinaryOp.Multiply => $"({a} * {b})",
                ScadBinaryOp.Divide   => $"({a} / {b})",
                ScadBinaryOp.Modulo   => $"({a} % {b})",
                ScadBinaryOp.Power    => $"pow({a}, {b})",
                _                     => $"({a} + {b})",
            };
        }
    }

    [ScadNode("Unary Fn", "Math")]
    [Serializable]
    public class MathUnaryNode : ScadNode
    {
        public ScadUnaryFn fn = ScadUnaryFn.Abs;

        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("x", "X", ScadPortType.Number, "0"));
            outputs.Add(ScadPort.Out("out", "Result", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var x = Read("x", c);
            return fn switch
            {
                ScadUnaryFn.Neg   => $"(-{x})",
                ScadUnaryFn.Abs   => $"abs({x})",
                ScadUnaryFn.Sqrt  => $"sqrt({x})",
                ScadUnaryFn.Floor => $"floor({x})",
                ScadUnaryFn.Ceil  => $"ceil({x})",
                ScadUnaryFn.Round => $"round({x})",
                ScadUnaryFn.Sin   => $"sin({x})",
                ScadUnaryFn.Cos   => $"cos({x})",
                ScadUnaryFn.Tan   => $"tan({x})",
                ScadUnaryFn.Asin  => $"asin({x})",
                ScadUnaryFn.Acos  => $"acos({x})",
                ScadUnaryFn.Atan  => $"atan({x})",
                ScadUnaryFn.Log   => $"ln({x})",
                ScadUnaryFn.Exp   => $"exp({x})",
                _                 => x,
            };
        }
    }

    [ScadNode("Min", "Math")]
    [Serializable]
    public class MinNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("a", "A", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("b", "B", ScadPortType.Number, "0"));
            outputs.Add(ScadPort.Out("out", "Result", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"min({Read("a", c)}, {Read("b", c)})";
    }

    [ScadNode("Max", "Math")]
    [Serializable]
    public class MaxNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("a", "A", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("b", "B", ScadPortType.Number, "0"));
            outputs.Add(ScadPort.Out("out", "Result", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            $"max({Read("a", c)}, {Read("b", c)})";
    }

    [ScadNode("Clamp", "Math")]
    [Serializable]
    public class ClampNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("x", "X", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("lo", "Min", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("hi", "Max", ScadPortType.Number, "1"));
            outputs.Add(ScadPort.Out("out", "Result", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var x = Read("x", c);
            var lo = Read("lo", c);
            var hi = Read("hi", c);
            return $"min({hi}, max({lo}, {x}))";
        }
    }

    [ScadNode("Lerp", "Math",
        Tooltip = "Linear blend: a + (b-a) * t")]
    [Serializable]
    public class LerpNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("a", "A", ScadPortType.Number, "0"));
            inputs.Add(ScadPort.In("b", "B", ScadPortType.Number, "1"));
            inputs.Add(ScadPort.In("t", "T", ScadPortType.Number, "0.5"));
            outputs.Add(ScadPort.Out("out", "Result", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var a = Read("a", c);
            var b = Read("b", c);
            var t = Read("t", c);
            return $"({a} + ({b} - {a}) * {t})";
        }
    }
}
