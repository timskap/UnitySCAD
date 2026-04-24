using System;
using System.Collections.Generic;

namespace SCADPlugin.Editor
{
    public enum ScadParameterType
    {
        Number,
        Integer,
        Boolean,
        String,
        NumberDropdown,
        StringDropdown,
        ColorVector,
    }

    [Serializable]
    public class ScadChoice
    {
        public string label;
        public string value;
    }

    [Serializable]
    public class ScadParameter
    {
        public string name;
        public string description;
        public string group;
        public ScadParameterType type;

        public string value;
        public string defaultValue;

        public bool hasRange;
        public double min;
        public double max;
        public double step;

        public List<ScadChoice> choices = new List<ScadChoice>();
    }
}
