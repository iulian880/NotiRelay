# NotiRelay

Windows tray app (WPF + a WinForms tray icon, .NET 10, `net10.0-windows10.0.19041.0`) that relays Outlook/Teams notifications to a phone through ntfy.sh. Public repo: every push to `main` publishes a GitHub Release.

## Commands
On this Mac the SDK is `~/.dotnet/dotnet` (it may be missing from PATH).
- Tests (CI runs the same on windows-latest): `dotnet test tests/NotiRelay.Tests -c Release`
- Compile the app (works on macOS because `EnableWindowsTargeting` is set): `dotnet build src -c Release`
- Single-file exe (same as CI): `dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out/single`
- A local Stop hook (`.claude/settings.local.json` → `~/.claude/hooks/notirelay-exe.sh`) rebuilds `out/single/NotiRelay-<Version>.exe` whenever `src/` changes. When it fails it blocks the turn: fix the build.
- CI: `.github/workflows/build.yml` tests and publishes on windows-latest for every PR; its release job runs on push to `main`.

## Verification policy
- macOS can compile and run the unit tests, nothing more. Runtime behaviour (notification listener, tray, Outlook, autostart, the UI itself) only runs on Windows.
- Windows check: push the feature branch (ask first) and follow the PR run with `gh run watch`, or the user tries the exe on a Windows PC.
- Never report the app as runtime-tested unless it ran on Windows; say what stayed unverified.

## Structure
- `src/`: one project, no NuGet packages, no MVVM toolkit. Logic lives in code-behind (`MainWindow.xaml.cs`, 2k+ lines) plus plain classes.
  - Pipeline: `UserNotificationListener` / `TeamsInboxWatcher` + `TeamsMessageParser` → `DeliveryQueue` (DPAPI-encrypted, retry/backoff) → `DeliveryWorker` → `NtfyClient` → ntfy.sh.
  - Calendar: `OutlookCalendarReader` (COM), `CalendarWatcher`, `CalendarNotifications`, `CalendarState`.
  - Data: `%LOCALAPPDATA%\NotiRelay`; the ntfy topic and the queue are DPAPI-protected.
- `tests/NotiRelay.Tests/`: xUnit on plain `net10.0`, linking platform-neutral sources one by one (see `.claude/rules/tests.md`).
- Packaging: `build-package.ps1` (sparse-identity MSIX, creates the signing cert), `install-current-user.ps1`, `uninstall-current-user.ps1`.

## Architecture decisions (keep them)
- No new NuGet packages, UI frameworks or MVVM libraries without the user's approval.
- `UserNotificationListener` needs package identity, which the sparse MSIX (`Package.appxmanifest`) provides. Don't switch to a full MSIX, a service or a scheduled task.
- Outlook is read through late-bound COM (`dynamic` + `CLSIDFromProgID`/`GetActiveObject`, then `Marshal.ReleaseComObject`), with no COMReference. Classic Outlook only, while it runs.
- Background work runs on `Task.Run`; UI updates go through `Dispatcher.BeginInvoke`.
- Teams bodies come from a Power Automate → OneDrive JSON folder, which avoids Graph admin consent and public webhooks.

## Gotchas
- Release: bump `<Version>`, `<FileVersion>` and `<InformationalVersion>` in `src/NotiRelay.csproj` before a user-visible release; otherwise the next push to `main` overwrites the current release's exe.
- `.gitignore` ignores every `settings.json` except `.claude/settings.json`.
- The C# LSP (csharp-ls) reports a false `CS0121 ambiguous between X and X` (identical signatures) once a file linked into both projects (e.g. `NtfyClient.cs`) is open. Ignore it; `dotnet build` is the authority.
- One user-facing test string is Romanian on purpose.

## Safety
- Never commit, print or read signing material: `*.pfx`, `*.snk`, `*.thumbprint.txt`, the `-PfxPassword` value.
- Don't push to `main`, merge PRs or delete releases without the user's explicit approval: `main` is the public release channel.

## UI and design
- Visual tokens: the Palette section of `src/App.xaml` (brushes and styles).
- Design system, once generated: `design-system/notirelay/MASTER.md`, the single source for visual decisions. Generate it with the `ui-ux-pro-max` skill: `--design-system -p notirelay --persist --output-dir <repo root>`, with the real stack in the query text ("WPF desktop tray app ..."), never the HTML/Tailwind default. `--design-system` ignores `--stack`; run `--stack wpf` as a separate search for WPF implementation guidelines.
- Owners: visual decisions → `ui-ux-pro-max`; WPF correctness (XAML, binding, threading) → `wpf` skill.

## Local third-party skills (not in git, excluded via `.git/info/exclude`)
- `.claude/skills/ui-ux-pro-max`: nextlevelbuilder/ui-ux-pro-max-skill, tag v2.15.0, commit a38d04c. Local patch on 11 lines of SKILL.md: `python "${CLAUDE_PLUGIN_ROOT}/.claude/skills/ui-ux-pro-max` → `python3 "${CLAUDE_SKILL_DIR}`.
- `.claude/skills/wpf`: managedcode/dotnet-skills, tag catalog-v2026.11.14.0, commit 6c69830.
- Uninstall: delete the folder.

## Compaction
Always keep: the files changed in this session, the test/build commands above with their last result, the current branch and `<Version>`, and any approval still pending (push, merge, release).
