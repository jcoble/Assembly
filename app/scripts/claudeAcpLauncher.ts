#!/usr/bin/env node
// TSK-1463. Runs the stock Claude ACP adapter as a library and adds, from
// outside the adapter, what Assembly's former pnpm patch added:
//   A. session/prompt answers at the turn's result, also while background
//      subagents still run (the foreground hook reports that result).
//   B. session_info_update with _meta.jetbrains.air.sessionState.
//   C. session_info_update with _meta.jetbrains.air.backgroundTaskCount, from
//      Claude Code's background_tasks_changed (raw SDK messages, opted in per
//      session through the documented `emitRawSDKMessages` session meta).
//   D. _meta.jetbrains.air.outOfTurn on root updates while no prompt is open.
//   E. session/cancel also stops the running background tasks.
//   F. The context window stays the effective one from getContextUsage (it
//      includes an autoCompactWindow override); results report the model's full
//      capacity, which the stock adapter would otherwise store and cache.
//   G. Out of a turn, ExitPlanMode offers no "clear context" choice: a fresh
//      context continues the open prompt, and there is none.
// The release build compiles this file with deno (prepareReleaseAdapters.ts).
import { privateMcpHeaders } from './claudeMcpHeaders.ts';
import { appendFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { agent as acpAgent, methods, ndJsonStream, type AgentContext } from '@agentclientprotocol/sdk';
import { ClaudeAcpAgent, nodeToWebReadable, nodeToWebWritable } from '@agentclientprotocol/claude-agent-acp';
import { runPromptWithCancellation } from '@agentclientprotocol/claude-agent-acp/dist/acp-agent.js';
import { withAirMeta } from '@agentclientprotocol/claude-agent-acp/dist/air-extension.js';
import { GOAL_CONTROL_METHOD, parseGoalRequest } from '@agentclientprotocol/claude-agent-acp/dist/goal-extension.js';
import { exitPlanClearContextMode } from '@agentclientprotocol/claude-agent-acp/dist/permissions/effects.js';
import { applyManagedPolicyEnv } from '@agentclientprotocol/claude-agent-acp/dist/managed-policy.js';
import { onSessionIndexRequests } from '@agentclientprotocol/claude-agent-acp/dist/session-index/connection.js';

await applyManagedPolicyEnv();
// stdout carries ACP; everything else goes to stderr.
console.log = console.info = console.warn = console.debug = console.error;
process.on('unhandledRejection', (reason) => console.error('Unhandled Rejection:', reason));

// Optional log file, as the stock adapter writes it when CLAUDE_AGENT_LOGS is set.
const logDirectory = process.env.CLAUDE_AGENT_LOGS;
const writeLog = (...args: unknown[]) => appendFileSync(join(logDirectory!, 'agent.log'),
  `${new Date().toISOString()} pid=${process.pid} ${args.map((a) => (a instanceof Error ? (a.stack ?? a.message) : String(a))).join(' ')}\n`);
if (logDirectory) mkdirSync(logDirectory, { recursive: true });
const logger = logDirectory ? { log: writeLog, error: (...args: unknown[]) => { console.error(...args); writeLog(...args); } } : undefined;
logger?.log('Claude ACP started');

type Params = Record<string, any>;
const RAW_TASKS = [{ type: 'system', subtype: 'background_tasks_changed' }];
const SESSION_LEVEL = new Set(['available_commands_update', 'current_mode_update', 'config_option_update', 'session_info_update']);
const openPrompts = new Map<string, (outcome: any) => void>(); // sessionId -> listener for the end of the active prompt's work
const backgroundTasks = new Map<string, Array<{ task_id: string }>>(); // sessionId -> Claude Code's latest background task list
let agent: LauncherAgent;
let client: Client;

class Client {
  private ctx: AgentContext;
  constructor(ctx: AgentContext) { this.ctx = ctx; }
  info(sessionId: string, key: string, value: unknown) {
    return this.ctx.notify(methods.client.session.update, {
      sessionId,
      update: { sessionUpdate: 'session_info_update', _meta: withAirMeta(undefined, key, value) }
    });
  }
  sessionUpdate(n: Params) {
    if (agent.sessions[n.sessionId] && !openPrompts.has(n.sessionId) && !SESSION_LEVEL.has(n.update.sessionUpdate)) {
      n = { ...n, update: { ...n.update, _meta: withAirMeta(n.update._meta, 'outOfTurn', true) } };
    }
    return this.ctx.notify(methods.client.session.update, n as any);
  }
  requestPermission(p: any, signal?: AbortSignal) {
    if (agent.sessions[p.sessionId] && !openPrompts.has(p.sessionId)) {
      p = { ...p, options: p.options.filter((o: any) => exitPlanClearContextMode(o.optionId) === undefined) };
    }
    return this.ctx.request(methods.client.session.requestPermission, p, { cancellationSignal: signal });
  }
  readTextFile(p: any) { return this.ctx.request(methods.client.fs.readTextFile, p); }
  writeTextFile(p: any) { return this.ctx.request(methods.client.fs.writeTextFile, p); }
  createElicitation(p: any, signal?: AbortSignal) { return this.ctx.request(methods.client.elicitation.create, p, { cancellationSignal: signal }); }
  completeElicitation(p: any) { return this.ctx.notify(methods.client.elicitation.complete, p); }
  extNotification(method: string, params: Params) {
    if (method === '_claude/sdkMessage' && params.message?.subtype === 'background_tasks_changed') {
      backgroundTasks.set(params.sessionId, params.message.tasks);
      return this.info(params.sessionId, 'backgroundTaskCount', params.message.tasks.length);
    }
    return this.ctx.notify(method, params);
  }
}

const foreground = (sessionId: string) => ({
  running: () => client.info(sessionId, 'sessionState', 'running'),
  awaitingUser: () => client.info(sessionId, 'sessionState', 'requires_action'),
  ended: (outcome: any) => {
    client.info(sessionId, 'sessionState', 'idle');
    openPrompts.get(sessionId)?.(outcome);
  },
  failed: () => client.info(sessionId, 'sessionState', 'idle')
});

class LauncherAgent extends ClaudeAcpAgent {
  prompt(params: Params): Promise<any> {
    const { sessionId } = params;
    let mine: (outcome: any) => void;
    return new Promise((resolve, reject) => {
      mine = resolve;
      // Open only once the prompt is active: until then, an earlier autonomous
      // cycle's updates stay out of turn and its end does not answer this prompt.
      this.startTurn(params as any, { inserted: () => openPrompts.set(sessionId, mine), ended: resolve, failed: reject } as any).catch(reject);
    }).finally(() => openPrompts.get(sessionId) === mine && openPrompts.delete(sessionId));
  }
  async cancel(params: Params) {
    const session = this.sessions[params.sessionId];
    await Promise.all([
      super.cancel(params as any),
      ...(backgroundTasks.get(params.sessionId) ?? []).map((task) => session.query.stopTask(task.task_id))
    ]);
  }
}

// F: a result never widens a window the session already holds as authoritative.
const clampedQueries = new WeakSet<object>();
function clampResultWindows(session: any) {
  const { query } = session;
  if (!query || clampedQueries.has(query)) return;
  clampedQueries.add(query);
  const next = query.next.bind(query);
  query.next = async (...args: any[]) => {
    const result = await next(...args);
    const message = result.value;
    if (!result.done && message?.type === 'result' && session.contextWindowAuthoritative && session.contextWindowSize > 0) {
      for (const usage of Object.values<any>(message.modelUsage ?? {})) {
        if (usage.contextWindow > session.contextWindowSize) usage.contextWindow = session.contextWindowSize;
      }
    }
    return result;
  };
}

const withRawTasks = (input: any) => {
  const p = privateMcpHeaders(input);
  return { ...p, _meta: { ...p._meta, claudeCode: { ...p._meta?.claudeCode, emitRawSDKMessages: RAW_TASKS } } };
};
const passThrough = { parse: (p: any) => p };
const app = acpAgent({ name: 'claude-code-acp' })
  .onConnect((connection) => {
    client = new Client(connection.client);
    agent = new LauncherAgent(client as any, logger as any, { foreground } as any);
    agent.sessions = new Proxy(agent.sessions, {
      set(sessions, id, session) {
        if (session) clampResultWindows(session);
        sessions[id as string] = session;
        return true;
      }
    });
  })
  .onRequest(methods.agent.initialize, (ctx) => agent.initialize(ctx.params))
  .onRequest(methods.agent.session.new, (ctx) => agent.newSession(withRawTasks(ctx.params)))
  .onRequest(methods.agent.session.load, (ctx) => agent.loadSession(withRawTasks(ctx.params)))
  .onRequest(methods.agent.session.fork, (ctx) => agent.unstable_forkSession(withRawTasks(ctx.params)))
  .onRequest(methods.agent.session.list, (ctx) => agent.listSessions(ctx.params))
  .onRequest(methods.agent.session.delete, (ctx) => agent.deleteSession(ctx.params))
  .onRequest(methods.agent.session.resume, (ctx) => agent.resumeSession(withRawTasks(ctx.params)))
  .onRequest(methods.agent.session.close, (ctx) => agent.closeSession(ctx.params))
  .onRequest(methods.agent.session.setMode, (ctx) => agent.setSessionMode(ctx.params))
  .onRequest(methods.agent.session.setConfigOption, (ctx) => agent.setSessionConfigOption(ctx.params))
  .onRequest(methods.agent.authenticate, (ctx) => agent.authenticate(ctx.params))
  .onRequest(methods.agent.providers.list, (ctx) => agent.unstable_listProviders(ctx.params))
  .onRequest(methods.agent.providers.set, (ctx) => agent.unstable_setProvider(ctx.params))
  .onRequest(methods.agent.providers.disable, (ctx) => agent.unstable_disableProvider(ctx.params))
  .onRequest(methods.agent.logout, (ctx) => agent.logout(ctx.params))
  .onRequest(methods.agent.session.prompt, (ctx) => runPromptWithCancellation(agent, ctx.params, ctx.signal))
  .onNotification(methods.agent.session.cancel, (ctx) => agent.cancel(ctx.params))
  // The adapter's own parsers for these two are not exported.
  .onRequest('_session/steering', passThrough, (ctx) => agent.steer(ctx.params))
  .onRequest('_session/async_task/stop', passThrough, (ctx) => agent.stopAsyncTask(ctx.params))
  .onRequest(GOAL_CONTROL_METHOD, { parse: parseGoalRequest }, (ctx) => agent.goal(ctx.params));

const connection = onSessionIndexRequests(app, () => agent as any)
  .connect(ndJsonStream(nodeToWebWritable(process.stdout), nodeToWebReadable(process.stdin) as any));
const shutdown = async () => {
  await agent?.dispose().catch((error) => console.error('Error during cleanup:', error));
  process.exit(0);
};
connection.closed.then(shutdown);
process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
process.stdin.resume();
