---
paths:
  - "src/**/*.cs"
---
# Privacy in logs and storage
- Never write notification or message bodies, meeting titles or meeting bodies to the activity log or the debug log.
- The ntfy topic is a secret (anyone who has it can read the user's notifications): keep it masked in the debug log.
- Secrets at rest use DPAPI (`ProtectedData`, CurrentUser); never store them in clear text.
