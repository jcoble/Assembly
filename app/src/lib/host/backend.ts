// The desktop's .NET backend, which the shell starts and names to the page before it loads:
// window.__ASSEMBLY__ = { port, token }. Outside the shell (vite dev) the port is the checkout's (vite.config.ts)
// and there's no token.
type Injected = { port: number; token: string };
const injected = (globalThis as { __ASSEMBLY__?: Injected }).__ASSEMBLY__;
declare const __ASSEMBLY_PORT__: number;

export const PORT = injected?.port ?? (typeof __ASSEMBLY_PORT__ === 'number' ? __ASSEMBLY_PORT__ : 18797);
export const TOKEN: string | undefined = injected?.token;
export const BASE = `http://127.0.0.1:${PORT}`;
export const BASE_WS = `ws://127.0.0.1:${PORT}`;
