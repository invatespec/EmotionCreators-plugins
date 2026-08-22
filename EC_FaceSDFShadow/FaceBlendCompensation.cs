using System.Collections.Generic;
using UnityEngine;

namespace EC_FaceSDFShadow
{
    /// <summary>
    /// 表情驱动 SDF 采样 UV 补偿。
    ///
    /// 问题：blendshape 只动顶点位置不动 UV，眨眼/眯眼时 SDF 阈值图案跟着脸皮收缩扩张
    /// （"画在脸上"感）。补偿：按 blendshape 权重线性驱动 5 区域 affine 位移场，
    /// 在 shader 采样 _SDFTex 前把 UV 沿位移场正向偏移，把图案锚回中性姿势位置。
    /// blendshape 线性性保证多通道叠加正确。
    ///
    /// 数据来源：research/blendshape_uv_fit.py 离线最小二乘拟合（cf_O_face 81 通道 ×
    /// 5 区域，任一区域 p95 &gt; 0.3 texel 才入表，共 79 通道）。系数单位 texel/(权重=100)，
    /// du = a*u+b*v+tx; dv = c*u+d*v+ty（u,v 为归一化 UV，系数以 512px 拟合基准记录）。
    /// 零点姿势 = 睁眼中性脸（def_op 位移场全零 = bind pose）。
    ///
    /// 通道名在运行时 mesh 上解析（换头/mod 头通道序可能漂移，名字最稳）。
    /// 权重由闭源 FBSAssist 的 FaceBlendShape.LateUpdate 每帧重写，onPreCull 在其后，
    /// 读到的是当帧终值。
    /// </summary>
    internal static class FaceBlendCompensation
    {
        // 区域序与 blendshape_uv_fit.py 的 REGIONS、shader 的矩形定义保持一致：
        // 0=R_cheek 1=L_cheek 2=R_eye 3=L_eye 4=mouth
        internal const int RegionCount = 5;
        private const int CoeffPerRegion = 6;
        internal const int TotalCoeffs = RegionCount * CoeffPerRegion;

        /// <summary>离线拟合系数的 texel 基准；不是运行时 SDF 贴图尺寸。</summary>
        // 这是离线拟合的位移单位，不是运行时贴图输入下限；重拟合前不得改动。
        internal const float TexelSize = SDFTextureResolution.TemplateCalibrationSize;

        private struct ChannelComp
        {
            internal readonly string Name;
            internal readonly float[] C; // [region*6 + k]，k 序 a,b,tx,c,d,ty
            internal ChannelComp(string name, params float[] c)
            {
                Name = name;
                C = c;
            }
        }

