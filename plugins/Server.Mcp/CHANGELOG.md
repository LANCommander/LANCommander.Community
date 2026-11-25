# Changelog

## Unreleased

- Add actions, scripts, save paths and multiplayer modes tools
  (`list_*`, `get_*`, `create_*`, `update_*`, `delete_*`) using the host's
  `IOwnedRecordProvider` contracts. Actions, scripts and save paths of a game
  are scoped to a game version (`versionId`, or the latest version when omitted).
- Add read-only `list_media`, `get_media`, `list_archives` and `get_archive`.
- Add `get_key_counts`, which reports total, free and claimed CD keys without
  exposing key values.
- Tool calls now return the host's lookup and validation errors (for example an
  unknown game version) and malformed arguments as tool errors with their
  message, instead of a generic "An error occurred invoking" message. Other
  failures still return the generic message.
- Host packages and minimum host version are unchanged (2.2.0-beta.4).

## 0.2.3

- Link package/project metadata to the transferred
  `LANCommander/LANCommander.Community` repository. The canonical project,
  package and assembly name remains `LANCommander.Community.Server.Mcp`.
- Keep the published 0.2.2 package immutable; runtime plugin ID and host
  compatibility are unchanged.
- Exercise the isolated host loader and HTTP contracts against the extracted
  NuGet payload as well as local build output.

## 0.2.2

- Update host packages and minimum host version to LANCommander 2.2.0-beta.4.
- The MCP setup page now shows the endpoint derived from LANCommander's address
  and path base (with an unsaved override), and tabbed, copyable client setup
  for the GitHub Copilot app, Claude Code, Codex and general MCP clients using
  token placeholders or environment variables.
- The **Settings > Integrations > MCP** navigation entry now shows the `Plug`
  icon.

## 0.2.1

Monorepo migration release. Includes the MCP setup page and LANCommander
2.2.0-beta.3 compatibility work. Aligns package and assembly names with the
project; the semantic plugin ID is unchanged. The published `0.2.0` artifact
is not replaced.

## 0.2.0

Historical release imported from the original MCP plugin repository.
