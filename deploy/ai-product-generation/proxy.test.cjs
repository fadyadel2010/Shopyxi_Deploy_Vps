'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { createProxy, parseTargets, publicIPv4 } = require('./proxy.cjs');

const R2 = 'e0d49325f8bd7676ea13ee30b0c52088.r2.cloudflarestorage.com';

test('allows the existing R2 host and the exact OpenRouter host', () => {
  assert.deepEqual(parseTargets(`${R2},openrouter.ai`), [R2, 'openrouter.ai']);
});

test('rejects lookalike, wildcard and unrelated hosts', () => {
  assert.deepEqual(parseTargets('openrouter.ai.evil.test,*.openrouter.ai,openrouter.ai,10.0.0.1'), ['openrouter.ai']);
  assert.deepEqual(parseTargets(''), []);
});

test('publicIPv4 rejects private, loopback, link-local and reserved ranges', () => {
  assert.equal(publicIPv4('104.18.2.1'), true);
  assert.equal(publicIPv4('10.0.0.1'), false);
  assert.equal(publicIPv4('127.0.0.1'), false);
  assert.equal(publicIPv4('169.254.1.1'), false);
  assert.equal(publicIPv4('192.168.1.1'), false);
  assert.equal(publicIPv4('100.64.0.1'), false);
  assert.equal(publicIPv4('198.18.0.1'), false);
  assert.equal(publicIPv4('::1'), false);
});

test('an empty allowlist is rejected instead of proxying everything', () => {
  assert.throws(() => createProxy(''), /Invalid target/);
});
