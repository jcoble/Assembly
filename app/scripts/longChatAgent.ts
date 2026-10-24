// A scripted ACP agent for the long-chat memory check. Started in a provider's
// adapter slot (for example through MCB_AGY_ACP_PATH), it answers every prompt
// with the same stream: thinking, shell tool calls with output, and text. The
// stream is fixed, so a run on main and a run on a branch see the same chat.
import { createInterface } from 'node:readline';

const ROUNDS = 40;
const PACE_MS = 20;
const sessions = new Set<string>();
const cancelled = new Set<string>();

function send(message: object): void {
  process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', ...message })}\n`);
}

// Paced like a real provider: one update every few milliseconds, not one burst.
async function update(sessionId: string, update: object): Promise<void> {
  send({ method: 'session/update', params: { sessionId, update } });
  await new Promise((resolve) => setTimeout(resolve, PACE_MS));
}

const text = (value: string) => ({ type: 'text', text: value });
// The app asks a provider for its models before it sends; this agent has one.
const configOptions = [{
  id: 'model', name: 'Model', category: 'model', type: 'select', currentValue: 'scripted',
  options: [{ value: 'scripted', name: 'Scripted stream' }],
}];

// Deterministic filler, so every run carries the same bytes.
function lines(round: number, count: number, width: number): string {
  return Array.from({ length: count }, (_, line) =>
    `${String(round).padStart(2, '0')}:${String(line).padStart(3, '0')} ${'abcdefghij'.repeat(width / 10)}`).join('\n');
}

async function prompt(sessionId: string): Promise<string> {
  cancelled.delete(sessionId);
  for (let round = 0; round < ROUNDS; round += 1) {
    if (cancelled.has(sessionId)) return 'cancelled';
    await update(sessionId, { sessionUpdate: 'agent_thought_chunk', content: text(`Round ${round}: checking the next file.\n${lines(round, 3, 80)}`) });
    const toolCallId = `${sessionId}-tool-${round}`;
    const command = `sed -n 1,40p src/file-${round}.ts`;
    await update(sessionId, { sessionUpdate: 'tool_call', toolCallId, title: command, kind: 'execute', status: 'in_progress', rawInput: { command } });
    await update(sessionId, {
      sessionUpdate: 'tool_call_update', toolCallId, status: 'completed',
      content: [{ type: 'content', content: text(lines(round, 40, 60)) }],
    });
    for (let chunk = 0; chunk < 6; chunk += 1) {
      await update(sessionId, { sessionUpdate: 'agent_message_chunk', content: text(`Round ${round}, part ${chunk}: ${'the file reads as expected. '.repeat(4)}\n`) });
    }
  }
  return 'end_turn';
}

async function answerPrompt(id: number | string, sessionId: string): Promise<void> {
  send({ id, result: { stopReason: await prompt(sessionId) } });
}

const handlers: Record<string, (params: Record<string, unknown>) => unknown> = {
  initialize: () => ({
    protocolVersion: 1,
    agentCapabilities: { loadSession: false, promptCapabilities: {} },
    agentInfo: { name: 'assembly-long-chat-agent', version: '1' },
    authMethods: [],
  }),
  authenticate: () => ({}),
  'session/new': () => {
    const sessionId = `long-chat-${sessions.size + 1}-${process.pid}`;
    sessions.add(sessionId);
    return { sessionId, configOptions };
  },
  'session/set_config_option': () => ({ configOptions }),
  'session/close': (params) => { sessions.delete(String(params.sessionId)); return {}; },
};

for await (const line of createInterface({ input: process.stdin })) {
  if (!line.trim()) continue;
  const message = JSON.parse(line) as { id?: number | string; method?: string; params?: Record<string, unknown> };
  if (!message.method) continue; // A response to a request of ours; this agent sends none.
  const params = message.params ?? {};
  if (message.method === 'session/cancel') { cancelled.add(String(params.sessionId)); continue; }
  if (message.id === undefined) continue;
  if (message.method === 'session/prompt') {
    void answerPrompt(message.id, String(params.sessionId));
    continue;
  }
  const handler = handlers[message.method];
  if (handler) send({ id: message.id, result: handler(params) });
  else send({ id: message.id, error: { code: -32601, message: `Method not found: ${message.method}` } });
}
