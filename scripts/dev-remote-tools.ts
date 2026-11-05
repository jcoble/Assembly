// Runs on a remote machine from a development package: its adapters and CLIs, built from the package's own sources with
// the same toolchain a lane uses, so they match the checkout they came from. Arguments: the adapters folder, the agents file.
import { fileURLToPath } from 'node:url';
import { ensureTools, latestTools } from './dev-toolchain.ts';

const owner = new AbortController();
process.on('SIGTERM', () => owner.abort());
const root = fileURLToPath(new URL('../', import.meta.url));
const [adapters, agents] = process.argv.slice(2);
await ensureTools(root, adapters, agents, await latestTools(owner.signal), owner.signal);
