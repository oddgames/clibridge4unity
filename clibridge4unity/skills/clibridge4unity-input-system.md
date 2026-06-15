---
name: clibridge4unity-input-system
description: Use for any input handling — keyboard, mouse, gamepad, touch, action maps, rebinding, mobile virtual buttons, deciding between the legacy `UnityEngine.Input` vs the new `Input System` package. Auto-trigger on `Input.GetKey`, `Input.GetKeyDown`, `Input.GetAxis`, `Input.GetMouseButton`, `InputAction`, `InputActionAsset`, `InputActionMap`, `PlayerInput`, `.performed +=`, `.canceled +=`, `OnEnable`/`OnDisable` action subscription, `Mouse.current`, `Keyboard.current`, `Gamepad.current`, `Touchscreen.current`, `EnhancedTouch`, action rebinding, "input doesn't work after scene reload", "action map not switching", "subscription leak", "touch input doubles up." Picking the wrong API (or mixing both) is the most common cause of silent input failures.
---

# Unity Input System

Unity ships two input APIs. The legacy `UnityEngine.Input` (a.k.a. "the old Input Manager") is polled-only, simple, and still works. The new `com.unity.inputsystem` package is event-driven, supports action maps + rebinding + multiple devices, and is what new projects should adopt. **Both can coexist** if you set "Active Input Handling" to *Both* in Player Settings — but mixing them in the same code path is the most common source of "input fires twice" or "input doesn't work at all" bugs. This skill is the picker + the subscription discipline.

## The one rule that prevents most of these

**Subscribe in `OnEnable`, unsubscribe in `OnDisable`, and never inside `Update`.** Every leaked input subscription is either a memory leak (closure capturing dead state) or a double-fire (subscribed twice, never unsubscribed once). The Input System package makes this easy with `action.Enable()` / `action.Disable()`. The old `Input` API has no subscriptions to leak but also can't be turned off — read it from `Update` only.

## When to use which

| Decision | Pick |
|---|---|
| New project, just starting | **New Input System** (`InputAction` + `InputActionAsset`). Worth the learning curve. |
| Established project entirely on `Input.GetKey` | Stay legacy unless rebinding/multi-device requirements appear |
| Need rebinding UI | New Input System (`InputActionRebindingExtensions`) |
| Multi-player local co-op | New Input System (`PlayerInputManager`) |
| Mobile touch beyond `Input.touchCount` | New Input System (`EnhancedTouch.Touch.activeTouches`) or `EnhancedTouch` enabled |
| Editor scripts polling for shortcuts | Legacy `Event.current` in `OnGUI` or `ShortcutManager` |
| Already shipped, considering migration | Don't, unless rebinding is on the roadmap. The migration is expensive |

## Pitfall catalog

### 1. Mixing `Input.GetKeyDown` and `InputAction.performed` in the same handler doubles up
"Active Input Handling = Both" + a `MonoBehaviour` that subscribes to an action AND polls `Input.GetKeyDown(KeyCode.Space)` for the same binding fires twice on each press.
- **Rule:** pick one API per project (or at least per system) and stick with it. If your project is mid-migration, the migrated systems use the new API exclusively; the legacy systems keep using `Input.*`. No system uses both.

### 2. Forgetting `action.Enable()` — the action is silent until enabled
A fresh `InputAction` (or one loaded from an `InputActionAsset`) is disabled by default. `action.performed += OnFire` subscribes but `OnFire` never fires.
- **Rule:** call `action.Enable()` in `OnEnable` (or when the action map should become active). `action.Disable()` in `OnDisable`. For an entire `InputActionMap`, call `map.Enable()` / `map.Disable()`. For an `InputActionAsset`, you can enable individual maps but not the whole asset at once.

