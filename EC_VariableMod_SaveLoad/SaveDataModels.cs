using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace EC_VariableMod_SaveLoad
{
    [Serializable]
    [XmlRoot("SaveFile")]
    public sealed class SaveFile
    {
        [XmlAttribute]
        public int version = 1;

        [XmlElement]
        public string timestampUtc;

        [XmlElement]
        public AdvInfo adv = new AdvInfo();

        [XmlArray("Globals")]
        [XmlArrayItem("Variable")]
        public List<GlobalVarEntry> globals = new List<GlobalVarEntry>();

        [XmlArray("Arrays")]
        [XmlArrayItem("Array")]
        public List<ArrayVarEntry> arrays = new List<ArrayVarEntry>();
    }

    [Serializable]
    public sealed class AdvInfo
    {
        [XmlElement]
        public string sceneTitle;

        [XmlElement]
        public string saveFolder;

        [XmlElement]
        public string partUID;

        [XmlElement]
        public string partName;

        [XmlElement]
        public int cutIndex;
    }

    [Serializable]
    public enum VarType
    {
        String,
        Integer,
        Double
    }

    [Serializable]
    public sealed class GlobalVarEntry
    {
        [XmlAttribute]
        public string name;

        [XmlAttribute]
        public VarType type;

        [XmlElement]
        public string value;
    }

    [Serializable]
    public sealed class ArrayVarEntry
    {
        [XmlAttribute]
        public string name;

        [XmlAttribute]
        public VarType elemType;

        [XmlAttribute]
        public int dim1;

        [XmlAttribute]
        public int dim2;

        [XmlArray("Values")]
        [XmlArrayItem("Row")]
        public List<ArrayRow> values = new List<ArrayRow>();
    }

    [Serializable]
    public sealed class ArrayRow
    {
        [XmlElement("Cell")]
        public List<string> cells = new List<string>();

        public ArrayRow() { }

        public ArrayRow(List<string> row)
        {
            cells = row ?? new List<string>();
        }
    }
}
