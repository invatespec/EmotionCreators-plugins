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

无头构建，不需要开 Unity 编辑器。注意 Unity 装在 D 盘：

```bash
"D:/Program Files/Unity/Editor/Unity.exe" \
  -batchmode -quit \
  -projectPath F:/Down/EC/AC/plugin/EC_FaceSDFShadow/UnityProject \
  -executeMethod BuildBundle.Build \
  -logFile F:/Down/EC/AC/plugin/EC_FaceSDFShadow/UnityProject/unity-build.log
```

构建期间 **Unity 编辑器不能开着同一个项目**（会抢 Library 锁）。
跑完看 log 尾部有 `Exiting batchmode successfully now!` 即成功。

### 编辑器菜单（手工）

Unity 菜单栏 → **Rainbowing → Build FaceSDF Bundle**

### 构建后（两种方式都要做）

1. 把 `AssetBundles/ec_facesdf.unity3d` 复制到 `plugin/EC_FaceSDFShadow/Resources/ec_facesdf.unity3d`（覆盖）
2. 回到 `plugin/EC_FaceSDFShadow/` 跑 `dotnet build -c Release`，bundle 作为 EmbeddedResource 打进 DLL
3. DLL 复制到 `G:\DL\z_Games\emotioncreators\BepInEx\plugins\Rainbowing\`

> 忘了复制第 1 步是最容易犯的错：dotnet build 会成功、DLL 也会更新，
> 但里面嵌的还是旧 shader，游戏里表现为"改了 shader 却毫无变化"。
> 复制后可比对 DLL 字节数是否变化来确认。

## 验证 bundle 是否正常

游戏启动后看 BepInEx 日志，应看到唯一的 shader：

```
Shader loaded: Rainbowing/FaceSDFOverlay
```

> v0.7 起 bundle 只含这一个 shader。另外五个（FaceSDFBake / FaceSDFPost /
> FaceSelfShadow×3）随阶段 1/2 烘焙与深度自遮挡 PoC 一并删除。

- 缺行 → shader 没打进 bundle，检查 `BuildBundle.cs` 的 `assetNames`
- `Failed to load shader bundle` → bundle 没复制到位或 Unity 版本不对
- `is not supported on this GPU/build` → shader 编译失败，通常是 Unity 版本不匹配

## 目录说明

```
UnityProject/
  Assets/
    Shaders/
      FaceSDFOverlay.shader     # 叠加阴影（唯一的 shader）
    Editor/
      BuildBundle.cs            # 构建菜单项
  AssetBundles/                 # 产出目录，gitignore
```

新增 shader 时记得在 `BuildBundle.cs` 的 `assetNames` 里追加，否则不会打进包。
