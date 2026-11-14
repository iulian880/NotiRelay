# ntfy topic saving fixes + GUI polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make saving the ntfy topic all-or-nothing and truthful in the UI, and polish the WPF window.

**Architecture:** Platform-neutral topic rules move to `src/NtfyTopic.cs` (linked into the test project, so they are unit-tested on macOS). `AppSettings` gets atomic, transactional saves and an honest read API. UI changes stay in the existing code-behind style.

**Tech Stack:** .NET 10, WPF + WinForms tray, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-14-ntfy-topic-and-gui-polish-design.md`

## Global Constraints
- ntfy host stays `https://ntfy.sh/`; topic: 1–64 chars, ASCII letters/digits, `_`, `-`.
- Weak-topic warning below 12 characters (save still allowed). Mask shows last 4 characters only.
- UI copy in English (existing convention). Build on macOS with `~/.dotnet/dotnet build src`.
- Commit only when the owner asks.

## Review Focus
1. Pasting `https://ntfy.sh/my-topic/` (scheme, trailing slash, surrounding whitespace/newline) → saved as `my-topic`.
2. Pasting a non-ntfy.sh URL (`https://ntfy.example.com/x`) → clear error, previously saved topic unchanged.
3. Corrupted `settings.json` → visible warning, corrupted file kept as `settings.corrupt.json`, not silently overwritten.
4. Double-clicking **Test ntfy** → a single send (button disabled while sending).
5. Topic changed while monitoring runs → next delivery uses the new topic (`_ntfy` reset; Teams reads via lambda).

---

### Task 1: `NtfyTopic` rules

**Files:** Create `src/NtfyTopic.cs`, `tests/NotiRelay.Tests/NtfyTopicTests.cs`; Modify `tests/NotiRelay.Tests/NotiRelay.Tests.csproj` (link `../../src/NtfyTopic.cs`).

**Interfaces — Produces:**
- `public static class NtfyTopic` with `const int MaxLength = 64`, `const int WeakBelowLength = 12`
- `static string Normalize(string? input)` — trim; drop `http://`/`https://`; drop leading `ntfy.sh/` (case-insensitive); drop `?query`/`#fragment`; trim trailing `/`.
- `static string? Validate(string topic)` — `null` when valid, otherwise the user-facing error.
- `static bool IsWeak(string topic)`; `static string Mask(string topic)` → `"••••" + last 4`, or `"••••"` when ≤ 4 chars.

- [ ] **Step 1: Failing tests** — `Normalize("  https://ntfy.sh/my-topic/ \n") == "my-topic"`, `Normalize("NTFY.SH/abc?x=1") == "abc"`, `Normalize("plain_topic") == "plain_topic"`; `Validate("my-topic") == null`; `Validate("")` contains `"Enter your ntfy topic"`; `Validate("ntfy.example.com/x")` contains `"ntfy.sh"`; `Validate("has space")` and `Validate(new string('a', 65))` not null; `IsWeak("short")`, `!IsWeak("notirelay-7f3k9q2x")`; `Mask("notirelay-7f3k9q2x") == "••••9q2x"`, `Mask("abc") == "••••"`.
- [ ] **Step 2:** `~/.dotnet/dotnet test tests/NotiRelay.Tests` → FAIL (type missing).
- [ ] **Step 3:** Implement `src/NtfyTopic.cs`; `MainWindow.ValidateTopic` delegates to it.
- [ ] **Step 4:** Tests → PASS.

### Task 2: `AppSettings` — atomic, transactional, honest

**Files:** Modify `src/AppSettings.cs`.

**Interfaces — Produces:**
- `bool OutlookForwardingConfirmed { get; set; }` (persisted, default `false`)
- `[JsonIgnore] string? LoadWarning` — set when `settings.json` existed but could not be read; the bad file is copied to `settings.corrupt.json`.
- `bool TryGetTopic(out string topic)` — `false` when empty or not decryptable (no throw).
- `bool HasStoredTopic` — `ProtectedTopic` not empty.
- `void SaveTopic(string topic)` — encrypt + `Save()`; on any exception restore the previous `ProtectedTopic` and rethrow.
- `Save()` writes `settings.json.tmp` then `File.Move(tmp, path, overwrite: true)`.

- [ ] **Step 1:** Implement; `~/.dotnet/dotnet build src` → 0 errors (DPAPI is Windows-only, so no unit test on macOS).

### Task 3: ntfy card UX

**Files:** Modify `src/MainWindow.xaml` (ntfy card), `src/MainWindow.xaml.cs` (ntfy region), `src/App.xaml` (status brushes, `GhostButton`/`LinkButton` styles).

**Interfaces — Consumes:** Task 1 + Task 2 APIs. **Produces:** `enum StatusKind { Neutral, Success, Warning, Error }`, `void SetStatus(TextBlock target, string text, StatusKind kind)`.

- [ ] **Step 1:** XAML: topic field in a `Grid` with a placeholder `TextBlock` (`Paste a topic name or an https://ntfy.sh/… link`), hidden `TextBox TopicPlainBox` for reveal, `ToggleButton` Show/Hide; `TopicSavedLabel` becomes a hint row; buttons **Save topic** (`SaveTopicButton`) and **Send test** (`TestNtfyButton`).
- [ ] **Step 2:** Code: Enter in either box saves; save flow = normalise → validate (error keeps old topic) → `SaveTopic` → success/weak warning; empty box + saved topic → neutral "Topic already saved…", empty + none → error. Test button disabled + "Sending…" during send. Initial status derived from `HasStoredTopic`/`TryGetTopic`; `LoadWarning` shown as error + logged. Saved hint: none / `✓ Saved: ••••9q2x · encrypted for this Windows user` / `⚠ The saved topic can't be decrypted on this account. Enter it again.`
- [ ] **Step 3:** Build → 0 errors, 0 warnings.

### Task 4: Header, tray, window, log, Outlook confirmation

**Files:** Modify `src/MainWindow.xaml`, `src/MainWindow.xaml.cs`, `README.md`.

- [ ] **Step 1:** Header subtitle `Teams & Outlook → ntfy → your phone`; pill text `RELAYING · TEAMS`, `· OUTLOOK`, `· TEAMS + OUTLOOK`; tray tooltip `NotiRelay — relaying (…)` / `NotiRelay — stopped`.
- [ ] **Step 2:** Window `Height=860`, `MinHeight=560`; on `SourceInitialized` clamp `Height`/`Top` to `SystemParameters.WorkArea`.
- [ ] **Step 3:** Close-to-tray balloon once per session (`ShowBalloonTip(4000, "NotiRelay is still running", "…double-click the tray icon to reopen…", Info)`).
- [ ] **Step 4:** Log card: fixed log height 240 with own scrollbar; dark-theme `ListBoxItem` style; **Copy** (clipboard, all lines oldest-first) and **Clear** buttons.
- [ ] **Step 5:** `AuthorizationCheckBox` bound to `OutlookForwardingConfirmed` (load on start, save on toggle, no forced `true`); README: "Save topic", URL paste, confirmation persisted.
- [ ] **Step 6:** `~/.dotnet/dotnet build src` → 0 errors, 0 warnings; `~/.dotnet/dotnet test tests/NotiRelay.Tests` → all pass.
