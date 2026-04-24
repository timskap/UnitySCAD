using System;
using UnityEngine;

namespace SCADPlugin.Editor.Graph.Nodes
{
    [ScadNode("Output", "IO", Hidden = true)]
    [Serializable]
    public class OutputNode : ScadNode
    {
        protected override void DefinePorts()
        {
            inputs.Add(ScadPort.In("geometry", "Geometry", ScadPortType.Solid, "cube([10, 10, 10])"));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            Read("geometry", c);
    }

    [ScadNode("Parameter", "IO")]
    [Serializable]
    public class ParameterNode : ScadNode
    {
        public string parameterName = "value";
        public ScadPortType parameterType = ScadPortType.Number;
        public string defaultLiteral = "0";

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Value", ScadPortType.Any));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            // Emit a reference to a top-level variable; the graph's
            // exposed-parameters block is responsible for defining it.
            return ScadGraphCompiler.SanitizeIdentifier(parameterName);
        }
    }

    [ScadNode("Number", "IO/Literals")]
    [Serializable]
    public class NumberLiteralNode : ScadNode
    {
        public float value;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Value", ScadPortType.Number));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            ScadLiteral.Number(value);
    }

    [ScadNode("Vector3", "IO/Literals")]
    [Serializable]
    public class Vector3LiteralNode : ScadNode
    {
        public Vector3 value;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Value", ScadPortType.Vector3));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            ScadLiteral.Vector3(value);
    }

    [ScadNode("Vector2", "IO/Literals")]
    [Serializable]
    public class Vector2LiteralNode : ScadNode
    {
        public Vector2 value;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Value", ScadPortType.Vector2));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            ScadLiteral.Vector2(value);
    }

    [ScadNode("Boolean", "IO/Literals")]
    [Serializable]
    public class BooleanLiteralNode : ScadNode
    {
        public bool value;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Value", ScadPortType.Boolean));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            ScadLiteral.Bool(value);
    }

    [ScadNode("Color", "IO/Literals")]
    [Serializable]
    public class ColorLiteralNode : ScadNode
    {
        public Color value = Color.white;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Color", ScadPortType.Color));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            ScadLiteral.Color(value);
    }

    [ScadNode("String", "IO/Literals")]
    [Serializable]
    public class StringLiteralNode : ScadNode
    {
        public string value = string.Empty;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Value", ScadPortType.String));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            ScadLiteral.String(value);
    }

    [ScadNode("Custom Expression", "IO/Advanced",
        Tooltip = "Emits a raw SCAD expression verbatim. Use sparingly.")]
    [Serializable]
    public class CustomExpressionNode : ScadNode
    {
        public string expression = "0";
        public ScadPortType outputType = ScadPortType.Any;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Value", ScadPortType.Any));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            string.IsNullOrWhiteSpace(expression) ? "undef" : "(" + expression + ")";
    }

    [ScadNode("Custom Statement", "IO/Advanced",
        Tooltip = "Emits a raw SCAD statement verbatim. Use sparingly.")]
    [Serializable]
    public class CustomStatementNode : ScadNode
    {
        public string statement = "cube([1, 1, 1])";

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c) =>
            string.IsNullOrWhiteSpace(statement) ? "union(){}" : statement;
    }

    // Created by the SCAD source importer when it sees a call to a
    // user-defined module (one whose `module foo(...) { ... }` body lives
    // in the graph's preamble). Hidden from the search menu because it's
    // not useful to create one by hand.
    [ScadNode("User Module Call", "IO/Advanced", Hidden = true,
        Tooltip = "Calls a user-defined module defined in the graph preamble.")]
    [Serializable]
    public class UserModuleCallNode : ScadNode
    {
        public string moduleName = "unknown";
        public string rawArguments = string.Empty;

        protected override void DefinePorts()
        {
            outputs.Add(ScadPort.Out("out", "Solid", ScadPortType.Solid));
        }

        public override string Emit(string outputPortId, ScadGraphCompiler c)
        {
            var name = string.IsNullOrWhiteSpace(moduleName) ? "union" : moduleName;
            return $"{name}({rawArguments})";
        }
    }
}
