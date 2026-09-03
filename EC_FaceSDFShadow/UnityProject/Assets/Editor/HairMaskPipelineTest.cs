// 无头复现(对照实验版):钉死 mask 相机的"CB 内容 vs 相机自身 pass"覆盖顺序,
// 以及 clearFlags/cullingMask 的正确取值。
//
// 用法(必须带图形,-nographics 不光栅化、回读全垃圾):
//   Unity.exe -batchmode -quit -projectPath UnityProject \
//     -executeMethod HairMaskPipelineTest.Run -logFile ptest.log
//
// 结论(见各实验注释):
//   A/C: clearFlags 默认(Skybox) → CB 的清屏色被相机自身 clear+skybox pass 覆盖。
//   D:   clearFlags=SolidColor + cullingMask=空层 → CB 内容完整存活,相机 pass 不污染。
//        且 CB 在空 cullingMask 下【仍然执行】——这是插件采用 D 配置的前提。
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class HairMaskPipelineTest
{
    // 与插件 HairShadowPass 一致:找一个没被项目命名的空层放置"什么都不画"的 cullingMask
    private const int EmptyLayer = 31;

    public static void Run()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "PipelineTest");
        Directory.CreateDirectory(dir);
        Debug.Log("[PipelineTest] colorSpace=" + PlayerSettings.colorSpace);

        // 发丝 alpha 纹理:3px 开/3px 关 竖条纹
        var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                tex.SetPixel(x, y, ((x % 6) < 3) ? new Color(1, 1, 1, 1) : new Color(1, 1, 1, 0));
        tex.Apply();

        var maskShader = Shader.Find("Rainbowing/HairShadowMask");
        if (maskShader == null) { Debug.LogError("[PipelineTest] HairShadowMask shader not found"); return; }

        var mSolid = new Material(maskShader);
        var mStripe = new Material(maskShader);
        mStripe.SetTexture("_MainTex", tex);
        mStripe.SetFloat("_Cutoff", 0.5f);

        // 实验A:旧插件配置(clearFlags 默认 Skybox + cullingMask=1)
        var rtA = MakeRT(1024, true); var rtAm = MakeRT(512, false); var rtAs = MakeRT(256, false);
        var qA = MakeQuads(mSolid, mStripe, 0);
        var camA = MakeCam(rtA, 1, false);
        var cbA = new CommandBuffer { name = "A_full" };
        cbA.SetRenderTarget(rtA);
        cbA.ClearRenderTarget(true, true, new Color(0f, 1f, 0f, 1f));
        cbA.DrawRenderer(qA[0], mSolid);
        cbA.DrawRenderer(qA[1], mStripe);
        cbA.Blit(rtA, rtAm);
        cbA.Blit(rtAm, rtAs);
        camA.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cbA);
        camA.Render();
        Dump("A_rt", rtA, dir);
        Dump("A_soft", rtAs, dir);

        // 实验B:纯相机直渲(无 CB),洋红背景——看相机自身 pass 到底画什么
        var rtB = MakeRT(1024, true);
        MakeQuads(mSolid, mStripe, 0);
        var camB = MakeCam(rtB, 1, true);
        camB.backgroundColor = new Color(1f, 0f, 1f, 1f);
        camB.Render();
        Dump("B_rt", rtB, dir);

        // 实验C:CB 只有洋红清屏(不画面片)+下采样——看 CB 内容能否在 Render 后幸存
        var rtC = MakeRT(1024, true); var rtCm = MakeRT(512, false);
        var camC = MakeCam(rtC, 1, false);
        var cbC = new CommandBuffer { name = "C_clearonly" };
        cbC.SetRenderTarget(rtC);
        cbC.ClearRenderTarget(true, true, new Color(1f, 0f, 1f, 1f));
        cbC.Blit(rtC, rtCm);
        camC.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cbC);
        camC.Render();
        Dump("C_rt", rtC, dir);
        Dump("C_soft", rtCm, dir);

        // ===== 实验D:候选修复配置 =====
        // clearFlags=SolidColor(绿) + cullingMask=空层(1<<31) + 面片放在 layer 0。
        // 两个待验证命题:
        //   D1 CB 在"cullingMask 空"时仍然执行吗?(若否 → 修复方案不可用)
        //   D2 相机自身 pass 是否还会把 layer 0 的面片画进来?(应当不会)
        // 判据:发区(条纹)存在 → D1 成立;背景恒为纯绿(0,1,0) → D2 成立、天空盒不再污染。
        var rtD = MakeRT(1024, true); var rtDm = MakeRT(512, false); var rtDs = MakeRT(256, false);
        var qD = MakeQuads(mSolid, mStripe, 0);
        var camD = MakeCam(rtD, 1 << EmptyLayer, true);
        camD.backgroundColor = new Color(0f, 1f, 0f, 1f);
        var cbD = new CommandBuffer { name = "D_fixed" };
        cbD.SetRenderTarget(rtD);
        cbD.ClearRenderTarget(true, true, new Color(0f, 1f, 0f, 1f));
        cbD.DrawRenderer(qD[0], mSolid);
        cbD.DrawRenderer(qD[1], mStripe);
        camD.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cbD);
        camD.Render();
        // 下采样链放 Render() 之后的 C# 侧
        Graphics.Blit(rtD, rtDm);
        Graphics.Blit(rtDm, rtDs);
        Dump("D_rt", rtD, dir);
        Dump("D_soft", rtDs, dir);

        // ===== 实验E:降采样链质量对照 =====
        // 命题:4× bilinear Blit 只取源 4×4 块居中 4 texel(丢 12 个),不是"16 texel 盒式低通"。
        // 对照:E_fast = 1024→256 一步 4×;E_good = 1024→512→256 连续 2× 折半。
        // 判据:发区中间值(非 0/1)占比——真低通应显著更高。
        var srcE = MakeRT(1024, true);
        var qE = MakeQuads(mSolid, mStripe, 0);
        var camE = MakeCam(srcE, 1 << EmptyLayer, true);
        camE.backgroundColor = new Color(0f, 1f, 0f, 1f);
        var cbE = new CommandBuffer { name = "E_src" };
        cbE.SetRenderTarget(srcE);
        cbE.ClearRenderTarget(true, true, new Color(0f, 1f, 0f, 1f));
        cbE.DrawRenderer(qE[1], mStripe);   // 只画条纹(高频信号)
        camE.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cbE);
        camE.Render();

        var eFast = MakeRT(256, false);
        Graphics.Blit(srcE, eFast);                     // 一步 4×
        Dump("E_fast256", eFast, dir);

        var eHalf1 = MakeRT(512, false); var eGood = MakeRT(256, false);
        Graphics.Blit(srcE, eHalf1);                    // 2×
        Graphics.Blit(eHalf1, eGood);                   // 2×
        Dump("E_good256", eGood, dir);

        Debug.Log("[PipelineTest] all done");
    }

    private static Renderer[] MakeQuads(Material mSolid, Material mStripe, int layer)
    {
        var a = CreateQuad("solid", new Vector3(0f, 0f, 0.10f), 0.30f, 0.20f, mSolid, layer);
        var b = CreateQuad("stripes", new Vector3(0.05f, 0.03f, 0.20f), 0.20f, 0.15f, mStripe, layer);
        return new[] { a, b };
    }

    private static Camera MakeCam(RenderTexture target, int cullingMask, bool solidColor)
    {
        var go = new GameObject("testcam_" + target.name);
        var cam = go.AddComponent<Camera>();
        cam.enabled = false;
        cam.orthographic = true;
        cam.orthographicSize = 0.45f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 2f;
        cam.cullingMask = cullingMask;
        if (solidColor) cam.clearFlags = CameraClearFlags.SolidColor;
        cam.transform.position = new Vector3(0, 0, 0.35f);
        cam.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
        cam.targetTexture = target;
        return cam;
    }

    private static RenderTexture MakeRT(int size, bool depth)
    {
        var r = new RenderTexture(size, size, depth ? 24 : 0, RenderTextureFormat.ARGBHalf)
        {
            name = "t" + size,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        r.Create();
        return r;
    }

    private static Renderer CreateQuad(string name, Vector3 pos, float w, float h, Material mat, int layer)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        go.layer = layer;
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        var mesh = new Mesh
        {
            vertices = new[]
            {
                new Vector3(-w/2, -h/2, 0), new Vector3(w/2, -h/2, 0),
                new Vector3(-w/2,  h/2, 0), new Vector3(w/2,  h/2, 0),
            },
            normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back },
            uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(0,1), new Vector2(1,1) },
            triangles = new[] { 0, 1, 2, 2, 1, 3 },
        };
        mf.sharedMesh = mesh;
        return mr;
    }

    private static void Dump(string tag, RenderTexture rt, string dir)
    {
        var old = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
        t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        t.Apply();
        RenderTexture.active = old;

        int w = rt.width, h = rt.height;
        Color c00 = t.GetPixel(2, 2), cTR = t.GetPixel(w - 3, h - 3), cC = t.GetPixel(w / 2, h / 2);
        float mn = 1, mx = 0, sum = 0; int n = 0;
        // 中间值统计:发区(R>0.02)里落在 (0.05,0.95) 的比例——判"是否真低通"
        int fg = 0, mid = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r = t.GetPixel(x, y).r;
                if (r <= 0.02f) continue;
                fg++;
                if (r > 0.05f && r < 0.95f) mid++;
            }
        for (int x = 0; x < 256 && x < w; x++)
        {
            Color c = t.GetPixel(Mathf.Clamp(w / 2 - 128 + x, 0, w - 1), h / 2);
            mn = Mathf.Min(mn, c.r); mx = Mathf.Max(mx, c.r); sum += c.r; n++;
        }
        Debug.Log(string.Format(
            "[PipelineTest] {0} ({1}x{2}) TL=({3:F3},{4:F3},{5:F3}) BR=({6:F3},{7:F3},{8:F3}) C=({9:F3},{10:F3},{11:F3}) rowR[min={12:F3} max={13:F3} mean={14:F3}] fg={15} midFrac={16:F3}",
            tag, w, h, c00.r, c00.g, c00.b, cTR.r, cTR.g, cTR.b, cC.r, cC.g, cC.b,
            mn, mx, sum / n, fg, fg > 0 ? (float)mid / fg : 0f));

        var png = new Texture2D(w, h, TextureFormat.RGBA32, false);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = t.GetPixel(x, y);
                png.SetPixel(x, y, new Color(Mathf.Pow(c.r, 1f / 2.2f), Mathf.Pow(c.g, 1f / 2.2f), Mathf.Pow(c.b, 1f / 2.2f)));
            }
        png.Apply();
        File.WriteAllBytes(Path.Combine(dir, tag + ".png"), png.EncodeToPNG());
        Object.DestroyImmediate(t);
        Object.DestroyImmediate(png);
    }
}
