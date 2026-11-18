'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawnSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..');
const vendor = path.join(root, 'vendor/picomatch');
const installed = require.resolve('picomatch');
const pm = require(installed);

test('all installed picomatch consumers use the maintained security correction', () => {
  assert.equal(fs.realpathSync(installed), fs.realpathSync(path.join(vendor, 'index.js')));
  assert.equal(require('picomatch/package.json').name, '@sarthico/picomatch');
  assert.equal(require('picomatch/package.json').sarthicoSecurityRevision, 1);
  const lock = JSON.parse(fs.readFileSync(path.join(root, 'package-lock.json'), 'utf8'));
  const entries = Object.entries(lock.packages).filter(([key]) => key.endsWith('node_modules/picomatch'));
  assert.ok(entries.length > 0);
  for (const [key, entry] of entries) {
    assert.equal(entry.link, true, `unexpected unpatched package: ${key}`);
    assert.equal(entry.resolved, 'vendor/picomatch');
  }
  for (const consumer of ['@angular/build', '@angular-devkit/core', 'tinyglobby', 'fdir', 'vite', 'vitest']) {
    const resolveFromConsumer = createRequire(require.resolve(consumer));
    assert.equal(fs.realpathSync(resolveFromConsumer.resolve('picomatch')), fs.realpathSync(installed), consumer);
  }
});

test('vendored source matches its reviewed integrity manifest', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(vendor, 'integrity.json'), 'utf8'));
  for (const [file, hash] of Object.entries(manifest)) {
    const actual = createHash('sha256').update(fs.readFileSync(path.join(vendor, file))).digest('hex');
    assert.equal(actual, hash, `review required for ${file}`);
  }
});

test('repeated wildcard globs cannot hang the matcher', () => {
  const attacks = ['+(*)', '+(*a)', '*(*a)', '+(*)b', '+(a*)b', '+(a*b)', '*(a*b)c',
    '+(*.js)x', '+(?*)', '+([a-z]*)', '+(@(*a))', '+(@(*)a)', '*(@(a*))b'];
  const options = [{}, { dot: true }, { windows: true }, { bash: true }, { nocase: true }, { capture: true }];
  const script = `const pm = require(${JSON.stringify(installed)});
    for (const o of ${JSON.stringify(options)})
      for (const pattern of ${JSON.stringify(attacks)})
        for (const input of ['a'.repeat(50000) + '/', 'a'.repeat(50000) + '!'])
          pm(pattern, o)(input);
    console.log('completed');`;
  const result = spawnSync(process.execPath, ['-e', script], { encoding: 'utf8', timeout: 15000 });
  assert.equal(result.error, undefined, 'regex exceeded the external deadline');
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'completed');
});

test('matching semantics and safe literal repetitions remain intact', () => {
  for (const options of [{}, { dot: true }, { windows: true }, { bash: true }, { nocase: true }, { capture: true }]) {
    for (const [input, pattern, expected] of [
      ['aaa', '+(*a)', true], ['ab', '+(*a)', false], ['abab', '+(a*b)', true],
      ['aba', '+(a*b)', false], ['axxb', '+(a*)b', true], ['abc', '+(*)c', true],
      ['x.jsy.js', '+(*.js)', true], ['x.jsy.ts', '+(*.js)', false],
      ['src/app/a.ts', '**/*.ts', true], ['src/app/a.js', '**/*.ts', false],
      ['app.spec.ts', '**/*.{spec,test}.ts', true], ['ababa', '+(ab)', false],
      ['abab', '+(ab)', true], ['***', '+(\\*)', true], ['***', '+([*])', true],
      ['***', '+("*")', true]
    ]) assert.equal(pm.isMatch(input, pattern, options), expected, `${input} ${pattern}`);
  }
  assert.equal(pm.isMatch('a/a', '+(*a)'), false);
  assert.equal(pm.isMatch('a/a', '+(*a)', { bash: true }), true);
  assert.equal(pm.isMatch('src/a/src/b/', '+(src/*/)'), true);
  assert.equal(pm.isMatch('src/a/lib/b/', '+(src/*/)'), false);
  assert.equal(pm.makeRe('+(*a)').source, '^(?:(?=.)(?:[^/]*?a))$');
  assert.equal(pm.makeRe('*(*a)').source, '^(?:(?=.)(?:[^/]*?a)?)$');
});
