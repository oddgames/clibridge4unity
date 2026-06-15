---
name: clibridge4unity-assets
description: Search, discover, move, copy, delete, and label assets in a Unity project. Use whenever the task is about asset files on disk under Assets/ — finding them, organizing them, batch operations, fixing corrupted YAML. Not for prefab editing (unity-prefab) or scene objects (unity-scene).
---

# Assets

These operations are GUID-preserving where Unity supports it (`ASSET_MOVE`) — references in other assets/scenes are kept intact.

## Discover what's in the project

```bash
clibridge4unity ASSET_DISCOVER             # summary of all categories
clibridge4unity ASSET_DISCOVER ui          # UI prefabs, sprites, fonts
clibridge4unity ASSET_DISCOVER sprites
clibridge4unity ASSET_DISCOVER prefabs
clibridge4unity ASSET_DISCOVER scenes
clibridge4unity ASSET_DISCOVER fonts
clibridge4unity ASSET_DISCOVER shaders
clibridge4unity ASSET_DISCOVER materials   # grouped by shader
clibridge4unity ASSET_DISCOVER models
clibridge4unity ASSET_DISCOVER variants    # prefab variants
```

For UXML/USS/TSS specifically: `UI_DISCOVER` inventories UI Toolkit files + custom VisualElement registrations.

## Search

```bash
clibridge4unity ASSET_SEARCH t:prefab
clibridge4unity ASSET_SEARCH t:material shader:URP
clibridge4unity ASSET_SEARCH "PlayerCharacter"   # name match
```

Uses Unity Search syntax. On a miss the CLI returns "did you mean" suggestions.

## Move / copy / delete (batch-capable)

```bash
clibridge4unity ASSET_MOVE Assets/Old.prefab Assets/New.prefab          # rename
clibridge4unity ASSET_MOVE Assets/A.prefab Assets/B.prefab Assets/Bin/  # multi-source → folder
clibridge4unity ASSET_COPY Assets/A.prefab Assets/B.prefab

# Pull a child out of a prefab/scene as its own asset
clibridge4unity ASSET_COPY Assets/Level.prefab/Enemy Assets/Enemy.prefab
clibridge4unity ASSET_COPY scene/Player Assets/Player.prefab

clibridge4unity ASSET_DELETE Assets/Unused.prefab Assets/Old.mat        # batch
clibridge4unity ASSET_MKDIR Assets/Art/Textures/UI                       # nested, batch
```

## Labels

```bash
clibridge4unity ASSET_LABEL Assets/Player.prefab               # read
clibridge4unity ASSET_LABEL Assets/Player.prefab +Character    # add
clibridge4unity ASSET_LABEL Assets/Player.prefab -Old +V2      # add/remove
```

## Fix corrupted asset YAML

```bash
clibridge4unity ASSET_RESERIALIZE Assets/Broken.prefab
clibridge4unity REIMPORT Assets/Broken.prefab                  # alias
```

Forces re-validation and re-import. Use this when an asset's YAML is malformed but the file is mostly intact.

## When to write code instead

For complex queries ("find every Material whose albedo texture is null") `unity-run-code` with `AssetDatabase.FindAssets` + a filter loop is usually clearer than chaining ASSET_SEARCH queries.
