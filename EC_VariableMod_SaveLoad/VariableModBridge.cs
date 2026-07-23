using BepInEx.Logging;
using HarmonyLib;
using HPlay;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EC_VariableMod_SaveLoad
{
    internal sealed class VariableModBridge
    {
        private static readonly string VariableModTypeName = "EC_VariableMod.EC_VariableModClass";
        private static readonly string HooksTypeName = "EC_VariableMod.EC_VariableModClass+Hooks";

        private readonly ManualLogSource _log;
        private Type _vmType;
        private Type _hooksType;

        private FieldInfo _varDataField;
        private FieldInfo _varArrayField;

        private FieldInfo _nextFlagField;
        private FieldInfo _nextPartUidField;
        private FieldInfo _nextCutIndexField;

        public VariableModBridge(ManualLogSource log)
        {
            _log = log;
        }

        public bool TryInit()
        {
            if (_vmType != null)
                return true;

            _vmType = AccessTools.TypeByName(VariableModTypeName);
            _hooksType = AccessTools.TypeByName(HooksTypeName);
            if (_vmType == null || _hooksType == null)
            {
                _log.LogWarning($"无法找到 EC_VariableMod 类型: {_vmType} / Hooks: {_hooksType}. 是否未安装或 GUID 不匹配？");
                return false;
            }

            // public static GlobalVariablesClass _varData; (field name: _varData)
            var varDataStaticField = _vmType.GetField("_varData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (varDataStaticField == null)
            {
                _log.LogWarning("无法找到 EC_VariableModClass._varData 静态字段。");
                return false;
            }

            object gvInstance = varDataStaticField.GetValue(null);
            if (gvInstance == null)
            {
                _log.LogWarning("EC_VariableModClass._varData 为 null（可能插件尚未 Start）。");
                return false;
            }

            Type gvType = gvInstance.GetType(); // EC_VariableMod.GlobalVariablesClass
            _varDataField = gvType.GetField("_varData", BindingFlags.Public | BindingFlags.Instance);
            _varArrayField = gvType.GetField("_varArray", BindingFlags.Public | BindingFlags.Instance);
            if (_varDataField == null || _varArrayField == null)
            {
                _log.LogWarning("无法找到 GlobalVariablesClass 的 _varData/_varArray 字段。");
                return false;
            }

            _nextFlagField = _hooksType.GetField("_nextFlag", BindingFlags.NonPublic | BindingFlags.Static);
            _nextPartUidField = _hooksType.GetField("_nextPartUID", BindingFlags.NonPublic | BindingFlags.Static);
            _nextCutIndexField = _hooksType.GetField("_nextCutIndex", BindingFlags.NonPublic | BindingFlags.Static);

            if (_nextFlagField == null || _nextPartUidField == null || _nextCutIndexField == null)
            {
                _log.LogWarning("无法找到 EC_VariableMod Hooks 的跳转字段（_nextFlag/_nextPartUID/_nextCutIndex）。");
                return false;
            }

            return true;
        }

        private object GetGlobalVariablesInstance()
        {
            var varDataStaticField = _vmType.GetField("_varData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return varDataStaticField?.GetValue(null);
        }

        public bool TrySnapshot(out List<GlobalVarEntry> globals, out List<ArrayVarEntry> arrays)
        {
            globals = null;
            arrays = null;

            if (!TryInit())
                return false;

            object gvInstance = GetGlobalVariablesInstance();
            if (gvInstance == null)
            {
                _log.LogWarning("无法读取 EC_VariableMod 全局变量实例。");
                return false;
            }

            var htVars = _varDataField.GetValue(gvInstance) as Hashtable;
            var htArrays = _varArrayField.GetValue(gvInstance) as Hashtable;
            if (htVars == null || htArrays == null)
            {
                _log.LogWarning("EC_VariableMod 的全局变量表为空或类型不匹配。");
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
            if (!TryInit())
                return false;

            object gvInstance = GetGlobalVariablesInstance();
            if (gvInstance == null)
                return false;

            var htVars = _varDataField.GetValue(gvInstance) as Hashtable;
            var htArrays = _varArrayField.GetValue(gvInstance) as Hashtable;
            if (htVars == null || htArrays == null)
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

                    // Build List<List<T>> based on elemType
                    if (a.elemType == VarType.String)
                    {
                        var list = new List<List<string>>();
                        foreach (var row in a.values)
                        {
                            list.Add(row?.cells ?? new List<string>());
                        }
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
                            {
                                row.Add(long.TryParse(cells[j], out var v) ? v : 0L);
                            }
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
                            {
                                row.Add(double.TryParse(cells[j], out var v) ? v : 0.0);
                            }
                            list.Add(row);
                        }
                        htArrays[a.name] = list;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 向 VariableMod 的全局变量表直接写入一个字符串变量。
        /// 变量名建议包含 # 定界符，如 "#SAVE_save1_DATE#"，
        /// 这样 VariableMod 的 ReplaceVariableNmae 机制会自动替换。
        /// </summary>
        public bool TrySetGlobalVar(string name, string value)
        {
            if (!TryInit())
                return false;

            object gvInstance = GetGlobalVariablesInstance();
            if (gvInstance == null)
                return false;

            var htVars = _varDataField.GetValue(gvInstance) as Hashtable;
            if (htVars == null)
            {
                _log.LogWarning("无法获取 VariableMod 的 _varData Hashtable，无法写入变量。");
                return false;
            }

            htVars[name] = value ?? "";
            _log.LogDebug($"[VariableModBridge] 已写入全局变量: {name} = {value}");
            return true;
        }

        public bool TrySetJump(string partUid, int cutIndex)
        {
            if (!TryInit())
                return false;

            _log.LogDebug($"[VariableModBridge] 设置跳转: PartUID={partUid}, CutIndex={cutIndex}");

            _nextPartUidField.SetValue(null, partUid ?? "");
            _nextCutIndexField.SetValue(null, cutIndex);
            _nextFlagField.SetValue(null, true);

            if (!string.IsNullOrEmpty(partUid) && Singleton<HPlayData>.Instance != null)
            {
                Singleton<HPlayData>.Instance.isNextPart = true;
                Singleton<HPlayData>.Instance.nextPart = 0;
                _log.LogDebug("[VariableModBridge] 已同步设置 HPlayData.isNextPart=true, nextPart=0");
            }

            // 验证设置是否成功
            var verifyPartUid = _nextPartUidField.GetValue(null) as string;
            var verifyCutIndex = (int)_nextCutIndexField.GetValue(null);
            var verifyFlag = (bool)_nextFlagField.GetValue(null);

            _log.LogDebug($"[VariableModBridge] 跳转标志验证: _nextPartUID={verifyPartUid}, _nextCutIndex={verifyCutIndex}, _nextFlag={verifyFlag}");

            return true;
        }
    }
}
