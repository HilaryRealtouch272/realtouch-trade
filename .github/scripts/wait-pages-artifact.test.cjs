const { test } = require('node:test');
const assert = require('node:assert/strict');
const wait = require('./wait-pages-artifact.cjs');
function fixture(responses) {
  let calls = 0;
  const sleeps = [];
  return { args: {
    github: { rest: { actions: { listWorkflowRunArtifacts: 'list' } },
      paginate: async (route, params) => {
        assert.equal(route, 'list'); assert.equal(params.run_id, 123);
        const result = responses[calls++];
        if (result instanceof Error) throw result;
        return result || [];
      } },
    context: { repo: { owner: 'owner', repo: 'repo' }, runId: 123 },
    core: { info() {} }, name: 'pages-123-1', delays: [0, 5, 10],
    sleep: async ms => sleeps.push(ms)
  }, calls: () => calls, sleeps };
}
const a = { name: 'pages-123-1', id: 9, expired: false };
test('waits for delayed metadata', async () => {
  const f = fixture([[], [], [a]]);
  assert.equal(await wait(f.args), 9);
  assert.equal(f.calls(), 3); assert.deepEqual(f.sleeps, [5, 10]);
});
test('ignores expired and wrong-name uploads; retries are bounded', async () => {
  const f = fixture([[{ ...a, expired: true }, { ...a, name: 'old-attempt' }]]);
  await assert.rejects(wait(f.args), /bounded retry window/);
  assert.equal(f.calls(), 3);
});
test('rejects ambiguous uploads immediately', async () => {
  const f = fixture([[a, a]]);
  await assert.rejects(wait(f.args), /ambiguous/); assert.equal(f.calls(), 1);
});
test('retries a temporary API error', async () => {
  const f = fixture([Object.assign(new Error('temporary'), { status: 503 }), [a]]);
  assert.equal(await wait(f.args), 9);
});
test('does not retry permission failures', async () => {
  const f = fixture([Object.assign(new Error('denied'), { status: 403 })]);
  await assert.rejects(wait(f.args), /denied/); assert.equal(f.calls(), 1);
});
