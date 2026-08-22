namespace EC_FaceSDFShadow
{
    /// <summary>SDF 运行时贴图尺寸的单一来源。</summary>
    internal static class SDFTextureResolution
    {
        internal const int DefaultSize = 1024;

        // 结构模板参数仍按旧素材的 texel 标定，不能与运行时输入下限混用。
        internal const int TemplateCalibrationSize = 512;
        internal const float TemplateCalibrationScale =
            DefaultSize / (float)TemplateCalibrationSize;
    }
}
