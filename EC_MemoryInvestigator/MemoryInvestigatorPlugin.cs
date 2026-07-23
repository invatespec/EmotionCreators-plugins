using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.UI;
using ADV;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HEdit;
using UnityEngine;
using YS_Node;
using HEditADVPart = HEdit.ADVPart;

namespace EC_MemoryInvestigator
{
    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class MemoryInvestigatorPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_MemoryInvestigator";
        public const string PluginName = "EC Memory Investigator";
        public const string Version = "0.7.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<int> CutLogInterval;
        internal static ConfigEntry<bool> IncludeUnityObjectCounts;
        internal static ConfigEntry<bool> ClassifyMaterials;
        internal static ConfigEntry<KeyboardShortcut> ManualUnloadKey;
        internal static ConfigEntry<int> AutoUnloadEveryNCuts;
        internal static MemoryInvestigatorPlugin Instance;
        private bool _manualUnloadRunning;
        // 缓存"真正卸载"的调用器——绕过 ResourceUnloadOptimizations 对公开 UnloadUnusedAssets 的 NativeDetour。
        private static Func<AsyncOperation> _unloadInvoker;
        private static bool _unloadInvokerResolved;

        internal void Awake()
        {
            Log = Logger;
            Instance = this;
            CutLogInterval = Config.Bind(
                "Logging",
                "CutLogInterval",
                25,
                new ConfigDescription(
                    "Log ADVPlay.LoadCut memory snapshot every N cuts.",
                    new AcceptableValueRange<int>(1, 1000)));
            IncludeUnityObjectCounts = Config.Bind(
                "Logging",
                "IncludeUnityObjectCounts",
                true,
                "Count selected UnityEngine.Object types in each logged snapshot.");
            ClassifyMaterials = Config.Bind(
                "Logging",
                "ClassifyMaterials",
                true,
                "Classify Material objects by source (Character / ADV UI / Other).");
            ManualUnloadKey = Config.Bind(
                "Diagnostics",
                "ManualUnloadKey",
                new KeyboardShortcut(KeyCode.F10),
                "按键手动跑一次 Resources.UnloadUnusedAssets()+GC，打印前后内存/对象数 delta。用于判断 native 增长中多少可被卸载回收，以及 playback 期卸载是否会闪退。");
            AutoUnloadEveryNCuts = Config.Bind(
                "Diagnostics",
                "AutoUnloadEveryNCuts",
                0,
                new ConfigDescription(
                    "实验：playback 期每 N cut 自动卸载一次（绕过 RUO 的 DisableUnload，仅 cut 推进期、不碰 load 期）。0=关闭。用于观察周期性卸载能否压平大场景增长曲线。",
                    new AcceptableValueRange<int>(0, 1000)));

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Hooks.HEditDataHooks));
            harmony.PatchAll(typeof(Hooks.ADVPlayHooks));
            harmony.PatchAll(typeof(Hooks.MaterialSourceHooks));
            harmony.PatchAll(typeof(Hooks.GuideObjectCleanupHooks));
            harmony.PatchAll(typeof(Hooks.GuideObjectInitHooks));

            Log.LogInfo($"{PluginName} v{Version} started.");
        }

        internal void Update()
        {
            if (ManualUnloadKey.Value.IsDown())
                StartUnload("Manual");
        }

        // 供热键与 LoadCut 自动触发共用；_manualUnloadRunning 防重入。
        internal void StartUnload(string reason)
        {
            if (!_manualUnloadRunning)
                StartCoroutine(UnloadCo(reason));
        }

        // 卸载并测量。必须 yield 等 AsyncOperation 完成后再采样 after，
        // 否则贴图/网格尚未真正释放，delta 会偏小失真。
        private IEnumerator UnloadCo(string reason)
        {
            _manualUnloadRunning = true;
            string tag = "[Unload:" + reason + " @ cut #" + Hooks.ADVPlayHooks.CutCount + "]";
            MemoryPoint before = MemoryPoint.CaptureBasic();
            Log.LogInfo(tag + " before " + before.Format() + " " + UnityObjectCounter.Capture());

            AsyncOperation op = null;
            Func<AsyncOperation> invoker = ResolveUnloadInvoker();
            try
            {
                if (invoker != null) op = invoker();
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Unload] 调用卸载失败: " + ex);
            }

            if (op == null)
                Log.LogWarning("[Unload] 卸载未真正执行（op=null）——绕过失败，本次结果不可信。");
            else
                yield return op;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            MemoryPoint after = MemoryPoint.CaptureBasic();
            Log.LogInfo(tag + " after " + after.Format() + " " +
                        MemoryPoint.FormatDelta(before, after) + " " + UnityObjectCounter.Capture());
            _manualUnloadRunning = false;
        }

        // 解析"真正卸载"的调用器，绕过 RUO 对公开 UnloadUnusedAssets 的 detour。两条路：
        // 1) 借 RUO 保存的原始 trampoline（被 detour 前的原方法，最稳）；
        // 2) 直接调 Resources 私有 UnloadUnusedAssetsInternal（未被 detour），按参数个数补 true。
        private static Func<AsyncOperation> ResolveUnloadInvoker()
        {
            if (_unloadInvokerResolved) return _unloadInvoker;
            _unloadInvokerResolved = true;

            try
            {
                Type ruo = AccessTools.TypeByName("IllusionFixes.ResourceUnloadOptimizations");
                FieldInfo f = ruo?.GetField("_originalUnload", BindingFlags.Static | BindingFlags.NonPublic);
                Func<AsyncOperation> del = f?.GetValue(null) as Func<AsyncOperation>;
                if (del != null)
                {
                    Log.LogInfo("[ManualUnload] 绕过方式：RUO._originalUnload trampoline。");
                    _unloadInvoker = del;
                    return _unloadInvoker;
                }
            }
            catch (Exception ex) { Log.LogWarning("[ManualUnload] RUO trampoline 解析失败: " + ex.Message); }

            try
            {
                foreach (MethodInfo m in typeof(Resources).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (m.Name != "UnloadUnusedAssetsInternal" || m.ReturnType != typeof(AsyncOperation)) continue;
                    object[] args = m.GetParameters().Length == 0 ? null : new object[] { true };
                    Log.LogInfo("[ManualUnload] 绕过方式：Resources." + m.Name + "(" + m.GetParameters().Length + " 参数)。");
                    _unloadInvoker = () => m.Invoke(null, args) as AsyncOperation;
                    return _unloadInvoker;
                }
            }
            catch (Exception ex) { Log.LogWarning("[ManualUnload] 内部方法解析失败: " + ex.Message); }

            Log.LogWarning("[ManualUnload] 两种绕过都失败。" + DumpResourcesMethods());
            return null;
        }

        // 失败诊断：列出 Resources 上所有返回 AsyncOperation 的静态方法及签名。
        private static string DumpResourcesMethods()
        {
            try
            {
                StringBuilder sb = new StringBuilder("Resources[");
                foreach (MethodInfo m in typeof(Resources).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.ReturnType != typeof(AsyncOperation)) continue;
                    sb.Append(m.IsPublic ? "pub " : "priv ").Append(m.Name).Append('(');
                    ParameterInfo[] ps = m.GetParameters();
                    for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(','); sb.Append(ps[i].ParameterType.Name); }
                    sb.Append(") ");
                }
                return sb.Append(']').ToString();
            }
            catch (Exception ex) { return "dump失败:" + ex.Message; }
        }
    }

    namespace Hooks
    {
        internal static class HEditDataHooks
        {
            [HarmonyPostfix]
            [HarmonyPatch(typeof(HEditData), "Load", new Type[] { typeof(string), typeof(NodeControl), typeof(bool) })]
            internal static void LoadPostfix(HEditData __instance, bool __result)
            {
                if (!__result || __instance == null) return;

                try
                {
                    MemoryInvestigatorPlugin.Log.LogInfo(
                        "[HEditData.Load] " +
                        MemoryPoint.CaptureBasic().Format() + " " +
                        UnityObjectCounter.Capture() + " " +
                        SceneAnalyzer.BuildSceneSummary(__instance));
                }
                catch (Exception ex)
                {
                    MemoryInvestigatorPlugin.Log.LogWarning("[HEditData.Load] diagnostics failed: " + ex);
                }
            }
        }

        internal static class ADVPlayHooks
        {
            // 全程累计的 cut 计数：只在 LoadCut 递增，绝不在 SetPart / Map 切换 / 场景 Load 时重置，
            // 保证 #N 反映真实播放进度（旧版按 part 重置导致 #N 失真）。参考已废弃 EC_Fix_MemoryDiagnostics。
            internal static int CutCount;

            [HarmonyPrefix]
            [HarmonyPatch(typeof(ADVPlay), "LoadCut", new Type[] { typeof(HEditADVPart.Cut) })]
            internal static void LoadCutPrefix(out MemoryPoint __state)
            {
                __state = MemoryPoint.CaptureBasic();
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(ADVPlay), "LoadCut", new Type[] { typeof(HEditADVPart.Cut) })]
            internal static void LoadCutPostfix(HEditADVPart.Cut _cut, MemoryPoint __state)
            {
                int call = ++CutCount;

                // 实验：每 N cut 自动卸载一次（独立于日志间隔）。协程下一帧执行，避开本 cut 的同步加载。
                int autoN = MemoryInvestigatorPlugin.AutoUnloadEveryNCuts.Value;
                if (autoN > 0 && call % autoN == 0 && MemoryInvestigatorPlugin.Instance != null)
                    MemoryInvestigatorPlugin.Instance.StartUnload("Auto");

                int interval = MemoryInvestigatorPlugin.CutLogInterval.Value;
                if (call != 1 && call % interval != 0) return;

                try
                {
                    MemoryPoint after = MemoryPoint.CaptureBasic();
                    MemoryInvestigatorPlugin.Log.LogInfo(
                        "[ADVPlay.LoadCut #" + call + "] " +
                        after.Format() + " " +
                        MemoryPoint.FormatDelta(__state, after) + " " +
                        UnityObjectCounter.Capture() + " " +
                        SceneAnalyzer.BuildCutSummary(_cut));
                }
                catch (Exception ex)
                {
                    MemoryInvestigatorPlugin.Log.LogWarning("[ADVPlay.LoadCut] diagnostics failed: " + ex);
                }
            }
        }

        // Phase 2: Hook AddText/AddEffect — the known Material creation hotpaths.
        // `new Material(existing)` is a Unity native icall, Harmony can't patch it.
        // Instead, count the two game-code call sites that we know create Materials.
        internal static class MaterialSourceHooks
        {
            internal static int AddTextCalls;
            internal static int AddEffectCalls;
            internal static int AddTextMaterials;
            internal static int AddEffectMaterials;

            [HarmonyPostfix]
            [HarmonyPatch(typeof(global::ADV.ADV), "AddText", new Type[] { typeof(HEditADVPart.SpeechBubbles), typeof(bool), typeof(bool) })]
            internal static void AddTextPostfix(GameObject __result)
            {
                if (__result == null) return;
                AddTextCalls++;
                AddTextMaterials += CountMaterialsOn(__result);
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(global::ADV.ADV), "AddEffect", new Type[] { typeof(HEditADVPart.ScreenEffect), typeof(bool), typeof(bool) })]
            internal static void AddEffectPostfix(GameObject __result)
            {
                if (__result == null) return;
                AddEffectCalls++;
                AddEffectMaterials += CountMaterialsOn(__result);
            }

            internal static string FormatSummary()
            {
                if (AddTextCalls == 0 && AddEffectCalls == 0) return "matSrc[none]";
                return "matSrc[addText=" + AddTextCalls + "/" + AddTextMaterials +
                       " addEffect=" + AddEffectCalls + "/" + AddEffectMaterials + "]";
            }

            private static int CountMaterialsOn(GameObject go)
            {
                int count = 0;
                // UI path: Graphic (Image/Text) has .material
                var graphics = go.GetComponentsInChildren<Graphic>(true);
                for (int i = 0; i < graphics.Length; i++)
                    if (graphics[i].material != null) count++;
                // 3D path: Renderer
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    count += renderers[i].materials.Length; // .materials triggers instantiation but we're in postfix after it already happened
                return count;
            }
        }

        // ponytail: snapshot GuideObject Renderer material IDs right after Awake (which calls
        // GuideBase.Init() → .material → implicit clone). On DeleteAll, use snapshot to
        // Destroy every runtime clone before DestroyImmediate kills the Renderers.
        internal static class GuideObjectInitHooks
        {
            // Key = Renderer.GetInstanceID(), Value = Material instanceIDs currently on that Renderer.
            // After GuideBase.Init(), these are runtime clones (negative IDs).
            private static readonly Dictionary<int, int[]> _rendererMatIds
                = new Dictionary<int, int[]>();

            internal static bool TryGetSnapshot(int rendererId, out int[] matIds)
            {
                return _rendererMatIds.TryGetValue(rendererId, out matIds);
            }

            internal static void Clear()
            {
                _rendererMatIds.Clear();
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(global::Map.GuideObject), "Awake")]
            internal static void GuideObjectAwakePostfix(global::Map.GuideObject __instance)
            {
                if (__instance == null) return;
                try
                {
                    var go = __instance.gameObject;
                    if (go == null) return;
                    var renderers = go.GetComponentsInChildren<Renderer>(true);
                    if (renderers == null) return;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null) continue;
                        var mats = r.sharedMaterials;
                        var ids = new int[mats.Length];
                        for (int j = 0; j < mats.Length; j++)
                            ids[j] = mats[j] != null ? mats[j].GetInstanceID() : 0;
                        _rendererMatIds[r.GetInstanceID()] = ids;
                    }
                }
                catch (Exception ex)
                {
                    MemoryInvestigatorPlugin.Log.LogWarning("[GuideObject.Awake:postfix] snapshot failed: " + ex);
                }
            }
        }

        // ponytail: v0.5 — reclaim runtime-cloned Materials from GuideObject Renderers before
        // GuideObjectManager.Delete/DeleteAll calls DestroyImmediate. Uses sharedMaterials
        // (no implicit clone) + instanceID<0 check to identify clones, then Destroy them.
        internal static class GuideObjectCleanupHooks
        {
            internal static int MaterialsReclaimed;
            internal static int ObjectsCleaned;
            internal static readonly Dictionary<string, int> CloneShaderFreq = new Dictionary<string, int>();

            // Cached reflection for DeleteAll — dicGuideObject is private
            private static FieldInfo _dicGuideObjectField;

            [HarmonyPrefix]
            [HarmonyPatch(typeof(global::Map.GuideObjectManager), "Delete",
                new[] { typeof(global::Map.GuideObject), typeof(bool) })]
            internal static void DeletePrefix(global::Map.GuideObject _object)
            {
                if (_object == null) return;
                try
                {
                    ReclaimGuideObjectClones(_object);
                }
                catch (Exception ex)
                {
                    MemoryInvestigatorPlugin.Log.LogWarning("[GuideObjectManager.Delete] reclaim failed: " + ex);
                }
            }

            [HarmonyPrefix]
            [HarmonyPatch(typeof(global::Map.GuideObjectManager), "DeleteAll")]
            internal static void DeleteAllPrefix(global::Map.GuideObjectManager __instance)
            {
                try
                {
                    if (_dicGuideObjectField == null)
                        _dicGuideObjectField = typeof(global::Map.GuideObjectManager).GetField(
                            "dicGuideObject", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_dicGuideObjectField == null) return;

                    var dict = _dicGuideObjectField.GetValue(__instance)
                        as Dictionary<global::Map.ObjectCtrl, global::Map.GuideObject>;
                    if (dict == null) return;

                    // Reset aggregate counters
                    MaterialsReclaimed = 0;
                    ObjectsCleaned = 0;
                    CloneShaderFreq.Clear();

                    foreach (var kvp in dict)
                    {
                        if (kvp.Value != null)
                            ReclaimGuideObjectClones(kvp.Value);
                    }

                    // Log aggregate summary
                    var sb = new StringBuilder("[GuideObjectReclaim] objs=");
                    sb.Append(ObjectsCleaned).Append(" reclaimed=").Append(MaterialsReclaimed);
                    sb.Append(" cloneShaders=");
                    int n = 0;
                    foreach (var kvp in CloneShaderFreq)
                    {
                        if (n++ > 0) sb.Append(',');
                        sb.Append(kvp.Key).Append('=').Append(kvp.Value);
                    }
                    MemoryInvestigatorPlugin.Log.LogInfo(sb.ToString());

                    GuideObjectInitHooks.Clear();
                }
                catch (Exception ex)
                {
                    MemoryInvestigatorPlugin.Log.LogWarning("[GuideObjectManager.DeleteAll] reclaim failed: " + ex);
                }
            }

            internal static string FormatSummary()
            {
                if (MaterialsReclaimed == 0 && ObjectsCleaned == 0) return "guideClean[none]";
                return "guideClean[objs=" + ObjectsCleaned + " reclaimed=" + MaterialsReclaimed + "]";
            }

            // ponytail: walk each Renderer's sharedMaterials, destroy any with negative instanceID
            // (runtime clone). No .material getter call — no extra instantiation.
            // Renderer is about to be DestroyImmediate'd so no need to reset sharedMaterial.
            private static void ReclaimGuideObjectClones(global::Map.GuideObject guideObj)
            {
                var go = guideObj.gameObject;
                if (go == null) return;
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) return;
                ObjectsCleaned++;

                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;

                    var mats = r.sharedMaterials;
                    for (int j = 0; j < mats.Length; j++)
                    {
                        var m = mats[j];
                        if (m == null) continue;
                        // Negative instanceID = runtime clone (not a prefab asset)
                        if (m.GetInstanceID() < 0)
                        {
                            string sn = m.shader != null ? m.shader.name : "<null>";
                            int c;
                            CloneShaderFreq.TryGetValue(sn, out c);
                            CloneShaderFreq[sn] = c + 1;
                            MaterialsReclaimed++;
                            UnityEngine.Object.Destroy(m);
                        }
                    }
                }
            }
        }
    }

    internal struct MemoryPoint
    {
        private const double Mb = 1024d * 1024d;

        internal long WorkingSet;
        internal long PrivateBytes;   // PagefileUsage = commit charge
        internal long ManagedBytes;

        internal static MemoryPoint CaptureBasic()
        {
            var counters = default(PROCESS_MEMORY_COUNTERS);
            counters.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
            GetProcessMemoryInfo(_currentProcessHandle, ref counters, counters.cb);

            return new MemoryPoint
            {
                WorkingSet = (long)counters.WorkingSetSize,
                PrivateBytes = (long)counters.PagefileUsage,
                ManagedBytes = GC.GetTotalMemory(false)
            };
        }

        internal string Format()
        {
            return "mem[ws=" + ToMb(WorkingSet) +
                   "MB private=" + ToMb(PrivateBytes) +
                   "MB managed=" + ToMb(ManagedBytes) + "MB]";
        }

        internal static string FormatDelta(MemoryPoint before, MemoryPoint after)
        {
            return "delta[ws=" + ToSignedMb(after.WorkingSet - before.WorkingSet) +
                   "MB private=" + ToSignedMb(after.PrivateBytes - before.PrivateBytes) +
                   "MB managed=" + ToSignedMb(after.ManagedBytes - before.ManagedBytes) + "MB]";
        }

        private static string ToMb(long bytes)
        {
            return (bytes / Mb).ToString("0.0");
        }

        private static string ToSignedMb(long bytes)
        {
            double mb = bytes / Mb;
            return (mb >= 0 ? "+" : string.Empty) + mb.ToString("0.0");
        }

        #region Win32 P/Invoke — replaces broken System.Diagnostics.Process in Unity

        // ponytail: P/Invoke instead of Process.WorkingSet64 which returns 0 in Unity 2017 .NET 4.6
        private static readonly IntPtr _currentProcessHandle = GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS counters, uint size);

        [StructLayout(LayoutKind.Sequential, Size = 72)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
        }

        #endregion
    }

    internal static class UnityObjectCounter
    {
        private static readonly string[,] TypeNames =
        {
            { "Texture2D", "UnityEngine.Texture2D, UnityEngine.CoreModule" },
            { "RenderTexture", "UnityEngine.RenderTexture, UnityEngine.CoreModule" },
            { "Mesh", "UnityEngine.Mesh, UnityEngine.CoreModule" },
            { "Material", "UnityEngine.Material, UnityEngine.CoreModule" },
            { "AnimationClip", "UnityEngine.AnimationClip, UnityEngine.AnimationModule" },
            { "GameObject", "UnityEngine.GameObject, UnityEngine.CoreModule" },
            { "AudioClip", "UnityEngine.AudioClip, UnityEngine.AudioModule" },
            { "AssetBundle", "UnityEngine.AssetBundle, UnityEngine.AssetBundleModule" }
        };

        internal static string Capture()
        {
            if (!MemoryInvestigatorPlugin.IncludeUnityObjectCounts.Value)
                return "unity[disabled]";

            var builder = new StringBuilder("unity[");
            for (int i = 0; i < TypeNames.GetLength(0); i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(TypeNames[i, 0]).Append('=').Append(Count(TypeNames[i, 1]));
            }
            builder.Append(']');

            if (MemoryInvestigatorPlugin.ClassifyMaterials.Value)
                builder.Append(' ').Append(ClassifyAllMaterials());

            builder.Append(' ').Append(Hooks.MaterialSourceHooks.FormatSummary());
            builder.Append(' ').Append(Hooks.GuideObjectCleanupHooks.FormatSummary());
            return builder.ToString();
        }

        // Type.GetType 依赖精确程序集名；EC 的 Unity 模块拆分名可能不匹配（如 AssetBundleModule）。
        // 回退到扫描已加载程序集按全名解析；结果缓存（含 null）避免每次快照重复扫描。
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        private static Type ResolveType(string assemblyQualifiedName)
        {
            Type cached;
            if (_typeCache.TryGetValue(assemblyQualifiedName, out cached)) return cached;

            Type t = Type.GetType(assemblyQualifiedName, false);
            if (t == null)
            {
                string fullName = assemblyQualifiedName.Split(',')[0].Trim();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(fullName, false);
                    if (t != null) break;
                }
            }
            _typeCache[assemblyQualifiedName] = t;
            return t;
        }

        private static string Count(string assemblyQualifiedName)
        {
            try
            {
                Type type = ResolveType(assemblyQualifiedName);
                if (type == null) return "?";
                return Resources.FindObjectsOfTypeAll(type).Length.ToString();
            }
            catch (Exception ex)
            {
                return "err:" + ex.GetType().Name;
            }
        }

        // ponytail: owner→materials inverted index. One pass over Graphic+Renderer,
        // build Dictionary<Material,int> (0=other,1=char,2=adv), then classify by lookup.
        // O(N+M) instead of O(N*M) per-Material FindObjectsOfTypeAll scan.
        private static string ClassifyAllMaterials()
        {
            int chars = 0, advUI = 0, @other = 0, indexed = 0, @orphan = 0;
            // shader→class: build from sharedMaterials of known Renderer owners
            Dictionary<string, int> shaderClass = null;
            UnityEngine.Object[] allMats = null;
            Dictionary<int, int> matClass = null;
            try
            {
                Type matType = typeof(Material);
                allMats = Resources.FindObjectsOfTypeAll(matType);

                // Build owner index: Material instanceID → 1=char, 2=adv
                matClass = new Dictionary<int, int>(allMats.Length);
                // Also build shader→class map from sharedMaterials (no instantiation).
                // Used to classify orphan Materials by shader provenance — avoids .material getter.
                shaderClass = new Dictionary<string, int>();
                try
                {
                    // Scan Graphics (UI)
                    var allGraphics = Resources.FindObjectsOfTypeAll<Graphic>();
                    for (int i = 0; i < allGraphics.Length; i++)
                    {
                        var g = allGraphics[i];
                        if (g == null || g.gameObject == null) continue;
                        int cls = ClassifyOwner(g.gameObject);
                        var m = g.material;
                        if (m != null)
                        {
                            matClass[m.GetInstanceID()] = cls;
                            if (cls != 0 && m.shader != null && !shaderClass.ContainsKey(m.shader.name))
                                shaderClass[m.shader.name] = cls;
                        }
                        var mf = g.materialForRendering;
                        if (mf != null && mf != m)
                        {
                            matClass[mf.GetInstanceID()] = cls;
                            if (cls != 0 && mf.shader != null && !shaderClass.ContainsKey(mf.shader.name))
                                shaderClass[mf.shader.name] = cls;
                        }
                    }

                    // Scan Renderers (3D) — ONLY sharedMaterials, never .material getter
                    var allRenderers = Resources.FindObjectsOfTypeAll<Renderer>();
                    for (int i = 0; i < allRenderers.Length; i++)
                    {
                        var r = allRenderers[i];
                        if (r == null || r.gameObject == null) continue;
                        int cls = ClassifyOwner(r.gameObject);
                        var sm = r.sharedMaterials;
                        for (int j = 0; j < sm.Length; j++)
                        {
                            if (sm[j] == null) continue;
                            matClass[sm[j].GetInstanceID()] = cls;
                            if (cls != 0 && sm[j].shader != null && !shaderClass.ContainsKey(sm[j].shader.name))
                                shaderClass[sm[j].shader.name] = cls;
                        }
                    }
                }
                catch { /* best-effort index build */ }

                // Classify each Material by lookup
                for (int i = 0; i < allMats.Length; i++)
                {
                    var mat = allMats[i] as Material;
                    if (mat == null) continue;
                    int cls;
                    if (matClass.TryGetValue(mat.GetInstanceID(), out cls))
                    {
                        indexed++;
                        if (cls == 1) chars++;
                        else advUI++;
                    }
                    else
                    {
                        @orphan++;
                        @other++;
                    }
                }
            }
            catch (Exception ex)
            {
                return "matClass[err:" + ex.GetType().Name + "]";
            }

            return "matClass[char=" + chars + " adv=" + advUI + " other=" + @other +
                   " idx=" + indexed + " orphan=" + @orphan + " " + ClassifyOrphanNames(allMats, matClass, shaderClass) + "]";
        }

        // ponytail: Top N orphan shader frequency + provenance guess via shaderClass map.
        // shaderClass: shaderName→1=char,2=adv built from sharedMaterials — indirect classification
        // without calling renderer.material (which would itself trigger implicit instantiation).
        private static string ClassifyOrphanNames(UnityEngine.Object[] allMats, Dictionary<int, int> matClass, Dictionary<string, int> shaderClass)
        {
            try
            {
                var freq = new Dictionary<string, int>();
                int provenCharShader = 0, provenAdvShader = 0, provenUnknown = 0;
                for (int i = 0; i < allMats.Length; i++)
                {
                    var mat = allMats[i] as Material;
                    if (mat == null || matClass.ContainsKey(mat.GetInstanceID())) continue;
                    var shader = mat.shader;
                    string key = (shader != null) ? shader.name : "<null>";
                    int c;
                    freq.TryGetValue(key, out c);
                    freq[key] = c + 1;
                    // Indirect provenance: same shader as a known char/adv sharedMaterial
                    if (shaderClass != null && shader != null && shaderClass.TryGetValue(shader.name, out int provenCls))
                    {
                        if (provenCls == 1) provenCharShader++;
                        else if (provenCls == 2) provenAdvShader++;
                    }
                    else provenUnknown++;
                }
                // Top 5 by count
                var top = new List<KeyValuePair<string, int>>(freq);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new StringBuilder("orphan[provenChar=").Append(provenCharShader)
                    .Append(" provenAdv=").Append(provenAdvShader)
                    .Append(" unknown=").Append(provenUnknown).Append(" topShaders=");
                int limit = Math.Min(5, top.Count);
                for (int i = 0; i < limit; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(top[i].Key).Append('=').Append(top[i].Value);
                }
                if (top.Count > limit) sb.Append(" ...+").Append(top.Count - limit);
                sb.Append(']');
                return sb.ToString();
            }
            catch { }
            return "orphanShaders[err]";
        }

        // ponytail: 1=character, 2=adv UI, 0=other.
        // ADVCanvas fallback: Canvas may be on same GO as ADVCanvas component.
        private static int ClassifyOwner(GameObject go)
        {
            // ADV UI: Canvas ancestor
            if (go.GetComponentInParent<Canvas>() != null)
                return 2;
            // ADV UI: ADVCanvas component (Canvas may be deactivated/unfindable)
            if (go.GetComponentInParent<ADVCanvas>() != null)
                return 2;
            // Character: ChaControl ancestor
            if (go.GetComponentInParent<ChaControl>() != null)
                return 1;
            return 0;
        }
    }

    internal static class SceneAnalyzer
    {
        internal static string BuildSceneSummary(HEditData data)
        {
            var summary = new SceneSummary
            {
                Maps = Count(data.maps),
                Nodes = Count(data.nodes),
                NodeBases = Count(data.nodeBases),
                CharaFiles = Count(data.charaFiles),
                RuntimeCharas = Count(data.charas)
            };

            if (data.nodes != null)
            {
                foreach (var node in data.nodes.Values)
                {
                    var adv = node as HEditADVPart;
                    if (adv == null)
                    {
                        summary.OtherParts++;
                        continue;
                    }

                    summary.AdvParts++;
                    if (adv.cuts == null) continue;

                    summary.Cuts += adv.cuts.Count;
                    foreach (HEditADVPart.Cut cut in adv.cuts)
                        AddCut(summary, cut);
                }
            }

            summary.Finish();
            return summary.ToLogString();
        }

        internal static string BuildCutSummary(HEditADVPart.Cut cut)
        {
            if (cut == null) return "cut[null]";

            int visible = 0;
            if (cut.charStates != null)
            {
                foreach (HEditADVPart.CharState state in cut.charStates)
                    if (state != null && state.visible) visible++;
            }

            return "cut[chars=" + Count(cut.charStates) +
                   " visible=" + visible +
                   " items=" + Count(cut.itemStates) +
                   " speech=" + Count(cut.speechBubbles) +
                   " effects=" + Count(cut.screenEffects) +
                   " end=" + cut.endCut + "]";
        }

        private static void AddCut(SceneSummary summary, HEditADVPart.Cut cut)
        {
            if (cut == null) return;
            if (cut.endCut) summary.EndCuts++;

            if (cut.charStates != null)
            {
                summary.CharStates += cut.charStates.Count;
                foreach (HEditADVPart.CharState state in cut.charStates)
                {
                    if (state == null) continue;
                    if (state.visible) summary.VisibleCharStates++;
                    summary.AddCoordinate(state.coordinate);
                    summary.AddPose(state.pose);
                }
            }

            summary.ItemStates += Count(cut.itemStates);

            if (cut.speechBubbles != null)
            {
                summary.SpeechBubbles += cut.speechBubbles.Count;
                foreach (HEditADVPart.SpeechBubbles speech in cut.speechBubbles)
                    summary.AddSpeech(speech);
            }

            summary.ScreenEffects += Count(cut.screenEffects);
        }

        private static int Count<T>(ICollection<T> items)
        {
            return items == null ? 0 : items.Count;
        }
    }

    internal sealed class SceneSummary
    {
        internal int Maps;
        internal int Nodes;
        internal int NodeBases;
        internal int CharaFiles;
        internal int RuntimeCharas;
        internal int AdvParts;
        internal int OtherParts;
        internal int Cuts;
        internal int EndCuts;
        internal int CharStates;
        internal int VisibleCharStates;
        internal int ItemStates;
        internal int SpeechBubbles;
        internal int ScreenEffects;
        internal int TextChars;
        internal int CoordinateNone;
        internal int CoordinateEmbedded;
        internal int CoordinateOriginal;
        internal int CoordinateOther;
        internal int CoordinateEntries;
        internal long CoordinateBytes;
        internal int DuplicateCoordinateEntries;
        internal long DuplicateCoordinateBytes;
        internal int DuplicateCoordinateBuckets;
        internal int PosePathCount;
        internal int PoseEmbeddedCount;
        internal int PoseFkBones;
        internal int PoseIkBones;

        // v0.6: pose dedup audit — confirm whether copied cuts produce redundant PoseInfo graphs.
        // puid is preserved across cut copies (PoseInfo.Copy), so uniqueByPuid << total proves redundancy.
        // content hash is a cross-check (quantized, allocation-free).
        internal int TotalPoses;
        internal long PngDataBytes;
        private readonly HashSet<string> _posePuids = new HashSet<string>();
        private readonly HashSet<uint> _poseContentHashes = new HashSet<uint>();
        internal int UniquePoseByPuid { get { return _posePuids.Count; } }
        internal int UniquePoseByContent { get { return _poseContentHashes.Count; } }

        private readonly Dictionary<uint, HashBucket> _coordinateHashes = new Dictionary<uint, HashBucket>();

        internal void AddCoordinate(HEditADVPart.CoordinateInfo coordinate)
        {
            if (coordinate == null) return;

            CoordinateEntries++;
            switch (coordinate.type)
            {
                case 0:
                    CoordinateNone++;
                    break;
                case 1:
                    CoordinateEmbedded++;
                    break;
                case 2:
                    CoordinateOriginal++;
                    break;
                default:
                    CoordinateOther++;
                    break;
            }

            byte[] data = coordinate.data;
            if (data == null || data.Length == 0) return;

            CoordinateBytes += data.Length;
            uint hash = HashBytes(data);
            HashBucket bucket;
            if (_coordinateHashes.TryGetValue(hash, out bucket))
            {
                bucket.Count++;
                bucket.TotalBytes += data.Length;
                return;
            }
            _coordinateHashes.Add(hash, new HashBucket(data.Length));
        }

        internal void AddPose(Pose.PoseInfo pose)
        {
            if (pose == null) return;

            if (string.IsNullOrEmpty(pose.loadPath))
                PoseEmbeddedCount++;
            else
                PosePathCount++;

            if (pose.dicFKBone != null) PoseFkBones += pose.dicFKBone.Count;
            if (pose.dicIKBone != null) PoseIkBones += pose.dicIKBone.Count;

            // Dedup audit
            TotalPoses++;
            _posePuids.Add(pose.puid ?? string.Empty);
            _poseContentHashes.Add(HashPose(pose));
            if (pose.pngData != null) PngDataBytes += pose.pngData.Length;
        }

        // ponytail: quantized FNV over bone keys + changeAmount — allocation-free, good enough to
        // cross-check puid-based dedup. Byte-identical copies hash identical; tiny collision risk OK
        // for a diagnostic.
        private static uint HashPose(Pose.PoseInfo pose)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = FoldBones(h, pose.dicFKBone);
                h = FoldBones(h, pose.dicIKBone);
                return h;
            }
        }

        private static uint FoldBones(uint h, Dictionary<int, Pose.OIBone> dic)
        {
            if (dic == null) return h;
            foreach (var kv in dic)
            {
                unchecked { h = (h ^ (uint)kv.Key) * 16777619u; }
                var bone = kv.Value;
                if (bone == null) continue;
                var ca = bone.changeAmount;
                if (ca == null) continue;
                h = FoldVec(h, ca.pos);
                h = FoldVec(h, ca.rot);
                h = FoldVec(h, ca.scale);
            }
            return h;
        }

        private static uint FoldVec(uint h, Vector3 v)
        {
            unchecked
            {
                h = (h ^ (uint)(int)(v.x * 1024f)) * 16777619u;
                h = (h ^ (uint)(int)(v.y * 1024f)) * 16777619u;
                h = (h ^ (uint)(int)(v.z * 1024f)) * 16777619u;
                return h;
            }
        }

        internal void AddSpeech(HEditADVPart.SpeechBubbles speech)
        {
            if (speech == null || speech.textLayouts == null) return;

            foreach (HEditADVPart.SpeechBubbles.TextLayout layout in speech.textLayouts)
            {
                if (layout == null || layout.msg == null) continue;
                TextChars += layout.msg.Length;
            }
        }

        internal void Finish()
        {
            foreach (HashBucket bucket in _coordinateHashes.Values)
            {
                if (bucket.Count <= 1) continue;

                DuplicateCoordinateBuckets++;
                DuplicateCoordinateEntries += bucket.Count - 1;
                DuplicateCoordinateBytes += bucket.TotalBytes - bucket.FirstBytes;
            }
        }

        internal string ToLogString()
        {
            return "scene[nodes=" + Nodes +
                   " nodeBases=" + NodeBases +
                   " maps=" + Maps +
                   " charaFiles=" + CharaFiles +
                   " runtimeCharas=" + RuntimeCharas +
                   " advParts=" + AdvParts +
                   " otherParts=" + OtherParts +
                   " cuts=" + Cuts +
                   " endCuts=" + EndCuts +
                   " charStates=" + CharStates +
                   " visibleCharStates=" + VisibleCharStates +
                   " itemStates=" + ItemStates +
                   " speech=" + SpeechBubbles +
                   " effects=" + ScreenEffects +
                   " textChars=" + TextChars +
                   " coordEntries=" + CoordinateEntries +
                   " coordTypes=0:" + CoordinateNone + "/1:" + CoordinateEmbedded + "/2:" + CoordinateOriginal + "/other:" + CoordinateOther +
                   " coordBytes=" + CoordinateBytes +
                   " coordDupEntries=" + DuplicateCoordinateEntries +
                   " coordDupBytes=" + DuplicateCoordinateBytes +
                   " coordDupBuckets=" + DuplicateCoordinateBuckets +
                   " posePath=" + PosePathCount +
                   " poseEmbedded=" + PoseEmbeddedCount +
                   " poseFkBones=" + PoseFkBones +
                   " poseIkBones=" + PoseIkBones +
                   " poseAudit=" + TotalPoses + "/" + UniquePoseByPuid + "/" + UniquePoseByContent +
                   " poseEstMB=" + ((PoseFkBones + PoseIkBones) * 800L / (1024L * 1024L)) +
                   " pngMB=" + (PngDataBytes / (1024L * 1024L)) + "]";
        }

        // ponytail: diagnostic-only FNV hash; switch to a cryptographic hash only if collisions become suspect.
        private static uint HashBytes(byte[] data)
        {
            unchecked
            {
                uint hash = 2166136261;
                hash = (hash ^ (uint)data.Length) * 16777619;
                for (int i = 0; i < data.Length; i++)
                    hash = (hash ^ data[i]) * 16777619;
                return hash;
            }
        }

        private sealed class HashBucket
        {
            internal readonly long FirstBytes;
            internal int Count = 1;
            internal long TotalBytes;

            internal HashBucket(int bytes)
            {
                FirstBytes = bytes;
                TotalBytes = bytes;
            }
        }
    }
}
