#!/usr/bin/env node

import { spawn, execFile } from "node:child_process";
import { mkdir } from "node:fs/promises";
import { createInterface } from "node:readline";
import { promisify } from "node:util";
import path from "node:path";

const execFileAsync = promisify(execFile);
const home = process.env.HOME;
const timeoutMs = Number(process.env.MUXSPIKE19_TIMEOUT_MS ?? 240_000);
const scratchA = "/tmp/mux-a";
const scratchB = "/tmp/mux-b";

if (!home) throw new Error("HOME is unavailable");

const adapters = [
  {
    name: "codex",
    command: process.env.MUXSPIKE19_CODEX_ADAPTER ?? path.join(home, ".mac-command-bar/codex-acp-bridge.sh"),
    args: [],
    env: process.env,
  },
  {
    name: "claude-code-acp",
    command:
      process.env.MUXSPIKE19_CLAUDE_ADAPTER ??
      path.join(home, ".mac-command-bar/claude-acp-wrapper.sh"),
    args: [],
    env: Object.fromEntries(
      Object.entries(process.env).filter(
        ([key]) =>
          ![
            "CLAUDECODE",
            "CLAUDE_CODE_ENTRYPOINT",
            "CLAUDE_CODE_SSE_PORT",
            "CLAUDE_CODE_PLUGIN_ROOT",
            "CLAUDE_PLUGIN_DATA",
          ].includes(key),
      ),
    ),
  },
];

const requested = new Set(process.argv.slice(2));
const selected = requested.size === 0 ? adapters : adapters.filter(({ name }) => requested.has(name));
if (selected.length === 0) {
  throw new Error(`No matching adapter. Choose: ${adapters.map(({ name }) => name).join(", ")}`);
}

await Promise.all([mkdir(scratchA, { recursive: true }), mkdir(scratchB, { recursive: true })]);

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function redactText(value) {
  return String(value)
    .replace(/\b(sk-[A-Za-z0-9_-]{8,}|Bearer\s+[A-Za-z0-9._~+\/-]{8,}|[A-Za-z0-9_-]{48,})\b/g, "[REDACTED]")
    .replaceAll(home, "~");
}

function redacted(value, key = "") {
  if (/token|secret|credential|authorization|api[_-]?key/i.test(key)) return "[REDACTED]";
  if (typeof value === "string") return redactText(value);
  if (Array.isArray(value)) return value.map((entry) => redacted(entry));
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([childKey, child]) => [childKey, redacted(child, childKey)]));
  }
  return value;
}

function compactUpdate(message, aliases) {
  const params = message.params ?? {};
  const update = params.update ?? {};
  const kind = update.sessionUpdate;
  let compact;
  if (kind === "available_commands_update") {
    compact = { sessionUpdate: kind, commandCount: update.availableCommands?.length ?? 0 };
  } else if (kind === "agent_message_chunk") {
    compact = { sessionUpdate: kind, content: redacted(update.content) };
  } else if (kind === "tool_call" || kind === "tool_call_update") {
    compact = redacted({
      sessionUpdate: kind,
      title: update.title,
      status: update.status,
      rawInput: update.rawInput,
      rawOutput: update.rawOutput,
      content: update.content,
      locations: update.locations,
      toolResponse: update._meta?.claudeCode?.toolResponse,
    });
  } else {
    compact = redacted(update);
  }
  const result = {
    jsonrpc: "2.0",
    method: "session/update",
    params: {
      sessionId: aliases.get(params.sessionId) ?? params.sessionId,
      update: compact,
    },
  };
  return result;
}

async function processRows() {
  const { stdout } = await execFileAsync("/bin/ps", ["-axo", "pid=,ppid=,pgid=,rss=,command="], {
    maxBuffer: 8 * 1024 * 1024,
  });
  return stdout
    .split("\n")
    .map((line) => line.match(/^\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(.*)$/))
    .filter(Boolean)
    .map((match) => ({
      pid: Number(match[1]),
      ppid: Number(match[2]),
      pgid: Number(match[3]),
      rssKb: Number(match[4]),
      command: match[5],
    }));
}

