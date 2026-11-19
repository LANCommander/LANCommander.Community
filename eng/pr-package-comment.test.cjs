const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const commentPackages = require('../.github/scripts/pr-package-comment.cjs');

function fixture() {
  const run = {
    id: 100, run_attempt: 2, workflow_id: 7, event: 'pull_request', status: 'completed',
    conclusion: 'success', head_sha: 'a'.repeat(40), head_branch: 'feature',
    repository: { full_name: 'LANCommander/LANCommander.Community' },
    head_repository: { id: 22 }, pull_requests: [{ number: 42 }],
  };
  const pull = {
    number: 42, state: 'open', base: { repo: run.repository },
    head: { sha: run.head_sha, ref: run.head_branch, repo: run.head_repository },
  };
  const state = {
    run, pull, workflowPath: '.github/workflows/ci.yml', recent: [],
    artifacts: [{ name: 'pr-package-Server.Mcp-100-2', id: 10, expired: false }],
    comments: [], associated: [pull], writes: [], warnings: [], summaries: [], info: [],
  };
  const github = {
    rest: {
      actions: {
        getWorkflowRun: async () => ({ data: state.run }),
        getWorkflow: async () => ({ data: { path: state.workflowPath } }),
        listWorkflowRuns: async () => ({ data: { workflow_runs: state.recent } }),
        listWorkflowRunArtifacts: 'artifacts',
      },
      repos: { listPullRequestsAssociatedWithCommit: 'associated' },
      pulls: {
        get: async () => {
          state.getPullCount = (state.getPullCount || 0) + 1;
          return { data: state.getPullCount > 1 && state.changedPull ? state.changedPull : state.pull };
        },
      },
      issues: {
        listComments: 'comments',
        createComment: async request => {
          if (state.writeError) throw state.writeError;
          state.writes.push({ operation: 'create', ...request });
        },
        updateComment: async request => state.writes.push({ operation: 'update', ...request }),
      },
    },
    paginate: async method => state[method],
  };
  const context = {
    repo: { owner: 'LANCommander', repo: 'LANCommander.Community' },
    payload: { workflow_run: { id: 100, run_attempt: 2 } },
  };
  const core = {
    info: message => state.info.push(message),
    warning: message => state.warnings.push(message),
    summary: { addRaw: body => ({ write: async () => state.summaries.push(body) }) },
  };
  return { state, execute: () => commentPackages({ github, context, core }) };
}

test('links current-attempt packages without reading or executing artifact contents', async () => {
  const { state, execute } = fixture();
  state.artifacts.push(
    { name: 'pr-package-Launcher.MQTT-100-2', id: 11 },
    { name: 'pr-package-Server.Mcp-100-1', id: 12 },
    { name: 'pr-package-Server.Mcp-99-2', id: 13 },
    { name: 'pr-package-Server.Quake3-100-2', id: 14, expired: true },
    { name: 'pr-package-[click](javascript:bad)-100-2', id: 15 },
    { name: 'release-manifest.json', id: 16 },
  );
  await execute();
  assert.equal(state.writes.length, 1);
  const { body } = state.writes[0];
  assert.match(body, /actions\/runs\/100\/artifacts\/10/);
  assert.match(body, /actions\/runs\/100\/artifacts\/11/);
  assert.doesNotMatch(body, /artifacts\/(12|13|14|15|16)/);
  assert.match(body, /Untrusted PR builds/);
  assert.match(body, /7 days/);
  assert.equal(state.summaries[0], body);
});

test('forks with no run PR metadata resolve via commit and verify live head repository', async () => {
  const { state, execute } = fixture();
  state.run.pull_requests = [];
  await execute();
  assert.equal(state.writes.length, 1);
});

for (const reason of ['head SHA', 'head repository', 'head branch', 'base repository', 'closed PR']) {
  test(`does not comment on a mismatched ${reason}`, async () => {
    const { state, execute } = fixture();
    if (reason === 'head SHA') state.pull.head.sha = 'b'.repeat(40);
    if (reason === 'head repository') state.pull.head.repo = { id: 999 };
    if (reason === 'head branch') state.pull.head.ref = 'other';
    if (reason === 'base repository') state.pull.base.repo = { full_name: 'other/repository' };
    if (reason === 'closed PR') state.pull.state = 'closed';
    await execute();
    assert.equal(state.writes.length, 0);
  });
}

test('does not overwrite comments after PR head moves during API reads', async () => {
  const { state, execute } = fixture();
  state.changedPull = { ...state.pull, head: { ...state.pull.head, sha: 'b'.repeat(40) } };
  await execute();
  assert.equal(state.writes.length, 0);
});

test('updates only its own marked bot comment, not user content', async () => {
  const { state, execute } = fixture();
  state.comments = [
    { id: 1, user: { login: 'contributor' }, body: '<!-- community-pr-packages -->' },
    { id: 2, user: { login: 'github-actions[bot]' }, body: '<!-- community-pr-packages --> old links' },
  ];
  await execute();
  assert.equal(state.writes[0].operation, 'update');
  assert.equal(state.writes[0].comment_id, 2);
});

for (const reason of ['newer run', 'newer attempt', 'wrong event']) {
  test(`skips ${reason}`, async () => {
    const { state, execute } = fixture();
    if (reason === 'newer run') state.recent = [{ id: 101 }];
    if (reason === 'newer attempt') state.run.run_attempt = 3;
    if (reason === 'wrong event') state.run.event = 'push';
    await execute();
    assert.equal(state.writes.length, 0);
  });
}

test('rejects runs from an unexpected workflow', async () => {
  const { state, execute } = fixture();
  state.workflowPath = '.github/workflows/other.yml';
  await assert.rejects(execute, /only describe the repository CI/);
});

test('failed or documentation-only attempts explicitly replace stale download links', async () => {
  const { state, execute } = fixture();
  state.run.conclusion = 'failure';
  state.artifacts = [];
  await execute();
  assert.match(state.writes[0].body, /\*\*failure\*\*/);
  assert.match(state.writes[0].body, /No downloadable packages/);
});

test('comment permission failures warn and preserve summary download links', async () => {
  const { state, execute } = fixture();
  state.writeError = { status: 403 };
  await execute();
  assert.equal(state.warnings.length, 1);
  assert.match(state.warnings[0], /comment unavailable \(403\)/);
  assert.match(state.summaries[0], /artifacts\/10/);
});

test('unexpected API failures are not suppressed', async () => {
  const { state, execute } = fixture();
  state.writeError = new Error('API unavailable');
  state.writeError.status = 500;
  await assert.rejects(execute, /API unavailable/);
});

test('privileged workflow checks out only its own trusted commit and grants no publishing permissions', () => {
  const workflow = readFileSync(join(__dirname, '../.github/workflows/pr-packages.yml'), 'utf8');
  assert.match(workflow, /workflow_run:/);
  assert.match(workflow, /ref: \$\{\{ github\.workflow_sha \}\}/);
  assert.match(workflow, /persist-credentials: false/);
  assert.doesNotMatch(workflow, /pull_request_target|download-artifact|secrets\.|contents: write|packages: write/);
});
