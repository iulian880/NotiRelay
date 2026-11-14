# Missed-messages prompt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ask at launch whether to forward messages missed while NotiRelay was closed.

**Architecture:** Pure selection/formatting rules in `src/MissedMessages.cs` (unit-tested on any OS). `TeamsInboxWatcher` holds the missed files and applies the decision on its own worker thread. `MainWindow` captures missed Outlook toasts at start, shows `MissedMessagesDialog`, and keeps `LastActiveUtc` current.

**Tech Stack:** .NET 10, WPF, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-14-missed-messages-prompt-design.md`

## Global Constraints
- Dialog text verbatim: `Do you want me to send the messages that you missed while the app was closed?` Buttons `Yes` / `No`.
- Show the dialog only when at least one message was missed.
- Missed title prefix `Missed · `; body prefix `[<when>] `; titles max 120 chars.
- Branch `gui-polish` (PR #1). Commit and push at the end (owner asked for commit/PR flow on this PR).

## Review Focus
1. Answer arrives while the Teams worker is mid-scan → decision must be applied on the worker thread, no shared-collection races.
2. "No" then OneDrive touches a held file again → it must not be sent (keys recorded as processed, not just file names).
3. App killed without a clean exit right after forwarding an Outlook toast → that toast must not be re-offered (LastActiveUtc saved on every forward).
4. First launch after the update (no LastActiveUtc) → no Outlook toast is offered.
5. Teams monitoring stopped before answering → held state cleared, decision ignored safely.

---

### Task 1: `MissedMessages` rules
**Files:** Create `src/MissedMessages.cs`, `tests/NotiRelay.Tests/MissedMessagesTests.cs`; link the source in the test csproj.

**Produces:** `static class MissedMessages` with
- `const string Question`
- `string MessageKey(TeamsInboxMessage m)` → `ConversationId + ":" + MessageId`
- `record MissedTeamsMessage(string FileName, string Key, TeamsInboxMessage Message, DateTimeOffset SortTimeUtc)`
- `IReadOnlyList<MissedTeamsMessage> SelectUnsent(IEnumerable<(string FileName, TeamsInboxMessage Message, DateTimeOffset FileTimeUtc)> files, ISet<string> baselineFileNames, ISet<string> processedKeys)` — drop baseline names and processed keys, dedupe by key, sort by `CreatedAt ?? FileTimeUtc` ascending.
- `bool IsMissedOutlook(DateTimeOffset createdUtc, DateTimeOffset? lastActiveUtc)` — false when `lastActiveUtc` is null.
- `string FormatWhen(DateTime local, DateTime nowLocal)` → `today 09:14`, `yesterday 09:14`, `Tue 09:14` (2–6 days), `12 Nov 09:14`, `12 Nov 2025 09:14` (other year); invariant culture.
- `string Describe(int teams, int outlook, DateTime? oldestLocal, DateTime nowLocal)` → `12 Teams messages · 1 Outlook notification · oldest from Tue 09:14` (zero parts omitted, singular forms).
- `string Title(string title)`, `string Body(string text, DateTime? whenLocal, DateTime nowLocal)`.

- [ ] Failing tests for each rule → `dotnet test` FAIL (type missing) → implement → PASS.

### Task 2: Teams holding
**Files:** `src/TeamsInboxWatcher.cs`.
- `IReadOnlyList<MissedTeamsMessage> FindUnsent(string inboxPath)` (call only while stopped; parse failures skipped).
- `Start(string inboxPath, IReadOnlyList<MissedTeamsMessage>? held = null)`; scan skips held paths.
- `void ResolveHeld(bool send)`; applied at the top of the worker loop: send → enqueue oldest first with `MissedMessages.Title/Body`, record keys; skip → record keys; both save processed keys and clear held. `StopAsync` clears held.
- [ ] Build 0/0.

### Task 3: Outlook capture, heartbeat, dialog, launch flow
**Files:** `src/AppSettings.cs` (`DateTimeOffset? LastActiveUtc`), `src/MainWindow.xaml.cs`, create `src/MissedMessagesDialog.xaml(.cs)`, `README.md`.
- Capture `_previousActiveUtc` before the heartbeat (30 s timer, on Outlook forward, on exit, on session end) overwrites it.
- `StartForwardingAsync(automatic, captureMissed)` stores missed Outlook toasts (supported app, readable text, `IsMissedOutlook`).
- Loaded: Outlook auto-start → `FindUnsent` (background thread) → Teams start with held → if total > 0 show dialog → resolve both, log.
- [ ] Build 0/0, tests green, exe rebuilt, commit + push, pre-release asset refreshed.