async function processTree(rootPid) {
  const rows = await processRows();
  const selectedPids = new Set([rootPid]);
  let changed = true;
  while (changed) {
    changed = false;
    for (const row of rows) {
      if (selectedPids.has(row.ppid) && !selectedPids.has(row.pid)) {
        selectedPids.add(row.pid);
        changed = true;
      }
    }
  }
  return rows.filter((row) => selectedPids.has(row.pid));
}

async function footprintBytes(pid) {
  try {
    const { stdout, stderr } = await execFileAsync(
      "/usr/bin/footprint",
      ["-p", String(pid), "--noCategories", "-f", "bytes"],
      { maxBuffer: 4 * 1024 * 1024 },
    );
    const match = `${stdout}\n${stderr}`.match(/Footprint:\s+(\d+)\s+B/);
    return match ? Number(match[1]) : null;
  } catch {
    return null;
  }
}

async function measure(rootPid) {
  const rows = await processTree(rootPid);
  const footprints = await Promise.all(rows.map(({ pid }) => footprintBytes(pid)));
  return {
    pids: rows.map(({ pid }) => pid),
    rssKb: rows.reduce((sum, row) => sum + row.rssKb, 0),
    footprintBytes: footprints.every((value) => value === null)
      ? null
      : footprints.reduce((sum, value) => sum + (value ?? 0), 0),
    processes: rows.map((row, index) => ({
      pid: row.pid,
      ppid: row.ppid,
      rssKb: row.rssKb,
      footprintBytes: footprints[index],
      executable: path.basename(row.command.split(/\s+/)[0]),
    })),
  };
}

class Peer {
  constructor(adapter) {
    this.adapter = adapter;
    this.nextId = 1;
    this.pending = new Map();
    this.updates = [];
    this.aliases = new Map();
    this.stderrLines = [];
    this.observedPids = new Set();
    this.child = spawn(adapter.command, adapter.args, {
      cwd: scratchA,
      env: adapter.env,
      stdio: ["pipe", "pipe", "pipe"],
      detached: true,
    });
    this.rootPid = this.child.pid;
    this.observedPids.add(this.rootPid);
    this.exit = new Promise((resolve) => {
      this.child.once("close", (code, signal) => resolve({ code, signal }));
    });
    this.child.stderr.setEncoding("utf8");
    let stderrBuffer = "";
    this.child.stderr.on("data", (chunk) => {
      stderrBuffer += chunk;
      const lines = stderrBuffer.split("\n");
      stderrBuffer = lines.pop() ?? "";
      this.stderrLines.push(...lines.map(redactText).filter(Boolean));
      if (this.stderrLines.length > 200) this.stderrLines.splice(0, this.stderrLines.length - 200);
    });
    this.reader = this.read();
    console.log(`${adapter.name} SPAWN pid=${this.rootPid}`);
  }

