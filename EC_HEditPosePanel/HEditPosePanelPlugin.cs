using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Manager;
using Map;
using Pose;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EC_HEditPosePanel
{
    // 轴系配置:Local=骨局部(默认,EC PoseCreate 原生语义)/World=世界固定 XYZ。仅影响平移;旋转恒局部(EC 无世界旋转)。
    internal enum AxisMode { Local, World }

    [BepInProcess("EmotionCreators")]
    [BepInPlugin(GUID, PluginName, Version)]
    public sealed class HEditPosePanelPlugin : BaseUnityPlugin
    {
        public const string GUID = "EC_HEditPosePanel";
        public const string PluginName = "EC HEdit Pose Panel";
        public const string Version = "2.0.0";

        internal static ManualLogSource Log;
        internal static HEditPosePanelPlugin Instance;

        private const int MODE_MOVE = 0;
        private const int MODE_ROTATION = 1;
        private const int MODE_SCALE = 2;

        private ConfigEntry<KeyCode> _cfgToggleKey;
        private ConfigEntry<KeyCode> _cfgAxisX;
        private ConfigEntry<KeyCode> _cfgAxisY;
        private ConfigEntry<KeyCode> _cfgAxisZ;
        private ConfigEntry<float> _cfgTrans;
        private ConfigEntry<float> _cfgRot;
        private ConfigEntry<AxisMode> _cfgAxisMode;
        private ConfigEntry<KeyCode> _cfgAxisModeKey;
        private ConfigEntry<float> _cfgWindowPositionX;
        private ConfigEntry<float> _cfgWindowPositionY;
        private ConfigEntry<float> _cfgThumbnailPositionX;
        private ConfigEntry<float> _cfgThumbnailPositionY;
        private ConfigEntry<bool> _cfgShowThumbnails;
        private ConfigEntry<ThumbnailSize> _cfgThumbnailSize;

        // 4 档锚点:档1=最低,档2=默认,档3=几何中项,档4=最大;刻度位置均匀。
        private static readonly float[] _transSteps = { 0.001f, 0.015f, 0.0387f, 0.1f };
        private static readonly float[] _rotSteps = { 1f, 4f, 7.746f, 15f };
        // 用户手动设定档位值(面板 ◀▶/刻度点击更新);ctrl/shift 临时覆盖不动这里,松开恢复。
        private static float _manualTransSens = _transSteps[1];
        private static float _manualRotSens = _rotSteps[1];
        private Harmony _harmony;

        private bool _windowVisible;
        private Rect _windowRect;
        private const float WinWidth = 220f;
        private const float WinHeight = 150f;
        private bool _rectSynced;
        private bool _windowPositionDirty;
        private int _windowScreenWidth;
        private int _windowScreenHeight;
        private PoseCompanionWindow _companionWindow;
        private PoseLibrary _poseLibrary;
        private ThumbnailService _thumbnailService;
        private FkPoseService _fkPoseService;
        private PoseEditContext _poseEditContext;
        private GuideAdjustmentSession _guideAdjustmentSession;

        // 活跃轴与来源区分:Mouse 松开看 !GetMouseButton(0),Keyboard 松开看 !GetKey(activeKeyCode)。
        // _wasInPoseEdit:进/出 PoseCreate 边沿,false→true 瞬间自动开窗。
        private string _activeAxis;
        private AxisSource _axisSource;
        private KeyCode _activeKeyCode;
        private bool _wasInPoseEdit;
        private bool _wasInHEdit;
        private bool _wasActiveGuideAvailable;
        // Local+平移时锁定首个目标的局部轴，避免 IK 目标在持轴期间重定向造成方向漂移。
        private Vector3 _localAxisDir;
        private bool _localAxisDirValid;
        private enum AxisSource { None, Mouse, Keyboard }

        // 伴生窗悬停即隔离相机；主窗保留原有左键操作时锁定语义。
        internal static bool ShouldLockCamera()
        {
            var inst = Instance;
            if (inst == null || !inst._windowVisible || !inst._rectSynced) return false;
            if (inst._activeAxis != null) return true;
            float mx = Input.mousePosition.x;
            float my = Screen.height - Input.mousePosition.y;
            var mousePosition = new Vector2(mx, my);
            if (inst._companionWindow != null && inst._companionWindow.ContainsMouse(mousePosition))
                return true;
            return inst._windowRect.Contains(mousePosition) && Input.GetMouseButton(0);
        }

        internal void Awake()
        {
            Log = Logger;
            Instance = this;
            _cfgToggleKey = Config.Bind("General", "ToggleKey", KeyCode.P, "开关姿态面板窗口");
            _cfgAxisX = Config.Bind("General", "AxisXKey", KeyCode.Z, "X 轴按键(按住+移动鼠标触发;生效时屏蔽 EC 原键响应)");
            _cfgAxisY = Config.Bind("General", "AxisYKey", KeyCode.X, "Y 轴按键");
            _cfgAxisZ = Config.Bind("General", "AxisZKey", KeyCode.C, "Z 轴按键");
            _cfgTrans = Config.Bind("General", "TransSens", 0.015f, "平移灵敏度(面板 4 档调节,请勿在配置管理器手动改)");
            _cfgRot = Config.Bind("General", "RotSens", 4f, "旋转灵敏度(面板 4 档调节,请勿在配置管理器手动改)");
            _cfgTrans.SettingChanged += (_, __) => Manipulator.TransSens = _cfgTrans.Value;
            _cfgRot.SettingChanged += (_, __) => Manipulator.RotSens = _cfgRot.Value;
            _cfgAxisMode = Config.Bind("General", "AxisMode", AxisMode.Local, "轴系:Local=骨局部(沿骨朝向,默认,EC 语义)/World=世界固定 XYZ。仅平移生效,旋转恒局部");
            _cfgAxisMode.SettingChanged += (_, __) => SyncCalcAxis();
            _cfgAxisModeKey = Config.Bind("General", "AxisModeKey", KeyCode.G, "切换轴系 Local/World 的快捷键( gameplay 中按一次翻转)");
            _cfgWindowPositionX = Config.Bind("Window", "WindowPositionX", 0.744f, "主窗口归一化 X 位置");
            _cfgWindowPositionY = Config.Bind("Window", "WindowPositionY", 0.697f, "主窗口归一化 Y 位置");
            _cfgWindowPositionX.SettingChanged += OnWindowPositionSettingChanged;
            _cfgWindowPositionY.SettingChanged += OnWindowPositionSettingChanged;
            _cfgThumbnailPositionX = Config.Bind("Window", "ThumbnailWindowPositionX", -1f, "缩略图窗口归一化 X 位置；负值表示自动定位");
            _cfgThumbnailPositionY = Config.Bind("Window", "ThumbnailWindowPositionY", -1f, "缩略图窗口归一化 Y 位置；负值表示自动定位");
            _cfgShowThumbnails = Config.Bind("Window", "ShowThumbnails", true, "是否显示姿势缩略图");
            _cfgThumbnailSize = Config.Bind("Window", "ThumbnailSize", ThumbnailSize.Small, "缩略图尺寸：Small/Medium/Large");
            _poseLibrary = new PoseLibrary(message => Log.LogWarning(message));
            _poseEditContext = new PoseEditContext();
            _guideAdjustmentSession = new GuideAdjustmentSession();
            _thumbnailService = new ThumbnailService(
                this,
                () => _windowVisible,
                visible => _windowVisible = visible);
            _fkPoseService = new FkPoseService(this, _poseLibrary, _thumbnailService,
                () => _guideAdjustmentSession.Cancel());
            _companionWindow = new PoseCompanionWindow(
                _cfgShowThumbnails,
                _cfgThumbnailSize,
                _cfgThumbnailPositionX,
                _cfgThumbnailPositionY,
                _poseLibrary,
                _fkPoseService,
                _poseEditContext,
                _guideAdjustmentSession);
            Manipulator.TransSens = _cfgTrans.Value;
            Manipulator.RotSens = _cfgRot.Value;
            SnapCfgToStep(_cfgTrans, _transSteps, ref _manualTransSens);
            SnapCfgToStep(_cfgRot, _rotSteps, ref _manualRotSens);

            _harmony = new Harmony(GUID);
            PatchCameraMethods();
            SyncCalcAxis();   // 同步 EC 原生 guide ball 平移箭头朝向(Local 时贴骨局部/World 时对齐世界)
            Log.LogInfo($"{PluginName} v{Version} loaded. 默认开窗键: P");
        }

        internal void OnDestroy()
        {
            if (_cfgWindowPositionX != null)
                _cfgWindowPositionX.SettingChanged -= OnWindowPositionSettingChanged;
            if (_cfgWindowPositionY != null)
                _cfgWindowPositionY.SettingChanged -= OnWindowPositionSettingChanged;
            _guideAdjustmentSession?.Cancel();
            _companionWindow?.Dispose();
        }

        private void OnWindowPositionSettingChanged(object sender, System.EventArgs args)
        {
            _rectSynced = false;
            _windowPositionDirty = false;
        }

        // 同步插件轴系到 EC 原生 GuideObjectManager.calcAxis(仅影响平移箭头 gizmo 可视化,GuideObject.LateUpdate:520 改 roots[0];不改旋转 gizmo,不改数据计算)。
        private void SyncCalcAxis()
        {
            if (!Singleton<GuideObjectManager>.IsInstance()) return;
            var gom = Singleton<GuideObjectManager>.Instance;
            if (gom == null) return;
            var want = (_cfgAxisMode.Value == AxisMode.World)
                ? GuideObjectManager.CalcAxis.World : GuideObjectManager.CalcAxis.Local;
            if (gom.calcAxis != want) gom.calcAxis = want;
        }

        // 裙子根球分色：00/01 白、02/03 黄、04/05 青、06/07 绿。
        private static readonly Color SkirtRootYellow = new Color(1f, 0.92f, 0.2f, 1f);
        private static readonly Color SkirtRootCyan = new Color(0.2f, 0.95f, 0.95f, 1f);
        private static readonly Color SkirtRootGreen = new Color(0.25f, 0.9f, 0.3f, 1f);

        private static void SetupFKPostfix(KinematicCtrl __instance)
        {
            ApplySkirtRootGuideColors(__instance);
        }

        private void TryColorSkirtRootGuides()
        {
            PoseEditSnapshot snapshot;
            string error;
            if (_poseEditContext == null || !_poseEditContext.TryResolve(out snapshot, out error)) return;
            ApplySkirtRootGuideColors(snapshot.Kinematic);
        }

        private static void ApplySkirtRootGuideColors(KinematicCtrl kinematic)
        {
            if (kinematic == null || kinematic.lstFKBone == null) return;
            foreach (OCBone bone in kinematic.lstFKBone)
            {
                if (bone == null || bone.group != OIBone.BoneGroup.Skirt) continue;
                if (bone.guideObject == null || bone.guideObject.guideSelect == null) continue;
                Color color;
                if (!TryGetSkirtRootColor(bone.Name, out color)) continue;
                bone.guideObject.guideSelect.color = color;
            }
        }

        private static bool TryGetSkirtRootColor(string boneName, out Color color)
        {
            color = default(Color);
            if (string.IsNullOrEmpty(boneName)) return false;
            // 只改根：cf_j_sk_00_00 ~ cf_j_sk_07_00
            if (boneName == "cf_j_sk_00_00" || boneName == "cf_j_sk_01_00")
            {
                color = Color.white;
                return true;
            }
            if (boneName == "cf_j_sk_02_00" || boneName == "cf_j_sk_03_00")
            {
                color = SkirtRootYellow;
                return true;
            }
            if (boneName == "cf_j_sk_04_00" || boneName == "cf_j_sk_05_00")
            {
                color = SkirtRootCyan;
                return true;
            }
            if (boneName == "cf_j_sk_06_00" || boneName == "cf_j_sk_07_00")
            {
                color = SkirtRootGreen;
                return true;
            }
            return false;
        }

        // 翻转 _cfgAxisMode(Local↔World);SettingChanged 自动驱动 SyncCalcAxis(同步原生 guide ball 平移箭头)。
        private void ToggleAxisMode()
        {
            _cfgAxisMode.Value = (_cfgAxisMode.Value == AxisMode.World) ? AxisMode.Local : AxisMode.World;
        }

        private void PatchCameraMethods()
        {
            // 悬停只拦截鼠标入口；LateUpdate 仅在插件接管轴或输入框聚焦时跳过。
            foreach (var name in new[] { "InputMouseProc", "InputMouseWheelZoomProc" })
            {
                var target = AccessTools.Method(typeof(SplitCameraCtrl), name);
                if (target == null) { Log.LogError($"[Harmony] 未找到 SplitCameraCtrl.{name}"); continue; }
                var prefix = new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(CameraMouseProcPrefix));
                _harmony.Patch(target, prefix: prefix);
            }
            var splitLateUpdate = AccessTools.Method(typeof(SplitCameraCtrl), "LateUpdate");
            if (splitLateUpdate != null)
                _harmony.Patch(splitLateUpdate,
                    prefix: new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(CameraFramePrefix)));
            else Log.LogError("[Harmony] 未找到 SplitCameraCtrl.LateUpdate");

            // HEdit 相机的鼠标入口定义在基类；Prefix 按实例类型过滤，避免影响 CameraControl_Preview。
            foreach (var name in new[] { "InputMouseProc", "InputMouseWheelZoomProc" })
            {
                var target = AccessTools.Method(typeof(BaseCameraControl_Ver2), name);
                if (target == null) { Log.LogError($"[Harmony] 未找到 BaseCameraControl_Ver2.{name}"); continue; }
                var prefix = new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(HEditCameraMouseProcPrefix));
                _harmony.Patch(target, prefix: prefix);
            }
            var heditLateUpdate = AccessTools.Method(typeof(CameraControl_Ver2), "LateUpdate");
            if (heditLateUpdate != null)
                _harmony.Patch(heditLateUpdate,
                    prefix: new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(CameraFramePrefix)));
            else Log.LogError("[Harmony] 未找到 CameraControl_Ver2.LateUpdate");

            // HEditScene 节点态 Z 切 Config.EtcData.Look 与插件 AxisXKey=Z 冲突;持轴期间整体跳过 HPartShortcutKey。
            var hpartKey = AccessTools.Method(typeof(HEdit.HEditScene), "HPartShortcutKey");
            if (hpartKey != null)
                _harmony.Patch(hpartKey, prefix: new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(HPartShortcutKeyPrefix)));
            else Log.LogError("[Harmony] 未找到 HEditScene.HPartShortcutKey");

            // 屏蔽 ShortcutKeyCtrl.Update:自定义键覆盖 EC 原键功能。
            // ShortcutKeyCtrl 在 add scene 已 return,本 Prefix 是保险(防叠加 scene 导致它跑)。
            var skcUpdate = AccessTools.Method(typeof(ShortcutKeyCtrl), "Update");
            if (skcUpdate != null)
                _harmony.Patch(skcUpdate, prefix: new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(ShortcutKeyCtrlUpdatePrefix)));
            else Log.LogError("[Harmony] 未找到 ShortcutKeyCtrl.Update");

            // IMGUI 不在 uGUI 命中链；清空 EventSystem 射线，避免点按键时穿透到底层 Guide/uGUI。
            var raycastAll = AccessTools.Method(typeof(EventSystem), "RaycastAll");
            if (raycastAll != null)
                _harmony.Patch(raycastAll, postfix: new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(EventSystemRaycastAllPostfix)));
            else Log.LogError("[Harmony] 未找到 EventSystem.RaycastAll");

            // 裙子 FK Setup 后给根球分色，便于辨认 8 根裙链。
            var setupFk = AccessTools.Method(typeof(KinematicCtrl), "SetupFK");
            if (setupFk != null)
                _harmony.Patch(setupFk, postfix: new HarmonyMethod(typeof(HEditPosePanelPlugin), nameof(SetupFKPostfix)));
            else Log.LogError("[Harmony] 未找到 KinematicCtrl.SetupFK");
        }

        // 鼠标在伴生/缩略图窗内时清空射线结果，阻断 GuideBase 等 uGUI 点击。
        private static void EventSystemRaycastAllPostfix(List<RaycastResult> raycastResults)
        {
            if (raycastResults == null || raycastResults.Count == 0) return;
            var inst = Instance;
            if (inst == null || !inst._windowVisible || inst._companionWindow == null) return;
            float mx = Input.mousePosition.x;
            float my = Screen.height - Input.mousePosition.y;
            if (!inst._companionWindow.ContainsMouse(new Vector2(mx, my))) return;
            raycastResults.Clear();
        }

        private static bool ShortcutKeyCtrlUpdatePrefix() => !ShouldLockMapShortcut();
        private static bool CameraMouseProcPrefix() => !ShouldLockCamera();
        private static bool HEditCameraMouseProcPrefix(BaseCameraControl_Ver2 __instance)
            => !(__instance is CameraControl_Ver2 && ShouldLockCamera());
        private static bool CameraFramePrefix() => !ShouldTakeOverCameraFrame();

        private static bool ShouldTakeOverCameraFrame()
        {
            var inst = Instance;
            if (inst == null || !inst._windowVisible) return false;
            if (inst._activeAxis != null) return true;
            return IsInPoseEdit() && inst._companionWindow != null
                && inst._companionWindow.IsKeyboardInputFocused();
        }

        // Map 的 Z/C 与默认轴键冲突。只在轴键实际按住时屏蔽该帧，避免长期禁用原生快捷键。
        private static bool ShouldLockMapShortcut()
            => (IsInPoseEdit() && Instance != null && Instance._windowVisible
                && Instance._companionWindow != null
                && Instance._companionWindow.IsKeyboardInputFocused())
                || (IsManipulatorAvailable() && IsAxisShortcutHeld());

        private static bool HPartShortcutKeyPrefix() => !ShouldLockHPartShortcut();
        private static bool ShouldLockHPartShortcut()
            => IsManipulatorAvailable() && IsAxisShortcutHeld();

        private static bool IsAxisShortcutHeld()
        {
            var inst = Instance;
            if (inst == null) return false;
            return Input.GetKey(inst._cfgAxisX.Value)
                || Input.GetKey(inst._cfgAxisY.Value)
                || Input.GetKey(inst._cfgAxisZ.Value);
        }

        internal void Update()
        {
            bool inPose = IsInPoseEdit();
            if (inPose && !_wasInPoseEdit)
            {
                _windowVisible = true;
                // 进 PoseCreate 时 GOM 才就绪；Awake 时同步常落空，导致首帧轴向乱
                SyncCalcAxis();
                TryColorSkirtRootGuides();
            }
            if (!inPose && _wasInPoseEdit)
            {
                _guideAdjustmentSession?.Cancel();
                _companionWindow?.Reset();
            }
            _wasInPoseEdit = inPose;

            if (inPose && _guideAdjustmentSession != null && _guideAdjustmentSession.IsActive)
            {
                string guideError;
                if (!_guideAdjustmentSession.TryRefresh(_poseEditContext, out guideError))
                    _companionWindow?.SetGuideStatus(guideError);
            }

            bool inHEdit = IsInHEditIK();
            if (inHEdit && !_wasInHEdit) _windowVisible = true;
            _wasInHEdit = inHEdit;

            // Workplace 父级常驻激活；仅在当前选择的 Guide 真正进入操作态时打开通用入口。
            bool activeGuideAvailable = HasActiveGuideTarget();
            if (activeGuideAvailable && !_wasActiveGuideAvailable) _windowVisible = true;
            _wasActiveGuideAvailable = activeGuideAvailable;

            bool canManipulate = inPose || inHEdit || activeGuideAvailable;

            if (Input.GetKeyDown(_cfgToggleKey.Value))
            {
                _windowVisible = !_windowVisible;
            }

            if (canManipulate && Input.GetKeyDown(_cfgAxisModeKey.Value))
            {
                ToggleAxisMode();
            }

            // 离开可操作路径时强制清轴，避免相机和光标锁残留。
            if (_activeAxis != null && !canManipulate)
            {
                var clearedAxis = _activeAxis;
                _activeAxis = null; _axisSource = AxisSource.None; _activeKeyCode = KeyCode.None;
                _localAxisDirValid = false;
                UnlockCursorIfLocked();
                SetAxisHighlight(clearedAxis, false);
            }

            if (canManipulate && _axisSource != AxisSource.Mouse)
            {
                KeyCode? triggered = null;
                if (Input.GetKeyDown(_cfgAxisX.Value)) triggered = _cfgAxisX.Value;
                else if (Input.GetKeyDown(_cfgAxisY.Value)) triggered = _cfgAxisY.Value;
                else if (Input.GetKeyDown(_cfgAxisZ.Value)) triggered = _cfgAxisZ.Value;
                if (triggered.HasValue) BeginAxis(KeyToAxis(triggered.Value), AxisSource.Keyboard, triggered.Value);
            }

            bool released = false;
            if (_activeAxis != null)
            {
                if (_axisSource == AxisSource.Mouse) released = !Input.GetMouseButton(0);
                else if (_axisSource == AxisSource.Keyboard) released = !Input.GetKey(_activeKeyCode) || !canManipulate;
            }
            if (released)
            {
                var clearedAxis = _activeAxis;
                _activeAxis = null; _axisSource = AxisSource.None; _activeKeyCode = KeyCode.None;
                _localAxisDirValid = false;
                UnlockCursorIfLocked();
                SetAxisHighlight(clearedAxis, false);
            }

            if (_activeAxis != null) ApplyAxis(_activeAxis);
        }

        private string KeyToAxis(KeyCode key)
        {
            if (key == _cfgAxisX.Value) return "X";
            if (key == _cfgAxisY.Value) return "Y";
            if (key == _cfgAxisZ.Value) return "Z";
            return null;
        }

        // 仅操作已由原生 GuideObjectManager 管理的选择对象，避免误碰普通 Selection。
        private static ObjectCtrl[] GetGuideTargets()
        {
            if (!Singleton<Selection>.IsInstance()) return new ObjectCtrl[0];
            var sel = Singleton<Selection>.Instance;
            var ocs = sel != null ? sel.selectCtrls : null;
            if (ocs == null) return new ObjectCtrl[0];
            return ocs.Where(oc => oc != null && oc.guideObject != null).ToArray();
        }

        private void BeginAxis(string axis, AxisSource src, KeyCode key)
        {
            if (axis == null) return;
            if (_activeAxis != null)
            {
                if (_axisSource == AxisSource.Keyboard && src == AxisSource.Keyboard)
                {
                    SetAxisHighlight(_activeAxis, false);   // 切轴:先清旧轴高亮
                    _activeAxis = axis;
                    _activeKeyCode = key;
                    CacheLocalAxisDir(axis);   // 切轴:axis 变 → 重锁方向
                }
                return;
            }
            _activeAxis = axis;
            _axisSource = src;
            _activeKeyCode = key;
            CacheLocalAxisDir(axis);
            SetAxisHighlight(axis, true);   // 按下:点亮对应轴原生高亮
            LockCursor();
        }

        // Local+Move 锁定按下瞬间首个 Guide 目标的轴向；World 与旋转不使用该缓存。
        private void CacheLocalAxisDir(string axis)
        {
            _localAxisDirValid = false;
            if (_cfgAxisMode.Value != AxisMode.Local) return;
            if (GuideObjectManager.GetMode() != MODE_MOVE) return;
            var first = GetGuideTargets().FirstOrDefault();
            if (first == null || first.transform == null) return;
            switch (axis)
            {
                case "X": _localAxisDir = first.transform.right; break;
                case "Y": _localAxisDir = first.transform.up; break;
                case "Z": _localAxisDir = first.transform.forward; break;
                default: return;
            }
            _localAxisDirValid = true;
        }

        // 键控轴触发原生高亮:遍历 guides[] 按 GuideMove.axis/GuideRotation.axis 枚举匹配(不赌数组下标——真机发现 guide[] 两段子顺序均非 X/Y/Z)。
        // 坑:colorHighlighted/colorNormal 是 protected 字段(非属性),Traverse 须 .Field() 读;.Property() 读字段返回 default(Color)=黑。colorNow 是 protected-set 属性,.Property().SetValue()。
        // 边界:不碰 operationTarget(会反抑鼠标 hover);isDrag 时跳过(原生自管);异常仅日志+return,绝不打断键控 pose 操作。
        private static void SetAxisHighlight(string axis, bool on)
        {
            if (axis == null) return;
            try
            {
                if (!Singleton<GuideObjectManager>.IsInstance()) return;
                var gom = Singleton<GuideObjectManager>.Instance;
                if (gom == null) return;
                var first = GetGuideTargets().FirstOrDefault();
                if (first == null) return;
                var dic = Traverse.Create(gom).Field("dicGuideObject").GetValue<System.Collections.Generic.Dictionary<ObjectCtrl, GuideObject>>();
                if (dic == null || !dic.TryGetValue(first, out var go) || go == null || go.guides == null) return;
                int mode = GuideObjectManager.GetMode();
                if (mode != MODE_MOVE && mode != MODE_ROTATION) return;
                // 按轴枚举匹配,避开 guide[] 子顺序假设
                GuideBase target = null;
                int axisEnum = (axis == "X") ? 0 : (axis == "Y") ? 1 : (axis == "Z") ? 2 : -1;
                if (axisEnum < 0) return;
                foreach (var gb in go.guides)
                {
                    if (gb == null || gb.isDrag) continue;
                    if (mode == MODE_MOVE && gb is GuideMove m && (int)m.axis == axisEnum) { target = gb; break; }
                    if (mode == MODE_ROTATION && gb is GuideRotation r && (int)r.axis == axisEnum) { target = gb; break; }
                }
                if (target == null) return;
                var t = Traverse.Create(target);
                var color = on ? t.Field("colorHighlighted").GetValue<Color>()
                               : t.Field("colorNormal").GetValue<Color>();
                t.Property("colorNow").SetValue(color);
            }
            catch (System.Exception e)
            {
                Log.LogInfo($"[highlight] {axis} on={on} 异常静默跳过:{e.Message}");
            }
        }

        // 两路锁:PoseCreate 有 GameCursor 单例走原生 Win32 锁(visible+复位到进入位置,零回归);
        // hpart 下 GameCursor 单例不加载(IsInstance false,日志确证),走 Unity 原生 Locked + Win32 自记屏幕坐标复位。
        private static int _hpartCursorX, _hpartCursorY;
        private static bool _hpartCursorSaved;
        private static void LockCursor()
        {
            if (Singleton<GameCursor>.IsInstance() && Singleton<GameCursor>.Instance != null)
            {
                var gc = Singleton<GameCursor>.Instance;
                if (GameCursor.isLock) gc.SetCursorLock(false);   // 残留先解锁,强制走完整锁路径
                gc.SetCursorLock(true);
            }
            else
            {
                // hpart 无 GameCursor: 自记屏幕坐标 → Unity 原生锁(locked 锁视口中心并吞原坐标)
                POINT p;
                if (GetCursorPos(out p)) { _hpartCursorX = p.X; _hpartCursorY = p.Y; _hpartCursorSaved = true; }
                else _hpartCursorSaved = false;
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        private static void UnlockCursorIfLocked()
        {
            if (Singleton<GameCursor>.IsInstance() && Singleton<GameCursor>.Instance != null)
            {
                Singleton<GameCursor>.Instance.SetCursorLock(false);
            }
            else
            {
                // hpart 无 GameCursor: Unity 原生解锁 + 复回进入锁前屏幕坐标(否侧 Locked 把光标留在视口中心)
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                if (_hpartCursorSaved) SetCursorPos(_hpartCursorX, _hpartCursorY);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void SetCursorPos(int x, int y);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        // crl=档1(最低),shift=档4(最高),都不按=手动设定档。临时覆盖不写 cfg(不持久化),松开回手动档值。
        private static float CurrentTransSens()
        {
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return _transSteps[0];
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return _transSteps[3];
            return _manualTransSens;
        }
        private static float CurrentRotSens()
        {
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return _rotSteps[0];
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return _rotSteps[3];
            return _manualRotSens;
        }

        // 每帧 apply:Mouse Y 自由度,按 mode 分支调 Manipulator。空集不调 UpdateUI。
        private static void ApplyAxis(string axis)
        {
            var targets = GetGuideTargets();
            if (targets.Length == 0) return;

            float dy = Input.GetAxis("Mouse Y");
            if (dy == 0f) return;

            int mode = GuideObjectManager.GetMode();
            switch (mode)
            {
                case MODE_MOVE:
                    // Local 使用按下瞬间锁定的目标轴向；World 使用固定世界分量。
                    if (Instance != null && Instance._cfgAxisMode.Value == AxisMode.World)
                        Manipulator.ApplyTransManyWorld(axis, targets, dy, CurrentTransSens());
                    else
                        Manipulator.ApplyTransManyBone(axis, targets, dy, CurrentTransSens(),
                            Instance._localAxisDir, Instance._localAxisDirValid);
                    break;
                case MODE_ROTATION:
                    Manipulator.ApplyRotMany(axis, targets, dy, CurrentRotSens()); break;
                default: return;
            }
            GuideObjectManager.UpdateUI();
        }


        internal void OnGUI()
        {
            if (!_windowVisible) return;
            if (!IsManipulatorAvailable()) return;

            SyncMainWindowRect();

            Rect before = _windowRect;
            _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "EC HEdit Pose Panel");
            _windowRect = WindowPlacement.ClampToScreen(_windowRect, Screen.width, Screen.height);
            TrackMainWindowPosition(before);

            if (IsInPoseEdit()) _companionWindow?.Draw(_windowRect);
        }

        private const int WindowId = 0x4F50;

        private void DrawWindow(int id)
        {
            int mode = GuideObjectManager.GetMode();
            // 当前模式行:左显模式,右挂 L/W 轴系切换键(22×22,与 ◀ 同尺寸);当前模式低亮,对侧高亮可点切换。快捷键 G 亦可切。
            GUILayout.BeginHorizontal();
            GUILayout.Label($"当前模式: {ModeToName(mode)}");
            GUILayout.FlexibleSpace();
            DrawAxisModeButton();
            if (IsInPoseEdit()) _companionWindow?.DrawExpandButton();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            DrawAxisButton("X");
            DrawAxisButton("Y");
            DrawAxisButton("Z");
            GUILayout.EndHorizontal();

            DrawSensSlider("Trans", _cfgTrans, _transSteps, ref _manualTransSens);
            DrawSensSlider("Rot", _cfgRot, _rotSteps, ref _manualRotSens);

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void SyncMainWindowRect()
        {
            bool screenChanged = _windowScreenWidth != Screen.width || _windowScreenHeight != Screen.height;
            if (_rectSynced && !screenChanged) return;

            _windowScreenWidth = Screen.width;
            _windowScreenHeight = Screen.height;
            _windowRect = WindowPlacement.FromNormalized(
                _cfgWindowPositionX.Value,
                _cfgWindowPositionY.Value,
                WinWidth,
                WinHeight,
                Screen.width,
                Screen.height);
            _rectSynced = true;
        }

        private void TrackMainWindowPosition(Rect before)
        {
            if (Input.GetMouseButton(0) && WindowPlacement.PositionChanged(before, _windowRect))
                _windowPositionDirty = true;
            if (Input.GetMouseButton(0) || !_windowPositionDirty) return;

            Vector2 normalized = WindowPlacement.ToNormalized(_windowRect, Screen.width, Screen.height);
            _cfgWindowPositionX.Value = normalized.x;
            _cfgWindowPositionY.Value = normalized.y;
            _windowPositionDirty = false;
        }

        // L/W 轴系切换键:显示当前模式字符(Local→L / World→W),点击翻转 _cfgAxisMode(SettingChanged 自动同步 calcAxis)。
        private void DrawAxisModeButton()
        {
            bool isWorld = _cfgAxisMode.Value == AxisMode.World;
            string label = isWorld ? "W" : "L";
            string tooltip = $"轴系: {(isWorld ? "World" : "Local")} — 点击或按 {_cfgAxisModeKey.Value} 切换";
            var content = new GUIContent(label, tooltip);
            if (GUILayout.Button(content, GUILayout.Width(22f), GUILayout.Height(22f)))
                ToggleAxisMode();
        }

        // 灵敏度 4 档离散调节:◀ [刻度条] ▶。刻度自绘,◀▶ 为普通 Button(单次,去连击)。
        // 档位锚点表 stepValues(非线性);ConfigEntry 为真值源(面板不显数值,外部重置生效)。
        private void DrawSensSlider(string label, ConfigEntry<float> cfg, float[] stepValues, ref float manipField)
        {
            int STEPS = stepValues.Length;

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(40f));

            int curStep = ValueToStep(cfg.Value, stepValues);

            Rect bar = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(22f), GUILayout.ExpandWidth(true));
            DrawStepBar(bar, curStep, STEPS);
            // 点击刻度位置跳档(MouseDown 单次事件 + Use 防多帧重复)。
            if (Event.current != null && Event.current.type == EventType.MouseDown && bar.Contains(Event.current.mousePosition))
            {
                int hit = Mathf.Clamp(Mathf.RoundToInt((Event.current.mousePosition.x - bar.x) / bar.width * (STEPS - 1)) + 1, 1, STEPS);
                if (hit != curStep) SetStep(cfg, hit, stepValues, ref manipField);
                Event.current.Use();
            }

            // ◀▶ 紧挨在刻度右侧(Space 8 隔离,边界档各自灰禁)。
            GUILayout.Space(8f);
            GUI.enabled = curStep > 1;
            if (GUILayout.Button("◀", GUILayout.Width(22f), GUILayout.Height(22f)) && curStep > 1)
                SetStep(cfg, curStep - 1, stepValues, ref manipField);
            GUI.enabled = curStep < STEPS;
            if (GUILayout.Button("▶", GUILayout.Width(22f), GUILayout.Height(22f)) && curStep < STEPS)
                SetStep(cfg, curStep + 1, stepValues, ref manipField);
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }

        // 背景槽 + N 根均匀竖线,当前档 3px 粗浅绿高亮,其余 2px 灰细线。
        private static void DrawStepBar(Rect bar, int curStep, int steps)
        {
            Rect slot = new Rect(bar.x, bar.y + bar.height / 2f - 1f, bar.width, 2f);
            DrawTexture(slot, new Color(0.4f, 0.4f, 0.4f, 1f));

            for (int i = 1; i <= steps; i++)
            {
                float cx = bar.x + (i - 1) * bar.width / (steps - 1);
                bool cur = (i == curStep);
                float w = cur ? 3f : 2f;
                float h = cur ? bar.height : bar.height * 0.6f;
                Rect tick = new Rect(cx - w / 2f, bar.y + (bar.height - h) / 2f, w, h);
                DrawTexture(tick, cur ? new Color(0.4f, 1f, 0.4f, 1f) : new Color(0.8f, 0.8f, 0.8f, 1f));
            }
        }

        private static void DrawTexture(Rect r, Color c)
        {
            Color oc = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = oc;
        }

        // 档位换算:值→档(最近锚点距离),档→值(查表)。
        private static int ValueToStep(float value, float[] stepValues)
        {
            int best = 1; float bestD = float.MaxValue;
            for (int i = 0; i < stepValues.Length; i++)
            {
                float d = Mathf.Abs(value - stepValues[i]);
                if (d < bestD) { bestD = d; best = i + 1; }
            }
            return best;
        }

        private static void SetStep(ConfigEntry<float> cfg, int step, float[] stepValues, ref float manipField)
        {
            float v = stepValues[Mathf.Clamp(step, 1, stepValues.Length) - 1];
            cfg.Value = v;
            manipField = v;
        }

        // Awake 启动吸档:旧 cfg 值不在锚点上时 round 到最近档。
        private static void SnapCfgToStep(ConfigEntry<float> cfg, float[] stepValues, ref float manipField)
        {
            int s = ValueToStep(cfg.Value, stepValues);
            float v = stepValues[s - 1];
            if (!Mathf.Approximately(v, cfg.Value))
            {
                cfg.Value = v;
                manipField = v;
            }
        }

        // RepeatButton 按压首帧边沿 → 锁定活跃轴(Mouse 来源)+ SetCursorLock 一次。
        private void DrawAxisButton(string axis)
        {
            if (!GUILayout.RepeatButton(axis, GUILayout.Height(40f), GUILayout.MinWidth(60f))) return;
            if (_activeAxis != null) return;
            BeginAxis(axis, AxisSource.Mouse, KeyCode.None);
        }

        private static string ModeToName(int mode)
        {
            switch (mode)
            {
                case MODE_MOVE: return "Move";
                case MODE_ROTATION: return "Rotation";
                case MODE_SCALE: return "Scale";
                default: return $"<未知:{mode}>";
            }
        }

        internal static bool HasActiveGuideTarget()
        {
            if (!Singleton<GuideObjectManager>.IsInstance()) return false;
            return GetGuideTargets().Any(oc =>
            {
                var guide = oc.guideObject;
                return guide != null && guide.isActive
                    && guide.gameObject != null && guide.gameObject.activeInHierarchy;
            });
        }

        internal static bool IsManipulatorAvailable()
            => HasActiveGuideTarget() || IsInPoseEdit() || IsInHEditIK();

        internal static bool IsInPoseEdit()
        {
            if (!Singleton<Scene>.IsInstance()) return false;
            var scene = Singleton<Scene>.Instance;
            if (scene == null) return false;
            return scene.AddSceneName == "PoseCreate";
        }

        // HEdit 主场景 IK 调态路径:LoadSceneName==HEditScene 且未叠加 PoseCreate(叠加时 PoseCreate 路径优先,互斥)。
        // 真机实测:HEditScene 是大类,仅判 LoadSceneName 节点编辑等子态也 true → 窗口常显。
        // 真判定 = HEditGlobal.objectCategoryBehaviour 里 MotionSetting(lstObj[3]) 或 PartInfoSetting(lstObj[6]) active(=动作设置态)。
        internal static bool IsInHEditIK()
        {
            if (!Singleton<Scene>.IsInstance()) return false;
            var sc = Singleton<Scene>.Instance;
            if (sc == null || sc.LoadSceneName != "HEditScene") return false;
            if (sc.AddSceneName == "PoseCreate") return false;
            if (sc.IsNowLoading || sc.IsNowLoadingFade) return false;   // 加载/淡入途不判(实测避免 MotionSetting 先激活又切走导致窗口闪现一帧)
            if (!Singleton<HEdit.HEditGlobal>.IsInstance()) return false;
            var hg = Singleton<HEdit.HEditGlobal>.Instance;
            if (hg == null || hg.objectCategoryBehaviour == null) return false;
            return hg.objectCategoryBehaviour.GetActive(3) || hg.objectCategoryBehaviour.GetActive(6);
        }
    }
}
