using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EC_BackVarSync
{
    /// <summary>
    /// EC_VariableMod 反射桥：快照/恢复全局变量与二维数组。
    /// 挂接目标均为公开静态或实例字段，无 DLL 引用（沿用已实测的反射路径）。
    /// </summary>
    internal sealed class VariableModBridge
    {
        private static readonly string VariableModTypeName = "EC_VariableMod.EC_VariableModClass";

        private readonly ManualLogSource _log;
        private Type _vmType;

        private FieldInfo _varDataStaticField;
        private FieldInfo _varDataField;
        private FieldInfo _varArrayField;

        public VariableModBridge(ManualLogSource log)
        {
            _log = log;
        }

        public bool TryInit()
        {
            if (_vmType != null)
                return true;

            _vmType = AccessTools.TypeByName(VariableModTypeName);
            if (_vmType == null)
            {
                _log.LogWarning($"无法找到 EC_VariableMod 类型: 是否未安装或 GUID 不匹配？");
                return false;
            }

            // public static GlobalVariablesClass _varData;
            _varDataStaticField = _vmType.GetField("_varData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (_varDataStaticField == null)
            {
                _log.LogWarning("无法找到 EC_VariableModClass._varData 静态字段。");
                return false;
            }

            return true;
        }

        private bool TryGetTables(out Hashtable htVars, out Hashtable htArrays)
        {
            htVars = null;
            htArrays = null;

            if (!TryInit())
                return false;

            object gvInstance = _varDataStaticField.GetValue(null);
            if (gvInstance == null)
            {
                _log.LogWarning("EC_VariableModClass._varData 为 null（可能插件尚未 Start）。");
                return false;
            }

            // GlobalVariablesClass 的 _varData / _varArray 字段每场景可能换实例，缓存实例字段句柄
            if (_varDataField == null || _varArrayField == null)
            {
                Type gvType = gvInstance.GetType(); // EC_VariableMod.GlobalVariablesClass
                _varDataField = gvType.GetField("_varData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _varArrayField = gvType.GetField("_varArray", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_varDataField == null || _varArrayField == null)
                {
                    _log.LogWarning("无法找到 GlobalVariablesClass 的 _varData/_varArray 字段。");
                    return false;
                }
            }

            htVars = _varDataField.GetValue(gvInstance) as Hashtable;
            htArrays = _varArrayField.GetValue(gvInstance) as Hashtable;
            return htVars != null && htArrays != null;
        }

        public bool TrySnapshot(out List<GlobalVarEntry> globals, out List<ArrayVarEntry> arrays)
        {
            globals = null;
            arrays = null;

            if (!TryGetTables(out var htVars, out var htArrays))
            {
                _log.LogWarning("无法读取 EC_VariableMod 全局变量表。");
                return false;
            }

            globals = new List<GlobalVarEntry>(htVars.Count);
            foreach (DictionaryEntry kv in htVars)
            {
                string name = kv.Key as string;
                object val = kv.Value;
                if (string.IsNullOrEmpty(name) || val == null)
                    continue;

                if (val is string s)
                    globals.Add(new GlobalVarEntry { name = name, type = VarType.String, value = s });
                else if (val is long l)
                    globals.Add(new GlobalVarEntry { name = name, type = VarType.Integer, value = l.ToString() });
                else if (val is double d)
                    globals.Add(new GlobalVarEntry { name = name, type = VarType.Double, value = d.ToString("R") });
                else
                    _log.LogWarning($"跳过不支持的全局变量类型: {name} = {val.GetType()}");
            }

            arrays = new List<ArrayVarEntry>(htArrays.Count);
            foreach (DictionaryEntry kv in htArrays)
            {
                string name = kv.Key as string;
                object arr = kv.Value;
                if (string.IsNullOrEmpty(name) || arr == null)
                    continue;

                if (arr is List<List<string>> s2)
                    arrays.Add(ToArrayEntry(name, VarType.String, s2));
                else if (arr is List<List<long>> l2)
                {
                    var stringData = l2.Select(row => row.Select(x => x.ToString()).ToList()).ToList();
                    arrays.Add(ToArrayEntry(name, VarType.Integer, stringData));
                }
                else if (arr is List<List<double>> d2)
                {
                    var stringData = d2.Select(row => row.Select(x => x.ToString("R")).ToList()).ToList();
                    arrays.Add(ToArrayEntry(name, VarType.Double, stringData));
                }
                else
                    _log.LogWarning($"跳过不支持的数组类型: {name} = {arr.GetType()}");
            }

            return true;
        }

        private static ArrayVarEntry ToArrayEntry(string name, VarType elemType, List<List<string>> values)
        {
            int dim1 = values.Count;
            int dim2 = dim1 > 0 ? values[0].Count : 0;

            var arrayEntry = new ArrayVarEntry
            {
                name = name,
                elemType = elemType,
                dim1 = dim1,
                dim2 = dim2
            };

            foreach (var row in values)
                arrayEntry.values.Add(new ArrayRow(row));

            return arrayEntry;
        }

        public bool TryRestore(List<GlobalVarEntry> globals, List<ArrayVarEntry> arrays)
        {
            if (!TryGetTables(out var htVars, out var htArrays))
                return false;

            htVars.Clear();
            htArrays.Clear();

            if (globals != null)
            {
                foreach (var e in globals)
                {
                    if (e == null || string.IsNullOrEmpty(e.name))
                        continue;

                    switch (e.type)
                    {
                        case VarType.String:
                            htVars[e.name] = e.value ?? "";
                            break;
                        case VarType.Integer:
                            if (long.TryParse(e.value, out var l)) htVars[e.name] = l;
                            break;
                        case VarType.Double:
                            if (double.TryParse(e.value, out var d)) htVars[e.name] = d;
                            break;
                    }
                }
            }

            if (arrays != null)
            {
                foreach (var a in arrays)
                {
                    if (a == null || string.IsNullOrEmpty(a.name))
                        continue;

                    if (a.elemType == VarType.String)
                    {
                        var list = new List<List<string>>();
                        foreach (var row in a.values)
                            list.Add(row?.cells ?? new List<string>());
                        htArrays[a.name] = list;
                    }
                    else if (a.elemType == VarType.Integer)
                    {
                        var list = new List<List<long>>(a.dim1);
                        for (int i = 0; i < a.values.Count; i++)
                        {
                            var row = new List<long>(a.dim2);
                            var cells = a.values[i]?.cells ?? new List<string>();
                            for (int j = 0; j < cells.Count; j++)
                                row.Add(long.TryParse(cells[j], out var v) ? v : 0L);
                            list.Add(row);
                        }
                        htArrays[a.name] = list;
                    }
                    else if (a.elemType == VarType.Double)
                    {
                        var list = new List<List<double>>(a.dim1);
                        for (int i = 0; i < a.values.Count; i++)
                        {
                            var row = new List<double>(a.dim2);
                            var cells = a.values[i]?.cells ?? new List<string>();
                            for (int j = 0; j < cells.Count; j++)
                                row.Add(double.TryParse(cells[j], out var v) ? v : 0.0);
                            list.Add(row);
                        }
                        htArrays[a.name] = list;
                    }
                }
            }

            return true;
        }
    }
}
