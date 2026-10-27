using System;
using System.Collections.Generic;
using System.Reflection;
using ADV;
using BepInEx.Logging;
using HarmonyLib;
using HEdit;
using HPlay;
using Manager;
using UnityEngine;

namespace EC_BackVarSync
{
    /// <summary>
    /// HPlay 前进控制器：自定义按键前进 + 自动前进。
    ///
    /// 前进必须走原生 ADVPlay.OnClick：VariableMod 的 event-next/goto-cut 等挂在它的 Prefix 上，
    /// 直接调 NextCut 会让这些脚本静默不执行。OnClick 解析不到时才退回复刻分支。
    ///
    /// 守卫自构而不调 Check()：Check() 含「文本隐藏点击」(TextHideClick)条件，游戏默认关闭，
    /// 原生点击会因文本未隐藏而无限期卡住；本控制器只在出现选项分支(isSelect)时停下。
    /// </summary>
    internal sealed class HPlayAdvanceManager
    {
        private readonly ManualLogSource _log;
        private readonly Func<KeyCode> _getAdvanceKey;
        private readonly Func<KeyCode> _getToggleKey;
        private readonly Func<bool> _getAutoEnabled;
        private readonly Func<float> _getAutoInterval;
        private readonly Action<bool> _setAutoEnabled;

        private readonly MethodInfo _onClick;
        private float _autoAccumulator;
        private readonly List<TypefaceAnimatorEx> _textAnimations = new List<TypefaceAnimatorEx>();

        public HPlayAdvanceManager(
            ManualLogSource log,
            Func<KeyCode> getAdvanceKey,
            Func<KeyCode> getToggleKey,
            Func<bool> getAutoEnabled,
            Func<float> getAutoInterval,
            Action<bool> setAutoEnabled)
        {
            _log = log;
            _getAdvanceKey = getAdvanceKey;
            _getToggleKey = getToggleKey;
            _getAutoEnabled = getAutoEnabled;
            _getAutoInterval = getAutoInterval;
            _setAutoEnabled = setAutoEnabled;

            _onClick = AccessTools.Method(typeof(ADVPlay), "OnClick", Type.EmptyTypes);
            if (_onClick == null)
                _log.LogWarning("未找到 ADVPlay.OnClick，前进键改用复刻分支：VariableMod 的 event-next 跳转不会执行。");
        }

        /// <summary>由 HPlayScene.Update 每帧调用。</summary>
        public void Tick()
        {
            // 切换自动前进的快捷键优先处理（不限当前是否处于可前进状态）
            HandleToggleKey();

            // 任意键打断自动前进：按下任意非「功能键」之外的真实输入即关，
            // 不响应 advance key / toggle key 自身，否则每次前进都会自己关自己。
            if (Input.anyKeyDown && _getAutoEnabled())
            {
                bool pressedFunctional = Input.GetKeyDown(_getAdvanceKey()) || Input.GetKeyDown(_getToggleKey());
                if (!pressedFunctional)
                {
                    _setAutoEnabled(false);
                    _log.LogMessage("自动前进: 关（按键打断）");
                }
            }

            if (!CanAdvanceNow())
                return;

            bool keyPressed = Input.GetKeyDown(_getAdvanceKey());
            bool autoDue = false;

            if (keyPressed)
            {
                Advance();
                return;
            }

            if (_getAutoEnabled())
            {
                float interval = _getAutoInterval();
                if (interval > 0f)
                {
                    autoDue = TickAutoDelay(interval);
                }
            }
            else
            {
                _autoAccumulator = 0f;
            }

            if (autoDue)
                Advance();
        }

        /// <summary>场景/PART 切换时重置计时，避免切换瞬间残留计时误触。</summary>
        public void Reset()
        {
            _autoAccumulator = 0f;
            _textAnimations.Clear();
        }

        /// <summary>新 CUT 加载完成后重建文本动画等待状态。</summary>
        public void OnCutLoaded(global::HEdit.ADVPart.Cut cut)
        {
            Reset();
            if (cut == null || cut.speechBubbles == null || cut.speechBubbles.Count == 0)
                return;

            CaptureTextAnimations(cut);
        }

        private bool TickAutoDelay(float interval)
        {
            if (HasPendingTextAnimation())
            {
                _autoAccumulator = 0f;
                return false;
            }

            _autoAccumulator += Time.deltaTime;
            if (_autoAccumulator < interval)
                return false;

            _autoAccumulator = 0f;
            return true;
        }

        private bool HasPendingTextAnimation()
        {
            for (int i = _textAnimations.Count - 1; i >= 0; i--)
            {
                TypefaceAnimatorEx typeface = _textAnimations[i];
                if (typeface == null || !typeface.enabled || !typeface.gameObject.activeInHierarchy)
                {
                    _textAnimations.RemoveAt(i);
                    continue;
                }

                if (typeface.progress >= 1f)
                {
                    _textAnimations.RemoveAt(i);
                    continue;
                }

                return true;
            }

            return false;
        }

