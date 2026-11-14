# Missed-messages prompt at launch — design

Date: 2026-11-14 · Size: small feature · Status: approved by owner

## Intent
At launch the owner wants to choose whether messages that arrived while NotiRelay was closed are forwarded, or skipped so relaying starts from now.

## Current behaviour (before)
- Teams: unsent inbox files are always forwarded on the next start (implicit "Yes").
- Outlook: every toast present at start is ignored (implicit "No").
- Items already in the delivery queue are delivered after a restart (delayed, not missed).

## Behaviour (after)
1. On launch NotiRelay finds what was missed:
   - Teams: inbox files not in the first-run baseline whose message key was never forwarded.
   - Outlook (only when the confirmation is ticked and Outlook monitoring starts): toasts still in the notification center created after the last time NotiRelay was running (`LastActiveUtc`, saved every 30 s, on every forwarded toast and on exit).
2. Only if something was missed, a top-most dialog in the app's style asks
   **"Do you want me to send the messages that you missed while the app was closed?"**
   with a detail line (`12 Teams messages · 3 Outlook notifications · oldest from Tue 09:14`) and **Yes / No**.
3. Monitoring of new messages starts immediately; only the missed ones wait for the answer.
4. Yes → sent oldest first, title `Missed · <title>`, body prefixed with the original time (`[Tue 09:14] …`). No (or closing the dialog) → marked as handled, never sent.
5. Delivery-queue items are still delivered regardless.

## Non-goals
- Recovering Outlook toasts the user dismissed or Windows cleared.
- Asking when monitoring is restarted manually within a session.

## Approaches
- **Chosen:** start monitoring immediately and hold only the missed items until the answer — nothing new is lost when nobody answers (autostart at sign-in).
- Rejected: block all monitoring until answered — an unattended PC would forward nothing.
- Rejected: native MessageBox — cannot show the counts nicely, may open behind other windows when the app starts in the tray.

## Builder's calls
- Selection, ordering and wording live in platform-neutral `MissedMessages.cs`, unit-tested.
- Closing the dialog with X counts as No.
- Teams files that cannot be parsed at launch are not held; they follow the normal path.
