import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';

import { DESIGNS, SPINNER_IDS, pickSpinner } from '../src/lib/shell/components/feedback/workingSpinners.ts';

// Exactly the owner's 16 kept designs, each with something to draw.
{
  assert.equal(SPINNER_IDS.length, 16);
  for (const id of SPINNER_IDS) assert.ok(DESIGNS[id].parts.length > 0, id);
}

// A session's seed always gives the same design, so its rail row and working row match.
{
  for (const seed of ['owned-a', 'owned-b', '0192f3c1-7a2e-7c11-9d1b-2f6a1c0e9b44', '']) {
    assert.equal(pickSpinner(seed), pickSpinner(seed));
  }
}

// Different sessions spread over the set.
{
  const picked = new Set(Array.from({ length: 20 }, (_, index) => pickSpinner(`session-${index}`)));
  assert.ok(picked.size >= 8, `20 seeds reached only ${picked.size} designs`);
}

// Without a seed the pick is random but always a real design.
{
  for (let index = 0; index < 50; index += 1) assert.ok(SPINNER_IDS.includes(pickSpinner()));
}

// Every design ships its animated image and its still frame for reduced motion.
{
  const folder = new URL('../src/lib/shell/components/feedback/spinners/', import.meta.url);
  for (const id of SPINNER_IDS) {
    for (const file of [`${id}.webp`, `${id}-still.webp`]) assert.ok(existsSync(new URL(file, folder)), file);
  }
}

console.log('working spinners: ok');
