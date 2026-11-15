# Battery Alerts and Topic Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Low-battery alerts at 30% and 10%, a topic per notification category, agenda at 08:45.

**Architecture:** `NotificationTopics` resolves a category to its topic (override or main); `AppSettings` stores overrides encrypted; producers enqueue with `GetTopicFor(category)`. `BatteryAlerts` decides alerts from (percent, on battery); `BatteryWatcher` polls `PowerManager` every 30 s.

**Tech Stack:** .NET 10, WPF, WinRT `Windows.System.Power`, xUnit.

**Spec:** docs/superpowers/specs/2026-11-15-battery-alerts-and-topic-routing-design.md

## Global Constraints

- Thresholds 30 and 10, re-arm margin 5, poll 30 s; no plug-in / Battery saver alerts.
- Categories: Teams messages, Outlook notifications, Meetings, Power & battery; empty = main topic.
- Agenda 08:45. Version 1.4.0.

## Review Focus

- Battery reading jitter around 30% → only one alert.
- App started at 8% on battery → only the 10% alert.
- A category topic that fails validation → nothing saved for that row, clear message.
- Settings copied from another PC (category topic cannot be decrypted) → falls back to main topic, no crash.
- Debug log never contains any configured topic in clear.

---

### Task 1: Pure logic — `NotificationTopics`, `BatteryAlerts`, multi-topic redaction (tests first)
### Task 2: Wiring — `AppSettings` overrides, `BatteryWatcher`, ntfy card rows, battery card, routing in producers, Send test per topic, agenda 08:45, README, version