        private static readonly ChannelComp[] CompTable =
        {
    new ChannelComp("eye_face.f00_def_cl",  // mesh idx 0
        -3.0634f, 6.2568f, -1.122f, -0.8120f, 14.3337f, -5.118f,  // R_cheek
        -3.0686f, -6.2502f, 4.186f, 0.8169f, 14.3313f, -5.932f,  // L_cheek
        7.0736f, -4.7597f, -0.719f, 82.8616f, -16.3339f, -38.653f,  // R_eye
        7.1169f, 4.8459f, -6.431f, -83.1536f, -16.3472f, 44.371f,  // L_eye
        -1.1089f, -0.0025f, 0.555f, -0.0093f, -6.0349f, 1.583f),
    new ChannelComp("eye_face.f00_gyu_cl",  // mesh idx 24
        2.2822f, -0.7360f, -0.600f, -1.1595f, 65.5164f, -24.015f,  // R_cheek
        2.2632f, 0.7245f, -1.666f, 1.1483f, 65.5250f, -25.172f,  // L_cheek
        5.6575f, -2.5709f, -1.438f, 77.7243f, -25.7970f, -29.604f,  // R_eye
        5.6972f, 2.6504f, -4.290f, -78.0131f, -25.8028f, 48.277f,  // L_eye
        0.0781f, -0.0022f, -0.039f, -0.0015f, -0.4232f, 0.186f),
    new ChannelComp("eye_face.f00_gyul_op",  // mesh idx 22
        -0.5092f, 0.3546f, 0.070f, -4.0066f, 13.6287f, -3.700f,  // R_cheek
        2.2632f, 0.7245f, -1.666f, 1.1483f, 65.5250f, -25.172f,  // L_cheek
        0.3336f, 0.3469f, -0.381f, 8.9145f, -0.7539f, -4.205f,  // R_eye
        5.6972f, 2.6504f, -4.290f, -78.0131f, -25.8028f, 48.277f,  // L_eye
        0.5887f, -0.0925f, -0.270f, 2.9222f, -0.4367f, -1.310f),
    new ChannelComp("eye_face.f00_gyur_op",  // mesh idx 23
        2.2822f, -0.7360f, -0.600f, -1.1595f, 65.5164f, -24.015f,  // R_cheek
        -0.5144f, -0.3694f, 0.448f, 3.9983f, 13.6067f, -7.694f,  // L_cheek
        5.6575f, -2.5709f, -1.438f, 77.7243f, -25.7970f, -29.604f,  // R_eye
        0.3284f, -0.3437f, 0.048f, -8.9537f, -0.7585f, 4.733f,  // L_eye
        0.5971f, 0.0907f, -0.322f, -2.9257f, -0.4363f, 1.614f),
    new ChannelComp("eye_face.f00_winkl_op",  // mesh idx 5
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        -2.6249f, 15.0923f, -4.531f, 44.7462f, 73.4640f, -50.167f,  // L_cheek
        -0.0000f, -0.0000f, 0.000f, -0.0043f, -0.0034f, 0.003f,  // R_eye
        9.4796f, 1.3744f, -6.038f, -46.2063f, -34.2252f, 34.934f,  // L_eye
        0.2761f, 0.2032f, -0.201f, -2.8879f, -2.4990f, 2.190f),
    new ChannelComp("eye_face.f00_egao_cl",  // mesh idx 2
        -2.5685f, -15.1944f, 7.173f, -44.8657f, 73.4795f, -5.381f,  // R_cheek
        -2.6249f, 15.0923f, -4.531f, 44.7462f, 73.4640f, -50.167f,  // L_cheek
        9.4170f, -1.2946f, -3.463f, 46.0672f, -34.2404f, -11.194f,  // R_eye
        9.4796f, 1.3744f, -6.038f, -46.2063f, -34.2252f, 34.934f,  // L_eye
        0.5581f, -0.0017f, -0.279f, -0.0074f, -4.9912f, 1.493f),
    new ChannelComp("eye_face.f00_winkr_op",  // mesh idx 6
        -2.5685f, -15.1944f, 7.173f, -44.8657f, 73.4795f, -5.381f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        9.4170f, -1.2946f, -3.463f, 46.0672f, -34.2404f, -11.194f,  // R_eye
        -0.0000f, 0.0000f, 0.000f, 0.0043f, -0.0033f, -0.001f,  // L_eye
        0.2820f, -0.2049f, -0.078f, 2.8806f, -2.4923f, -0.696f),
    new ChannelComp("eye_face.f00_gyu02_cl",  // mesh idx 27
        -6.3673f, -28.5324f, 13.275f, -52.0827f, 120.1866f, -18.730f,  // R_cheek
        -6.4471f, 28.4300f, -6.820f, 51.9054f, 120.2504f, -70.729f,  // L_cheek
        8.5034f, 0.8781f, -4.306f, 34.0909f, -41.5287f, -0.574f,  // R_eye
        8.5609f, -0.8098f, -4.272f, -34.2179f, -41.5253f, 33.580f,  // L_eye
        5.0368f, -0.0011f, -2.518f, -0.0039f, -0.2566f, 0.387f),
    new ChannelComp("eye_face.f00_gyur02_op",  // mesh idx 26
        -6.3673f, -28.5324f, 13.275f, -52.0827f, 120.1866f, -18.730f,  // R_cheek
        13.8050f, -5.5629f, -6.771f, -2.5316f, 14.2858f, -6.762f,  // L_cheek
        8.5034f, 0.8781f, -4.306f, 34.0909f, -41.5287f, -0.574f,  // R_eye
        4.0418f, 1.8732f, -4.100f, 13.7739f, 10.6210f, -14.364f,  // L_eye
        -2.0217f, -2.3705f, 1.579f, -10.7579f, -1.7992f, 5.934f),
    new ChannelComp("eye_face.f00_gyul02_op",  // mesh idx 25
        13.7951f, 5.5654f, -7.031f, 2.5629f, 14.3443f, -9.327f,  // R_cheek
        -6.4471f, 28.4300f, -6.820f, 51.9054f, 120.2504f, -70.729f,  // L_cheek
        4.0359f, -1.8741f, 0.061f, -13.7313f, 10.6145f, -0.605f,  // R_eye
        8.5609f, -0.8098f, -4.272f, -34.2179f, -41.5253f, 33.580f,  // L_eye
        -2.0281f, 2.3686f, 0.446f, 10.7439f, -1.8004f, -4.816f),
    new ChannelComp("kuti_face.f00_kuwae_op",  // mesh idx 58
        -0.5939f, 5.0876f, -1.908f, 0.5727f, -0.5219f, 0.014f,  // R_cheek
        -0.5943f, -5.1001f, 2.507f, -0.5719f, -0.5249f, 0.587f,  // L_cheek
        -0.0010f, 0.0010f, -0.000f, 0.0708f, -0.0639f, 0.011f,  // R_eye
        -0.0010f, -0.0009f, 0.001f, -0.0706f, -0.0637f, 0.082f,  // L_eye
        29.4567f, 0.0027f, -14.731f, 0.3220f, 84.1392f, -34.939f),
    new ChannelComp("kuti_face.f00_sinken03_op",  // mesh idx 46
        -0.0588f, 0.1047f, -0.021f, 0.4563f, -0.4887f, 0.032f,  // R_cheek
        -0.0589f, -0.1049f, 0.080f, -0.4565f, -0.4885f, 0.488f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        4.5843f, 0.0066f, -2.296f, 0.1770f, 48.5596f, -19.807f),
    new ChannelComp("kuti_face.f00_ikari02_op",  // mesh idx 43
        -0.0359f, 0.0597f, -0.011f, -0.0316f, 0.0464f, -0.007f,  // R_cheek
        -0.0363f, -0.0604f, 0.048f, 0.0318f, 0.0466f, -0.039f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        32.0105f, -0.0031f, -16.004f, 0.2234f, 49.7120f, -20.807f),
    new ChannelComp("kuti_face.f00_tabe_op",  // mesh idx 80
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -15.5543f, 0.0267f, 7.763f, 0.3184f, 40.7586f, -17.489f),
    new ChannelComp("kuti_face.f00_name02_op",  // mesh idx 60
        -0.0146f, 0.0215f, -0.003f, 0.0007f, -0.0012f, 0.000f,  // R_cheek
        -0.0147f, -0.0216f, 0.018f, -0.0007f, -0.0012f, 0.001f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        7.3549f, 0.0113f, -3.684f, 0.2145f, 39.1887f, -16.585f),
    new ChannelComp("kuti_face.f00_niko_op",  // mesh idx 77
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        9.1684f, -0.0176f, -4.574f, 0.2261f, 24.8250f, -10.741f),
    new ChannelComp("kuti_face.f00_uresi_op",  // mesh idx 33
        -0.0183f, 0.0299f, -0.005f, 0.0516f, -0.0544f, 0.003f,  // R_cheek
        -0.0184f, -0.0301f, 0.024f, -0.0516f, -0.0545f, 0.055f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        5.6833f, -0.0005f, -2.842f, 0.1601f, 36.9453f, -14.739f),
    new ChannelComp("kuti_face.f00_a_l_op",  // mesh idx 62
        -0.0004f, 0.0007f, -0.000f, -0.0018f, 0.0019f, -0.000f,  // R_cheek
        -0.0004f, -0.0007f, 0.001f, 0.0018f, 0.0018f, -0.002f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -5.8589f, -0.0001f, 2.930f, 0.1146f, 29.5448f, -12.473f),
    new ChannelComp("kuti_face.f00_name_op",  // mesh idx 57
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        4.2538f, 0.0102f, -2.132f, 0.1235f, 20.4344f, -8.848f),
    new ChannelComp("kuti_face.f00_sinken02_op",  // mesh idx 44
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -15.0631f, 0.0049f, 7.529f, 0.0967f, 22.9954f, -9.761f),
    new ChannelComp("kuti_face.f00_odoro_op",  // mesh idx 52
        -0.0071f, -0.0457f, 0.022f, -0.0846f, 1.2214f, -0.478f,  // R_cheek
        -0.0071f, 0.0459f, -0.015f, 0.0848f, 1.2254f, -0.564f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -24.4452f, 0.0050f, 12.220f, 0.1274f, 24.4451f, -10.422f),
    new ChannelComp("kuti_face.f00_egao_op",  // mesh idx 30
        -0.0001f, 0.0001f, -0.000f, -0.0000f, 0.0000f, -0.000f,  // R_cheek
        -0.0001f, -0.0002f, 0.000f, 0.0000f, 0.0000f, -0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -3.4984f, 0.0040f, 1.747f, 0.1029f, 34.3076f, -14.069f),
    new ChannelComp("kuti_face.f00_u_s_op",  // mesh idx 65
        4.1222f, -12.8137f, 3.768f, -0.3253f, 0.9655f, -0.278f,  // R_cheek
        4.1353f, 12.8520f, -7.915f, 0.3273f, 0.9738f, -0.608f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -65.6923f, 0.0109f, 32.840f, 0.0397f, 7.6028f, -3.580f),
    new ChannelComp("kuti_face.f00_o_l_op",  // mesh idx 70
        3.1940f, -0.4092f, -1.065f, -0.3165f, -0.2421f, 0.222f,  // R_cheek
        3.2035f, 0.4146f, -2.137f, 0.3182f, -0.2420f, -0.095f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -40.7174f, 0.0064f, 20.355f, 0.1272f, 21.2684f, -9.144f),
    new ChannelComp("kuti_face.f00_odoro_s_op",  // mesh idx 53
        -0.0013f, -0.0102f, 0.005f, -0.0250f, 0.3000f, -0.115f,  // R_cheek
        -0.0013f, 0.0103f, -0.003f, 0.0251f, 0.3012f, -0.141f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -30.3297f, 0.0064f, 15.161f, 0.0617f, 15.5249f, -6.628f),
    new ChannelComp("kuti_face.f00_san_op",  // mesh idx 75
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -11.9479f, 0.0084f, 5.969f, 0.1175f, 13.8740f, -5.993f),
    new ChannelComp("kuti_face.f00_e_l_op",  // mesh idx 68
        -0.0004f, 0.0007f, -0.000f, -0.0018f, 0.0019f, -0.000f,  // R_cheek
        -0.0004f, -0.0007f, 0.001f, 0.0018f, 0.0018f, -0.002f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -9.2993f, -0.0011f, 4.650f, 0.0955f, 22.8668f, -9.658f),
    new ChannelComp("kuti_face.f00_aseri_op",  // mesh idx 49
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        2.3938f, 0.0051f, -1.199f, 0.0803f, 14.0750f, -6.108f),
    new ChannelComp("kuti_face.f00_def_op",  // mesh idx 29
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -4.3179f, 0.0036f, 2.157f, 0.0886f, 22.6343f, -9.533f),
    new ChannelComp("kuti_face.f00_ikari_op",  // mesh idx 42
        -0.0178f, 0.0296f, -0.006f, -0.0159f, 0.0233f, -0.004f,  // R_cheek
        -0.0180f, -0.0300f, 0.024f, 0.0160f, 0.0233f, -0.020f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        16.2099f, -0.0007f, -8.105f, 0.1317f, 23.6484f, -10.120f),
    new ChannelComp("eye_face.f00_keno_op",  // mesh idx 13
        -9.8144f, -17.1285f, 10.142f, -12.4874f, 40.3759f, -10.437f,  // R_cheek
        -9.8276f, 17.0904f, -0.305f, 12.4429f, 40.3511f, -22.888f,  // L_cheek
        -8.3861f, 2.9029f, 2.224f, -14.1716f, 7.3088f, -0.871f,  // R_eye
        -8.4129f, -2.9069f, 6.178f, 14.1275f, 7.3097f, -15.022f,  // L_eye
        1.8344f, 0.0001f, -0.917f, -0.0108f, -5.0077f, 1.291f),
    new ChannelComp("kuti_face.f00_o_s_op",  // mesh idx 69
        3.1940f, -0.4092f, -1.065f, -0.3167f, -0.2417f, 0.222f,  // R_cheek
        3.2035f, 0.4146f, -2.137f, 0.3184f, -0.2416f, -0.095f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -42.4885f, 0.0074f, 21.240f, 0.0945f, 11.7152f, -5.129f),
    new ChannelComp("kuti_face.f00_e_s_op",  // mesh idx 67
        0.0275f, -0.0458f, 0.009f, -0.0033f, 0.0046f, -0.001f,  // R_cheek
        0.0276f, 0.0460f, -0.036f, 0.0033f, 0.0046f, -0.004f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -20.5113f, 0.0038f, 10.254f, 0.0460f, 18.7983f, -7.868f),
    new ChannelComp("kuti_face.f00_doya_op",  // mesh idx 55
        -0.3559f, 0.5568f, -0.097f, 0.1136f, -0.1755f, 0.030f,  // R_cheek
        -0.3569f, -0.5582f, 0.454f, -0.1144f, -0.1768f, 0.144f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        17.6355f, -0.0091f, -8.812f, -0.0782f, 8.4889f, -2.636f),
    new ChannelComp("eye_face.f00_gag",  // mesh idx 21
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        -10.3068f, -1.3450f, 4.977f, -2.3451f, -4.3811f, 3.561f,  // R_eye
        -10.4422f, 1.4670f, 5.334f, 2.2780f, -4.3222f, 1.219f,  // L_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f),
    new ChannelComp("kuti_face.f00_kisu_op",  // mesh idx 59
        0.0221f, -0.0419f, 0.009f, -0.0020f, 0.0039f, -0.001f,  // R_cheek
        0.0225f, 0.0426f, -0.032f, 0.0021f, 0.0040f, -0.003f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -41.1999f, 0.0056f, 20.597f, 0.0069f, 0.4182f, -0.292f),
    new ChannelComp("eye_face.f00_ikari_op",  // mesh idx 9
        -11.0380f, -8.3520f, 7.298f, -8.1089f, 35.2966f, -10.147f,  // R_cheek
        -11.0474f, 8.3293f, 3.754f, 8.0770f, 35.3029f, -18.239f,  // L_cheek
        -6.5327f, 1.8398f, 2.003f, -6.8025f, 5.5393f, -2.746f,  // R_eye
        -6.5547f, -1.8395f, 4.541f, 6.7543f, 5.5391f, -9.524f,  // L_eye
        1.2672f, 0.0000f, -0.634f, -0.0070f, -4.2269f, 1.088f),
    new ChannelComp("kuti_face.f00_doki_ss_op",  // mesh idx 40
        0.9500f, -0.1317f, -0.312f, -0.1880f, 0.0469f, 0.052f,  // R_cheek
        0.9529f, 0.1333f, -0.641f, 0.1887f, 0.0471f, -0.136f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -25.9663f, 0.0106f, 12.978f, 0.0402f, 10.2472f, -4.514f),
    new ChannelComp("kuti_face.f00_doki_s_op",  // mesh idx 37
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -9.7011f, 0.0076f, 4.847f, 0.0402f, 11.1849f, -4.696f),
    new ChannelComp("kuti_face.f00_uresi_s_op",  // mesh idx 34
        -0.0092f, 0.0150f, -0.003f, 0.0258f, -0.0272f, 0.002f,  // R_cheek
        -0.0092f, -0.0150f, 0.012f, -0.0258f, -0.0272f, 0.027f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        4.5009f, 0.0001f, -2.251f, 0.0802f, 15.0139f, -5.913f),
    new ChannelComp("kuti_face.f00_keno_op",  // mesh idx 47
        -0.6730f, 5.4975f, -2.042f, -0.0011f, -0.5313f, 0.222f,  // R_cheek
        -0.6749f, -5.5136f, 2.723f, 0.0009f, -0.5352f, 0.222f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        24.2713f, -0.0089f, -12.131f, 0.0023f, 9.1858f, -3.706f),
    new ChannelComp("kuti_face.f00_u_l_op",  // mesh idx 66
        3.1946f, -0.4147f, -1.063f, -0.3169f, -0.2434f, 0.223f,  // R_cheek
        3.2041f, 0.4203f, -2.139f, 0.3186f, -0.2434f, -0.095f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -41.4193f, 0.0068f, 20.706f, 0.0496f, 7.9250f, -3.437f),
    new ChannelComp("kuti_face.f00_a_s_op",  // mesh idx 61
        0.6133f, -1.0225f, 0.192f, -0.0325f, 0.0603f, -0.013f,  // R_cheek
        0.6153f, 1.0255f, -0.808f, 0.0328f, 0.0608f, -0.046f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -26.4761f, 0.0044f, 13.236f, 0.0406f, 12.4531f, -5.306f),
    new ChannelComp("kuti_face.f00_doki_ss_cl",  // mesh idx 39
        0.0000f, -0.0000f, 0.000f, -0.0000f, 0.0000f, -0.000f,  // R_cheek
        0.0000f, 0.0000f, -0.000f, 0.0000f, 0.0000f, -0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -35.0437f, 0.0070f, 17.518f, 0.0194f, 0.7171f, -0.489f),
    new ChannelComp("kuti_face.f00_akire_op",  // mesh idx 51
        -0.1301f, -2.4276f, 1.072f, 0.0684f, 0.2758f, -0.141f,  // R_cheek
        -0.1306f, 2.4323f, -0.943f, -0.0686f, 0.2772f, -0.073f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -25.6861f, 0.0053f, 12.840f, 0.0332f, 8.4281f, -3.703f),
    new ChannelComp("eye_face.f00_rakutan_op",  // mesh idx 18
        -0.2687f, -0.7628f, 0.399f, -2.7490f, 12.9603f, -3.895f,  // R_cheek
        -0.2721f, 0.7502f, -0.124f, 2.7429f, 12.9439f, -6.634f,  // L_cheek
        -3.5130f, 1.0689f, 0.952f, 14.7924f, 1.1978f, -8.644f,  // R_eye
        -3.5371f, -1.0643f, 2.571f, -14.8536f, 1.1979f, 6.181f,  // L_eye
        0.8733f, 0.0004f, -0.437f, -0.0077f, -0.7916f, 0.209f),
    new ChannelComp("eye_face.f00_bisyou_op",  // mesh idx 4
        -4.1460f, -5.9428f, 3.792f, -5.1048f, 11.8336f, -2.524f,  // R_cheek
        -4.1490f, 5.9341f, 0.359f, 5.0937f, 11.8331f, -7.622f,  // L_cheek
        -0.4498f, 0.4870f, -0.134f, 4.1800f, 3.0273f, -5.541f,  // R_eye
        -0.4571f, -0.4824f, 0.584f, -4.2363f, 3.0202f, -1.327f,  // L_eye
        2.0023f, 0.0003f, -1.001f, -0.0022f, -1.2719f, 0.333f),
    new ChannelComp("eye_face.f00_kurusi_op",  // mesh idx 12
        -6.9096f, -12.3562f, 7.289f, -9.8686f, 29.5468f, -7.402f,  // R_cheek
        -6.9172f, 12.3227f, -0.362f, 9.8397f, 29.5344f, -17.249f,  // L_cheek
        -3.0765f, 2.7289f, -0.143f, -0.6509f, 3.5768f, -3.720f,  // R_eye
        -3.0917f, -2.7299f, 3.228f, 0.5994f, 3.5719f, -4.341f,  // L_eye
        1.4381f, 0.0004f, -0.719f, -0.0068f, -3.1952f, 0.826f),
    new ChannelComp("kuti_face.f00_doya_cl",  // mesh idx 54
        -0.9391f, 1.4393f, -0.243f, 0.7106f, -1.0597f, 0.172f,  // R_cheek
        -0.9415f, -1.4426f, 1.184f, -0.7131f, -1.0632f, 0.886f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        24.1818f, -0.0088f, -12.085f, -0.0905f, -1.5419f, 1.315f),
    new ChannelComp("kuti_face.f00_sabisi_op",  // mesh idx 48
        1.6524f, -5.1284f, 1.508f, -0.1304f, 0.3867f, -0.111f,  // R_cheek
        1.6576f, 5.1437f, -3.170f, 0.1312f, 0.3900f, -0.243f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -33.9423f, 0.0063f, 16.968f, 0.0254f, 4.0578f, -1.953f),
    new ChannelComp("eye_face.f00_setunai_op",  // mesh idx 7
        -0.5092f, 0.3546f, 0.070f, -4.0066f, 13.6287f, -3.700f,  // R_cheek
        -0.5144f, -0.3694f, 0.448f, 3.9983f, 13.6067f, -7.694f,  // L_cheek
        0.2858f, 0.3417f, -0.380f, 8.4509f, -0.9207f, -3.972f,  // R_eye
        0.2808f, -0.3383f, 0.095f, -8.4905f, -0.9252f, 4.502f,  // L_eye
        1.1086f, 0.0005f, -0.554f, -0.0019f, -0.4750f, 0.124f),
    new ChannelComp("eye_face.f00_sinken_op",  // mesh idx 10
        -5.0025f, -25.4441f, 11.487f, -7.4285f, -7.9134f, 5.849f,  // R_cheek
        -4.9952f, 25.4141f, -6.478f, 7.4088f, -7.9055f, -1.570f,  // L_cheek
        -1.3977f, 3.9462f, -1.670f, -6.7884f, 8.2296f, -3.606f,  // R_eye
        -1.3982f, -3.9479f, 3.069f, 6.7784f, 8.2208f, -10.385f,  // L_eye
        4.4452f, 0.0013f, -2.223f, -0.0081f, -5.2978f, 1.346f),
    new ChannelComp("kuti_face.f00_i_l_op",  // mesh idx 64
        1.0570f, 3.9855f, -2.084f, -0.2030f, -0.5332f, 0.300f,  // R_cheek
        1.0594f, -3.9933f, 1.029f, 0.2036f, -0.5355f, 0.098f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        19.3799f, -0.0060f, -9.687f, 0.0141f, 4.4734f, -1.896f),
    new ChannelComp("eye_face.f00_aseri_op",  // mesh idx 17
        0.4399f, 2.8123f, -1.440f, 12.8100f, -18.4635f, -0.704f,  // R_cheek
        0.4865f, -2.7995f, 0.966f, -13.1383f, -17.7643f, 12.049f,  // L_cheek
        -1.2405f, 1.2250f, -0.101f, -1.6841f, 9.5597f, -4.218f,  // R_eye
        -1.2488f, -1.2215f, 1.344f, 1.7001f, 9.4360f, -5.833f,  // L_eye
        0.8604f, 0.0001f, -0.430f, 0.0304f, -2.2075f, 0.431f),
    new ChannelComp("eye_face.f00_egao_op",  // mesh idx 3
        13.5778f, 5.5170f, -6.929f, 2.8462f, 14.3964f, -9.455f,  // R_cheek
        13.5895f, -5.5159f, -6.656f, -2.8126f, 14.3376f, -6.608f,  // L_cheek
        1.8895f, -1.7064f, 0.633f, -12.7013f, 8.9930f, -0.194f,  // R_eye
        1.8800f, 1.7011f, -2.514f, 12.7465f, 9.0014f, -12.926f,  // L_eye
        -6.3104f, 0.0016f, 3.155f, -0.0133f, -2.3981f, 0.501f),
    new ChannelComp("eye_face.f00_naki_op",  // mesh idx 16
        -0.0209f, -2.4012f, 0.889f, -8.0505f, 46.0415f, -13.894f,  // R_cheek
        -0.0374f, 2.3817f, -0.851f, 8.0267f, 46.0811f, -21.946f,  // L_cheek
        -1.9205f, -0.1590f, 1.016f, 6.2230f, -2.6807f, -2.106f,  // R_eye
        -1.9313f, 0.1634f, 0.908f, -6.2480f, -2.6729f, 4.125f,  // L_eye
        0.0949f, -0.0003f, -0.047f, 0.0020f, 0.4105f, -0.090f),
    new ChannelComp("kuti_face.f00_human_cl",  // mesh idx 50
        -3.3477f, -5.8885f, 3.771f, -3.2126f, 22.3615f, -8.314f,  // R_cheek
        -3.3558f, 5.9016f, -0.424f, 3.2201f, 22.3667f, -11.535f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -12.2075f, 0.0041f, 6.101f, 0.0535f, 0.3851f, -0.372f),
    new ChannelComp("eye_face.f00_tumara_op",  // mesh idx 11
        -2.6428f, -8.8966f, 4.381f, -6.4492f, 15.3023f, -3.362f,  // R_cheek
        -2.6461f, 8.8721f, -1.727f, 6.4339f, 15.2880f, -9.797f,  // L_cheek
        -0.5250f, 1.9493f, -0.887f, 2.3447f, 1.2096f, -2.629f,  // R_eye
        -0.5326f, -1.9508f, 1.416f, -2.3777f, 1.2050f, -0.264f,  // L_eye
        0.7916f, 0.0004f, -0.396f, -0.0038f, -1.3654f, 0.354f),
    new ChannelComp("kuti_face.f00_i_s_op",  // mesh idx 63
        1.0944f, 3.6557f, -1.952f, -0.2021f, -0.5200f, 0.294f,  // R_cheek
        1.0978f, -3.6645f, 0.859f, 0.2029f, -0.5225f, 0.092f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        3.5106f, -0.0022f, -1.754f, 0.0157f, 4.8658f, -2.088f),
    new ChannelComp("eye_face.f00_tere_op",  // mesh idx 8
        -0.5400f, -2.8447f, 1.477f, -11.7441f, 1.9149f, 6.416f,  // R_cheek
        -0.5630f, 2.8141f, -0.911f, 11.7738f, 2.0729f, -5.411f,  // L_cheek
        1.4125f, -0.9736f, -0.127f, 4.1673f, -6.7362f, 1.464f,  // R_eye
        1.4183f, 0.9815f, -1.294f, -4.1869f, -6.7320f, 5.639f,  // L_eye
        -0.7817f, -0.0005f, 0.391f, -0.0053f, 1.9887f, -0.397f),
    new ChannelComp("eye_face.f00_kanasi_op",  // mesh idx 15
        -0.0000f, 0.0001f, -0.000f, 0.0002f, -0.0003f, 0.000f,  // R_cheek
        -0.0000f, -0.0001f, 0.000f, -0.0002f, -0.0003f, 0.000f,  // L_cheek
        -0.5535f, -0.4668f, 0.547f, 9.1802f, -0.0302f, -4.751f,  // R_eye
        -0.5600f, 0.4704f, 0.008f, -9.2002f, -0.0325f, 4.441f,  // L_eye
        -0.0015f, -0.0000f, 0.001f, -0.0001f, -0.0112f, 0.003f),
    new ChannelComp("kuti_face.f00_neko_op",  // mesh idx 74
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        0.5753f, -0.0006f, -0.287f, 0.0001f, 1.5387f, -0.695f),
    new ChannelComp("kuti_face.f00_uresi_ss_op",  // mesh idx 35
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        1.5461f, -0.0010f, -0.772f, 0.0227f, 4.2800f, -1.715f),
    new ChannelComp("kuti_face.f00_sinken03_cl",  // mesh idx 45
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -0.3563f, 0.0050f, 0.175f, 0.0770f, 1.9531f, -1.159f),
    new ChannelComp("kuti_face.f00_niko_cl",  // mesh idx 76
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -6.6538f, -0.0046f, 3.330f, -0.0469f, -1.5462f, 0.929f),
    new ChannelComp("eye_face.f00_doya_op",  // mesh idx 20
        -2.7527f, 0.6628f, 0.770f, 5.4182f, -24.4828f, 7.186f,  // R_cheek
        -2.7434f, -0.6433f, 1.970f, -5.4052f, -24.4698f, 12.592f,  // L_cheek
        -0.3419f, -0.5752f, 0.623f, -9.2565f, 4.0017f, 1.315f,  // R_eye
        -0.3326f, 0.5769f, -0.288f, 9.2967f, 4.0050f, -7.966f,  // L_eye
        -0.1176f, -0.0005f, 0.059f, -0.0027f, -0.5544f, 0.143f),
    new ChannelComp("eye_face.f00_sian_op",  // mesh idx 14
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.6351f, -0.4808f, -0.016f, 6.1590f, 1.3763f, -3.854f,  // R_eye
        0.6336f, 0.4849f, -0.621f, -6.1796f, 1.3753f, 2.317f,  // L_eye
        -0.0117f, -0.0001f, 0.006f, -0.0012f, -0.1852f, 0.049f),
    new ChannelComp("kuti_face.f00_n_s_cl",  // mesh idx 71
        -0.1866f, -2.6481f, 1.180f, 0.0688f, 0.3128f, -0.157f,  // R_cheek
        -0.1874f, 2.6550f, -0.996f, -0.0690f, 0.3146f, -0.088f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -8.3861f, -0.0013f, 4.194f, -0.0139f, -0.7227f, 0.419f),
    new ChannelComp("kuti_face.f00_ikari_cl",  // mesh idx 41
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -0.7951f, 0.0048f, 0.395f, 0.0498f, 0.9726f, -0.670f),
    new ChannelComp("eye_face.f00_komaru_op",  // mesh idx 19
        -0.2406f, -0.2599f, 0.189f, -0.0491f, 0.0397f, 0.004f,  // R_cheek
        -0.2404f, 0.2602f, 0.051f, 0.0487f, 0.0398f, -0.045f,  // L_cheek
        0.9992f, -0.0167f, -0.452f, 5.4340f, 0.8670f, -2.977f,  // R_eye
        0.9971f, 0.0181f, -0.546f, -5.4652f, 0.8647f, 2.476f,  // L_eye
        0.0132f, 0.0000f, -0.007f, -0.0002f, -0.0402f, 0.010f),
    new ChannelComp("kuti_face.f00_mogu_cl",  // mesh idx 79
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -8.4029f, -0.0047f, 4.202f, -0.0117f, -0.3297f, 0.188f),
    new ChannelComp("kuti_face.f00_pero_cl",  // mesh idx 56
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        0.3588f, -0.0020f, -0.178f, -0.0269f, -0.0664f, 0.067f),
    new ChannelComp("kuti_face.f00_mogu_op",  // mesh idx 78
        2.8621f, 15.3684f, -7.662f, -2.5691f, -5.0966f, 3.083f,  // R_cheek
        2.8676f, -15.3855f, 4.805f, 2.5731f, -5.1219f, 0.522f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        8.5423f, -0.0050f, -4.268f, -0.0330f, -0.6604f, 0.404f),
    new ChannelComp("kuti_face.f00_uresi_cl",  // mesh idx 32
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        5.5118f, -0.0039f, -2.754f, -0.0331f, -0.5693f, 0.352f),
    new ChannelComp("kuti_face.f00_neko_cl",  // mesh idx 73
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        0.3602f, -0.0041f, -0.178f, -0.0236f, -0.9022f, 0.533f),
    new ChannelComp("kuti_face.f00_n_l_cl",  // mesh idx 72
        -0.3243f, -5.7769f, 2.543f, 0.1268f, 0.6946f, -0.338f,  // R_cheek
        -0.3257f, 5.7921f, -2.224f, -0.1271f, 0.6987f, -0.213f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -3.3119f, -0.0008f, 1.656f, -0.0138f, -0.6843f, 0.314f),
    new ChannelComp("kuti_face.f00_doki_s_cl",  // mesh idx 38
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -3.6919f, 0.0015f, 1.845f, 0.0131f, 0.1378f, -0.082f),
    new ChannelComp("kuti_face.f00_bisyou_op",  // mesh idx 31
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        0.6441f, -0.0026f, -0.321f, -0.0168f, -0.3953f, 0.244f),
    new ChannelComp("kuti_face.f00_doki_cl",  // mesh idx 36
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_cheek
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // R_eye
        0.0000f, 0.0000f, 0.000f, 0.0000f, 0.0000f, 0.000f,  // L_eye
        -0.0912f, 0.0003f, 0.045f, 0.0020f, 0.3339f, -0.199f),
        };

        /// <summary>def_cl 在 CompTable 的下标（基线可配置，见 BlendCompDefClBase）</summary>
        private static readonly int DefClTableIndex = FindChannel("eye_face.f00_def_cl");

        private static int FindChannel(string name)
        {
            for (int i = 0; i < CompTable.Length; i++)
            {
                if (CompTable[i].Name == name) return i;
            }
            return -1;
        }

        // mesh → CompTable 各通道在该 mesh 上的 blendshape 索引（-1 = mesh 无此通道）
        private static readonly Dictionary<Mesh, int[]> _idxCache = new Dictionary<Mesh, int[]>();

        // 每帧累加 buffer：5 区域合成 affine（texel 单位）。onPreCull 主线程独占，复用零 GC。
        private static readonly float[] _accum = new float[TotalCoeffs];

        /// <summary>
        /// onPreCull 阶段读当帧 blendshape 权重，把 5 区域合成 affine 推给 overlay 材质。
        /// 关闭时只推 enable=0，shader 直接跳过（旧 bundle 无该 uniform，Set 静默无害）。
        /// </summary>
        internal static void Push(SkinnedMeshRenderer smr, Material mat)
        {
            if (mat == null) return;

            bool on = FaceSDFShadowPlugin.BlendCompensation.Value;
            mat.SetFloat(ShaderIDs.BlendCompEnable, on ? 1f : 0f);
            if (!on) return;
            if (smr == null)
            {
                mat.SetFloat(ShaderIDs.BlendCompEnable, 0f);
                return;
            }

            var mesh = smr.sharedMesh;
            if (mesh == null)
            {
                mat.SetFloat(ShaderIDs.BlendCompEnable, 0f);
                return;
            }

            var idx = ResolveIndices(mesh);

            // def_cl 基线：游戏"睁眼程度 1"实为 def_cl=23/def_op=77。若阈值图按该姿势
            // 标定，中性态会有静态偏差；把基线调到 23 即归零（默认 0 = 按完全睁眼标定）
            float defClBase = FaceSDFShadowPlugin.BlendCompDefClBase.Value;
            // 嘴区 affine 拟合无效（残余≈原始），PoC 只开颊+眼，嘴区留观察开关
            float mouthScale = FaceSDFShadowPlugin.BlendCompMouth.Value ? 1f : 0f;

            System.Array.Clear(_accum, 0, TotalCoeffs);
            for (int t = 0; t < CompTable.Length; t++)
            {
                int i = idx[t];
                if (i < 0) continue;
                float w = smr.GetBlendShapeWeight(i);
                float dw = (t == DefClTableIndex ? w - defClBase : w) / 100f;
                if (dw == 0f) continue; // 多数通道多数帧为 0，跳过省 30 次乘加

                var c = CompTable[t].C;
                for (int k = 0; k < TotalCoeffs; k++)
                    _accum[k] += dw * c[k];
            }

            if (mouthScale < 1f)
            {
                int b = 4 * CoeffPerRegion;
                for (int k = 0; k < CoeffPerRegion; k++)
                    _accum[b + k] *= mouthScale;
            }

            // 诊断增益：1=物理量（离线拟合值）。拉高用于区分"量级不足"与"affine 结构性不足"
            float gain = FaceSDFShadowPlugin.BlendCompGain.Value;
            if (gain != 1f)
            {
                for (int k = 0; k < TotalCoeffs; k++)
                    _accum[k] *= gain;
            }

#if DEBUG
            if (DumpOnce)
            {
                DumpOnce = false;
                DumpToLog(smr, mesh, idx, gain);
            }
#endif

            mat.SetFloatArray(ShaderIDs.BlendCompAffine, _accum);
        }

#if DEBUG
        /// <summary>一次性诊断标志：下一次 Push 时打印非零权重通道与合成 affine</summary>
        internal static bool DumpOnce;

        private static readonly string[] RegionNames = { "R_cheek", "L_cheek", "R_eye", "L_eye", "mouth" };

        private static void DumpToLog(SkinnedMeshRenderer smr, Mesh mesh, int[] idx, float gain)
        {
            var log = FaceSDFShadowPlugin.Log;
            log.LogWarning($"=== BlendComp dump: mesh={mesh.name} gain={gain} ===");
            for (int t = 0; t < CompTable.Length; t++)
            {
                int i = idx[t];
                float w = i < 0 ? -1f : smr.GetBlendShapeWeight(i);
                if (i >= 0 && w == 0f) continue;
                log.LogWarning($"  {(i < 0 ? "MISSING" : w.ToString("F1").PadLeft(6))}  {CompTable[t].Name}");
            }
            // 各区域在区域中心的合成位移（texel），直观看补偿量级
            var centers = new[]
            {
                new Vector2(0.38f, 0.40f), new Vector2(0.62f, 0.40f),
                new Vector2(0.40f, 0.57f), new Vector2(0.60f, 0.57f), new Vector2(0.50f, 0.32f),
            };
            for (int r = 0; r < RegionCount; r++)
            {
                var uv = centers[r];
                int b = r * CoeffPerRegion;
                float du = _accum[b] * uv.x + _accum[b + 1] * uv.y + _accum[b + 2];
                float dv = _accum[b + 3] * uv.x + _accum[b + 4] * uv.y + _accum[b + 5];
                log.LogWarning($"  {RegionNames[r],8}: du={du,7:F2} dv={dv,7:F2} texel (at region center)");
            }
            log.LogWarning("=== end dump ===");
        }

        /// <summary>
        /// BakeMesh 验证：分离"blendshape 贡献"与"残余（骨骼/其它系统）贡献"。
        /// 做法同帧两次烘焙：#1 当前姿势、#2 临时清零全部 blendshape（FBSAssist 下一帧
        /// 会自动重写回来，同帧内还原权重，渲染不受影响）。#1−#2 = 纯 blendshape 位移；
        /// 若屏幕位移 >> 该值，则脸颊上移来自骨骼动画等 blendshape 之外的驱动。
        /// 顶点在两帧间 SMR transform 不变的前提下直接比较（BakeMesh 输出同空间）。
        /// </summary>
        internal static void VerifyBake(SkinnedMeshRenderer smr)
        {
            if (smr == null || smr.sharedMesh == null) return;
            var log = FaceSDFShadowPlugin.Log;
            var shared = smr.sharedMesh;
            int nv = shared.vertexCount;

            var bakedNow = new Mesh();
            smr.BakeMesh(bakedNow);
            var pNow = bakedNow.vertices;

            // 清零全部通道 → 再烘一次 → 立即还原（FBSAssist 下一帧重写，双保险）
            int nc = shared.blendShapeCount;
            var origW = new float[nc];
            for (int i = 0; i < nc; i++)
            {
                origW[i] = smr.GetBlendShapeWeight(i);
                smr.SetBlendShapeWeight(i, 0f);
            }
            var bakedZero = new Mesh();
            smr.BakeMesh(bakedZero);
            var pZero = bakedZero.vertices;
            for (int i = 0; i < nc; i++)
                smr.SetBlendShapeWeight(i, origW[i]);

            // 按 UV 分桶统计顶点位移（|#1-#2| = blendshape 贡献）
            var uvs = shared.uv;
            var mags = new float[RegionCount][];
            var counts = new int[RegionCount];
            for (int r = 0; r < RegionCount; r++) mags[r] = new float[nv];
            // 区域矩形与离线 REGIONS 一致
            var rects = new[]
            {
                new Vector4(0.32f, 0.36f, 0.44f, 0.44f), new Vector4(0.56f, 0.36f, 0.68f, 0.44f),
                new Vector4(0.30f, 0.45f, 0.50f, 0.70f), new Vector4(0.50f, 0.45f, 0.70f, 0.70f),
                new Vector4(0.44f, 0.15f, 0.56f, 0.50f),
            };
            log.LogWarning("=== BlendComp BakeMesh verify: blendshape-only vertex displacement ===");
            var dvSum = new float[RegionCount];
            for (int v = 0; v < nv; v++)
            {
                var uv = uvs[v];
                for (int r = 0; r < RegionCount; r++)
                {
                    if (uv.x < rects[r].x || uv.x > rects[r].z || uv.y < rects[r].y || uv.y > rects[r].w)
                        continue;
                    var d = pNow[v] - pZero[v];
                    float texel = d.magnitude * TexelSize; // 位移换算按 UV 尺度粗算，量级参考
                    mags[r][counts[r]++] = texel;
                    dvSum[r] += d.y;
                    break;
                }
            }
            for (int r = 0; r < RegionCount; r++)
            {
                if (counts[r] == 0) continue;
                var arr = mags[r];
                System.Array.Sort(arr, 0, counts[r]);
                float meanDy = dvSum[r] / counts[r] * TexelSize;
                log.LogWarning($"  {RegionNames[r],8}: n={counts[r],4} |d| p50={arr[counts[r]/2],7:F2} " +
                               $"p95={arr[(int)(counts[r]*0.95)],7:F2} max={arr[counts[r]-1],7:F2} texel-ish, dyMean={meanDy,7:F2}");
            }
            log.LogWarning("=== end verify（|d| 单位粗略：mesh 单位×512，量级参考用）===");

            Object.Destroy(bakedNow);
            Object.Destroy(bakedZero);
        }
#endif

        private static int[] ResolveIndices(Mesh mesh)
        {
            if (_idxCache.TryGetValue(mesh, out var cached)) return cached;

            var arr = new int[CompTable.Length];
            for (int t = 0; t < CompTable.Length; t++)
                arr[t] = mesh.GetBlendShapeIndex(CompTable[t].Name);
            _idxCache[mesh] = arr;
            return arr;
        }

        /// <summary>
        /// 清掉已销毁 mesh 的缓存项。Unity fake-null 会持续占位，换头频繁时逐步积累。
        /// 由 FaceOverlayCore.Poll 的死条目清理顺带调用。
        /// </summary>
        internal static void CleanupDead()
        {
            List<Mesh> dead = null;
            foreach (var m in _idxCache.Keys)
            {
                if (m == null)
                    (dead ?? (dead = new List<Mesh>())).Add(m);
            }
            if (dead == null) return;
            foreach (var m in dead)
                _idxCache.Remove(m);
        }

        internal static void Dispose()
        {
            _idxCache.Clear();
            System.Array.Clear(_accum, 0, _accum.Length);
        }
    }
}
