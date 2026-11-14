# Launch Backlog Prompt and Stop Sending Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hold all sending until the launch question is answered, add a Stop/Resume sending control, and pace sending on ntfy 429.

**Architecture:** `SendingGate` (hold reasons) checked by `DeliveryWorker` before each item; `DeliveryThrottle` paces after 429 without failing the item; `DeliveryQueue` snapshot/remove for "start from now" and "discard"; `MissedMessages` builds prompt texts; `MissedMessagesDialog` becomes a generic warning prompt.

**Tech Stack:** .NET 10, WPF, xUnit (platform-neutral sources linked into tests).

**Spec:** docs/superpowers/specs/2026-11-14-launch-backlog-and-stop-sending-design.md

## Global Constraints

- Launch question text stays: "Do you want me to send the messages that you missed while the app was closed?"
- Pacing interval 5 s, doubling to 300 s on consecutive 429; normal speed only when the queue is empty.
- "No" at launch removes only queue items present before monitoring started.
- Version 1.2.0 (Version, FileVersion 1.2.0.0, InformationalVersion).

## Review Focus

- New notifications arriving while the launch popup is open → kept and sent after any answer.
- Stop sending pressed while a request is in flight → that one finishes, nothing after it.
- Resume with items waiting, user picks No → only items listed in the prompt are removed.
- ntfy daily quota (429 that never clears) → no hammering, a single log line.
- Popup closed with X → treated as No (start from now / discard).

---

### Task 1: SendingGate, DeliveryThrottle, NtfyErrors.IsRateLimit, prompt texts

**Files:** Create `src/SendingGate.cs`, `src/DeliveryThrottle.cs`; Modify `src/NtfyErrors.cs`, `src/MissedMessages.cs`; Tests `SendingGateTests.cs`, `DeliveryThrottleTests.cs`, additions to `NtfyErrorsTests.cs`, `MissedMessagesTests.cs`; link new files in the test csproj.

**Produces:** `SendingGate.Hold/Release/IsHeld(SendingHold)`, `IsOpen`; `DeliveryThrottle.RecordRateLimited(int pending, DateTime nowUtc) → string?`, `RecordSent(int pending, DateTime nowUtc) → string?`, `NextSendUtc`, `IsActive`; `NtfyErrors.IsRateLimit(Exception)`; `MissedMessages.Describe(int queued, int teams, int outlook, DateTime? oldest, DateTime now)`, `DescribeWaiting(int count, DateTime? oldest, DateTime now)`, `LaunchPrompt(string details)`, `ResumePrompt(string details)` → `PromptText`.

- [ ] Failing tests → implement → green.

### Task 2: Wiring

**Files:** `src/DeliveryQueue.cs` (`GetPendingSnapshotAsync`, `RemoveAsync`), `src/DeliveryWorker.cs` (gate + throttle), `src/MissedMessagesDialog.xaml(.cs)` (generic warning prompt), `src/MainWindow.xaml(.cs)` (launch flow, Stop/Resume button, pill, tray item, hints), `src/App.xaml` (Danger button style), `src/NotiRelay.csproj` (1.2.0), `README.md`.

- [ ] Windows build 0/0, full suite green, exe rebuilt and PE-checked.
