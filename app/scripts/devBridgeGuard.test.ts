import assert from 'node:assert/strict';
import type { IncomingHttpHeaders, IncomingMessage, ServerResponse } from 'node:http';
import test from 'node:test';

import { createDevBridgeGuard } from '../src/lib/server/devBridgeGuard.ts';

const TOKEN = 'a'.repeat(64);
const OWN = { host: '127.0.0.1:5177', origin: 'http://127.0.0.1:5177', 'x-mcb-bridge-token': TOKEN };

/** Runs one request through the guard: its status when refused, or `next` when let through, and any cookie set. */
function run(url: string, headers: IncomingHttpHeaders) {
  const response = { statusCode: 200, headers: {} as Record<string, unknown>, body: '',
    setHeader(name: string, value: unknown) { this.headers[name.toLowerCase()] = value; },
    end(body: string) { this.body = body; } };
  let passed = false;
  createDevBridgeGuard(TOKEN)({ url, headers } as IncomingMessage, response as unknown as ServerResponse, () => { passed = true; });
  return { passed, status: response.statusCode, cookie: response.headers['set-cookie'] };
}

test('pages get the token cookie for their port, and their bridge calls with it go through', () => {
  const page = run('/next', { host: '127.0.0.1:5177' });
  assert.equal(page.passed, true);
  assert.equal(page.cookie, `mcb-bridge-5177=${TOKEN}; Path=/; SameSite=Strict`);
  assert.equal(run('/next', { host: 'evil.example:5177' }).cookie, undefined, 'a rebound DNS name gets no token');
  for (const route of ['/__mcb/source/read', '/__mcb/git/status']) assert.equal(run(route, OWN).passed, true, route);
  // Without an Origin header, the Referer names the page instead.
  const { origin: _, ...referer } = OWN;
  assert.equal(run('/__mcb/source/read', { ...referer, referer: 'http://127.0.0.1:5177/next' }).passed, true);
});

test('a forged request without the token is refused on both bridges, even with the right Origin', () => {
  for (const route of ['/__mcb/source/write', '/__mcb/git/status']) {
    const { 'x-mcb-bridge-token': _, ...forged } = OWN;
    assert.deepEqual([run(route, forged).passed, run(route, forged).status], [false, 403], route);
    assert.equal(run(route, { ...OWN, 'x-mcb-bridge-token': 'b'.repeat(64) }).passed, false, route);
    assert.equal(run(route, { ...OWN, 'x-mcb-bridge-token': 'short' }).passed, false, route);
  }
});

test('the bridges refuse other pages and rebound names, even with the token', () => {
  for (const headers of [
    { ...OWN, origin: 'http://127.0.0.1:3000' },
    { ...OWN, origin: 'null' },
    // A website whose DNS name now points at 127.0.0.1 is same-origin with itself, not with this server.
    { ...OWN, host: 'evil.example:5177', origin: 'http://evil.example:5177' },
    { host: OWN.host, 'x-mcb-bridge-token': TOKEN }
  ]) assert.equal(run('/__mcb/source/read', headers).passed, false, JSON.stringify(headers));
});
