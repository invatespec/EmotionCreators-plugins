# FaceSDF Shader Bundle 构建说明

插件的 shader 以嵌入式 AssetBundle 形式打进 DLL。bundle **必须用 Unity 2017.4.24
构建**（EC 的引擎版本）——跨版本构建的 bundle 在 EC 里会静默加载失败或 shader 变粉。

shader 每次改动都要重跑一遍。

## 一次性准备

1. 装 Unity **2017.4.24f1**（Unity Hub 或官网 Download Archive 都行）
   - 安装时勾选 **Windows Build Support**
2. 用该版本打开本目录（`plugin/EC_FaceSDFShadow/UnityProject/`）
   - 首次打开会生成 `Library/`、`ProjectSettings/` 等，已在 .gitignore 排除
   - 如果 Unity 提示升级项目版本，**拒绝**

## 每次构建

### 命令行（推荐，AI/脚本可直接跑）

无头构建，不需要开 Unity 编辑器：

```bash
"path/to/your/2017.4.24f1" \
  -batchmode -quit \
  -projectPath ./plugin/EC_FaceSDFShadow/UnityProject \
  -executeMethod BuildBundle.Build \
  -logFile ./plugin/EC_FaceSDFShadow/UnityProject/unity-build.log
```

构建期间 **Unity 编辑器不能开着同一个项目**（会抢 Library 锁）。
跑完看 log 尾部有 `Exiting batchmode successfully now!` 即成功。

### 编辑器菜单（手工）

Unity 菜单栏 → **Rainbowing → Build FaceSDF Bundle**

### 构建后

跑 `dotnet build -c Debug`（默认 Debug，带诊断日志）
  - **`AssetBundles/` → `Resources/` 的复制已自动化**：csproj 的 `SyncShaderBundle`
     target 在编译前按时间戳同步，命中时打印 `[FaceSDF] shader bundle 有更新`
  - 若 shader 源码比 bundle 新（= 改了 shader 但没重打包），`WarnStaleShaderBundle`
     会点名具体文件告警——**看到它就回上一步重打 bundle**


## 验证 bundle 是否正常

游戏启动后看 BepInEx 日志（Info 级即可），应看到**三行**：

```
Shader loaded: Rainbowing/FaceSDFOverlay
Shader loaded: Rainbowing/HairShadowMask
Shader loaded: Rainbowing/HairShadowMarker
```

> 少任何一行 = 该 shader 不在包里，对应功能会静默降级（尤其 HairShadowMask 缺失
> 时发影完全失效，但不报错、只在 Rebuild 时告警一次；HairShadowMarker 缺失时
> 饰品头发受影禁用）。

- 缺行 → shader 没打进 bundle，检查 `BuildBundle.cs` 的 `assetNames`
- `Failed to load shader bundle` → bundle 没复制到位或 Unity 版本不对
- `is not supported on this GPU/build` → shader 编译失败，通常是 Unity 版本不匹配

## 目录说明

```
UnityProject/
  Assets/
    Shaders/
      FaceSDFOverlay.shader     # 叠加阴影（接收端）
      HairShadowMask.shader     # 发影遮罩写入端（R=有发、G=归一化眼深）
      HairMaskBlur.shader       # 遮罩软化：R 高斯平均 + G 邻域 min（深度膨胀）
    Editor/
      BuildBundle.cs            # 构建入口（菜单项 + batchmode）
      HairMaskPipelineTest.cs   # 无头对照实验（遮罩链各级质量/半影宽度）
  verify_embed.ps1              # 校验 DLL 内嵌 bundle 与 Resources/ 一致
  AssetBundles/                 # 产出目录，gitignore
  PipelineTest/                 # 无头实验回读产物，gitignore
```

新增 shader 时记得在 `BuildBundle.cs` 的 `assetNames` 里追加，否则不会打进包。
