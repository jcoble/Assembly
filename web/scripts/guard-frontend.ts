/**
 * Automated architecture & hygiene guard for `Assembly/web/`.
 * Run via: `node web/scripts/guard-frontend.ts` (or `pnpm web:guard`).
 *
 * Enforces the rules in Notion: "Guide: Hybrid Frontend (Assembly/web/) & GPUIKit Lane Rules":
 *   1. Zero `@tauri-apps/*` imports anywhere under `web/src/`.
 *   2. Zero raw `new WebSocket(...)` outside `web/src/lib/infrastructure/`.
 *   3. Zero `rows/` Chat socket calls or subscriptions in `web/src/` (GPUIKit owns Chat).
 *   4. Zero direct cross-feature imports between sibling folders under `web/src/lib/features/*`.
 *   5. Zero source files over 600 lines under `web/src/`.
 */

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../src', import.meta.url));
const MAX_LINES = 600;
const SOURCE_EXTS = new Set(['.ts', '.svelte', '.css']);

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const stat = statSync(full);
    if (stat.isDirectory()) {
      walk(full, out);
    } else {
      const dot = entry.lastIndexOf('.');
      if (dot >= 0 && SOURCE_EXTS.has(entry.slice(dot))) {
        out.push(full);
      }
    }
  }
  return out;
}

const failures: string[] = [];
const files = walk(root);

for (const file of files) {
  const rel = relative(root, file).split(sep).join('/');
  const text = readFileSync(file, 'utf8');
  const lines = text.split('\n');

  if (lines.length > MAX_LINES) {
    failures.push(`${rel}: ${lines.length} lines (limit ${MAX_LINES})`);
  }

  if (/['"]@tauri-apps\//.test(text)) {
    failures.push(`${rel}: forbidden @tauri-apps/* import`);
  }

  const inInfra = rel.startsWith('lib/infrastructure/');
  if (!inInfra && /\bnew\s+WebSocket\s*\(/.test(text)) {
    failures.push(`${rel}: raw WebSocket construction outside lib/infrastructure/`);
  }

  if (
    /\bbackend\.call\s*(?:<[^>]*>)?\s*\(\s*['"]rows['"]/.test(text) ||
    /\bbackend\.subscribe\s*(?:<[^>]*>)?\s*\(\s*['"]rows(?:\/[^'"]*)?['"]/.test(text)
  ) {
    failures.push(`${rel}: forbidden Chat 'rows' socket call/subscription in SvelteKit`);
  }

  const featureMatch = rel.match(/^lib\/features\/([^/]+)\//);
  if (featureMatch) {
    const ownFeature = featureMatch[1];
    const importRegex = /from\s+['"]([^'"]+)['"]/g;
    let match: RegExpExecArray | null;
    while ((match = importRegex.exec(text)) !== null) {
      const spec = match[1];
      const aliasTarget = spec.match(/^\$lib\/features\/([^/]+)/);
      if (aliasTarget && aliasTarget[1] !== ownFeature) {
        failures.push(
          `${rel}: cross-feature import '${spec}' (features/${ownFeature} -> features/${aliasTarget[1]})`
        );
      }
      const relTarget = spec.match(/^(?:\.\.\/)+features\/([^/]+)/);
      if (relTarget && relTarget[1] !== ownFeature) {
        failures.push(
          `${rel}: cross-feature import '${spec}' (features/${ownFeature} -> features/${relTarget[1]})`
        );
      }
    }
  }
}

if (failures.length > 0) {
  console.error('guard-frontend: FAILED');
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  process.exit(1);
}

console.log(`guard-frontend: ok (${files.length} files checked)`);
