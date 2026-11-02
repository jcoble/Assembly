#!/usr/bin/env node
// Temporary bridge for claude-agent-acp PR 1499. Delete scripts/adapters/ and use
// stock dist/index.js when the daily latest supports steering on its v2 surface.
import { appendFileSync, mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { v2AgentApp } from "@agentclientprotocol/claude-agent-acp/dist/v2/agent.js";
import { nodeToWebReadable, nodeToWebWritable } from "@agentclientprotocol/claude-agent-acp/dist/utils.js";
import { applyManagedPolicyEnv } from "@agentclientprotocol/claude-agent-acp/dist/managed-policy.js";

// Resolve from the adapter, including a pnpm installation with no hoisted SDK.
const requireFromAdapter = createRequire(import.meta.resolve("@agentclientprotocol/claude-agent-acp"));
const v2 = await import(pathToFileURL(requireFromAdapter.resolve("@agentclientprotocol/sdk/experimental/v2")).href);
type Logger = { log: (...args: unknown[]) => void; error: (...args: unknown[]) => void };
type Agent = Parameters<NonNullable<Parameters<typeof v2AgentApp>[1]>>[0];

function parseSteer(params: unknown) {
  const invalid = () => { throw v2.RequestError.invalidParams(undefined, "Invalid steering parameters"); };
  if (!params || typeof params !== "object" || Array.isArray(params)) return invalid();
  const { sessionId, prompt, _meta } = params as Record<string, any>;
  if (typeof sessionId !== "string" || sessionId.length === 0 || !Array.isArray(prompt) || prompt.length === 0) return invalid();
  const idleBehavior = _meta?.steering?.idleBehavior;
  if (idleBehavior !== undefined && idleBehavior !== "promptRequired") return invalid();
  return { sessionId, prompt, _meta };
}

export function launcherRouter(logger: Logger | undefined, onAgent: (agent: Agent) => void) {
  let agent: Agent;
  return v2.agentProtocolRouter().withV2(v2AgentApp(logger, created => {
    agent = created; onAgent(created);
  }).onRequest("_session/steering", { parse: parseSteer }, ctx => agent.steer(ctx.params)));
}

// Frame bytes without decoding/re-encoding unmodified lines, even across UTF-8
// boundaries. Keep partial frames as chunks instead of repeatedly copying them.
function lines(visit: (line: Buffer) => void) {
  let parts: Buffer[] = [];
  return {
    push(bytes: Uint8Array) {
      const chunk = Buffer.from(bytes); let start = 0;
      for (let end = chunk.indexOf(10); end !== -1; end = chunk.indexOf(10, start)) {
        parts.push(chunk.subarray(start, end + 1)); visit(Buffer.concat(parts));
        parts = []; start = end + 1;
      }
      if (start < chunk.length) parts.push(chunk.subarray(start));
    },
    flush() { if (parts.length) visit(Buffer.concat(parts)); parts = []; },
  };
}
function json(line: Buffer) { try { return JSON.parse(line.toString("utf8")); } catch { return undefined; } }

export function withSteeringMeta(output: WritableStream<Uint8Array>, input: ReadableStream<Uint8Array>) {
  const initializeIds = new Set<unknown>();
  const observe = lines(line => {
    const message = json(line);
    if (message?.method === "initialize" && message.id != null) initializeIds.add(message.id);
  });
  const writer = output.getWriter();
  let ready: Buffer[] = [];
  const patch = lines(line => {
    const message = json(line);
    if (message && initializeIds.has(message.id) && !message.method && (message.result || message.error)) {
      initializeIds.delete(message.id);
      if (message.result) {
        message.result._meta = { ...message.result._meta, steering: { supported: true } };
        const ending = line.at(-1) === 10 ? (line.at(-2) === 13 ? "\r\n" : "\n") : "";
        line = Buffer.from(JSON.stringify(message) + ending);
      }
    }
    ready.push(line);
  });
  const drain = async () => { for (const bytes of ready) await writer.write(bytes); ready = []; };
  return {
    input: input.pipeThrough(new TransformStream<Uint8Array, Uint8Array>({
      transform(chunk, controller) { observe.push(chunk); controller.enqueue(chunk); },
      flush() { observe.flush(); },
    })),
    output: new WritableStream<Uint8Array>({
      async write(chunk) { patch.push(chunk); await drain(); },
      async close() { try { patch.flush(); await drain(); await writer.close(); } finally { writer.releaseLock(); } },
      async abort(reason) { try { await writer.abort(reason); } finally { writer.releaseLock(); } },
    }),
  };
}

async function main() {
  // Stock 0.99.1 dist/index.js:11–43: delegate CLI first, including flags,
  // inherited stdio, exit code and signal forwarding. Reuse the exported entry.
  if (process.argv.some(arg => ["--cli", "--version", "-v"].includes(arg))) {
    await import("@agentclientprotocol/claude-agent-acp/dist/index.js"); return;
  }
  // index.js:46–59: policy before SDK calls; diagnostics never enter ACP stdout.
  await applyManagedPolicyEnv();
  console.log = console.info = console.warn = console.debug = console.error;
  process.on("unhandledRejection", (reason, promise) => console.error("Unhandled Rejection at:", promise, "reason:", reason));
  // index.js:60–80: optional CLAUDE_AGENT_LOGS/agent.log and matching formatting.
  const directory = process.env.CLAUDE_AGENT_LOGS;
  const writeLog = (...args: unknown[]) => appendFileSync(join(directory!, "agent.log"),
    `${new Date().toISOString()} pid=${process.pid} ${args.map(arg => arg instanceof Error ? (arg.stack ?? arg.message) : String(arg)).join(" ")}\n`);
  if (directory) mkdirSync(directory, { recursive: true });
  const logger = directory ? { log: writeLog, error: (...args: unknown[]) => { console.error(...args); writeLog(...args); } } : undefined;
  logger?.log("Claude ACP started");
  let agent: Agent;
  const stream = withSteeringMeta(nodeToWebWritable(process.stdout), nodeToWebReadable(process.stdin));
  const { closed } = launcherRouter(logger, created => { agent = created; }).connect(v2.ndJsonStream(stream.output, stream.input));
  // index.js:84–97: dispose on close/SIGTERM/SIGINT, keep stdin alive. Coalesce
  // overlapping shutdown triggers so the same agent is disposed only once.
  let shuttingDown: Promise<void> | undefined;
  const shutdown = () => shuttingDown ??= (async () => {
    try { await agent?.dispose(); } catch (error) { console.error("Error during cleanup:", error); }
    process.exit(0);
  })();
  closed.then(shutdown);
  process.on("SIGTERM", shutdown); process.on("SIGINT", shutdown);
  process.stdin.resume();
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => { console.error(error); process.exitCode = 1; });
}
