# Emotion Creators Plugins

BepInEx 5 plugins for **Emotion Creators** (Unity 2017.4).  
Focused on HEdit authoring QoL, ADV playback tooling, lighting, and memory fixes.

> Built for personal / community use. Drop compiled DLLs into `BepInEx/plugins/`.

## Requirements

- Emotion Creators
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx)
- Some plugins depend on other community mods (noted below)

## Plugins

### Authoring / HEdit

| Plugin | Version | Description |
|--------|---------|-------------|
| **[EC_HEditPosePanel](EC_HEditPosePanel/)** | 2.0.0 | Companion panel for Pose Adjust: keyboard/mouse axis control with Local/World translation modes, hand & skirt FK pose library (save / apply / folders / thumbnails), and guide open-close adjustment sliders. |
| **[EC_NodeSidePanel](EC_NodeSidePanel/)** | 2.1.0 | Node-chart side panel: search, sort, per-scene folders, canvas pan scrollbars, node nudge, and expanded node canvas (default 1×). Replaces `EC_NodeCanvasExpand`. |
| **[EC_BatchTextReplace](EC_BatchTextReplace/)** | 1.1.1 | Batch find/replace across ADV text in the current HEdit scene (hotkey window, default `F5`). |
| **[EC_NodePlayabilityDiag](EC_NodePlayabilityDiag/)** | 1.0.0 | When Play Check fails, logs *which* parts/nodes are unplayable instead of only showing the generic “not playable” UI. |
| **[EC_SceneExport](EC_SceneExport/)** | 1.2.1 | Export / import HEdit parts as `.part` files (and scene link data). Based on monophony’s Scene Export; hotkeys configurable (default Alt+E / Alt+I). |
| **[EC_ADVCameraViewport](EC_ADVCameraViewport/)** | 2.0.0 | Shrinks the ADV camera viewport so UI doesn’t cover the scene; syncs ADV canvas, auto-repositions Chara State panel, and shrinks the list panel. Toggle with `F8`. |

### Playback / Story

| Plugin | Version | Description |
|--------|---------|-------------|
| **[EC_VariableMod_SaveLoad](EC_VariableMod_SaveLoad/)** | 1.3.0 | Save/load for **`EC_VariableMod`**: persist variables + ADV position via `<save>` / `<load>` script tags, shared save folders across chapters, auto-save, and HPlay advance / auto-advance keys. **Requires `EC_VariableMod`.** |
| **[EC_GamePOV_DOFPatch](EC_GamePOV_DOFPatch/)** | 1.0.1 | Soft-depends on `EC_GamePOV`. While POV is on, temporarily disables global DOF so the eye-locked camera doesn’t go fully blurry; restores DOF when POV turns off. |

### Lighting

| Plugin | Version | Description |
|--------|---------|-------------|
| **[EC_LightingEnhance](EC_LightingEnhance/)** | 1.2.0 | Higher-quality ground shadows that show outfit silhouette (via map-layer shadow proxies; approach informed by [StarPlugins](https://github.com/starstormhun/StarPlugins)), optional character self-shadow direction lock, and sunlight intensity sync for mod maps. |

### Character / Face

| Plugin | Version | Description |
|--------|---------|-------------|
| **[EC_FaceSDFShadow](EC_FaceSDFShadow/)** | 2.0.0 | Anime-style hard-edge face shadows (Uma Musume / Genshin look) that sweep with the light: hand-drawable SDF threshold frames, per-character color & shape via MaterialEditor, a screen-picking shadow color mixer, and a neck-band blend for the jaw/neck seam. Hair-only shadow projection onto the face in two forms — screen-space offset silhouette and light-space per-object shadow map — with multi-character isolation and accessory-hair marking via MaterialEditor shader tags. One-click display-mode presets. See [USAGE.md](EC_FaceSDFShadow/USAGE.md). |

### Memory / Performance Fixes

| Plugin | Version | Description |
|--------|---------|-------------|
| **[EC_Fix_PoseDedup](EC_Fix_PoseDedup/)** | 0.1.0 | During **playback** scene load, interns identical `PoseInfo` instances so large scenarios don’t keep thousands of duplicate pose graphs on the managed heap(**16g → 4g**). Skipped in HEdit edit mode. |
| **[EC_Fix_AnimationClipCleanup](EC_Fix_AnimationClipCleanup/)** | 1.5.0 | On `ChaControl.LoadAnimation`, destroys previous body `AnimationClip`s that are no longer referenced, reducing clip churn when poses change. |
| **[EC_MemoryInvestigator](EC_MemoryInvestigator/)** | 0.7.0 | Diagnostics for ADV playback memory growth: per-cut snapshots, Material classification, GuideObject cleanup hooks, and optional manual/auto `UnloadUnusedAssets` probes. Intended for investigation, not everyday play. |

### Deprecated

| Plugin | Status |
|--------|--------|
| **[EC_NodeCanvasExpand](EC_NodeCanvasExpand/)** | **Merged into `EC_NodeSidePanel`.** Remove the old DLL to avoid double-patching `NodeControl.Update`. Kept only as historical reference. |


## License / credit

- Scene Export originates from **[monophony](https://github.com/monophony-hub/MonoECPlugins)** (adapted for BepInEx 5).
- High-quality ground / outfit silhouette shadows in `EC_LightingEnhance` draw on ideas from **[starstormhun/StarPlugins](https://github.com/starstormhun/StarPlugins)**.
