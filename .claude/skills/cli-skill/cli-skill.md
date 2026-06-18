---
description: Author a new shippable clibridge4unity skill. Use whenever the user asks to "make/add/create a skill for the CLI / clibridge / the bridge", or to add a per-task skill that ships with the tool. Explains the file convention, where it lives, how it gets embedded into the exe and deployed to users, and how to verify it.
---

# Authoring a clibridge4unity skill

Skill goal: $ARGUMENTS

A "CLI skill" / "clibridge skill" is a single markdown file that ships **embedded in the CLI exe**
and is unpacked into each user's `<project>/.claude/skills/` by `clibridge4unity SETUP`. It teaches
Claude how to do one focused task with the bridge (or a related tool). This is NOT a personal
`~/.claude/skills/` skill and NOT a `.claude/skills/` repo-only skill — it must live in the embed dir.

## The one rule that makes it ship

Put the file here, named with the `clibridge4unity-` prefix:

```
clibridge4unity/skills/clibridge4unity-<topic>.md
```

That's it. The csproj globs it in automatically — see [clibridge4unity.csproj](../../../clibridge4unity/clibridge4unity.csproj):

```xml
<EmbeddedResource Include="skills\*.md">
  <LogicalName>skills/%(Filename)%(Extension)</LogicalName>
```

On `SETUP`, the CLI enumerates every `skills/*.md` embedded resource and unpacks it (the unpack +
delete-on-rename logic keys off the `clibridge4unity-` prefix — see `UnpackSkills` in
[clibridge4unity.cs](../../../clibridge4unity/clibridge4unity.cs)). **No manifest, registry, or list
to update** — adding the file is the whole registration step. Drop the prefix and it won't ship.

## File format

```markdown
---
name: clibridge4unity-<topic>
description: <what it does> + <when to use it — concrete triggers>. Use whenever you need to ...
---

# Title

Concise, runnable `clibridge4unity <CMD>` examples in bash fences. Show the 3-6 highest-value
invocations, a "how to choose" table if there are modes, and the gotchas. Match the house style of
the existing skills — terse, example-first, no fluff.
```

Conventions to copy from neighbors (e.g. [clibridge4unity-screenshot.md](../../../clibridge4unity/skills/clibridge4unity-screenshot.md)):

- **`name`** = filename without `.md` (`clibridge4unity-<topic>`).
- **`description`** is the retrieval hook — pack it with *what* + *when/triggers*. This is the only
  thing the model sees when deciding to load the skill, so make the triggers explicit.
- Keep it one file. If a skill genuinely needs helper scripts, it can't be a single embedded `.md` —
  reconsider scope or make it a repo `.claude/skills/<name>/` dir skill instead (those don't ship).
- Body uses real `clibridge4unity COMMAND ...` lines users can run verbatim.

## Steps to add one

1. Create `clibridge4unity/skills/clibridge4unity-<topic>.md` with the frontmatter above.
2. Write example-first content (steal structure from an existing skill in that folder).
3. Build so the resource embeds, then verify it unpacks:
   ```bash
   cd clibridge4unity && dotnet build -c Release
   clibridge4unity SETUP        # unpacks skills into <project>/.claude/skills/
   ```
   Confirm `<project>/.claude/skills/clibridge4unity-<topic>.md` appeared.
4. To ship to users, `/deploy` (bumps version + builds + releases). Per the deploy rule, **always
   bump the version** — never reuse a tag.

## Don't

- Don't put it in `~/.claude/skills/` (personal, not shipped) or `<repo>/.claude/skills/` (repo-only,
  not embedded) when the intent is to ship it with the tool.
- Don't forget the `clibridge4unity-` prefix — it's how SETUP identifies shipped skills to refresh.
- Don't register a CLI-side-only skill as a Unity `[BridgeCommand]`; skills are docs, not commands.
