import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

// Tauri has no Node server, so the app is a static SPA (tauri.app/start/frontend/sveltekit).
// Port 1430 is the shell's dev URL and one of the backend's CORS origins.
export default defineConfig({
  plugins: [sveltekit({ adapter: adapter({ fallback: 'index.html' }) })],
  clearScreen: false,
  server: { port: 1430, strictPort: true },
});
