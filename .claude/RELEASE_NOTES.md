### Changes
- Added project heartbeat metadata for discovering the correct Unity named pipe from the project folder.
- Added `SETUP` configuration for local Codex sandbox pipe access using a project-local allowed SID list.
- Added secured Windows named-pipe creation when project pipe access is configured.

### Verification
- Built the CLI with `dotnet build`.
- Refreshed the MTD Unity project against the local package and confirmed Unity compiled the package changes.
