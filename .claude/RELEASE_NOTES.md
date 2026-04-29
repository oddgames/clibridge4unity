## Fix: COMPILE no longer forces a recompile when nothing changed

v1.0.77 added a "fallback" path: if `AssetDatabase.Refresh` didn't trigger compilation within 250ms, call `CompilationPipeline.RequestScriptCompilation()` to force one. That made `COMPILE` always cost a domain reload, even on idle projects with no edits — visible in `Editor.log` as a lone:

```
Reloading assemblies after forced synchronous recompile.
```

That fallback is now removed. `COMPILE` is just `AssetDatabase.Refresh(ForceUpdate)` — Refresh handles the script-edit case on its own and does nothing if nothing changed. No reload when there's nothing to compile.

If you genuinely need to force a recompile on an unchanged project (rare — usually for stale-cache cleanup), edit any `.cs` and re-run `COMPILE`, or use the Unity Editor menu `Assets > Reimport All`.
