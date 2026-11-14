# Launch backlog prompt, Stop sending, and 429 pacing

Approved by the user on 2026-11-14 (short design).

## Why

The user's debug log showed 140+ old Teams notifications in the persisted delivery queue.
The delivery worker started in the constructor and sent them before the launch question,
which only covered Teams files and Outlook toasts missed while closed. ntfy.sh then answered
429 (burst, then about one per 5 s), and the outage logic logged "reachable again" and
"paused" for every message. Stop Teams / Stop Outlook only stop capture, so the queue kept
draining with no way to halt it.

## Goals

1. **Nothing is sent at launch until the user answers.** The warning-style popup (amber band,
   ⚠, the user's own question) counts everything that would go out: unsent queue items from
   before, Teams messages and Outlook toasts missed while closed, oldest time.
   - Yes, send them: everything goes out oldest first.
   - No, start from now: the old queue items (snapshot taken before monitoring starts) are
     deleted and missed messages skipped; anything new that arrives meanwhile is kept.
   - No popup when there is nothing old to send (the user kept this choice).
2. **Stop sending** button (header, red) and tray menu item: nothing more goes to ntfy,
   including queued items (only a send already in flight finishes). It becomes **Resume
   sending**; if items are waiting it asks: send them, or discard them. Session-only; the
   header pill shows SENDING STOPPED.
3. **429 pacing:** on 429 the worker keeps the item (no failure count, order kept) and sends
   one every 5 s, doubling up to 5 min while every try is refused; one log line when this
   starts, one when the queue is empty again.
4. Stop Teams / Stop Outlook log a hint when notifications are still waiting.

## Approaches

1. **Hold gate in the delivery worker (chosen).** One `SendingGate` with two reasons
   (launch question, stopped by user); the worker runs but takes nothing while held. Small,
   keeps the queue and worker design, testable.
2. Start/stop the worker itself. The worker cannot be restarted once stopped and a stop
   waits for an in-flight request; more lifecycle code for the same effect.
3. Clear the queue on every launch. Simplest, but silently loses messages the user may want.

## Decided

- Popup texts and the backlog description live in `MissedMessages` (unit-tested);
  `SendingGate` and `DeliveryThrottle` are platform-neutral and unit-tested; queue snapshot
  and removal are on `DeliveryQueue` (DPAPI, Windows-only, covered by build).
- Version bumped to 1.2.0 so the v1.1.0 release is not overwritten.

## Left to the builder

Exact copy of hints and button tooltips, red button styling.