### 3. Subscribing in `OnEnable` without unsubscribing in `OnDisable` leaks across scene loads
Standard pattern is to do `action.performed += OnFire;` in `OnEnable`. Forgetting to do `action.performed -= OnFire;` in `OnDisable` means the next scene's instance of the same MonoBehaviour subscribes again — duplicate callbacks, and the destroyed instance's callback still fires (with `MissingReferenceException` when it touches `transform`).
- **Rule:** every `+=` has a matching `-=` in the same component, in the symmetric Enable/Disable callback. If subscriptions get complex, use a list of `Action` cleanups: `_cleanups.Add(() => action.performed -= OnFire);` and iterate in `OnDisable`.

### 4. Action map switching mid-frame fires the new map immediately (or misses the next press)
Disabling map A and enabling map B in a callback fired from map A can cause the next `Update` to also process input through map B even though the player hasn't released the key. Different bindings, same press, double-fire.
- **Rule:** defer map switches one frame. `StartCoroutine` a no-op `yield return null` then switch, or use `Application.onBeforeRender` / `PlayerLoop` to switch outside the input phase. For UI-driven map switching (gameplay → menu), pair the switch with a brief disabled state of both maps.

### 5. `PlayerInput` component vs explicit subscription — pick one
`PlayerInput` is a convenience component that auto-wires action assets to method names ("OnFire" → invokes `void OnFire(InputAction.CallbackContext)` on the same GameObject). Explicit `action.performed += OnFire` is more code but more controllable. Mixing — having a `PlayerInput` AND subscribing manually — fires both, and the subscription order is implementation-defined.
- **Rule:** for single-player projects with one action set, `PlayerInput` is fine. For systems where you need conditional handling, fine-grained map switching, or runtime rebinding, use explicit subscriptions. Don't add manual subscriptions to a `MonoBehaviour` that already has `PlayerInput`.

### 6. `EnhancedTouch.Touch.activeTouches` and `Input.touches` give different snapshots
The new Input System's EnhancedTouch (`UnityEngine.InputSystem.EnhancedTouch`) lets you enumerate touches by ID, phase, and history. The legacy `Input.touches` is a snapshot of *this frame*. Mixing them confuses touch IDs across frames.
- **Rule:** for any touch handling beyond "is the user tapping?", use EnhancedTouch. Enable once at startup: `UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.Enable();`. Then iterate `Touch.activeTouches` — same IDs across frames, real phases (Began/Stationary/Moved/Ended/Canceled).

### 7. Reading `Mouse.current.position.ReadValue()` returns screen pixels, not GUI / world space
The new Input System's `Mouse.current.position.ReadValue()` is in screen pixels (origin bottom-left). UI Toolkit panel-relative coordinates and `Camera.ScreenToWorldPoint` both require this — but `EditorGUIUtility.GUIToScreenPoint` and IMGUI's `Event.current.mousePosition` use a different (origin top-left, GUI scale) coordinate system. Mixing crashes the math.
- **Rule:** know the coordinate space at every read. `Mouse.current.position.ReadValue()` → screen pixels (0,0 bottom-left, +Y up). UI Toolkit pointer events → panel-local. IMGUI `Event.current.mousePosition` → GUI space (0,0 top-left, +Y down). Convert at the boundary, not mid-calculation.

### 8. Composite bindings (WASD as Vector2) read with `ReadValue<Vector2>()`, not separate axes
"Move" as a composite binding (WASD → Vector2) is one action, one `ReadValue<Vector2>()` call. Trying to read it via `Input.GetAxis("Horizontal") + Input.GetAxis("Vertical")` defeats the purpose AND mixes APIs (see pitfall #1).
- **Rule:** for any composite (Vector2, Vector3, Axis), bind it in the action asset and `action.ReadValue<T>()` for the current state. `action.performed` fires once when the composite crosses the "interaction" threshold (e.g. any key down); for continuous reading, `ReadValue` in `Update`.

### 9. Rebinding UI without `InputAction.PerformInteractiveRebinding()` is reinventing the wheel
Custom keybind UIs that listen for the next key with their own `OnGUI` / `Input.anyKeyDown` polling miss controller axes, multi-key composites, and platform-specific bindings.
- **Rule:** use the package's `PerformInteractiveRebinding()` API. It handles every input type, lets you exclude binding paths (e.g. don't allow binding to mouse), and returns the bound path string that you can save to player prefs. Sample code is in the Input System package's `Samples~/Rebinding UI`.

