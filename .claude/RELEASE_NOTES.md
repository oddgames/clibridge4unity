## Fix: COMPILE no longer triggers double domain reload

Previous behaviour: `COMPILE` always called `AssetDatabase.Refresh(ForceUpdate)` *and* `CompilationPipeline.RequestScriptCompilation()`. If the user had edited a script, Refresh detected the change and triggered a compile + reload, then RequestScriptCompilation queued a *second* compile that fired right after — doubling domain reload time on every COMPILE after a script edit.

Visible in `Editor.log` as adjacent pairs:

```
Reloading assemblies after finishing script compilation.
Reloading assemblies after forced synchronous recompile.
```

On a large project (MTD-class) this turned every `COMPILE` after an edit into a multi-minute freeze.

Fix in `Compile()`: call `AssetDatabase.Refresh` first, sleep 250ms for Unity to start the compile if it's going to, then only call `RequestScriptCompilation` when `EditorApplication.isCompiling` is still false. Single reload in the common case, fallback to forced recompile only when nothing changed.
