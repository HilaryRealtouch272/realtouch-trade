// Read-only readiness check. Never dispatches a workflow, scan or notification.
module.exports = async function waitForPagesArtifact({ github, context, core, name,
  sleep = ms => new Promise(resolve => setTimeout(resolve, ms)),
  delays = [0, 5000, 10000, 20000, 30000, 45000] }) {
  for (let attempt = 0; attempt < delays.length; attempt++) {
    if (delays[attempt]) await sleep(delays[attempt]);
    let artifacts;
    try {
      artifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, {
        ...context.repo, run_id: context.runId, per_page: 100
      });
    } catch (error) {
      if (![408, 429, 500, 502, 503, 504].includes(error.status) || attempt === delays.length - 1) throw error;
      core.info(`Pages metadata temporarily unavailable (${error.status}); retrying lookup.`);
      continue;
    }
    const matches = artifacts.filter(a => a.name === name && !a.expired);
    if (matches.length > 1) throw new Error(`Multiple current-run Pages artifacts match ${name}; refusing ambiguous deployment.`);
    if (matches.length === 1) {
      core.info(`Pages artifact visible: ${name} (id ${matches[0].id}).`);
      return matches[0].id;
    }
    core.info(`Pages artifact not visible yet; lookup ${attempt + 1}/${delays.length}.`);
  }
  throw new Error(`Pages artifact ${name} did not become visible within the bounded retry window.`);
};
