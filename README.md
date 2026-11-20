# LANCommander.Community

Community plugins for LANCommander Server and Launcher. The repository shares
build and validation infrastructure while maintaining independent plugin
projects and releases.

## Plugins

- [Server.Mcp](plugins/Server.Mcp/README.md)
- [Server.RecompCatalog](plugins/Server.RecompCatalog/README.md)
- [Server.Quake3](plugins/Server.Quake3/README.md)
- [Launcher.MQTT](plugins/Launcher.MQTT/README.md)

Each child README is the canonical guide for its plugin's requirements,
compatibility, setup, installation, behavior, and limitations.

## Build and validate

Install the .NET 10 SDK and PowerShell 7. Additional prerequisites for an
individual plugin, including any required source materialization, are
documented in its README. Consult those instructions before building the full
solution.

```powershell
dotnet build .\LANCommander.Community.slnx -c Release
```

Run the repository checks:

```powershell
pwsh .\eng\Test-ReleaseSelection.ps1
pwsh .\eng\Test-Repository.ps1
pwsh .\eng\Test-ReleaseDestinations.ps1
pwsh .\eng\Test-NuGetPublication.ps1
pwsh .\eng\Test-GitHubPackages.ps1
pwsh .\eng\Test-CISelection.ps1
node --test .\eng\pr-package-comment.test.cjs
```

Validate a single plugin and create its local install artifact with
`eng/Validate-Plugin.ps1`, selecting a component from the registry:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin <Server.Mcp|Server.RecompCatalog|Launcher.MQTT|Server.Quake3> -OutputDirectory <new-output-directory>
```

The output directory must not already exist. See each plugin README for its
focused build, test, and installation instructions.

## Pull request packages

PR CI validates and packages impacted plugins. Component changes (including
packaged READMEs) select that component; shared build, dependency, tooling,
workflow, registry, source-pin, and unclassified changes select all components.
Root documentation, `docs/`, and agent-skill-only changes do not produce packages.
Pushes to `main` continue to validate every registered plugin.

Download a ZIP from the **PR plugin packages** comment, or from the CI run's
**Artifacts** / job-summary links. Extract it to get the `.nupkg`, SHA-256 sidecar
and build manifest. Artifacts expire after seven days; GitHub sign-in and
repository access are required. Failed attempts can provide packages for
components that finished validation; the comment shows the overall run result.
Versions remain component-owned and unchanged: these are **untrusted PR builds,
not releases**, and nothing is published to a feed. Back up installed plugin data
before trying one; do not assume same-version packages are interchangeable.

Fork PRs use the same read-only build workflow, without repository secrets.
GitHub may require a maintainer to approve a fork's first run. A separate
`workflow_run` workflow posts or updates the comment using only artifact metadata
and tooling from its own trusted default-branch commit. It never downloads,
extracts or executes PR artifacts. It verifies the current PR head/repository
and excludes stale attempts. If commenting is denied, it reports a warning and
the CI artifact/job-summary downloads remain available.

The comment workflow must first exist on the default branch. Until this change
is merged, package links are available from the PR's CI run rather than an
automatic comment. Commenting also depends on Actions being allowed to request
`pull-requests: write`; no PAT or additional secret is needed.

## Repository guidance

- [Contributing](CONTRIBUTING.md) — shared project conventions and contributor
  checks.
- [Plugin release procedure](RELEASES.md) — versioning, validation, and
  component release workflow.
- [Repository agent skills](.github/skills/) — focused `SKILL.md` guidance for
  [creating server plugins](.github/skills/create-server-plugin/SKILL.md),
  [creating launcher plugins](.github/skills/create-launcher-plugin/SKILL.md),
  plugin maintenance, release preparation, and authoring more skills.

### Skill evals

The **Skill evals** workflow runs Vally spec lint on PRs that touch the
creation skills or eval configuration, including PRs from forks. Run
`npm ci` and `npm run lint` locally for the same static check. Lint validates
the eval definitions; it does not exercise skill routing or grade generated
plugin scaffolds. The workflow summary explains when model-based checks
were not run.

Model runs can use AI credits and require the organization policy
**Allow use of Copilot CLI billed to the organization**. Once enabled,
maintainers can manually dispatch **Skill evals** on a trusted ref to run
all cases (the workflow must be on the default branch to be dispatchable),
or set the repository Actions variable `ENABLE_SKILL_MODEL_EVALS` to `true`
to run affected cases automatically on same-repository PRs. Fork PRs never
receive model-request permissions. Keep the optional `run` job out of
required checks; use `lint` for static validation. An opted-in model job
fails if it cannot authenticate or complete the evals.

The community repository is independent and is not an official LANCommander
release channel.
