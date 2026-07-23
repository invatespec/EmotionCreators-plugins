using Map;
using UnityEngine;

namespace EC_HEditPosePanel
{
    // XYZ 轴骨骼变换单写(研究 14/25/26):
    //   旋转 — 以 oc.localRotation 反解 quaternion,乘单轴增量写回 setter(走 ObjectCtrl.localRotation → changeAmount.rot → CalcTransform)
    //   平移 — oc.localPosition 单分量增量 + 同步 guideObject.transform.localPosition(guide 球显示)
    // 过滤: 平移 enablePos / 旋转 enableRot(对照原生 OnDragTransXZ-Y/OnDragRotX-Y-Z LINQ where)。
    // sens 由调用方传(面板手动档 / ctrl/shift 临时覆盖);mode 由 Plugin 调用处分支,这里不接 mode。
    // delta 统一用 Mouse Y(鼠标上=增加、下=减少),三轴同源无 X/Z Mouse X 噪声分支。
    internal static class Manipulator
    {
        // 兼容字段(SettingChanged 仍写,ApplyAxis 已不读 — 实际生效值由调用方 sens 参数传入)。保留作调试观察。
        internal static float TransSens = 0.01f;
        internal static float RotSens = 1f;

        // 旋转:以 oc.localRotation(changeAmount.rot)为起点反解 quaternion,乘单轴增量写回 setter。
        // 单写不双写 transform — OCBone FK 路径由 CalcFKBone 用 changeAmount.rot 单点覆写 localEulerAngles,
        // 双写 transform*= 与 changeAmount 反解互斥 → quaternion↔euler 轴序歧义漂移(视觉多轴抖动,研究 26)。
        // 同构 advguideon PoseSave.cs:957-983 RotChangeMethod(只动 changeAmount.rot,不动 guideObject.transform)。
        internal static void ApplyRotMany(string axis, ObjectCtrl[] ocs, float delta, float sens)
        {
            if (ocs == null) return;
            float deg = delta * sens;
            Vector3 incr;
            switch (axis)
            {
                case "X": incr = new Vector3(deg, 0f, 0f); break;
                case "Y": incr = new Vector3(0f, deg, 0f); break;
                case "Z": incr = new Vector3(0f, 0f, deg); break;
                default: return;
            }
            for (int i = 0; i < ocs.Length; i++)
            {
                var oc = ocs[i];
                if (oc == null || !oc.enableRot) continue;
                Vector3 r0 = oc.localRotation;
                Quaternion q = Quaternion.Euler(r0) * Quaternion.Euler(incr);
                Vector3 e = q.eulerAngles;
                e.x %= 360f; e.y %= 360f; e.z %= 360f;
                oc.localRotation = e;   // setter 触发 CalcTransform → OCBone.CalcFKBone/IK 单点生效
            }
        }

        // 世界轴平移:固定世界分量(Vector3.right/up/forward * mag)+ oc.position setter(自动反算 transRoot 局部 changeAmount → CalcIKTarget → FinalIK)。
        // ponytail: 相机 TransformVector 仅原生 drag 用(其位移走相机平面需投影);插件用约定"世界 XYZ 固定",直世界分量累加最简零歧义。
        internal static void ApplyTransManyWorld(string axis, ObjectCtrl[] ocs, float delta, float sens)
        {
            if (ocs == null) return;
            float mag = delta * sens;
            Vector3 axisWorld;
            switch (axis)
            {
                case "X": axisWorld = Vector3.right; break;
                case "Y": axisWorld = Vector3.up; break;
                case "Z": axisWorld = Vector3.forward; break;
                default: return;
            }
            Vector3 worldIncr = axisWorld * mag;
            foreach (var oc in ocs)
            {
                if (oc == null || !oc.enablePos) continue;
                Vector3 p = oc.position;
                p += worldIncr;
                oc.position = p;
            }
        }

        // 骨局部轴平移:用按下瞬间锁定的统一方向(dirValid 时)作为所有选中骨共享增量;无缓存时降级取首骨 oc.transform 轴方向。
        // 写 oc.position setter 自动反算 transRoot 局部 changeAmount → CalcIKTarget → FinalIK。
        // ponytail: 锁定方向而非每帧重读 oc.transform —— IK 每帧重解算致 transform 朝向漂移,每帧重读会让移动方向偏离按下时所见 guide 箭头(用户体感"偏离轴向")。
        // 多选共享单方向:IK 调态常态单选,多选近似可接受;真多骨各自方向需未来升级。
        internal static void ApplyTransManyBone(string axis, ObjectCtrl[] ocs, float delta, float sens,
            Vector3 lockedDir, bool dirValid)
        {
            if (ocs == null) return;
            float mag = delta * sens;
            Vector3 sharedAxis;
            if (dirValid) sharedAxis = lockedDir;                       // 持轴期间 Locked 方向(按下瞬间 guide 箭头方向)
            else
            {
                // 降级:取首骨当前轴方向(无缓存时,如外部未触发 BeginAxis 的调用路径)
                var first = (ocs.Length > 0) ? ocs[0] : null;
                if (first == null || first.transform == null) return;
                switch (axis)
                {
                    case "X": sharedAxis = first.transform.right; break;
                    case "Y": sharedAxis = first.transform.up; break;
                    case "Z": sharedAxis = first.transform.forward; break;
                    default: return;
                }
            }
            Vector3 worldIncr = sharedAxis * mag;
            foreach (var oc in ocs)
            {
                if (oc == null || !oc.enablePos) continue;
                Vector3 p = oc.position;
                p += worldIncr;
                oc.position = p;
            }
        }

        // 平移(局部 — 转发 ApplyTransManyBone;无锁定方向降级取首骨朝向)。
        internal static void ApplyTransMany(string axis, ObjectCtrl[] ocs, float delta, float sens)
            => ApplyTransManyBone(axis, ocs, delta, sens, default, false);
    }
}
