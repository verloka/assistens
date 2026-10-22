# Personal Agent Windows preview

Version **1.0.0-preview.1**, Windows x64. Extract the entire ZIP into a stable,
private local folder and run **PersonalAgent.App.exe**. Keep every bundled file
together. The package includes .NET and Windows Desktop runtime; it needs no
separate .NET installation or administrator privileges. Other architectures are
not included. This preview is unsigned and still has the release gates recorded
in the repository tracker; publishing a ZIP does not mean final MVP acceptance.

On first use, choose Local demo in Setup for a no-API text task. Real models,
MCP and Telegram need your own keys and explicit tests/permissions. The app
does not ship credentials or automatically authorize screen/input/tool access.
History/settings stay under `%LOCALAPPDATA%\PersonalAgent`; credentials are in
its separate CurrentUser DPAPI-protected secret store. Keep both private.

**Start with Windows:** open Settings, review the checkbox, then explicitly
click **Save startup choice**. It is off for a fresh Windows profile. Opening
Settings, toggling without saving, importing settings or exiting never enables
it. Saving creates only this user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
value `PersonalAgent.App`, containing the quoted installed executable path. At
sign-in it opens the normal app; desktop/idle/approval/recovery gates still apply.
Windows policy/Task Manager can independently disable startup entries.

**Exit:** the tray's Exit stops services and releases the tray/hotkey/hooks.
Closing the main window exits unless you explicitly selected Keep running in
tray. Exit stops this session; it does not change your saved login startup choice.
Disable startup in Settings before removing/moving the folder. After moving to a
new version's folder, deliberately save startup again to update the path. Use a
local folder available at sign-in, not a mapped/network drive or temporary ZIP
extraction location. Development launches via dotnet/testhost cannot enable it.

**Upgrade:** exit every instance, preserve private app data, and extract the new
package into another stable folder. The new app checks the schema under exclusive
DB ownership, verifies a SQLite backup before pending migrations, and preserves
history. It refuses unknown newer migrations or failed backups. No automatic
downgrade/backup restore is performed; old snapshots can lack later action receipts.
Backups in `history.db.backups` contain private history, exclude separate secret
files, and are outside automatic retention. Portable Settings export excludes
credentials/history/consent and imports disabled templates; it is not a DB restore.

For a local packaging diagnostic, run `PersonalAgent.App.exe --package-smoke`
from PowerShell using `Start-Process -Wait -PassThru` and inspect its exit code.
Exit0 means the **bundled** CoreCLR/WPF assemblies loaded, actual unshown XAML,
SQLite migrations and one persisted local fake task worked. It creates/deletes
isolated temporary data, opens no windows, installs no hooks/tray, changes no
production data/autostart, sends no native input and makes no API calls. This
mode deliberately refuses a framework-dependent development build.

Build a fresh local ZIP from the repository:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-windows.ps1
```

The script uses locked dependencies and `dotnet publish -c Release` with the
project's win-x64/self-contained defaults, validates bundled components and runtime
configuration, runs the diagnostic with empty installed-runtime roots, and emits
a ZIP/SHA256 under a new `artifacts/packages` directory. It never installs anything
or registers login startup. Host trace and fixed diagnostic output stay locally
beside the ZIP; the archive contains only published files and these instructions.

Before accepting PA-121, verify the extracted package in a clean Windows x64
account/VM **without preinstalled .NET**: launch Setup, complete one Local demo,
check tray Close/Open/Exit, opt into login startup and sign out/in to observe it,
disable it and confirm no launch on the following sign-in. Report Windows version,
package SHA256, absence of .NET, demo result and each observed startup/exit result.
Repository tests and redirected runtime roots on a developer PC cannot establish
this separate clean-OS/sign-in acceptance. No screenshots or keys are needed.

Preview changes: explicit safe settings templates, verified pre-upgrade/private
SQLite backups, optional per-user login startup and a self-contained x64 ZIP.
The tracker retains outstanding corporate/paid-provider/hardware/fresh-profile
acceptance. Do not treat these release notes as evidence those gates passed.