  write(message) {
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  request(method, params, label = method) {
    const id = this.nextId++;
    const frame = { jsonrpc: "2.0", id, method, params };
    const response = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(String(id));
        reject(new Error(`${label} timed out after ${timeoutMs}ms`));
      }, timeoutMs);
      this.pending.set(String(id), {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
    });
    const loggedFrame = redacted(frame);
    if (loggedFrame.params?.sessionId) {
      loggedFrame.params.sessionId = this.aliases.get(params.sessionId) ?? params.sessionId;
    }
    console.log(`${this.adapter.name} TX ${JSON.stringify(loggedFrame)}`);
    this.write(frame);
    return response;
  }

  async read() {
    const lines = createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    for await (const line of lines) {
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        continue;
      }
      if (message.method === "session/update") {
        this.updates.push(message);
        console.log(`${this.adapter.name} RX ${JSON.stringify(compactUpdate(message, this.aliases))}`);
        continue;
      }
      if (message.method === "session/request_permission") {
        const options = message.params?.options ?? [];
        const allow =
          options.find((option) => String(option.kind).startsWith("allow")) ??
          options.find((option) => /allow/i.test(String(option.name))) ??
          options[0];
        const reply = allow
          ? { jsonrpc: "2.0", id: message.id, result: { outcome: { outcome: "selected", optionId: allow.optionId } } }
          : { jsonrpc: "2.0", id: message.id, error: { code: -32000, message: "No safe allow option" } };
        console.log(
          `${this.adapter.name} RX ${JSON.stringify(
            redacted({
              jsonrpc: "2.0",
              id: message.id,
              method: message.method,
              params: {
                sessionId: this.aliases.get(message.params?.sessionId) ?? message.params?.sessionId,
                title: message.params?.title,
                options: options.map(({ optionId, name, kind }) => ({ optionId, name, kind })),
              },
            }),
          )}`,
        );
        console.log(`${this.adapter.name} TX ${JSON.stringify(reply)}`);
        this.write(reply);
        continue;
      }
      if (message.method && message.id !== undefined) {
        const reply = { jsonrpc: "2.0", id: message.id, error: { code: -32601, message: "Unsupported probe client method" } };
        console.log(`${this.adapter.name} RX_REQUEST method=${message.method} id=${message.id}`);
        this.write(reply);
        continue;
      }
      const pending = this.pending.get(String(message.id));
      if (!pending) continue;
      this.pending.delete(String(message.id));
      if (message.error) {
        const error = Object.assign(new Error(redactText(message.error.message ?? "JSON-RPC error")), redacted(message.error));
        console.log(`${this.adapter.name} RX ${JSON.stringify(redacted(message))}`);
        pending.reject(error);
      } else {
        pending.resolve(message.result);
      }
    }
    for (const pending of this.pending.values()) pending.reject(new Error("Adapter stdout closed"));
    this.pending.clear();
  }

  aliasSession(sessionId, alias) {
    this.aliases.set(sessionId, alias);
  }

  async rememberTree() {
    for (const { pid } of await processTree(this.rootPid)) this.observedPids.add(pid);
  }

  relevantStderr() {
    const important = this.stderrLines.filter((line) => /error|auth|login|fail|invalid|denied/i.test(line));
    return (important.length > 0 ? important : this.stderrLines).slice(-12);
  }

  async stop() {
    await this.rememberTree().catch(() => {});
    if (!this.child.stdin.destroyed) this.child.stdin.end();
    await Promise.race([this.exit, delay(1_000)]);
    try {
      process.kill(-this.rootPid, "SIGTERM");
    } catch {}
    await delay(1_000);
    try {
      process.kill(-this.rootPid, "SIGKILL");
    } catch {}
    await Promise.race([this.exit, delay(2_000)]);
    await this.reader.catch(() => {});
    let survivors = [];
    for (let attempt = 0; attempt < 10; attempt += 1) {
      const rows = await processRows();
      survivors = rows
        .filter((row) => row.pgid === this.rootPid || this.observedPids.has(row.pid))
        .map(({ pid }) => pid);
      if (survivors.length === 0) break;
      await delay(250);
    }
    console.log(
      `${this.adapter.name} CLEANUP root=${this.rootPid} observed=${[...this.observedPids].join(",")} survivors=${survivors.join(",") || "none"}`,
    );
    return { rootPid: this.rootPid, observedPids: [...this.observedPids], survivors };
  }
}

function textFor(updates, sessionId) {
  return updates
    .filter(
      ({ params }) =>
        params?.sessionId === sessionId && params?.update?.sessionUpdate === "agent_message_chunk",
    )
    .map(({ params }) => params.update?.content?.text ?? "")
    .join("")
    .trim();
}

function finalTextFor(updates, sessionId) {
  const sessionUpdates = updates.filter(({ params }) => params?.sessionId === sessionId);
  let afterIndex = 0;
  for (let index = 0; index < sessionUpdates.length; index += 1) {
    const update = sessionUpdates[index].params?.update;
    if (update?.sessionUpdate === "tool_call_update" && update?.status === "completed") {
      afterIndex = index + 1;
    }
  }
  return textFor(sessionUpdates.slice(afterIndex), sessionId);
}

function cwdEvidence(updates, sessionId, cwd) {
  return updates
    .filter(({ params }) => params?.sessionId === sessionId)
    .some((message) => JSON.stringify(message.params?.update ?? {}).includes(cwd));
}

