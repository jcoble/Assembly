import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { compileModule } from 'svelte/compiler';

// Svelte's client runtime, as the webview loads it (node would pick the server build for 'svelte').
export const svelteUrl = new URL('../node_modules/svelte/src/index-client.js', import.meta.url).href;
export const clientUrl = new URL('../node_modules/svelte/src/internal/client/index.js', import.meta.url).href;
export const discloseUrl = new URL('../node_modules/svelte/src/internal/disclose-version.js', import.meta.url).href;

// Compiles a runes module for the client and returns it as an importable data: URL. `imports` swaps an import's
// specifier ('../api') for another module's URL, such as a fake made with jsModule.
export function runesModule(source: string, filename: string, imports: Record<string, string> = {}): string {
  const { js } = compileModule(source, { generate: 'client', filename });
  let code = js.code
    .replaceAll("'svelte/internal/client'", `'${clientUrl}'`)
    .replaceAll("'svelte/internal/disclose-version'", `'${discloseUrl}'`)
    .replaceAll("from 'svelte'", `from '${svelteUrl}'`);
  for (const [from, to] of Object.entries(imports)) code = code.replaceAll(`from '${from}'`, `from '${to}'`);
  return jsModule(code);
}

// A runes module of the app's source, by its path under src/lib.
export const libModule = (path: string, imports: Record<string, string> = {}): string =>
  runesModule(stripTypeScriptTypes(readFileSync(new URL(`../src/lib/${path}`, import.meta.url), 'utf8')), path.replace(/\.ts$/, '.js'), imports);

// Plain JavaScript as an importable data: URL.
export const jsModule = (code: string): string => `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`;
