# MariaDB Backup Tray (C# / WinForms)

A Windows system-tray utility that backs up selected MariaDB databases on a schedule via **Windows Task Scheduler**, compresses them into an AES-256 password-protected zip, and emails clients and dealers a success or failure notification.

## Features

- **Setup wizard** on first run:
  1. **ODY site key** (e.g. ODY9710), verified live against the Odyssey Control Panel webservice (`Get_CompanyDetailsOnConfig`) — setup only continues if the key exists and the site is registered for Cloud Backups. The verified site name + key are stored and shown on the backup emails. The same registration check runs again **before every backup**; a site reported as unknown or no longer registered aborts with a FAILED email, while an unreachable webservice only logs a warning (a network outage must not stop local backups).
  2. Database connection (server, user, password, port) with a live "Test connection" button.
  3. Database selection fetched from the server (system DBs hidden; Select all / none).
  4. Schedule: **"Every day"** or a specific weekday, plus time of day (24h).
  5. Client email(s) + dealer email(s) and a "Send test email" button (mail is sent via the built-in SMTP account).
  6. Zip password (optional), dump exe path (auto-detected, overridable), backup folder, retention count.
- **Test backup runs immediately** after setup finishes.
- **Windows Task Scheduler integration** — the backup runs at the scheduled time even when the tray app is closed. The task runs `"Odyssey Cloud Backups.exe" --backup` headlessly.
- **Dashboard** — opens after setup and on every manual launch (double-clicking the tray icon reopens it): last backup + result, next scheduled run, database count, retention, a full backup history list (date, result, trigger, databases, size, duration), and a Logs tab showing the backup log. Buttons for Run backup now, Settings (reopens the wizard pre-filled with current settings), Open backup folder, Refresh.
- **Tray menu**: Dashboard, Run backup now, Settings, Open backup folder, Exit. Autostart at login uses `--tray` so only the tray icon appears (no dashboard popup).
- **Run history** recorded to `%APPDATA%\MariaDBBackupTray\history.jsonl` (last 200 runs) — this feeds the dashboard.
- **Success / failure emails** to all client + dealer addresses; failure emails include the error message.
- Stored passwords (DB, SMTP, zip) are encrypted with **Windows DPAPI**.
- Automatic pruning of old archives (default: keep last 3 — each archive contains all selected databases, so that's 3 backups per database; long-term copies will live in AWS S3 once cloud upload is added).
- Optional tray autostart at login.

## Built-in email account

The SMTP account used for notifications lives in **`EmailDefaults.cs`** (host, port, user, password, security mode, From address) and is compiled into the exe. End users never see or configure SMTP — the wizard only asks for the client/dealer recipient addresses.

`EmailDefaults.cs` is **gitignored** because this repository is public: copy `EmailDefaults.cs.template` to `EmailDefaults.cs` and fill in the real credentials before building.

Use a dedicated send-only account with an app password (not a personal/admin login): compiled-in credentials can be extracted from the exe by a determined user, so keep the account's permissions minimal and rotate the password if you ever suspect exposure.

## Opening in VS Code

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download) and the **C# Dev Kit** extension in VS Code.
2. Open this folder in VS Code (`File → Open Folder`).
3. In the VS Code terminal:

```bat
dotnet restore
dotnet run
```

`dotnet restore` pulls the four NuGet packages (MySqlConnector, SharpZipLib, MailKit, ProtectedData) the first time.

To debug with breakpoints: Run → Start Debugging (F5) and pick **C#** when prompted; VS Code generates the launch config automatically.

## Publishing a single exe

```bat
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true --self-contained
```

Output: `bin\Release\net8.0-windows\win-x64\publish\Odyssey Cloud Backups.exe` — one file, no .NET install required on the target machine. (Drop `--self-contained` for a much smaller exe if .NET 8 is installed on target machines.)

## Installer

`installer\build-installer.ps1` publishes the exe and compiles an Inno Setup installer to `installer\Output\OdysseyCloudBackupsSetup-<version>.exe` (requires Inno Setup 6: `winget install JRSoftware.InnoSetup`). The installer:

- Installs **per-user** to `%LOCALAPPDATA%\Programs\Odyssey Cloud Backups` — no admin rights needed, which also lets the auto-updater replace the exe without elevation.
- Creates Start Menu + optional desktop shortcuts and offers to launch the app (which runs the setup wizard on first install).
- On uninstall, removes the scheduled backup task and the login autostart entry.

## Auto-update (GitHub Releases)

Set `GitHubRepo` in **`UpdateService.cs`** to the `"owner/repo"` that hosts releases (must be public; leave empty to disable). The app then checks the latest release at startup and every 12 hours; a newer release's exe asset is downloaded in the background and swapped in automatically on the next app start (rename-self + relaunch, no admin needed).

To publish an update:
1. Bump `<Version>` in the csproj (e.g. `1.0.1`) and run `installer\build-installer.ps1`.
2. Create a GitHub release tagged `v1.0.1` and attach the published **exe** as an asset (the updater picks the first `.exe` asset not containing "Setup"; attach the installer exe too for new installs if you like).

## How the backup works

The app shells out to `mariadb-dump.exe` / `mysqldump.exe` with `--single-transaction --routines --events --triggers`. The exe is auto-detected across common installs (MariaDB, MySQL, XAMPP, WAMP, Laragon) with a manual override in the wizard. Credentials are passed via a temporary defaults file so the password never appears in the process list.

## File locations

| What | Where |
|---|---|
| Config | `%APPDATA%\MariaDBBackupTray\config.json` |
| Log | `%APPDATA%\MariaDBBackupTray\backup.log` |
| Backups (default) | `%APPDATA%\MariaDBBackupTray\backups\backup_YYYYMMDD_HHMMSS.zip` |
| Scheduled task | Task Scheduler → `MariaDBBackupTray` |

## Notes

- The scheduled task runs under the logged-in user. If you need backups while **nobody is logged in**, open Task Scheduler, edit the `MariaDBBackupTray` task, and enable "Run whether user is logged on or not" (Windows will ask for the account password).
- Gmail / Office 365 SMTP typically require an app password.
- AES-encrypted zips open with 7-Zip or WinRAR (Windows Explorer can't open AES zips).
- To re-run setup: tray menu → Settings, or delete `config.json` and restart.
- **Reset settings** (dashboard button): erases the config, backup history, scheduled task, and autostart, then restarts the app into the setup wizard. Backup archives are kept. No backups run until setup is completed again.
