namespace EC_ADVBatchTransform
{
    using Cut = HEdit.ADVPart.Cut;

    // 每帧入口：先包装新回调，再同步选中集合，最后处理按键
    internal static class EditorDriver
    {
        private static bool wasInPart;
        private static Cut lastCut;

        internal static void Tick()
        {
            if (!EditorContext.InPart)
            {
                if (wasInPart) LeavePart();
                return;
            }
            if (!wasInPart)
            {
                wasInPart = true;
                KeyInput.HookCamera();
            }
            var cut = EditorContext.Cut;
            if (cut != lastCut) ResetAll();
            DragSync.Scan();
            MultiSelection.Tick();
            // 先结束已松开的插件手势，再提交原生拖动，避免同一手势两处各记一条
            KeyInput.Tick();
            EditRecorder.Tick();
            PanelWatcher.Tick();
        }

        // Part 的 Active/Deactive 与切 cut 都会重建全部 UICtrl，旧的选中、手势、撤销都作废
        internal static void ResetAll()
        {
            BatchOps.Cancel();
            SlotOps.Cancel();
            UndoStack.Clear();
            EditRecorder.Reset();
            MultiSelection.Clear();
            lastCut = EditorContext.Cut;
        }

        internal static void OnPartActive()
        {
            ResetAll();
            KeyInput.HookCamera();
        }

        internal static void OnPartDeactive()
        {
            ResetAll();
            KeyInput.ReleaseNav();
        }

        private static void LeavePart()
        {
            wasInPart = false;
            ResetAll();
            KeyInput.ReleaseNav();
        }
    }
}
