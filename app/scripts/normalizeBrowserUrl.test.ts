import assert from 'node:assert/strict';
import { normalizeBrowserUrl } from '../src/lib/shell/browser/normalizeBrowserUrl.ts';

// empty / whitespace input is not a URL
{
  assert.equal(normalizeBrowserUrl(''), '');
  assert.equal(normalizeBrowserUrl('   '), '');
  assert.equal(normalizeBrowserUrl('\t\n'), '');
}

// a bare port becomes localhost on that port
{
  assert.equal(normalizeBrowserUrl(':5177'), 'http://localhost:5177/');
  assert.equal(normalizeBrowserUrl('  :3000  '), 'http://localhost:3000/');
  assert.equal(normalizeBrowserUrl(':5177/next'), 'http://localhost:5177/next');
}

// loopback host shorthands get the http scheme
{
  assert.equal(normalizeBrowserUrl('localhost:5177'), 'http://localhost:5177/');
  assert.equal(normalizeBrowserUrl('localhost'), 'http://localhost/');
  assert.equal(normalizeBrowserUrl('LOCALHOST:5177'), 'http://localhost:5177/');
  assert.equal(normalizeBrowserUrl('127.0.0.1:8080/health'), 'http://127.0.0.1:8080/health');
  assert.equal(normalizeBrowserUrl('0.0.0.0:9000'), 'http://0.0.0.0:9000/');
  assert.equal(normalizeBrowserUrl('[::1]:5177'), 'http://[::1]:5177/');
}

// full URLs survive, normalized by the URL parser
{
  assert.equal(normalizeBrowserUrl('http://example.com'), 'http://example.com/');
  assert.equal(normalizeBrowserUrl('https://example.com/a?b=1#c'), 'https://example.com/a?b=1#c');
  assert.equal(normalizeBrowserUrl('HTTPS://Example.COM/Path'), 'https://example.com/Path');
  assert.equal(normalizeBrowserUrl(' http://localhost:5177/next '), 'http://localhost:5177/next');
}

// ordinary hostnames default to safe https
{
  assert.equal(normalizeBrowserUrl('www.google.com'), 'https://www.google.com/');
  assert.equal(normalizeBrowserUrl('example.com/docs'), 'https://example.com/docs');
  assert.equal(normalizeBrowserUrl('docs.example.co.uk:8443/start'), 'https://docs.example.co.uk:8443/start');
}

// explicit local file URLs are accepted for native validation
{
  assert.equal(
    normalizeBrowserUrl('file:///Users/me/My Report.html'),
    'file:///Users/me/My%20Report.html'
  );
  assert.equal(normalizeBrowserUrl(' file:///tmp/report.html#findings '), 'file:///tmp/report.html#findings');
}

// other schemes and remote file authorities are rejected
{
  assert.equal(normalizeBrowserUrl('file://other-computer/tmp/report.html'), '');
  assert.equal(normalizeBrowserUrl('mailto:someone@example.com'), '');
  assert.equal(normalizeBrowserUrl('javascript:alert(1)'), '');
  assert.equal(normalizeBrowserUrl('ftp://example.com'), '');
  assert.equal(normalizeBrowserUrl('tauri://localhost'), '');
}

// unparseable text is rejected rather than thrown
{
  assert.equal(normalizeBrowserUrl('not a url'), '');
  assert.equal(normalizeBrowserUrl('/Users/me/project'), '');
  assert.equal(normalizeBrowserUrl('::::'), '');
}

// the result of normalizing is stable: feeding it back changes nothing
{
  for (const input of [':5177', 'localhost:5177/next', 'https://example.com/a', 'file:///tmp/report.html']) {
    const once = normalizeBrowserUrl(input);
    assert.equal(normalizeBrowserUrl(once), once, `stable for ${input}`);
  }
}

console.log('normalizeBrowserUrl: all tests passed');
