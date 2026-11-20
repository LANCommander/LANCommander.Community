---
name: create-launcher-plugin
description: Use when creating or scaffolding a new LANCommander.Community launcher plugin, including source-pinned host integration, projects, packaging, loader tests and registry integration.
---

# Create a launcher plugin

Create one launcher component using verified host contracts. Shared requirements
come from [CONTRIBUTING.md](../../../CONTRIBUTING.md),
[plugin maintenance](../plugin-maintenance/SKILL.md) and
[RELEASES.md](../../../RELEASES.md); the existing
[MQTT README](../../../plugins/Launcher.MQTT/README.md) is evidence for launcher
packaging/storage, not a requirement to add MQTT or Home Assistant.

## Establish identity and host evidence

1. Inspect remote/status, fetch canonical
   `LANCommander/LANCommander.Community` main, and use a separate feature branch
   based on current main (an existing clean isolated task branch is sufficient).
   Preserve unrelated changes; stop if the base/worktree conflicts with the
   task. Use app branch tooling in an app-managed worktree.
2. Read [eng/plugins.json](../../../eng/plugins.json),
   [root defaults](../../../Directory.Build.props),
   [central packages](../../../Directory.Packages.props) and the
   [MQTT entry project](../../../plugins/Launcher.MQTT/src/LANCommander.Community.Launcher.MQTT/LANCommander.Community.Launcher.MQTT.csproj).
   Check published package availability and inspect actual APIs before choosing
   dependencies. Current launcher service contracts are source-coupled: use
   [eng/lancommander-source.json](../../../eng/lancommander-source.json) and
   `eng\Prepare-LANCommanderSource.ps1`, preserving `LANCOMMANDER_SOURCE_ROOT`
   and the explicit `LANCommanderSourceRoot` override. Never substitute a
   mutable checkout or fabricate NuGet adapters for missing contracts.
3. Choose key `Launcher.<Name>` and root
   `LANCommander.Community.Launcher.<Name>`. Under `plugins\Launcher.<Name>`,
   use `src\<root>\<root>.csproj` with exact casing and **no `.Plugin` suffix**.
   Let SDK defaults derive `PackageId`, `AssemblyName` and `RootNamespace`
   from the filename. Put host-independent logic and transports
   in suffixed helper projects only when needed; keep helpers/tests non-packable.
4. Add the component `Directory.Build.props` importing
   `plugins\Directory.Build.props`. Shared plugin defaults load the component's
   `version.txt` for `Version`/`PackageVersion`; don't duplicate that logic in
   each component. Use central dependency versions, initial independent full
   SemVer, matching non-empty changelog, README and required
   license/third-party notices.

## Implement the launcher boundary

- Inspect the
  [entry point](../../../plugins/Launcher.MQTT/src/LANCommander.Community.Launcher.MQTT/HomeAssistantLauncherPlugin.cs)
  and pinned host lifecycle/DI/settings APIs. Implement `IPlugin` and the
  assembly `LANCommanderPlugin` attribute with `PluginHost.Launcher`, a unique
  stable semantic ID (matching `IPlugin.Id`) and informational version reporting.
  Source-coupled MQTT has exact min/max host bounds; do not claim a wider range
  for a new adapter without evidence and loader tests.
- Separate GUI-only behavior from other host modes and clean up background work
  on host shutdown. Keep service scopes alive across asynchronous operations;
  use the authenticated user's visible resources and normal host services, not
  direct database changes or bypasses of host action/install checks. UI must
  remain usable when config is missing/invalid; fail operations visibly.
- Store writable settings/journals in the host-preserved `Data` directory under
  the plugin directory (or the verified context data API), never at package
  root. Ship only blank examples; mask credentials in UI and never log secrets.
  Preserve semantic IDs, data/config keys and external protocol/entity IDs for
  existing behavior. A new package ID is **not** an in-place upgrade: document
  stopped-host backup, removal of old entry DLLs and explicit transfer of `Data`.
  Same-ID updates preserve only `Data`; uninstall deletes it too.
- Declare `LANCommander.Plugin;LANCommander.Plugin.Launcher` package types plus
  tags. Reference host projects with `Private="false"` and `PrivateAssets="all"`
  as in MQTT, retaining exclusions for host-provided runtime/native payloads.
  Stage only verified private assemblies, `.deps.json` and blank examples under
  `lib/net10.0` for the current target. Suppress NuGet dependencies: the installer
  does not restore them. Do not package a ZIP wrapper or the host build output.

## Register and validate the deliverable

Add the solution entry and `eng\plugins.json` registry entry with project/tests,
identities, host bounds, version file, independent tag prefix, artifact format
and explicit feed eligibility. CI and release selection derive from this
registry; don't copy redundant allowlists or component counts into release
scripts and workflows. Inspect component-specific tests and workflow
conditions, adding only cases the registry-driven path does not cover. Update
root navigation and release documentation. Do not copy MQTT-specific
HA/template validation into a generic launcher component.

Add focused host-independent tests and source-pinned host-loader tests. Adapt
[Test-LauncherPackage.ps1](../../../eng/Test-LauncherPackage.ps1) and
[package tests](../../../plugins/Launcher.MQTT/tests/LANCommander.Community.Launcher.MQTT.HostLoader.Tests/PluginPackageTests.cs)
for the new component, not by weakening MQTT's checks. Validate the actual
extracted nupkg: correct-host discovery, wrong-host/version rejection, entry
assembly/private dependencies and shared host type identity, installation and
same-ID update preservation of `Data`. Wire this into component validation.

Run the repository checks from RELEASES.md and, after adding the new selection:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin Launcher.<Name> -OutputDirectory .\artifacts\new-launcher-validation
```

Use fresh output. Install extra test dependencies only if that component needs
them. Report untested real GUI/installation/integration behavior separately from
automated loader tests. Stop for unavailable source/packages, unknown storage
or auth contracts, or unverified compatibility. Creation is not authorization
to open a PR, publish packages, push release tags, change credentials/permissions
or migrate installed identities.
