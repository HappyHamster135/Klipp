'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { requireSigningContext, readOutput } = require('./actions.cjs');
const { isAllowed, sign } = require('./sign.cjs');
const { versionInput, build } = require('./build.cjs');

function environment(root) {
  return {
    GITHUB_ACTIONS: 'true', GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REPOSITORY: 'HappyHamster135/Klipp', GITHUB_REF: 'refs/heads/main',
    RUNNER_ENVIRONMENT: 'github-hosted', GITHUB_WORKSPACE: root, RUNNER_TEMP: root,
    SIGNPATH_ENABLED: 'true', SIGNPATH_REQUESTED: 'true', SIGNPATH_POLICY: 'release-signing',
    SIGNPATH_API_TOKEN: 'test-token-not-a-credential', SIGNPATH_GITHUB_TOKEN: 'test-github-token',
    SIGNPATH_ORGANIZATION_ID: 'test-organization', SIGNPATH_PROJECT_SLUG: 'klipp',
    SIGNPATH_ARTIFACT_CONFIGURATION_SLUG: 'approved-binaries', SIGNPATH_EXPECTED_PUBLISHER: 'CN=Approved Test Publisher',
    ACTIONS_RUNTIME_TOKEN: 'test-runtime-token', ACTIONS_RESULTS_URL: 'https://example.invalid/',
    GITHUB_SHA: '0123456789abcdef0123456789abcdef01234567', GITHUB_RUN_ID: '123',
    GITHUB_OUTPUT: path.join(root, 'build-output'), INPUT_VERSION: '1.0.7', INPUT_SIGN: 'true',
  };
}

test('signature context excludes unapproved hosts, sources and missing settings', () => {
  const good = environment('unused-test-directory');
  assert.doesNotThrow(() => requireSigningContext(good));
  for (const [key, value] of Object.entries({ GITHUB_EVENT_NAME: 'pull_request_target', GITHUB_REPOSITORY: 'fork/Klipp',
    GITHUB_REF: 'refs/heads/unreviewed', RUNNER_ENVIRONMENT: 'self-hosted', SIGNPATH_ENABLED: 'false',
    SIGNPATH_REQUESTED: 'false', SIGNPATH_POLICY: 'test-signing', SIGNPATH_API_TOKEN: '', SIGNPATH_EXPECTED_PUBLISHER: '' })) {
    assert.throws(() => requireSigningContext({ ...good, [key]: value }), undefined, key);
  }
});

test('callback allowlist includes owned payload and approved installer helpers only', () => {
  for (const name of ['Klipp.Desktop.exe', 'Klipp.Core.dll', 'Klipp.GameHook.dll', 'Klipp_ExecutionStub.exe',
    'Squirrel.exe', 'Update.exe', 'HappyHamster135.Klipp-win-Setup.exe']) assert.equal(isAllowed(name), true, name);
  for (const name of ['Microsoft.UI.Xaml.dll', 'unrelated.exe', 'Klipp.NewUnreviewed.dll', 'Klipp.Desktop.exe.cmd']) assert.equal(isAllowed(name), false, name);
});

test('version is validated before passing arguments to packaging', () => {
  assert.equal(versionInput('1.0.7'), '1.0.7');
  assert.equal(versionInput('1.0.7-beta.1'), '1.0.7-beta.1');
  for (const value of ['', '1.0', '1.0.7;command', '-SignTemplate', '1.0.7\n']) assert.throws(() => versionInput(value));
});

test('official action outputs require valid complete values', () => {
  assert.equal(readOutput('artifact-id=123\r\n', 'artifact-id'), '123');
  assert.equal(readOutput('result<<END\na\nb\nEND\n', 'result'), 'a\nb');
  assert.throws(() => readOutput('result<<END\na\n', 'result'));
  assert.throws(() => readOutput('unrelated=123\n', 'artifact-id'));
});

function callbackFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'klipp-signpath-test-'));
  const original = path.join(root, 'Klipp.Desktop.exe');
  fs.writeFileSync(original, 'original unsigned fixture');
  return { root, original, env: environment(root) };
}

test('missing context or an upstream file never starts an action', () => {
  const { root, original, env } = callbackFixture();
  const upstream = path.join(root, 'Microsoft.Library.dll');
  fs.writeFileSync(upstream, 'upstream');
  const never = () => assert.fail('No action should run');
  assert.throws(() => sign([original], { ...env, SIGNPATH_ENABLED: 'false' }, never));
  assert.throws(() => sign([upstream], env, never));
  assert.throws(() => sign([original, original], env, never));
  assert.equal(fs.readFileSync(original, 'utf8'), 'original unsigned fixture');
});

