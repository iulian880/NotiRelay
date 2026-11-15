# Battery alerts, per-category ntfy topics, agenda at 08:45

Approved by the user on 2026-11-15 (short design).

## Goals

- **Power & battery alerts** (variant A, the app reads the battery itself): one alert when
  the laptop battery drops to **30%** and one at **10%** while it is not charging. No alert
  for plugging in or Battery saver (user's choice). A threshold is re-armed only after the
  battery climbs 5 points above it; starting below 10% sends only the 10% alert. Checked
  every 30 s. New "5 · Power & battery" card with a remembered on/off checkbox and the
  current level.
- **Separate ntfy topics per category**: Teams messages, Outlook notifications, Meetings,
  Power & battery. Each row in the ntfy card takes its own topic; empty means the main
  topic. Topics are validated and stored DPAPI-encrypted like the main one, shown masked,
  and "Use main" clears a row. Send test sends one test per distinct topic, naming the
  categories in the title. The debug log masks every configured topic.
- **Meetings agenda at 08:45** instead of 08:00.

## Approaches (battery)

- **A. Read the battery level with Windows' power API (chosen).** Works whether or not
  Windows shows its own toast.
- B. Forward Windows' battery toasts. Low-battery warnings are often system UI that the
  notification listener does not expose, so alerts could silently never arrive.

## Decided

- Pure logic (`BatteryAlerts`, `NotificationTopics`) is unit-tested; `BatteryWatcher` uses
  `Windows.System.Power.PowerManager` and the delivery queue (Stop sending and the launch
  question apply). Priorities: 30% high, 10% urgent.
- The main topic stays required; category topics are optional overrides.
- Version 1.4.0.
