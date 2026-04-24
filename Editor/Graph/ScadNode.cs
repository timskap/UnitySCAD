using System;
using System.Collections.Generic;
using UnityEngine;

namespace SCADPlugin.Editor.Graph
{
    [Serializable]
    public abstract class ScadNode
    {
        public string id = Guid.NewGuid().ToString("N");
        public Vector2 position;

        // Ports are serialized as part of the node so connections keep
        // working even if a node type later changes its port set — old
        // ports are retained until a connection is rewired.
        [SerializeField] public List<ScadPort> inputs = new List<ScadPort>();
        [SerializeField] public List<ScadPort> outputs = new List<ScadPort>();

        protected ScadNode()
        {
            DefinePorts();
        }

        // Node authors add their ports here. Called from ctor so newly
        // instantiated nodes always have a valid port layout. Deserialised
        // nodes have their ports replaced from the asset — fields set in
        // here are overwritten by Unity's serialization, which is fine.
        protected abstract void DefinePorts();

        public ScadPort InputById(string portId) => Find(inputs, portId);
        public ScadPort OutputById(string portId) => Find(outputs, portId);

        static ScadPort Find(List<ScadPort> list, string id)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].id == id) return list[i];
            return null;
        }

        // Return the SCAD expression that represents the value flowing out
        // of `outputPortId`. Value nodes emit expressions (`"10"`,
        // `"[0,0,0]"`, `"(a + b)"`); geometry nodes emit statements
        // (`"cube([10,10,10])"`). The compiler resolves inputs via
        // ScadGraphCompiler.ReadInput and passes them here as strings.
        public abstract string Emit(string outputPortId, ScadGraphCompiler compiler);

        // Convenience wrappers used by concrete node implementations.
        protected string Read(string inputPortId, ScadGraphCompiler c) =>
            c.ReadInput(this, inputPortId);

        protected string ReadSolid(string inputPortId, ScadGraphCompiler c)
        {
            // Geometry inputs default to an empty union when unconnected so
            // generated SCAD never dangles with a missing child.
            var conn = c.FindIncoming(id, inputPortId);
            return conn != null ? c.ReadInput(this, inputPortId) : "union(){}";
        }
    }
}
