# Outlook Calendar Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Send meeting changes, a daily agenda and 10-minute reminders from the classic Outlook calendar to ntfy.

**Architecture:** `OutlookCalendarReader` (late-bound COM, returns `CalendarMeeting` list or null when Outlook is not running) → `CalendarWatcher` (15 s loop: diff, agenda, reminders, enqueue) using pure `CalendarNotifications` and persisted `CalendarState`.

**Tech Stack:** .NET 10, WPF, COM late binding (`dynamic`), xUnit.

**Spec:** docs/superpowers/specs/2026-11-15-outlook-calendar-notifications-design.md

## Global Constraints

- Scan 15 s; window today 00:00 to +7 days; reminder lead 10 min; agenda 08:00.
- Never read the meeting body; never start Outlook.
- All notifications via `DeliveryQueue.EnqueueAsync` (gate and throttle apply).
- Version 1.3.0.

## Review Focus

- Midnight: the window moves a day; meetings of the new last day must not be reported as new.
- A meeting that ends or starts while the app runs must not be reported as cancelled.
- App restart within 10 minutes of a meeting → no second reminder.
- Outlook closed / busy (COM call rejected) → no crash, one status line, retry.
- Recurring meeting occurrences each get their own reminder.

---

### Task 1: CalendarNotifications + CalendarState (pure)

**Files:** Create `src/CalendarNotifications.cs`, `src/CalendarState.cs`; Tests `CalendarNotificationsTests.cs`, `CalendarStateTests.cs`; link both in the test csproj.

**Produces:** `record CalendarMeeting(Key, Subject, StartLocal, EndLocal, Location, AllDay, Cancelled, Declined, Free)`; `CalendarNotifications.Diff(before, after, nowLocal, previousWindowEndLocal)`, `DueReminders(meetings, nowLocal, lead, sentKeys)`, `ReminderKey`, `IsAgendaDue(nowLocal, lastAgendaDate, agendaTime)`, `AgendaMeetings(meetings, nowLocal)`, text builders; `CalendarState.Load(path)`, `Save()`, `MarkReminderSent`, `WasReminderSent`, `LastAgendaDate`, `Prune(nowUtc)`.

- [ ] Failing tests → implement → green.

### Task 2: Reader, watcher, UI

**Files:** Create `src/OutlookCalendarReader.cs`, `src/CalendarWatcher.cs`; Modify `src/AppSettings.cs` (`CalendarEnabled`), `src/MainWindow.xaml(.cs)` (card, start/stop, auto-start after the launch snapshot, pill, exit, debug summary), `src/NotiRelay.csproj` (1.3.0), `README.md`.

- [ ] Windows build 0/0, suite green, exe rebuilt and PE-checked.
