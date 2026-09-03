using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 阴影颜色反算工具窗（IMGUI，仅捏人界面）。
    ///
    /// 原理：overlay 是乘算层，阴影区最终色 = 基底色 P × lerp(白, C.rgb, C.a)。
    /// 初值用 linear 域比值 m = linear(S)/linear(P) 分解（alpha = 1-min(m)、
    /// C = (m-min(m))/(1-min(m))，恒在 [0,1]）。
    ///
    /// 实测发现管线存在非线性环节（后处理/tonemap）：一步解析解无法精确命中
    /// （实测 V 反解的有效乘子在 G/B 通道给出不一致的强度）。故提供迭代校准：
    /// 填入上轮结果后用 V 槽吸"实际呈现"，修正 m ← m × linear(S)/linear(V)，
    /// 重新分解打日志，2~3 轮收敛；不依赖任何管线模型假设。
    ///
    /// 工作流：ME 把 Enable 拉 0 取 S → 拉回 1 取 P → 日志填 ME →
    /// （校准轮）V 槽吸当前阴影处 → 校准 → 新日志再填 ME → 重复至满意。
    /// 取色 5x5 均值抗噪。ReadPixels 必须在 WaitForEndOfFrame 协程里执行：
    /// IMGUI 事件阶段调用会报 "not inside drawing frame" 且读回值不可靠。
    /// IMGUI 契约见 docs/ec-knowledge/03-patterns/imgui-window-pattern.md。
    /// </summary>
    public class ShadowColorMixer : MonoBehaviour
    {
        private const int WindowId = 0x46534443;   // "FSDC"
        private const float StatusExpirySeconds = 8f;
        private const int SampleSize = 5;

        private bool _show;
        private Rect _windowRect = new Rect(160f, 160f, 380f, 10f);
        private Color _slotS = Color.white;
        private Color _slotP = Color.white;
        private Color _slotV = Color.white;
        private bool _hasS;
        private bool _hasP;
        private bool _hasV;
        private Color _lastMult = Color.white;     // 上轮日志对应的 linear 域乘子
        private int _solveRound;                    // 校准轮次（0=初值）
        private bool _vConsumed;                    // V 已被校准消费，重取前不得复用
        private int _pickTarget;                    // 0=无 1=取S 2=取P 3=取V
        private Texture2D _readTex;
        private Color _previewColor = Color.white;
        private string _status = "";
        private float _statusClearAt;
        // OnGUI 点击只登记坐标，实际 ReadPixels 由帧末协程执行（见类注释）
        private Vector2 _pendingPos;
        private int _pendingSlot;

        // ---- ADV 光强对齐 ----
        // 捏人 direct light（CvsDrawCtrl.objLight）固定 intensity=1，advscene 常用 0.9，
        // 取色环境偏亮 → 反算色偏亮、与 advscene 实际呈现对不上。勾选临时压到 0.9，
        // 取消/关窗/场景切出必须还原——玩家的光绝不能被永久改掉。
        private const float AdvAlignedIntensity = 0.9f;
        private bool _alignAdvLight;
        private bool _lightMissing;       // 勾选沿探测失败 → Toggle 禁用显示；场景切换复位重试
        private Light _makerLight;
        private float _makerLightOriginal;
        private static FieldInfo _objLightField;

        internal static ShadowColorMixer Create()
        {
            var go = new GameObject("EC_FaceSDFShadow_ShadowColorMixer");
            DontDestroyOnLoad(go);
            return go.AddComponent<ShadowColorMixer>();
        }

        internal void Awake()
        {
            // 场景事件 + 初始状态同步（IMGUI 模式坑：只听未来事件会漏当前已加载场景）
            SceneManager.sceneLoaded += OnSceneChanged;
            SceneManager.sceneUnloaded += OnSceneChanged;
            enabled = IsCustomScene();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneChanged;
            SceneManager.sceneUnloaded -= OnSceneChanged;
            ApplyAlignToggle(false);   // 卸载兜底还原（幂等，OnDisable 已还原过）
            if (_readTex != null) Destroy(_readTex);
        }

        private void OnSceneChanged(Scene scene, LoadSceneMode mode) => enabled = IsCustomScene();
        private void OnSceneChanged(Scene scene) => enabled = IsCustomScene();

        // GameObject 不销毁（DontDestroyOnLoad），协程不因 enabled=false 停，
        // 场景切出时显式关窗退吸管，防止协程空转与窗口在别的场景残留
        private void OnDisable()
        {
            _show = false;
            _pickTarget = 0;
            _pendingSlot = 0;
            ApplyAlignToggle(false);   // 场景切出必须还原光强
            _lightMissing = false;     // 下个场景允许重新探测
        }

        private static bool IsCustomScene()
        {
            // CustomScene 是全局命名空间类（BaseLoader 子类），捏人场景入口
            return FindObjectOfType<CustomScene>() != null;
        }

        private void Update()
        {
            // IMGUI 模式坑：KeyboardShortcut.IsDown() 在 Map 加载后失效，用 Input.GetKeyDown
            var key = FaceSDFShadowPlugin.ColorMixerKey.Value;
            if (key != KeyCode.None && Input.GetKeyDown(key))
            {
                _show = !_show;
                if (!_show) ApplyAlignToggle(false);   // 关窗还原，光强不跨窗口会话
            }
        }

        private void OnGUI()
        {
            if (_pickTarget != 0)
            {
                DrawPickingOverlay();
                return;
            }
            if (!_show) return;

            if (_status.Length > 0 && Time.realtimeSinceStartup > _statusClearAt)
                _status = "";
            _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "FaceSDFShadow 阴影取色器");
        }

        // ---- 采样协程 ----

        private IEnumerator PickLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (_pickTarget != 0)
            {
                yield return wait;
                if (_pendingSlot != 0)
                {
                    var picked = SampleAverage(_pendingPos);
                    // 诊断：取色器曾出现"V-S 差 20+ 仍判收敛"的状态错乱，
                    // 每次取色把吸到的原始值落日志，与窗口显示互相对照
                    FaceSDFShadowPlugin.Log.LogInfo(string.Format(
                        "[ColorMixer] picked slot {0}: ({1},{2},{3})",
                        _pendingSlot == 1 ? "S" : _pendingSlot == 2 ? "P" : "V",
                        (int)(picked.r * 255), (int)(picked.g * 255), (int)(picked.b * 255)));
                    if (_pendingSlot == 1)
                    {
                        _slotS = picked;
                        _hasS = true;
                        SetStatus("S 已取");
                        if (_hasP) LogResult();      // 目标变了 → 重算初值
                    }
                    else if (_pendingSlot == 2)
                    {
                        _slotP = picked;
                        _hasP = true;
                        SetStatus("P 已取");
                        if (_hasS) LogResult();
                    }
                    else
                    {
                        _slotV = picked;
                        _hasV = true;
                        _vConsumed = false;
                        // V 只用于校准修正；没有初值时先攒着等 S/P
                        if (_solveRound > 0) Calibrate();
                        else SetStatus("V 已取（先取 S/P 出初值）");
                    }
                    _pendingSlot = 0;
                    _pickTarget = 0;
                }
                else
                {
                    _previewColor = SampleAverage(Input.mousePosition);
                }
            }
        }

        /// <summary>屏幕像素坐标处 5x5 均值采样。仅在 WaitForEndOfFrame 后调用。</summary>
        private Color SampleAverage(Vector2 screenPos)
        {
            if (_readTex == null)
                _readTex = new Texture2D(SampleSize, SampleSize, TextureFormat.ARGB32, false);

            float x = Mathf.Clamp(screenPos.x - SampleSize * 0.5f, 0f, Screen.width - SampleSize);
            float y = Mathf.Clamp(screenPos.y - SampleSize * 0.5f, 0f, Screen.height - SampleSize);
            _readTex.ReadPixels(new Rect(x, y, SampleSize, SampleSize), 0, 0, false);

            var px = _readTex.GetPixels();
            var sum = new Color();
            for (int i = 0; i < px.Length; i++)
                sum += px[i];
            return sum / px.Length;
        }

        // ---- 取色覆盖层 ----

        private void DrawPickingOverlay()
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // 只登记，采样在帧末协程做（ReadPixels 时机契约）
                _pendingPos = Input.mousePosition;
                _pendingSlot = _pickTarget;
                e.Use();
                return;
            }
            if ((e.type == EventType.MouseDown && e.button == 1) ||
                (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape))
            {
                _pickTarget = 0;
                e.Use();
                return;
            }
            if (e.type != EventType.Repaint) return;

            var mouse = Input.mousePosition;
            var box = new Rect(mouse.x - 22f, Screen.height - mouse.y - 50f, 44f, 44f);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.Box(box, GUIContent.none);
            GUI.color = _previewColor;
            GUI.Box(new Rect(box.x + 4f, box.y + 4f, 36f, 36f), GUIContent.none);
            GUI.color = prev;

            var label = new Rect(
                Mathf.Clamp(box.x - 80f, 4f, Screen.width - 208f), box.yMax + 4f, 204f, 40f);
            GUI.Label(label, "左键取色 / 右键或 Esc 取消\n" +
                (_pickTarget == 1 ? "正在取 S：点皮肤阴影处"
                : _pickTarget == 2 ? "正在取 P：点同一皮肤亮部"
                : "正在取 V：点当前阴影处"));
        }

        // ---- 主窗口 ----

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Space(4f);

            GUILayout.Label("1. ME 把FaceSDFOverlay的 Enable 拉到 0 取期望阴影色 S");
            GUILayout.Label("2. Enable 拉回 1 并取受光肤色 P");
            GUILayout.Label("3. 点应用后 V 取当前阴影色,校准,应用.重复3直到颜色匹配");
            DrawSlot("S 目标阴影色(原版)", _slotS, _hasS, 1);
            DrawSlot("P 基底肤色(受光区)", _slotP, _hasP, 2);
            DrawSlot("V 实际呈现(校准用)", _slotV, _hasV, 3);

            GUILayout.Space(6f);
            DrawResult();

            GUILayout.Space(6f);
            DrawAlignLightToggle();

            GUILayout.Space(6f);
            if (GUILayout.Button("恢复跟随全局（清除本卡定制）"))
                ResetCustomization();

            GUILayout.Space(4f);
            GUILayout.Label(_status, GUILayout.Height(20f));
            GUILayout.Space(2f);
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        // ---- ADV 光强对齐 ----

        /// <summary>
        /// 解锁 ME 定制锁存：清当前角色三项（颜色/边界/软度）定制、推回全局值。
        /// 取色器是唯一解锁入口——ME 改过即锁存、无其它 UI 可解。
        /// </summary>
        private void ResetCustomization()
        {
            int n = FaceOverlayCore.ResetCustomizationAll();
            SetStatus(n > 0
                ? $"已恢复 {n} 个角色跟随全局（保存角色卡后生效）"
                : "当前场景没有可重置的角色");
        }

        private void DrawAlignLightToggle()
        {
            // 探测失败后禁用显示（勾选沿才 Find，失败不抛错）；场景切换复位
            var prevEnabled = GUI.enabled;
            GUI.enabled = !_lightMissing;
            bool newVal = GUILayout.Toggle(_alignAdvLight, "对齐 ADV 光强（0.9）");
            GUI.enabled = prevEnabled;
            // 变化沿检测用返回值比对（GUI.changed 会被其它控件污染）
            if (newVal != _alignAdvLight)
                ApplyAlignToggle(newVal);
            GUILayout.Label("勾上仅影响取色环境，取消勾选/关窗还原", GUILayout.Height(18f));
        }

        private void ApplyAlignToggle(bool on)
        {
            if (on)
            {
                var light = FindMakerLight();
                if (light == null)
                {
                    _lightMissing = true;
                    SetStatus("未找到捏人光源，无法对齐");
                    FaceSDFShadowPlugin.Log.LogWarning(
                        "[ColorMixer] align ADV light: CvsDrawCtrl/objLight/Light not found");
                    return;
                }
                // 每次勾选重新缓存：还原时清空，避免跨场景持有 stale 引用还原错对象
                _makerLight = light;
                _makerLightOriginal = light.intensity;
                light.intensity = AdvAlignedIntensity;
                _alignAdvLight = true;
                SetStatus($"光强已设 {AdvAlignedIntensity:0.0}，取消勾选还原");
            }
            else
            {
                // Unity fake null：light 随场景销毁时跳过写入，只清缓存
                if (_makerLight != null)
                    _makerLight.intensity = _makerLightOriginal;
                _makerLight = null;
                _alignAdvLight = false;
            }
        }

        /// <summary>
        /// 取捏人界面的 direct light：场景序列化连接在 CvsDrawCtrl.objLight
        /// （私有 GameObject，正是两个光照滑条控制的那个）。场景多光源，
        /// 禁止 FindObjectsOfType 猜对象。只在勾选变化沿调用（低频），不进 OnGUI 每帧。
        /// </summary>
        private static Light FindMakerLight()
        {
            var draw = FindObjectOfType<ChaCustom.CvsDrawCtrl>();
            if (draw == null) return null;
            if (_objLightField == null)
                _objLightField = typeof(ChaCustom.CvsDrawCtrl).GetField("objLight",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            var lightObj = _objLightField != null
                ? _objLightField.GetValue(draw) as GameObject
                : null;
            return lightObj != null ? lightObj.GetComponent<Light>() : null;
        }

        private void DrawSlot(string label, Color slot, bool has, int target)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(22f));
            GUILayout.Label(label, GUILayout.Width(178f));
            var rect = GUILayoutUtility.GetRect(34f, 20f);
            var prev = GUI.color;
            GUI.color = slot;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Label(has
                ? $"{(int)(slot.r * 255)},{(int)(slot.g * 255)},{(int)(slot.b * 255)}"
                : "未取", GUILayout.Width(84f));
            if (GUILayout.Button("取色", GUILayout.Width(48f)))
            {
                _pickTarget = target;
                _pendingSlot = 0;
                StartCoroutine(PickLoop());
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>linear 域通道比值。p 近黑返回 1（无信息）。
        /// 不钳上界：校准修正比 corr = S/V 合法地 >1（往浅修的方向），
        /// Clamp01 会把全部修正钳死（实测每轮输出原样重复的根因）。</summary>
        private static float RatioLinear(float s, float p)
        {
            float ls = Mathf.GammaToLinearSpace(Mathf.Clamp01(s));
            float lp = Mathf.GammaToLinearSpace(Mathf.Clamp01(p));
            return lp > 1e-6f ? ls / lp : 1f;
        }

        /// <summary>linear 域比值（供初值/校准）。返回值可能 &gt;1，调用方按需钳域。</summary>
        private static Color RatioLinearRgb(Color s, Color p)
        {
            return new Color(
                RatioLinear(s.r, p.r),
                RatioLinear(s.g, p.g),
                RatioLinear(s.b, p.b));
        }

        /// <summary>乘子域约束：每通道钳 [0,1]（乘算只能压暗）。</summary>
        private static Color ClampMult(Color m)
        {
            return new Color(
                Mathf.Clamp01(m.r),
                Mathf.Clamp01(m.g),
                Mathf.Clamp01(m.b));
        }

        /// <summary>把乘子分解为 (C.rgb, alpha) 规范解：alpha = 1-min(m)、C = (m-min)/alpha。</summary>
        private static bool Decompose(Color m, out Color result)
        {
            float min = Mathf.Min(m.r, Mathf.Min(m.g, m.b));
            float alpha = 1f - min;
            if (alpha < 1e-3f)
            {
                result = Color.white;
                return false;   // m≈1：无阴影差
            }
            result = new Color((m.r - min) / alpha, (m.g - min) / alpha, (m.b - min) / alpha, alpha);
            return true;
        }

        /// <summary>记录本轮乘子并打日志。</summary>
        private void SolveFromMult(Color m)
        {
            _lastMult = m;
            _solveRound++;
            if (!Decompose(m, out var c))
            {
                FaceSDFShadowPlugin.Log.LogWarning(
                    "[ColorMixer] 乘子全为 1：无阴影差，本轮无输出。");
                SetStatus("无阴影差，未更新");
                return;
            }
            // ME 填的是 0-1 float（RGB 255 只在游戏调色板），float 值优先给足精度
            FaceSDFShadowPlugin.Log.LogInfo(
                $"[ColorMixer] 第 {_solveRound} 轮结果：Color(0-1) = " +
                $"({c.r:0.000000}, {c.g:0.000000}, {c.b:0.000000}, {c.a:0.000000})；" +
                $"RGBA(255) = {(int)(c.r * 255)},{(int)(c.g * 255)},{(int)(c.b * 255)},{(int)(c.a * 255)}");
            SetStatus($"第 {_solveRound} 轮已出，可应用或复制日志");
        }

        private void DrawResult()
        {
            if (!(_hasS && _hasP))
            {
                GUILayout.Label("取完 S 和 P 自动计算并打日志", GUILayout.Height(20f));
                return;
            }

            if (!Decompose(_lastMult, out var c))
            {
                GUILayout.Label("S 与 P 几乎相同：无阴影差可反算", GUILayout.Height(20f));
                return;
            }

            GUILayout.Label(
                $"第 {_solveRound} 轮 ShadowColor = ({c.r:0.000}, {c.g:0.000}, {c.b:0.000}, {c.a:0.000})",
                GUILayout.Height(20f));
            if (_slotS.r > _slotP.r + 1e-3f || _slotS.g > _slotP.g + 1e-3f || _slotS.b > _slotP.b + 1e-3f)
                GUILayout.Label("注意：S 某通道比 P 亮，检查取色点", GUILayout.Height(20f));

            // 预览条：左=P（基底亮），右=阴影效果。显示 sRGB：linear 域乘后转回
            var shadowPreview = new Color(
                Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(_slotP.r) * _lastMult.r),
                Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(_slotP.g) * _lastMult.g),
                Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(_slotP.b) * _lastMult.b));
            var bar = GUILayoutUtility.GetRect(340f, 18f);
            var prev = GUI.color;
            GUI.color = _slotP;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * 0.5f, bar.height), Texture2D.whiteTexture);
            GUI.color = shadowPreview;
            GUI.DrawTexture(new Rect(bar.x + bar.width * 0.5f, bar.y, bar.width * 0.5f, bar.height),
                Texture2D.whiteTexture);
            GUI.color = prev;

            // 校准：需要先有初值（_solveRound>0）且取过 V（当前实际呈现）
            if (_solveRound == 0)
            {
                GUILayout.Label("先填入日志结果，再取 V 校准", GUILayout.Height(20f));
                return;
            }
            if (_hasV)
            {
                GUILayout.Label(string.Format(
                    "V-S 差(255)：{0},{1},{2}",
                    (int)(_slotV.r * 255) - (int)(_slotS.r * 255),
                    (int)(_slotV.g * 255) - (int)(_slotS.g * 255),
                    (int)(_slotV.b * 255) - (int)(_slotS.b * 255)), GUILayout.Height(20f));
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("校准修正", GUILayout.Height(22f)))
                Calibrate();
            GUILayout.Label(
                !_hasV ? "未取 V" : _vConsumed ? "V 已消费，请重取" : "V 可用",
                GUILayout.Width(110f));
            GUILayout.EndHorizontal();

            // 写材质（ME 真源同步）：手填 alpha 在 ME 里是百分比域，255 制数值无处填，
            // 直接应用是唯一可靠路径；效果不满意可继续取 V 校准
            if (GUILayout.Button("应用到当前捏人角色", GUILayout.Height(24f)))
            {
                int applied = FaceOverlayCore.ApplyShadowColorAll(c);
                SetStatus(applied > 0 ? $"已写入 {applied} 个角色，效果即所见"
                    : "没有已挂 overlay 的角色");
            }
        }

        /// <summary>
        /// 迭代校准：m ← m × linear(S)/linear(V)。把实测偏差乘回去，
        /// 不依赖管线模型（后处理/tonemap 的非线性由逐轮逼近吸收）。
        /// 收敛（修正比≈1）时不再发新轮次，明确告知用户已完成。
        /// </summary>
        private void Calibrate()
        {
            if (!_hasV)
            {
                SetStatus("先取 V");
                return;
            }
            if (_vConsumed)
            {
                SetStatus("V 已用于校准，请重取 V");
                return;
            }

            var corr = RatioLinearRgb(_slotS, _slotV);
            _vConsumed = true;
            // 诊断：参与运算的内部值全落日志（含 round 与修正比）。
            // 实测出现过"V-S 差 20+ 仍判收敛"的状态错乱，靠这行定位哪一步的值不对
            FaceSDFShadowPlugin.Log.LogInfo(string.Format(
                "[ColorMixer] calibrate round={0} S=({1},{2},{3}) V=({4},{5},{6}) corr=({7:0.000},{8:0.000},{9:0.000})",
                _solveRound,
                (int)(_slotS.r * 255), (int)(_slotS.g * 255), (int)(_slotS.b * 255),
                (int)(_slotV.r * 255), (int)(_slotV.g * 255), (int)(_slotV.b * 255),
                corr.r, corr.g, corr.b));

            // 收敛判据用 V-S 的 255 制直差：与肉眼判断同一标准
            float maxDev255 = Mathf.Max(
                Mathf.Abs(_slotV.r - _slotS.r),
                Mathf.Max(Mathf.Abs(_slotV.g - _slotS.g), Mathf.Abs(_slotV.b - _slotS.b))) * 255f;
            if (maxDev255 < 1.5f)
            {
                FaceSDFShadowPlugin.Log.LogInfo(
                    $"[ColorMixer] 已收敛：V-S 最大差 {maxDev255:0.0}/255，当前结果即最终值。");
                SetStatus("已收敛：V 与 S 一致");
                return;
            }

            var m = ClampMult(new Color(
                _lastMult.r * corr.r,
                _lastMult.g * corr.g,
                _lastMult.b * corr.b));
            SolveFromMult(m);
        }

        /// <summary>S/P 任一更新后的初值重算：清掉陈旧 V 与轮次，避免旧实测混进新目标。
        /// 初值乘子钳 [0,1]；S 某通道比 P 亮（高光/反光）时该通道无阴影差。</summary>
        private void LogResult()
        {
            _slotV = Color.white;
            _hasV = false;
            _solveRound = 0;
            var m = ClampMult(RatioLinearRgb(_slotS, _slotP));
            SolveFromMult(m);
        }

        private void SetStatus(string text)
        {
            _status = text;
            _statusClearAt = Time.realtimeSinceStartup + StatusExpirySeconds;
        }
    }
}
