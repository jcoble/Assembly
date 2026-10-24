import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';

// Regressions 2caea8523 (a frontend reload showed live machines as disconnected)
// and 055b16958 (the start-up snapshot overwrote a newer connection event).
// conversationService.ts imports Tauri and sits at its size baseline, so this
// runs the real setupConversationEvents source against stand-in dependencies.
const source = readFileSync(new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url), 'utf8');
const start = source.indexOf('async function setupConversationEvents(');
const end = source.indexOf('/** Reconcile compact runtime records after reconnect');
assert.ok(start >= 0 && end > start, 'setupConversationEvents moved; point this test at it again');
const code = stripTypeScriptTypes(source.slice(start, end), { mode: 'strip' });

type Handler = (event: { payload: { profileId: string; state: string } }) => void;
const handlers = new Map<string, Handler>();
const states: Record<string, string> = {};
let environmentRequested!: () => void;
const requested = new Promise<void>((resolve) => { environmentRequested = resolve; });
let releaseEnvironment!: (value: unknown) => void;
const environment = new Promise((resolve) => { releaseEnvironment = resolve; });
const rail = { remoteProfiles: [] as unknown[] };
const dependencies = {
  registerAgentConversationStream: async () => ({ unregister: async () => {} }),
  registerAguiStream: async () => ({ unregister: async () => {} }),
  admitSelectedConversationChunks: () => undefined,
  handleConversationStreamEnvelope: () => {},
  handleConversationStreamResync: () => {},
  listen: async (name: string, handler: Handler) => { handlers.set(name, handler); return () => handlers.delete(name); },
  trackTauriListener: (stop: () => void) => stop,
  showNamedSession: () => {},
  setRemoteConnection: (profileId: string, state: string) => { states[profileId] = state; },
  refreshRemoteSessionActivity: async () => {},
  reacquireSelectedChildHistory: async () => {},
  readRemoteAssemblyEnvironmentFromTauri: () => { environmentRequested(); return environment; },
  rail,
  conversationEventsDisposed: false,
  conversationEventsGeneration: 1,
  conversationStream: null,
  unlistenTitles: null,
  unlistenRemoteConnections: null
};
const setupConversationEvents = Function(...Object.keys(dependencies), `${code}\nreturn setupConversationEvents;`)(
  ...Object.values(dependencies)
);

const setup = setupConversationEvents(1, new AbortController().signal);
await requested; // both listeners are registered before the snapshot read starts
handlers.get('remote-connection-changed')!({ payload: { profileId: 'live-event', state: 'reconnecting' } });
releaseEnvironment({
  profiles: [{ id: 'live-event' }, { id: 'already-connected' }, { id: 'idle' }],
  readyProfileIds: ['live-event', 'already-connected']
});
await setup;

assert.deepEqual(
  states,
  { 'live-event': 'reconnecting', 'already-connected': 'connected', idle: 'disconnected' },
  'the snapshot seeds machines the backend already holds, and an event that arrived during the read wins'
);
assert.equal(rail.remoteProfiles.length, 3);
console.log('remoteConnectionSeed: all tests passed');
