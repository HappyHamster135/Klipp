'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

function run(executable, args, options = {}) {
  const result = spawnSync(executable, args, { stdio: 'inherit', windowsHide: true, ...options });
  if (result.error || result.status !== 0) throw new Error(`Process failed: ${path.basename(executable)} (${result.status ?? 'not started'}).`);
}

function requireSigningContext(env) {
  if (env.GITHUB_ACTIONS !== 'true' || env.GITHUB_EVENT_NAME !== 'workflow_dispatch' ||
      env.GITHUB_REPOSITORY !== 'HappyHamster135/Klipp' || env.GITHUB_REF !== 'refs/heads/main' ||
      env.RUNNER_ENVIRONMENT !== 'github-hosted') throw new Error('Signing requires a manual main-branch build on a GitHub-hosted runner.');
  if (env.SIGNPATH_ENABLED !== 'true' || env.SIGNPATH_REQUESTED !== 'true') throw new Error('SignPath signing is not enabled and requested.');
  for (const key of ['SIGNPATH_API_TOKEN', 'SIGNPATH_GITHUB_TOKEN', 'SIGNPATH_ORGANIZATION_ID',
    'SIGNPATH_PROJECT_SLUG', 'SIGNPATH_ARTIFACT_CONFIGURATION_SLUG', 'SIGNPATH_EXPECTED_PUBLISHER',
    'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_RESULTS_URL', 'GITHUB_WORKSPACE', 'RUNNER_TEMP']) {
    if (!env[key]?.trim()) throw new Error(`Missing signing setting: ${key}.`);
  }
  if (env.SIGNPATH_POLICY !== 'release-signing') throw new Error('Only the approved release-signing policy is supported.');
}

function runOfficialAction(entry, inputs, directory, env, execute = run) {
  const output = path.join(directory, 'action-output');
  fs.writeFileSync(output, '', { flag: 'wx' });
  const childEnv = Object.fromEntries(Object.entries(env).filter(([key]) => !key.startsWith('INPUT_')));
  childEnv.GITHUB_OUTPUT = output;
  for (const [key, value] of Object.entries(inputs)) childEnv[`INPUT_${key.toUpperCase()}`] = String(value);
  execute(process.execPath, [entry], { env: childEnv });
  return fs.readFileSync(output, 'utf8');
}

function readOutput(contents, key) {
  const lines = contents.replace(/\r/g, '').split('\n');
  const direct = lines.find(line => line.startsWith(`${key}=`));
  if (direct) return direct.substring(key.length + 1);
  const start = lines.findIndex(line => line.startsWith(`${key}<<`));
  if (start >= 0) {
    const delimiter = lines[start].substring(key.length + 2);
    const end = lines.indexOf(delimiter, start + 1);
    if (end > start) return lines.slice(start + 1, end).join('\n');
  }
  throw new Error(`Required action output is missing: ${key}.`);
}

module.exports = { run, requireSigningContext, runOfficialAction, readOutput };
