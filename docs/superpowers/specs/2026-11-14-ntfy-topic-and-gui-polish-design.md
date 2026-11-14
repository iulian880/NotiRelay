# ntfy topic saving fixes + GUI polish — design

Date: 2026-11-14 · Size: small change · Status: approved by owner

## Intent
Owner asked to check whether the ntfy topic ("key") is saved correctly and to make any GUI improvement worth making.

## Findings that drive the work
1. Topic is not validated before saving: an invalid value (pasted URL, spaces) overwrites a valid saved topic.
2. `NtfyStatus` starts as "ntfy is not configured." even when a topic is saved.
3. "Save settings" with an empty box reports success although nothing was saved.
4. `settings.json` is written in place; a crash mid-write corrupts it and `Load()` silently returns empty settings (topic lost, no warning).
5. "Saved securely" label is shown even when the stored topic cannot be decrypted (other PC/user).
6. If the disk write fails, memory holds the new topic while disk holds the old one.

## Goals
- Saving a topic either succeeds fully (validated, encrypted, written atomically) or changes nothing, with a clear message.
- The UI always tells the truth about the saved topic's state.
- GUI polish: placeholder, show/hide, Enter to save, URL paste normalisation, weak-topic warning, masked "Saved: ••••k9x2" hint, coloured status messages, busy state on Test, header pill naming active sources, tray tooltip with state, window fits 768 px screens, activity log with own scroll + Copy/Clear, one-time "still running in tray" balloon.
- Outlook forwarding confirmation is persisted in settings (default unchecked), not forced on at every launch.

## Non-goals
- No self-hosted ntfy server support (host stays `ntfy.sh`).
- No change to delivery queue, Teams watcher, packaging.

## Approaches
- **Chosen:** keep the existing code-behind WPF structure; move platform-neutral topic logic (normalise / validate / mask / weak check) into `NtfyTopic.cs`, linked into the test project so it is unit-tested on any OS. UI changes in XAML + code-behind.
- Rejected: MVVM refactor — far larger than the request, no user-visible gain.
- Rejected: leaving validation in `MainWindow` — cannot be unit-tested off Windows.

## Constraints
- Development machine is macOS: verification = `dotnet build` (EnableWindowsTargeting) + unit tests. Visual check must happen on Windows.

## Builder's calls
- Weak-topic threshold: under 12 characters → warning (save still allowed).
- Mask shows only the last 4 characters.
- Existing installs: the persisted confirmation defaults to unchecked, so Outlook auto-start stops until the owner ticks it once (approved).
