'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { run, requireSigningContext, runOfficialAction, readOutput } = require('./actions.cjs');

// Keep this list aligned with Pack-KlippInstaller.ps1. Upstream dependencies are
// included in packages but must never be submitted under Klipp's signing policy.
const binaries = new Set(['klipp.desktop.exe', 'klipp.desktop.dll', 'klipp.core.dll',
  'klipp.storage.dll', 'klipp.capture.dll', 'klipp.encoding.dll', 'klipp.audioeffects.dll',
  'klipp.gamehook.dll', 'klipp_executionstub.exe', 'squirrel.exe', 'update.exe',
  'happyhamster135.klipp-win-setup.exe', 'klipp-setup.exe']);

function isAllowed(file) { return binaries.has(path.basename(file).toLowerCase()); }

function sign(files, env = process.env, execute = run) {
  requireSigningContext(env);
  if (!files.length || files.some(file => !isAllowed(file) || !fs.statSync(file).isFile())) {
    throw new Error('Signing input contains a missing or unapproved binary.');
  }
  const names = files.map(file => path.basename(file).toLowerCase());
  if (new Set(names).size !== names.length) throw new Error('Duplicate signing filenames are not allowed.');
  const root = fs.mkdtempSync(path.join(env.RUNNER_TEMP, 'klipp-signing-'));
  const input = path.join(root, 'input');
  const signed = path.join(root, 'signed');
  fs.mkdirSync(input);
  const mappings = files.map(file => ({ original: path.resolve(file), signed: path.join(signed, path.basename(file)) }));
  for (const mapping of mappings) fs.copyFileSync(mapping.original, path.join(input, path.basename(mapping.original)));

  const uploadDirectory = path.join(root, 'upload-action');
  fs.mkdirSync(uploadDirectory);
  const upload = runOfficialAction(path.join(env.GITHUB_WORKSPACE, '.signpath-tools/upload/dist/upload/index.js'), {
    name: `klipp-signing-${env.GITHUB_RUN_ID}-${crypto.randomUUID()}`,
    path: input, 'if-no-files-found': 'error', 'retention-days': '7',
    'compression-level': '6', overwrite: 'false', 'include-hidden-files': 'false', archive: 'true',
  }, uploadDirectory, env, execute);
  const artifact = readOutput(upload, 'artifact-id');
  if (!/^\d+$/.test(artifact)) throw new Error('GitHub did not return a valid artifact ID.');

  const submitDirectory = path.join(root, 'submit-action');
  fs.mkdirSync(submitDirectory);
  const submission = runOfficialAction(path.join(env.GITHUB_WORKSPACE, '.signpath-tools/submit/index.js'), {
    'connector-url': 'https://githubactions.connectors.signpath.io',
    'api-token': env.SIGNPATH_API_TOKEN, 'organization-id': env.SIGNPATH_ORGANIZATION_ID,
    'project-slug': env.SIGNPATH_PROJECT_SLUG, 'artifact-configuration-slug': env.SIGNPATH_ARTIFACT_CONFIGURATION_SLUG,
    'signing-policy-slug': env.SIGNPATH_POLICY, 'github-artifact-id': artifact,
    'github-token': env.SIGNPATH_GITHUB_TOKEN, 'parameters': '', 'wait-for-completion': 'true',
    'output-artifact-directory': signed, 'skip-decompress': 'false',
    'wait-for-completion-timeout-in-seconds': '3600', 'service-unavailable-timeout-in-seconds': '600',
    'download-signed-artifact-timeout-in-seconds': '300',
  }, submitDirectory, env, execute);
  const requestUrl = readOutput(submission, 'signing-request-web-url');
  const parsedUrl = new URL(requestUrl);
  if (parsedUrl.protocol !== 'https:' || parsedUrl.hostname !== 'app.signpath.io') throw new Error('Unexpected signing request URL.');

  const list = path.join(root, 'signed-files.json');
  fs.writeFileSync(list, JSON.stringify(mappings.map(mapping => mapping.signed)));
  execute('pwsh', ['-NoProfile', '-NonInteractive', '-File', path.join(__dirname, 'Verify-SigningBatch.ps1'),
    '-FilesJson', list, '-ExpectedPublisher', env.SIGNPATH_EXPECTED_PUBLISHER], { env });
  // Do not replace any original unless every returned signature passed policy.
  for (const mapping of mappings) fs.copyFileSync(mapping.signed, mapping.original);
  if (env.GITHUB_STEP_SUMMARY) fs.appendFileSync(env.GITHUB_STEP_SUMMARY,
    `- [Manual SignPath approval](${requestUrl}) verified for ${files.length} binary files.\n`);
  return mappings.length;
}

if (require.main === module) {
  try { sign(process.argv.slice(2)); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { isAllowed, sign };
