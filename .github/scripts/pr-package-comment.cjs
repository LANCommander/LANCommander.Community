const marker = '<!-- community-pr-packages -->';

module.exports = async function commentPackages({ github, context, core }) {
  const { owner, repo } = context.repo;
  const repository = `${owner}/${repo}`;
  const eventRun = context.payload.workflow_run;
  const { data: run } = await github.rest.actions.getWorkflowRun({ owner, repo, run_id: eventRun.id });
  if (run.event !== 'pull_request' || run.status !== 'completed' ||
      run.repository.full_name !== repository || run.run_attempt !== eventRun.run_attempt) {
    core.info('Ignoring an unrelated or superseded workflow run.');
    return;
  }
  const { data: workflow } = await github.rest.actions.getWorkflow({ owner, repo, workflow_id: run.workflow_id });
  if (workflow.path !== '.github/workflows/ci.yml') {
    throw new Error('Package comments may only describe the repository CI workflow.');
  }
  const { data: recent } = await github.rest.actions.listWorkflowRuns({
    owner, repo, workflow_id: run.workflow_id, event: 'pull_request', head_sha: run.head_sha, per_page: 100,
  });
  if (recent.workflow_runs.some(candidate => candidate.id > run.id)) {
    core.info('A newer CI run already covers this commit.');
    return;
  }
  // Fork runs can have an empty pull_requests array. Resolve via the commit API,
  // then verify against the live PR; never accept a PR number from an artifact.
  const candidates = run.pull_requests.length ? run.pull_requests :
    await github.paginate(github.rest.repos.listPullRequestsAssociatedWithCommit, {
      owner, repo, commit_sha: run.head_sha, per_page: 100,
    });
  const pulls = [];
  for (const candidate of candidates) {
    const { data: pull } = await github.rest.pulls.get({ owner, repo, pull_number: candidate.number });
    if (pull.state === 'open' && pull.base.repo.full_name === repository &&
        pull.head.sha === run.head_sha && pull.head.ref === run.head_branch &&
        pull.head.repo?.id === run.head_repository.id) {
      pulls.push(pull);
    }
  }
  if (!pulls.length) {
    core.info('No open PR still matches this run head; leaving comments unchanged.');
    return;
  }
  const artifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, {
    owner, repo, run_id: run.id, per_page: 100,
  });
  const packages = artifacts.flatMap(artifact => {
    const match = /^pr-package-((?:Server|Launcher)\.[A-Za-z][A-Za-z0-9]*)-([0-9]+)-([0-9]+)$/.exec(artifact.name);
    return match && match[2] === String(run.id) && match[3] === String(run.run_attempt) && !artifact.expired ?
      [{ component: match[1], id: artifact.id }] : [];
  }).sort((a, b) => a.component.localeCompare(b.component));
  const server = process.env.GITHUB_SERVER_URL || 'https://github.com';
  const runUrl = `${server}/${repository}/actions/runs/${run.id}`;
  const lines = [
    marker, '### PR plugin packages',
    `Commit \`${run.head_sha}\` · [CI run ${run.id}, attempt ${run.run_attempt}](${runUrl}) · **${run.conclusion}**`,
    '',
    ...packages.map(pkg => `- [${pkg.component}: download package ZIP](${runUrl}/artifacts/${pkg.id})`),
    ...(packages.length ? [
      '',
      'Each ZIP contains the `.nupkg`, SHA-256 sidecar and build manifest. Extract it before installing.',
      'Artifacts expire after 7 days and downloading requires GitHub sign-in and repository access.',
      '**Untrusted PR builds, not published releases.** Versions are unchanged; do not install over a trusted release without a backup.',
    ] : [
      'No downloadable packages were produced by this attempt (documentation-only, failed, cancelled, or expired). See the CI run for details.',
    ]),
    '',
    'Direct download links are also available in the CI job summaries when PR comments are restricted.',
  ];
  const body = lines.join('\n');
  await core.summary.addRaw(body).write();
  for (const pull of pulls) {
    try {
      const comments = await github.paginate(github.rest.issues.listComments, {
        owner, repo, issue_number: pull.number, per_page: 100,
      });
      const existing = comments.find(comment =>
        comment.user?.login === 'github-actions[bot]' && comment.body?.startsWith(marker));
      // Recheck the live head immediately before writing, as the PR may have moved during API reads.
      const { data: current } = await github.rest.pulls.get({ owner, repo, pull_number: pull.number });
      if (current.state !== 'open' || current.head.sha !== run.head_sha) {
        core.info('PR changed while resolving packages; skipping stale comment.');
        continue;
      }
      if (existing) {
        await github.rest.issues.updateComment({ owner, repo, comment_id: existing.id, body });
      } else {
        await github.rest.issues.createComment({ owner, repo, issue_number: pull.number, body });
      }
    } catch (error) {
      if (error.status !== 403 && error.status !== 404) throw error;
      core.warning(`PR #${pull.number} comment unavailable (${error.status}); download packages from ${runUrl}.`);
    }
  }
};
