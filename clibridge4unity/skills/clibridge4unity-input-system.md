---
name: clibridge4unity-input-system
description: Use for any input handling — keyboard, mouse, gamepad, touch, action maps, rebinding, mobile virtual buttons, deciding between the legacy `UnityEngine.Input` vs the new `Input System` package. Auto-trigger on `Input.GetKey`, `Input.GetKeyDown`, `Input.GetAxis`, `Input.GetMouseButton`, `InputAction`, `InputActionAsset`, `InputActionMap`, `PlayerInput`, `.performed +=`, `.canceled +=`, `OnEnable`/`OnDisable` action subscription, `Mouse.current`, `Keyboard.current`, `Gamepad.current`, `Touchscreen.current`, `EnhancedTouch`, action rebinding, "input doesn't work after scene reload", "action map not switching", "subscription leak", "touch input doubles up." Picking the wrong API (or mixing both) is the most common cause of silent input failures.
---

# Unity Input System

Apply standard Unity input knowledge (action types, enable/disable + subscribe/unsubscribe symmetry, `Dispose` of generated wrappers, `PerformInteractiveRebinding` + binding-override JSON persistence, `EnhancedTouchSupport.Enable()`, Active Input Handling / `InputSystemUIInputModule` migration traps, `ReadValue<T>` typing, deferring map switches one frame). This project adds no CLI commands or house conventions for input beyond those standards.

## Related
- `clibridge4unity-async-mainthread` — callbacks run on main thread; don't block
- `clibridge4unity-domain-reload` — re-wire subscriptions after reload
- `clibridge4unity-editor-tools` — `ShortcutManager` + `Event.current` for editor input
