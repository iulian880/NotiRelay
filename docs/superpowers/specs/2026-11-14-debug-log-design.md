# Debugging button: write a full debug log next to the exe

Approved by the user on 2026-11-14 (short design, small change).

## Intent

Deliveries to ntfy.sh fail on the user's Windows laptop and the on-screen log only shows a
short reason. The user asked for a "Debugging" button that, when pressed, writes everything
that happens to a file in the executable's folder, so they can send one file instead of
copying lines.

## Goals

- One **Debugging** button in the Activity log header (next to Copy / Clear). Click to turn
  on ("Debugging ON", highlighted), click again to turn off.
- File `NotiRelay-debug.log` in the folder of `NotiRelay.exe`; if that folder is not
  writable, `%LOCALAPPDATA%\NotiRelay\NotiRelay-debug.log`. The path is written to the log.
  A **Show file** button opens Explorer on it while debugging is on.
- Contents:
  1. Session header: app version, Windows, .NET, exe path, culture/time zone, settings
     summary (topic masked, Teams folder, options, monitoring state, queue size).
  2. Every Activity log line, with date and milliseconds; turning it on also copies what is
     already on screen.
  3. Details the screen does not show: full exception chain (type, message, HResult,
     socket error, HTTP status / HttpRequestError) with stack trace, delivery timings,
     unhandled exceptions.
  4. A network check for ntfy.sh each time debugging starts: system proxy for
     `https://ntfy.sh`, DNS answer, TCP connect to port 443, HTTPS GET
     `https://ntfy.sh/v1/health` through the app's own HttpClient. A one-line summary is
     also shown on screen.
- Stays on across restarts until turned off (the queue is flushed at startup, which is when
  failures show). Size cap: at 5 MB the file is renamed to `NotiRelay-debug.old.log` and a
  new one starts (at most ~10 MB).

## Non-goals / anti-goals

- Never write the ntfy topic (masked; any occurrence is redacted) or message text from
  Teams/Outlook (the activity log already never contains it).
- No upload, no sending the log anywhere; the user decides who gets the file.
- The network check only reads `/v1/health`; it never publishes and never tries other
  routes around a blocked network.

## Approaches

1. **Mirror the activity log + targeted detail hooks (chosen).** Every line already goes
   through `AddLog`; mirror it to the file and add exception detail at the few catch sites
   that matter (delivery, Send test, Teams watcher, unhandled). Small, no new dependency.
2. Add a logging framework (Serilog / Microsoft.Extensions.Logging) and route everything
   through it. More flexible, but a new dependency and a rewrite of every log call for a
   diagnostics toggle.
3. One-shot "Export log" button that dumps the screen. Simplest, but loses the details the
   screen never had, so it would not explain the delivery failure.

## Decided

- Platform-neutral pieces (`DebugLogFile`, `DebugReport`, `NetworkProbe`) are unit-tested
  on the Mac; WPF wiring is checked by build.
- `DebugTrace` is a small static sink so workers can add detail without constructor changes.
- The setting is stored as `DebugLoggingEnabled` in settings.json.

## Left to the builder

Exact wording of log lines, button colours (existing Warning brush), indentation format.
