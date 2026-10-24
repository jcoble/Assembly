import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import { existsSync, readFileSync } from 'node:fs';

// A lane worktree's .lane (scripts/lane.sh) gives it its own dev port, 1430 + its number.
const lane = existsSync('../.lane') ? Number(/LANE_NUMBER=(\d+)/.exec(readFileSync('../.lane', 'utf8'))?.[1] ?? 0) : 0;

// Tauri has no Node server, so the app is a static SPA (tauri.app/start/frontend/sveltekit).
// Port 1430 (main) to 1439 (lanes) is the dev URL, one of the backend's CORS origins.
export default defineConfig({
  plugins: [sveltekit({ adapter: adapter({ fallback: 'index.html' }) })],
  clearScreen: false,
  // The page's backend port outside the shell, which hands it over otherwise (src/lib/host/backend.ts).
  define: { __ASSEMBLY_PORT__: 18797 + lane * 10 },
  server: { port: 1430 + lane, strictPort: true },
});
