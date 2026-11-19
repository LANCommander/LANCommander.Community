---
name: create-server-plugin
description: Use when creating or scaffolding a new LANCommander.Community server plugin, including its projects, host contracts, packaging, tests and registry integration.
---

# Create a server plugin

Create one installable server component, not a new host or an independent release
pipeline. Follow [CONTRIBUTING.md](../../../CONTRIBUTING.md),
[plugin maintenance](../plugin-maintenance/SKILL.md) and
[RELEASES.md](../../../RELEASES.md) for shared conventions.

## Establish the component

1. Inspect remote/status and fetch current canonical `main` from
   `LANCommander/LANCommander.Community`. Start on a separate feature branch
   based on that main; an existing clean, isolated task branch is sufficient.
   Never discard unrelated changes or rebase a dirty/unrelated branch silently.
   In an app-managed worktree, use the app's branch tooling.
2. Read [eng/plugins.json](../../../eng/plugins.json),
   [root build defaults](../../../Directory.Build.props),
   [central packages](../../../Directory.Packages.props), and the
   [Quake3 project](../../../plugins/Server.Quake3/LANCommander.Community.Server.Quake3/LANCommander.Community.Server.Quake3.csproj)
   and [entry point](../../../plugins/Server.Quake3/LANCommander.Community.Server.Quake3/Quake3Plugin.cs).
   Inspect the actual published SDK/Server.Plugins/UI APIs for the chosen
   baseline and verify package availability before adding references.
   Source evidence can be obtained from the immutable
   [host pin](../../../eng/lancommander-source.json) using
   `eng\Prepare-LANCommanderSource.ps1`; do not assume unreleased APIs exist.
3. Use component key `Server.<Name>` and root
   `LANCommander.Community.Server.<Name>` with exact casing. Put the entry
   project below `plugins\Server.<Name>` (a `src` directory is optional, as
   existing components demonstrate). Name its directory and `.csproj` basename
   after that root: **no `.Plugin` suffix**. Let SDK defaults derive `PackageId`,
   `AssemblyName` and `RootNamespace` from the filename. Helpers/tests append descriptive suffixes
   to the same root and remain non-packable.
4. Add the component `Directory.Build.props` importing
   `plugins\Directory.Build.props`. Shared plugin defaults load the component's
   `version.txt` for `Version`/`PackageVersion`; don't duplicate that logic in
   each component. Keep dependencies centrally versioned; only the entry
   project is packable. Add a README, license/notices where required, initial
   full SemVer and a non-empty changelog section for that exact version.

## Implement the server boundary

- Implement the verified `IPlugin` lifecycle and assembly
  `LANCommanderPlugin` attribute with `PluginHost.Server`, deliberate compatible
  host bounds, a unique stable semantic ID and informational version reporting.
  Keep the attribute and `IPlugin.Id` consistent. IDs are not package names:
  preserve semantic IDs, data IDs, config keys and protocols when replacing or
  extracting existing behavior. A package rename requires a new version/feed
  identity and explicit replacement instructions, never an overwritten package.
- Reuse host DI contracts for game/import/storage behavior; do not write host
  database rows through guessed schemas. Keep mutable plugin state in
  `PluginContext.DataDirectory`, not shipped output. Do not copy secrets into
  examples or log credentials.
- For UI, use verified route/navigation extensions and server UI controls.
  Inspect [Quake3](../../../plugins/Server.Quake3/README.md) and
  [RecompCatalog](../../../plugins/Server.RecompCatalog/README.md) for relevant
  patterns. Navigation visibility is not authorization: enforce page
  `PluginAccess` and endpoint/service authorization on the server. Test denied
  access as well as allowed behavior. Register host services with appropriate
  lifetimes and report initialization/operation failures explicitly.
- Declare `LANCommander.Plugin;LANCommander.Plugin.Server` package types and
  supplemental tags. Host package references use `ExcludeAssets="runtime"`
  and `PrivateAssets="all"`; never bundle SDK, Server.Plugins, Server.UI or
  host UI binaries. Bundle the complete private dependency payload under the
  target `lib` folder; suppress NuGet dependencies because the installer does
  not restore them. Follow existing package targets, not `dotnet publish` output.

## Integrate and verify

Update the root solution and `eng\plugins.json` registry with project/test paths,
canonical identities, independent version/tag prefix, host bounds, artifact
format and explicit feed eligibility. CI and release selection derive from this
registry; don't copy redundant allowlists or component counts into release
scripts and workflows. Inspect component-specific tests and workflow
conditions, adding only cases the registry-driven path does not cover. Update
README navigation and the release table; adding a project alone does not make
it selectable or releasable.

Add focused behavior/auth tests and a private-only package inventory check,
plus isolated loading of the **actual packaged entry assembly** with shared host
contract/UI identity (see Quake3's package and host-loader tests). Wire those
checks into `Build-Plugin.ps1`/`Validate-Plugin.ps1`.
Run the repository checks listed in RELEASES.md, then:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin Server.<Name> -OutputDirectory .\artifacts\new-server-validation
```

Use a fresh output directory. Verify emitted nuspec ID/version/types/repository,
assembly names, payload and loadability, not just filenames. Report live-host
behavior not exercised. Stop for unavailable contracts/packages, uncertain
authorization/data semantics or incompatible host bounds rather than inventing
adapters. Creation does not authorize pushing release tags, publishing packages,
changing feed permissions, opening a PR or migrating installed identities.