async function runAdapter(adapter) {
  const peer = new Peer(adapter);
  const result = {
    adapter: adapter.name,
    rootPid: peer.rootPid,
    cleanup: null,
  };
  try {
    const initialized = await peer.request("initialize", { protocolVersion: 1 });
    result.capabilities = initialized?.agentCapabilities ?? null;
    console.log(
      `${adapter.name} INIT ${JSON.stringify(
        redacted({
          protocolVersion: initialized?.protocolVersion,
          agentInfo: initialized?.agentInfo,
          agentCapabilities: initialized?.agentCapabilities,
        }),
      )}`,
    );

    const first = await peer.request("session/new", { cwd: scratchA, mcpServers: [] }, "first session/new");
    if (typeof first?.sessionId !== "string") throw new Error("First session/new returned no sessionId");
    const s1 = first.sessionId;
    peer.aliasSession(s1, "S1");
    console.log(`${adapter.name} RX ${JSON.stringify({ jsonrpc: "2.0", result: { sessionId: "S1" } })}`);
    await peer.rememberTree();
    result.oneSession = await measure(peer.rootPid);
    console.log(`${adapter.name} MEMORY one-session ${JSON.stringify(result.oneSession)}`);

    const second = await peer.request("session/new", { cwd: scratchB, mcpServers: [] }, "second session/new");
    if (typeof second?.sessionId !== "string") throw new Error("Second session/new returned no sessionId");
    const s2 = second.sessionId;
    peer.aliasSession(s2, "S2");
    result.sessionIdsDistinct = s1 !== s2;
    console.log(`${adapter.name} RX ${JSON.stringify({ jsonrpc: "2.0", result: { sessionId: "S2" } })}`);
    await peer.rememberTree();
    result.twoSessions = await measure(peer.rootPid);
    console.log(`${adapter.name} MEMORY two-sessions ${JSON.stringify(result.twoSessions)}`);

    const p1 = peer.request(
      "session/prompt",
      {
        sessionId: s1,
        prompt: [{ type: "text", text: "Run pwd with the shell tool. Then reply exactly ALPHA and no other text." }],
      },
      "S1 session/prompt",
    );
    const p2 = peer.request(
      "session/prompt",
      {
        sessionId: s2,
        prompt: [{ type: "text", text: "Run pwd with the shell tool. Then reply exactly BRAVO and no other text." }],
      },
      "S2 session/prompt",
    );
    const promptResults = await Promise.allSettled([p1, p2]);
    result.promptResults = promptResults.map((entry) =>
      entry.status === "fulfilled"
        ? {
            status: entry.status,
            result: {
              stopReason: entry.value?.stopReason,
              turnId: entry.value?.turnId ? "[TURN_ID]" : undefined,
            },
          }
        : { status: entry.status, reason: redactText(entry.reason?.message ?? entry.reason) },
    );
    result.finalText = { S1: finalTextFor(peer.updates, s1), S2: finalTextFor(peer.updates, s2) };
    result.updateSessionIds = [...new Set(peer.updates.map(({ params }) => params?.sessionId))].map(
      (sessionId) => peer.aliases.get(sessionId) ?? "UNKNOWN",
    );
    result.routingPass =
      promptResults.every(({ status }) => status === "fulfilled") &&
      result.finalText.S1 === "ALPHA" &&
      result.finalText.S2 === "BRAVO" &&
      !result.finalText.S1.includes("BRAVO") &&
      !result.finalText.S2.includes("ALPHA") &&
      result.updateSessionIds.every((sessionId) => sessionId === "S1" || sessionId === "S2");
    result.cwd = {
      S1: cwdEvidence(peer.updates, s1, scratchA),
      S2: cwdEvidence(peer.updates, s2, scratchB),
    };
    console.log(
      `${adapter.name} RESULT ${JSON.stringify({
        sessionIdsDistinct: result.sessionIdsDistinct,
        promptResults: result.promptResults,
        finalText: result.finalText,
        updateSessionIds: result.updateSessionIds,
        routingPass: result.routingPass,
        cwd: result.cwd,
      })}`,
    );
  } catch (error) {
    result.failure = redactText(error?.stack ?? error);
    result.stderrStatus = peer.relevantStderr();
    console.log(`${adapter.name} FAILURE ${JSON.stringify({ failure: result.failure, stderrStatus: result.stderrStatus })}`);
  } finally {
    result.cleanup = await peer.stop();
  }
  return result;
}

const results = [];
for (const adapter of selected) results.push(await runAdapter(adapter));
console.log(`MUXSPIKE19_SUMMARY ${JSON.stringify(results)}`);
if (results.some(({ cleanup }) => cleanup?.survivors?.length > 0)) process.exitCode = 2;