function signingProcess(original, failVerification, calls) {
  return (executable, args, options) => {
    calls.push({ executable, args, env: options.env });
    if (args[0].endsWith(path.join('dist', 'upload', 'index.js'))) {
      assert.equal(options.env.INPUT_ARCHIVE, 'true');
      assert.equal(options.env.INPUT_SIGN, undefined);
      fs.appendFileSync(options.env.GITHUB_OUTPUT, 'artifact-id=456\n');
    } else if (args[0].endsWith(path.join('submit', 'index.js'))) {
      assert.equal(options.env['INPUT_GITHUB-ARTIFACT-ID'], '456');
      assert.equal(options.env['INPUT_WAIT-FOR-COMPLETION'], 'true');
      const output = options.env['INPUT_OUTPUT-ARTIFACT-DIRECTORY'];
      fs.mkdirSync(output);
      fs.writeFileSync(path.join(output, path.basename(original)), 'verified signed fixture');
      fs.appendFileSync(options.env.GITHUB_OUTPUT, 'signing-request-web-url=https://app.signpath.io/test/request\n');
    } else {
      assert.equal(executable, 'pwsh');
      if (failVerification) throw new Error('Unsigned returned fixture rejected');
    }
  };
}

test('unsigned or wrong-publisher response cannot replace original files', () => {
  const { original, env } = callbackFixture();
  const calls = [];
  assert.throws(() => sign([original], env, signingProcess(original, true, calls)), /rejected/);
  assert.equal(calls.length, 3);
  assert.equal(fs.readFileSync(original, 'utf8'), 'original unsigned fixture');
  assert.ok(calls.every(call => !call.args.some(value => value.includes(env.SIGNPATH_API_TOKEN))));
});

test('all response files are verified before originals are replaced', () => {
  const { original, env } = callbackFixture();
  const calls = [];
  assert.equal(sign([original], env, signingProcess(original, false, calls)), 1);
  assert.equal(fs.readFileSync(original, 'utf8'), 'verified signed fixture');
  assert.equal(calls.length, 3);
  assert.ok(calls[2].args.includes(env.SIGNPATH_EXPECTED_PUBLISHER));
});

test('unapproved signing request URL leaves originals unchanged', () => {
  const { original, env } = callbackFixture();
  const calls = [];
  const base = signingProcess(original, false, calls);
  const malicious = (exe, args, options) => {
    base(exe, args, options);
    if (args[0].endsWith(path.join('submit', 'index.js'))) {
      fs.writeFileSync(options.env.GITHUB_OUTPUT, 'signing-request-web-url=https://app.signpath.io.evil.invalid/request\n');
    }
  };
  assert.throws(() => sign([original], env, malicious), /Unexpected/);
  assert.equal(fs.readFileSync(original, 'utf8'), 'original unsigned fixture');
});

function buildProcess(env, calls) {
  return (executable, args, options) => {
    calls.push({ executable, args, env: options.env });
    if (!args.some(value => value.endsWith('Publish-KlippRelease.ps1'))) return;
    const root = args[args.indexOf('-OutputRoot') + 1];
    const release = path.join(root, 'release');
    fs.mkdirSync(release);
    const assets = ['Klipp-Setup.exe', 'releases.win.json', 'Klipp-1.0.7-win-x64.zip'].map(name => {
      const file = path.join(release, name); fs.writeFileSync(file, 'fixture');
      return { name, path: file, size: 7, sha256: 'test-hash' };
    });
    fs.writeFileSync(path.join(release, 'release-artifacts.json'), JSON.stringify({
      version: '1.0.7', sourceCommit: env.GITHUB_SHA, installer: assets[0].path,
      updateFeed: assets[1].path, packageDirectory: release, releaseAssets: assets,
    }));
  };
}

test('review build needs no signing token and is labelled unsigned', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'klipp-review-test-'));
  const env = { ...environment(root), INPUT_SIGN: 'false', SIGNPATH_API_TOKEN: '', SIGNPATH_ENABLED: 'false' };
  const calls = [];
  const directory = build(env, buildProcess(env, calls));
  assert.equal(calls.length, 1);
  assert.ok(!calls[0].args.includes('-SignTemplate'));
  assert.match(fs.readFileSync(env.GITHUB_OUTPUT, 'utf8'), /unsigned-review/);
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'release-artifacts.json'), 'utf8'));
  assert.ok(manifest.releaseAssets.every(asset => asset.path === asset.name));
});

test('signed build requires policy before packaging and verifies staged artifacts', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'klipp-release-test-'));
  const env = environment(root); const calls = [];
  assert.throws(() => build({ ...env, SIGNPATH_ENABLED: 'false' }, () => assert.fail('No build should run')));
  build(env, buildProcess(env, calls));
  assert.equal(calls.length, 2);
  assert.ok(calls[0].args.includes('-RequireSigning'));
  assert.ok(calls[0].args[calls[0].args.indexOf('-SignTemplate') + 1].endsWith('{{file...}}'));
  assert.ok(calls[1].args.some(value => value.endsWith('Test-KlippReleaseSignatures.ps1')));
  assert.match(fs.readFileSync(env.GITHUB_OUTPUT, 'utf8'), /Klipp-1\.0\.7-signed/);
});
