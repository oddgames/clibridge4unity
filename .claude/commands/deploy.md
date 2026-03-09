Deploy clibridge4unity: build, package, upload release assets, and verify.

Steps:
1. Run `deploy.ps1` from the project root using PowerShell
2. After deployment, verify the release has the correct assets by running: `gh release view v<VERSION> --json assets -q '.assets[].name'`
3. Verify the install script works by checking the download URL is accessible: `gh release view v<VERSION> --json assets -q '.assets[] | select(.name == "clibridge4unity-win-x64.zip") | .url'`
4. Report the release URL and asset list to the user

Usage:
- `/deploy` — Build and upload assets to the existing release (version from csproj)
- `/deploy create` — Also create a new GitHub release if it doesn't exist

The deploy script (`deploy.ps1`) handles:
- Building the CLI via `dotnet publish -c Release`
- Creating `clibridge4unity-win-x64.zip` from the published exe
- Copying the exe to `Package/Tools/win-x64/`
- Uploading both the zip and bare exe to the GitHub release

If the argument includes "create", pass `-CreateRelease` to `deploy.ps1`.

Run the PowerShell script like this:
```
powershell -ExecutionPolicy Bypass -File deploy.ps1
```
Or with create:
```
powershell -ExecutionPolicy Bypass -File deploy.ps1 -CreateRelease
```

After the script completes, always verify by listing the release assets with `gh release view`.
