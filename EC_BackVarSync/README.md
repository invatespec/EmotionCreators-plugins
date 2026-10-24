# EC_BackVarSync

Variable save/load for **`EC_VariableMod`** global variables, designed to pair with **`EC_back`**.
Replaces `EC_VariableMod_SaveLoad` (script-tag driven) with GUI-driven workflows.

## Requirements

- **`EC_VariableMod`** — hard dependency (it owns the variables).
- **`EC_back`** — optional. Without it the chapter panel, auto-save, and advance keys still work; only the bookmark linkage is disabled.

> Remove the old `EC_VariableMod_SaveLoad.dll` before installing this one — running both makes every advance key press skip two cuts.

## Features

### EC_back bookmark linkage

- Pressing **Save** on an EC_back bookmark slot snapshots all VariableMod global variables (and 2D arrays) next to EC_back's own data: `<bookmark_dir>/<scene>/vars/<NN>.xml`.
- Clicking a bookmark thumbnail to jump back restores that slot's variables (a rejected/invalid jump restores nothing).
- **Clear**ing a slot in EC_back also deletes its variable file.
- EC_back's slider jump (bottom console) intentionally does not restore variables — it's a position jump, not a bookmark load.

### Chapter panel (default `F9`)

- Two-level structure: **play (剧本) → chapter (章节) → named saves**, shown as a collapsible tree with a compact fixed-size dark panel (`F9`, docked below EC_back by default, freely draggable — the manual position is remembered across restarts as a screen-relative percentage; reset `Panel.DockUnderECBack` in the F1 plugin settings to re-enable docking).
- Typical flow: finish chapter A → save; start chapter B's scene → load chapter A's save so variables carry over.
- Loading a chapter save restores variables only; it never jumps position (use EC_back bookmarks to return to a spot).
- With EC_back installed the panel shows/hides together with the expanded/collapsed EC_back window (config: `Panel.FollowECBack`); F9 also toggles it from any state.
- Clicks inside the panel are blocked from reaching the game (no accidental cut-advance); typing in its fields suppresses the advance key.

### Auto-save (per scene)

- Off by default. When enabled, saves variables + position every N cuts (default 10) to `UserData/PluginData/EC_BackVarSync/autosave/<scene>/AutoSave.xml`.
- Each scene (script) gets its own auto-save. Writes are throttled so fast-forwarding never stalls on disk IO.

### HPlay advance controls (inherited from EC_VariableMod_SaveLoad)

- `Advance.Key` (default `Enter`) — advance to next cut/part.
- `Advance.AutoToggleKey` (default `Backspace`) — toggle auto-advance; any other key interrupts it.

## Data layout

```
UserData/PluginData/EC_BackVarSync/
  chapters/<play>/<chapter>/<save>.xml   # manual chapter saves
  autosave/<scene>/AutoSave.xml          # per-scene auto-save
<EC_back bookmark_dir>/<scene>/
  vars/<NN>.xml                          # per-bookmark-slot variable snapshots
```

Save files are plain XML (timestamp + scene/part/cut + variables), one per slot/save.
