# Changelog

## 0.1.0-beta.1

- Add a separate administrator-only Discord webhook management page.
- Consume beta.4 server events with plugin-owned filters, enabled subscriptions,
  protected webhook credentials, safe message embeds and synthetic test delivery.
- Persist accepted work and bounded retries in host-preserved plugin Data storage.
  Expose delivery history, capacity warnings, secret failures and HTTP status.
- Add offline payload, transport, persistence, retry, authorization, rendered UI,
  registration and extracted-package loader tests.
- Document best-effort receipt, restart recovery, duplicate delivery, single-host
  storage and operational limits. Generic webhook management is unchanged.