        private void CaptureTextAnimations(global::HEdit.ADVPart.Cut cut)
        {
            var currentTexts = ReadCurrentTextObjects();
            if (currentTexts == null || currentTexts.Count == 0)
                return;

            foreach (var speech in cut.speechBubbles)
            {
                if (speech == null || speech.textLayouts == null)
                    continue;
                if (!currentTexts.TryGetValue(speech, out GameObject gameObject) || gameObject == null)
                    continue;

                var component = gameObject.GetComponent<TextComponent>();
                if (component == null || component.textInfos == null)
                    continue;

                int count = Math.Min(speech.textLayouts.Length, component.textInfos.Length);
                for (int i = 0; i < count; i++)
                {
                    var layout = speech.textLayouts[i];
                    var textInfo = component.textInfos[i];
                    if (!ShouldWatchTextAnimation(layout, textInfo))
                        continue;

                    _textAnimations.Add(textInfo.typeface);
                }
            }
        }

        private static Dictionary<global::HEdit.ADVPart.SpeechBubbles, GameObject> ReadCurrentTextObjects()
        {
            try
            {
                var adv = Singleton<global::ADV.ADV>.Instance;
                if (adv == null)
                    return null;

                var field = Traverse.Create(adv).Field("dicText");
                if (field != null && field.FieldExists())
                    return field.GetValue<Dictionary<global::HEdit.ADVPart.SpeechBubbles, GameObject>>();
            }
            catch
            {
                // 反射失败时退回固定延迟，避免自动前进功能整体失效。
            }

            return null;
        }

        private static bool ShouldWatchTextAnimation(
            global::HEdit.ADVPart.SpeechBubbles.TextLayout layout,
            TextComponent.TextInfo textInfo)
        {
            if (layout == null || textInfo == null)
                return false;
            if (!layout.anime || string.IsNullOrEmpty(layout.msg))
                return false;

            TypefaceAnimatorEx typeface = textInfo.typeface;
            if (typeface == null || !typeface.enabled)
                return false;

            return typeface.style == TypefaceAnimatorEx.Style.Once;
        }

        private void HandleToggleKey()
        {
            if (!Input.GetKeyDown(_getToggleKey()))
                return;

            bool next = !_getAutoEnabled();
            _setAutoEnabled(next);
            _log.LogMessage(next ? "自动前进: 开" : "自动前进: 关");
        }

        /// <summary>
        /// 复刻 ADVPlay.Check() 守卫，但移除「文本隐藏点击」条目：
        /// 文本未隐藏也允许前进。select 分支由 Advance() 内部 isSelect 拦截，不在此处。
        /// </summary>
        private static bool CanAdvanceNow()
        {
            var hpd = Singleton<HPlayData>.Instance;
            if (hpd == null || hpd.basePart == null || hpd.basePart.kind == 0)
                return false; // 非 ADV 模式（kind==0 是 H 动作模式）

            var scene = Singleton<Scene>.Instance;
            if (scene == null)
                return false;
            if (!string.IsNullOrEmpty(scene.AddSceneName))
                return false; // 子界面（Config/Check 等）打开中
            if (scene.IsFadeNow)
                return false;

            var heg = Singleton<HEditGlobal>.Instance;
            if (heg != null && heg.spriteFadeCtrl != null && heg.spriteFadeCtrl.IsFade())
                return false;

            return true;
        }

        /// <summary>
        /// 等同一次原生点击：选项分支显示时不动，其余交给 ADVPlay.OnClick。
        /// OnClick 不可用时复刻其分支：末尾 CUT → 下一个 PART（nextPart=0），否则 NextCut。
        /// </summary>
        private void Advance()
        {
            var advPlay = Singleton<ADVPlay>.Instance;
            if (advPlay == null)
                return;

            var advpart = Singleton<HPlayData>.Instance?.basePart as global::HEdit.ADVPart;
            if (advpart == null || advpart.cuts == null)
                return;

            // cutIndex 读取失败返回 -1，按非末尾 CUT 处理
            int cutIndex = ReadField<int>(advPlay, "cutIndex", -1);
            bool isSelect = ReadField<bool>(advPlay, "isSelect", false);
            bool isLastCut = advpart.cuts.Count <= cutIndex;

            // 选项分支(isSelect)显示时不动，等玩家选择
            if (isLastCut && isSelect)
                return;

            if (_onClick != null)
            {
                _onClick.Invoke(advPlay, null);
                return;
            }

            if (isLastCut)
            {
                Singleton<HPlayData>.Instance.isNextPart = true;
                Singleton<HPlayData>.Instance.nextPart = 0;
            }
            else
            {
                advPlay.NextCut();
            }
        }

        // 反射读私有字段用 Traverse；字段索引稳定（cutIndex/isSelect 自游戏首发未变）。
        // fallback 返回默认值，不抛异常——一次读不到不影响后续帧。
        private static T ReadField<T>(object instance, string name, T fallback)
        {
            try
            {
                var tr = Traverse.Create(instance).Field(name);
                if (tr != null && tr.FieldExists())
                    return tr.GetValue<T>();
            }
            catch
            {
                // 反射异常静默：避免每帧刷屏，由调用方的默认值兜底
            }
            return fallback;
        }
    }
}
