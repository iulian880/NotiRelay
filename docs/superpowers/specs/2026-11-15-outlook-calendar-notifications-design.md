# Outlook calendar notifications

Approved by the user on 2026-11-15 (short design, "without Join link").

## Why

The user wants to see on the phone when they have to attend meetings, close to real time.
Copying the company calendar into a personal iCloud/Google calendar was declined (company
data stored in a personal cloud account needs IT approval). Notifications through the
existing ntfy relay give the same "when do I have to be there" without storing meetings
anywhere else.

## Goals

- Read the **classic Outlook** calendar on this PC while Outlook is open (never start
  Outlook). Check every 15 s.
- **Changes** in the next 7 days: new, moved (old → new time) or cancelled meetings, sent
  right away. The first check after start only records what exists (no flood). Meetings that
  enter the 7-day window at midnight are not reported as new. "Cancelled" means Outlook's
  own Canceled flag; a meeting that just disappears (declined in classic Outlook, deleted,
  moved beyond 7 days) is not reported (review finding: the read cannot tell these apart).
  A recurring occurrence moved to another day keeps the key of its original slot, so it is
  reported as moved. An empty read right after a full one is ignored.
- **Agenda**: once a day at 08:00, or at the first check after 08:00, the rest of today's
  meetings; nothing when there are none.
- **Reminder** 10 minutes before each meeting with time and location. Skips declined,
  cancelled, all-day and "Free" items. Sent once per meeting and start time (persisted).
- Everything goes through the delivery queue, so Stop sending, the launch question and 429
  pacing apply.
- UI: an "Outlook calendar" card with Start / Stop, remembered across restarts; the header
  pill lists Calendar among the active sources.

## Non-goals / anti-goals

- No Teams Join link (user's choice): the meeting body is never read, which also avoids
  Outlook's security prompt.
- Nothing is written to any calendar; nothing is stored except reminder keys and the last
  agenda date.
- "New Outlook" is not supported (it has no programming interface for other apps).

## Approaches

1. **Poll the Outlook object model every 15 s and diff (chosen).** Late-bound COM, every
   reference released after each check so Outlook can close normally. Simple and robust;
   at most ~15 s delay.
2. Outlook item events (ItemAdd/ItemChange/ItemRemove). Instant, but removals do not say
   what was removed, recurring occurrences are awkward, and event sinks from .NET need the
   interop assembly; cannot be tested here.
3. Power Automate flow writing to OneDrive (like Teams). Works with new Outlook but adds
   minutes of delay and a flow for the user to build.

## Decided

- Pure logic (`CalendarNotifications`, `CalendarState`) is unit-tested on the Mac; the
  Outlook reader and watcher are Windows-only and covered by build.
- Meeting key: GlobalAppointmentID, plus the occurrence date for recurring meetings.
- Priorities: changes high, reminders urgent, agenda default.
- Version 1.3.0, branch stacked on `launch-prompt-stop-sending` (PR #2).
