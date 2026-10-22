'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { run, requireSigningContext } = require('./actions.cjs');

function versionInput(value) {
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(value || '')) throw new Error('Version must be a release version such as 1.0.7.');
  return value;
}

function build(env = process.env, execute = run) {
  if (env.GITHUB_ACTIONS !== 'true' || env.GITHUB_EVENT_NAME !== 'workflow_dispatch' ||
      env.GITHUB_REPOSITORY !== 'HappyHamster135/Klipp' || env.GITHUB_REF !== 'refs/heads/main' ||
      env.RUNNER_ENVIRONMENT !== 'github-hosted') throw new Error('Release builds require manual dispatch from Klipp main on a GitHub-hosted runner.');
  const version = versionInput(env.INPUT_VERSION);
  if (!['true', 'false'].includes(env.INPUT_SIGN)) throw new Error('The sign input must be true or false.');
  const signed = env.INPUT_SIGN === 'true';
  const childEnv = { ...env, SIGNPATH_REQUESTED: signed ? 'true' : 'false' };
  if (signed) requireSigningContext(childEnv);
  if (!env.RUNNER_TEMP || !env.GITHUB_WORKSPACE || !env.GITHUB_OUTPUT) throw new Error('GitHub runner context is incomplete.');
  const outputRoot = fs.mkdtempSync(path.join(env.RUNNER_TEMP, 'klipp-release-'));
  const args = ['-NoProfile', '-NonInteractive', '-File',
    path.join(env.GITHUB_WORKSPACE, 'tools/Publish-KlippRelease.ps1'), '-Version', version, '-OutputRoot', outputRoot];
  if (signed) {
    const callback = path.join(__dirname, 'sign.cjs');
    args.push('-SignTemplate', `"${process.execPath}" "${callback}" {{file...}}`,
      '-ExpectedPublisher', env.SIGNPATH_EXPECTED_PUBLISHER, '-RequireSigning');
  }
  execute('pwsh', args, { env: childEnv, cwd: env.GITHUB_WORKSPACE });
  const manifests = fs.readdirSync(outputRoot, { withFileTypes: true }).filter(entry => entry.isDirectory())
    .map(entry => path.join(outputRoot, entry.name, 'release-artifacts.json')).filter(file => fs.existsSync(file));
  if (manifests.length !== 1) throw new Error('Build did not produce exactly one release manifest.');
  const manifest = JSON.parse(fs.readFileSync(manifests[0], 'utf8').replace(/^\uFEFF/, ''));
  if (manifest.version !== version || manifest.sourceCommit !== env.GITHUB_SHA) throw new Error('Release metadata does not match this workflow source and version.');
  const artifactRoot = fs.mkdtempSync(path.join(env.RUNNER_TEMP, 'klipp-artifact-'));
  const releaseRoot = path.dirname(manifests[0]);
  const names = new Set();
  for (const asset of manifest.releaseAssets) {
    if (!asset.name || path.basename(asset.name) !== asset.name || names.has(asset.name.toLowerCase())) throw new Error('Unsafe or duplicate release asset name.');
    names.add(asset.name.toLowerCase());
    const source = path.resolve(releaseRoot, asset.path);
    if (!source.toLowerCase().startsWith((releaseRoot + path.sep).toLowerCase())) throw new Error('Release asset is outside the build directory.');
    fs.copyFileSync(source, path.join(artifactRoot, asset.name));
    asset.path = asset.name;
  }
  manifest.installer = path.basename(manifest.installer);
  manifest.updateFeed = path.basename(manifest.updateFeed);
  manifest.packageDirectory = '.';
  const portableManifest = path.join(artifactRoot, 'release-artifacts.json');
  fs.writeFileSync(portableManifest, JSON.stringify(manifest, null, 2) + '\n');
  if (signed) execute('pwsh', ['-NoProfile', '-NonInteractive', '-File',
    path.join(env.GITHUB_WORKSPACE, 'tools/Test-KlippReleaseSignatures.ps1'),
    '-ReleaseManifestPath', portableManifest, '-ExpectedPublisher', env.SIGNPATH_EXPECTED_PUBLISHER], { env: childEnv });
  const label = signed ? 'signed' : 'unsigned-review';
  fs.appendFileSync(env.GITHUB_OUTPUT, `artifact-directory=${artifactRoot}\nartifact-name=Klipp-${version}-${label}\n`);
  if (env.GITHUB_STEP_SUMMARY) fs.appendFileSync(env.GITHUB_STEP_SUMMARY,
    `Built Klipp ${version} from ${env.GITHUB_SHA}. Status: **${label}**. Assets are retained for review; this workflow does not create or publish a GitHub release.\n`);
  return artifactRoot;
}

if (require.main === module) {
  try { build(); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { versionInput, build };
