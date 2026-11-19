# Changelog

## 0.4.4

- Rename the entry project, namespace, assembly and NuGet package to
  `LANCommander.Community.Server.RecompCatalog` (remove `.Plugin`).
- This is a new feed identity, not an update to the published
  `LANCommander.Community.Server.RecompCatalog.Plugin` 0.4.2/0.4.3 packages.
  Follow the README's stopped-host replacement procedure; the runtime plugin
  ID, catalog/game/ROM IDs, configuration and imported data are unchanged.
- Link package metadata to the transferred LANCommander community repository.

## 0.4.3

- Update host packages and minimum host version to LANCommander 2.2.0-beta.4.
- Emit the beta.4 `ActionType.Primary` discriminator in generated game manifests.

## 0.4.2

Monorepo migration release. Builds against the LANCommander 2.2.0-beta.3
dependency baseline. Aligns package and owned assembly names with their project
names; the semantic plugin ID is unchanged.

## 0.4.1

Existing source version imported from the original Recomp Catalog repository.