### 10. Editor shortcuts via the legacy API fight with the Input System's "send events to game view" toggle
Pressing a key in the Scene view might be consumed by the Game view (if it has focus) and never reach your scene tool's `Event.current` check. Or your shortcut fires twice (once in Scene view, once routed to Game view).
- **Rule:** editor shortcuts → `ShortcutManager` (`[Shortcut("Tools/MyShortcut", KeyCode.X, ShortcutModifiers.Alt)]`) — the modern API. Scene tool gizmos → `Event.current` in `SceneView` callbacks; check `event.type` and `event.Use()` to consume the event so other handlers don't see it.

## Workflow

1. **Pick the API for this project.** New project → new Input System. Old project → stick with what works unless rebinding becomes a requirement.
2. **If new Input System:** create an `InputActionAsset`, define action maps (Gameplay, Menu, Pause), define actions per map, define bindings per action. Generate the C# wrapper (`Generate C# Class`) for compile-time access to action names.
3. **Subscription discipline:** every `MonoBehaviour` that listens to actions wires them in `OnEnable` (Enable + subscribe), unwinds in `OnDisable` (unsubscribe + Disable). Match `+=` with `-=` 1:1.
4. **Map switching:** one place owns the active map (`GameStateMachine.SwitchTo(GameState)` style). Switch via Disable old map → Enable new map → defer one frame if needed.
5. **Test the symmetry:** load the scene, fire input, reload the scene, fire input again. If callbacks fire 2× or 0×, you have a subscription bug.
6. **Touch:** `EnhancedTouchSupport.Enable()` once at startup. Iterate `Touch.activeTouches`. Never mix with `Input.touches`.

## Quick reference — explicit action wiring

```csharp
public class PlayerController : MonoBehaviour
{
    [SerializeField] private InputActionAsset _actions;
    private InputAction _move, _fire;

    private void OnEnable()
    {
        var map = _actions.FindActionMap("Gameplay", true);
        _move  = map.FindAction("Move", true);
        _fire  = map.FindAction("Fire", true);

        _fire.performed += OnFire;
        map.Enable();
    }

    private void OnDisable()
    {
        _fire.performed -= OnFire;
        _actions.FindActionMap("Gameplay").Disable();
    }

    private void Update()
    {
        var movement = _move.ReadValue<Vector2>();
        // apply movement
    }

    private void OnFire(InputAction.CallbackContext ctx) { /* fire weapon */ }
}
```

## Quick reference — map switching

```csharp
public class InputModeController
{
    private InputActionAsset _actions;

    public void SwitchTo(string mapName)
    {
        foreach (var map in _actions.actionMaps) map.Disable();
        _actions.FindActionMap(mapName, true).Enable();
    }
}
```

## Quick reference — legacy holdout pattern (for editor / debug shortcuts)

```csharp
void Update()
{
    if (!Application.isEditor) return;
    if (Input.GetKeyDown(KeyCode.F1)) ToggleDebugUI();
    if (Input.GetKeyDown(KeyCode.F2)) ReloadScene();
}
```
Legacy is fine for editor-only debug shortcuts. For production gameplay input, prefer the new system.

## Related
- `clibridge4unity-async-mainthread` — input callbacks fire on the main thread; don't block in them
- `clibridge4unity-domain-reload` — `InputActionAsset` survives reload (it's an asset), but in-flight subscriptions die — re-wire in `[InitializeOnLoad]`/`OnEnable`
- `clibridge4unity-editor-tools` — `ShortcutManager` + `Event.current` for editor-specific input
