import assert from 'node:assert/strict';
import test from 'node:test';
import { loadPhonePanel, pairingQrPayload } from '../src/lib/shell/components/phonePairing.ts';

const pairing = { url: 'http://100.64.0.5:7778', machineName: 'Workbox Test', code: 'K7QM4XTD9P', expiresAt: 1_760_000_000_000 };

test('pairing_qr_payload_is_versioned_json_with_url_machine_and_code', () => {
  assert.deepEqual(JSON.parse(pairingQrPayload(pairing)),
    { v: 1, url: 'http://100.64.0.5:7778', machine: 'Workbox Test', code: 'K7QM4XTD9P' });
});

test('closing_panel_aborts_pairing_request', async () => {
  const calls: string[] = [];
  const { promise, resolve } = Promise.withResolvers<unknown>();
  const panel = new AbortController();
  const loading = loadPhonePanel(async (operation, args) => {
    calls.push(`${operation} ${JSON.stringify(args)}`);
    return operation === 'phonePairing' ? promise : { devices: [] };
  }, 'Workbox Test', panel.signal);
  panel.abort();
  resolve(pairing);
  assert.equal(await loading, null);
  assert.deepEqual(calls, ['phonePairing {"machineName":"Workbox Test"}']);
});

test('an open panel loads the code, its QR and the paired phones', async () => {
  const devices = [{ id: 'd1', name: 'Pixel', createdAt: 1, lastSeenAt: null }];
  const loaded = await loadPhonePanel(async (operation) => operation === 'phonePairing' ? pairing : { devices },
    'Workbox Test', new AbortController().signal);
  assert.equal(loaded?.pairing, pairing);
  assert.deepEqual(loaded?.devices, devices);
  assert.match(loaded?.qr ?? '', /^data:image\/png;base64,/);
});
