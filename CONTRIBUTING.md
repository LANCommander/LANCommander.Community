# Contributing

Name each installable entry `.csproj` `LANCommander.Community.<Server|Launcher>.<Name>.csproj`,
with exact `LANCommander` casing and no `.Plugin` suffix. Use the SDK defaults
for `AssemblyName`, `RootNamespace` and `PackageId` rather than repeating the
identity in project files or props. Helpers/tests may append
`.Core`, `.Infrastructure`, `.Providers`, `.Transport` or `.Tests` (including
more focused test suffixes) to that component root and must remain non-packable.
Current component roots are:

- `LANCommander.Community.Server.Mcp`
- `LANCommander.Community.Server.RecompCatalog`
- `LANCommander.Community.Server.Quake3` and its `.Core` / test projects
- `LANCommander.Community.Launcher.MQTT`

## Repository layout

- `plugins/<Component>/` contains one independently versioned plugin, its
  `version.txt`, component documentation, and source and test projects. The
  exact docs and project subdirectories vary; use the plugin README for its
  layout and prerequisites.
- `eng/plugins.json` is the component registry. It maps each component key to
  its installable project and tests, and is used by the shared build,
  validation, CI, and release-selection scripts in `eng/`.
- `Directory.Build.props` holds shared .NET build defaults;
  `Directory.Packages.props` centralizes NuGet versions; and
  `plugins/Directory.Build.props` applies each component's version to its
  projects.
- `LANCommander.Community.slnx` is the solution for building the repository.
- `.github/skills/` has focused contributor workflows for
  [creating a server plugin](.github/skills/create-server-plugin/SKILL.md) and
  [creating a launcher plugin](.github/skills/create-launcher-plugin/SKILL.md).

Keep semantic plugin IDs, data IDs, configuration keys and protocol identities
stable. A package/assembly rename is a new feed identity and needs a new version
and explicit replacement instructions; never rewrite published versions. Keep
Home Assistant discovery as the HA-specific integration layer; Launcher.MQTT's
command/state protocol is usable by other MQTT clients too.

Shared compiler and package defaults live in the root `Directory.Build.props`.
`plugins/Directory.Build.props` imports them and reads the nearest component
`version.txt` for both entry and helper/test projects, keeping component versions
independent. Component-level props should contain only exceptions and import
the shared plugin props. Do not copy shared defaults into individual projects;
keep component-specific packaging and host runtime exclusions in their projects.

## Build one plugin

Install the .NET 10 SDK and PowerShell 7, then run commands from the repository
root. Use a component key from `eng/plugins.json`; component-specific
build prerequisites are listed in that plugin's README. The
[validation prerequisites](RELEASES.md#prerequisites-and-validation) also cover
extra tooling needed by component test suites.

Build one installable project in Release:

```powershell
pwsh .\eng\Build-Plugin.ps1 -Plugin Server.Mcp
```

To also create and verify a local NuGet package, provide a new output directory:

```powershell
pwsh .\eng\Build-Plugin.ps1 -Plugin Server.Mcp -Package -OutputDirectory .\artifacts\server-mcp-package
```

For the focused local check, including the component's tests and package
validation, use `Validate-Plugin.ps1` instead. Its output directory must also
not already exist:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin Server.Mcp -OutputDirectory .\artifacts\validation-mcp
```

See the root [build and validation guide](README.md#build-and-validate) for the
full-solution build and repository checks.

`eng/plugins.json` is the component allowlist used by build/validation scripts,
the CI matrix, and release selection. New components do not need a copied CI
matrix entry or release job. The generic component tag trigger still fails closed
on unknown prefixes, mismatched versions, and commits not reachable from `main`.
PR impact selection is conservative for shared or unknown paths; keep its
regressions in `eng/Test-CISelection.ps1` current when classifying a new docs-only
area. See [PR packages](README.md#pull-request-packages) for download links,
fork behavior and commenting prerequisites.

Before submitting changes, run the repository checks and the validation
command(s) for affected components from the root [README](README.md#build-and-validate).
See [Plugin releases](RELEASES.md) for versioning, packaging, and release
procedures. Repository-local agent guidance is under
[`.github/skills`](.github/skills/):
[creating server plugins](.github/skills/create-server-plugin/SKILL.md),
[creating launcher plugins](.github/skills/create-launcher-plugin/SKILL.md),
[plugin maintenance](.github/skills/plugin-maintenance/SKILL.md),
[release preparation](.github/skills/plugin-release/SKILL.md), and
[authoring more skills](.github/skills/repository-skill-authoring/SKILL.md).

Do not create or push release tags, publish releases, or change deployed
identities without explicit maintainer authorization.
