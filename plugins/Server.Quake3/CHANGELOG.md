# Changelog

## 0.1.0-beta.2

- Link package metadata to the transferred
  `LANCommander/LANCommander.Community` repository. The canonical project,
  package and assembly name remains `LANCommander.Community.Server.Quake3`.
- Keep the published 0.1.0-beta.1 package immutable; runtime plugin ID,
  persisted server state and host compatibility are unchanged.

## 0.1.0-beta.1

- Update host packages and minimum host version to LANCommander 2.2.0-beta.4.
- Retain the host-routed file contracts and validate package loading against the
  beta.4 host packages.

## 0.1.0-alpha.1

- Add native administrator-only Quake 3 server selection, status, bounded
  match configuration, rotations, presets and match/map actions.
- Use server-side RCON credentials and host-routed files; Docker remains
  responsible for hosting, lifecycle, mounts and consoles.
- Add durable per-server plugin state and cached PK3/loose-map discovery with
  explicit refresh and stale/unavailable results.
- Add independent registry, core/host tests and package validation.
- Name the plugin, sidebar link and administration page Quake 3 Server Manager;
  use LANCommander's built-in GameController navigation icon.
- Use published LANCommander 2.2.0-beta.3 host-routed file contracts.
- Validate against actual published host packages and emitted package loading;
  journal startup write intent and live acknowledgement separately so partial
  external writes remain visible after plugin restart.
