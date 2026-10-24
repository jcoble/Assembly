#!/usr/bin/env node

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { createInterface } from "node:readline";

const directory = path.dirname(fileURLToPath(import.meta.url));
const bridgePath = path.join(directory, "bridge.mjs");
const mockCodexPath = path.join(directory, "mock-codex.mjs");
const mockMode = process.argv.includes("--mock");

class ProbePeer {
  constructor() {
    this.nextId = 1;
    this.pending = new Map();
    this.chunks = [];
    this.stderr = "";
    this.child = spawn(process.execPath, [bridgePath], {
      env: mockMode
        ? {
            ...process.env,
            CODEX_BIN: mockCodexPath,
            BRIDGE_MOCK_SCENARIO: "normal",
          }
        : process.env,
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.child.stderr.on("data", (chunk) => {
      this.stderr += chunk;
    });
    this.exit = new Promise((resolve) => {
      this.child.on("close", (code, signal) => resolve({ code, signal }));
    });
    this.lines = createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    this.reader = this.read();
  }

  request(method, params, timeoutMs = 120_000) {
    const id = this.nextId++;
    const response = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(String(id));
        reject(new Error(`Timed out waiting for ${method}`));
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
    this.child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id, method, params })}\n`);
    return response;
  }

  async read() {
    for await (const line of this.lines) {
      const message = JSON.parse(line);
      if (message.method === "session/update") {
        const text = message.params?.update?.content?.text;
        if (typeof text === "string") this.chunks.push(text);
        continue;
      }
      if (message.method === "session/request_permission") {
        this.child.stdin.write(
          `${JSON.stringify({
            jsonrpc: "2.0",
            id: message.id,
            result: { outcome: { outcome: "selected", optionId: "allow" } },
          })}\n`,
        );
        continue;
      }
      const pending = this.pending.get(String(message.id));
      if (!pending) continue;
      this.pending.delete(String(message.id));
      if (message.error) pending.reject(Object.assign(new Error(message.error.message), message.error));
      else pending.resolve(message.result);
    }
    for (const pending of this.pending.values()) pending.reject(new Error("Bridge transport closed"));
    this.pending.clear();
  }

  async initialize() {
    return this.request("initialize", { protocolVersion: 1 });
  }

  async close(sessionId) {
    await this.request("session/close", { sessionId });
    const exited = await this.exit;
    await this.reader;
    assert.equal(exited.code, 0, this.stderr);
  }

  async stop() {
    if (this.child.exitCode !== null || this.child.signalCode !== null) return;
    if (!this.child.stdin.destroyed) this.child.stdin.end();
    const stopped = await Promise.race([
      this.exit.then(() => true),
      new Promise((resolve) => setTimeout(() => resolve(false), 1_000)),
    ]);
    if (!stopped) this.child.kill("SIGTERM");
    await this.exit;
  }
}

const scratch = await mkdtemp(path.join(directory, ".bridge-probe-"));
let modelUsed;
const peers = [];
try {
  console.log(`mode: ${mockMode ? "mock" : "live"}`);
  const first = new ProbePeer();
  peers.push(first);
  const initialized = await first.initialize();
  assert.equal(initialized.protocolVersion, 1);
  assert.equal(initialized.agentCapabilities.loadSession, true);
  assert.equal(typeof initialized.agentInfo.version, "string");
  console.log(
    `initialize: ok protocolVersion=1 loadSession=true agentVersion=${initialized.agentInfo.version}`,
  );

  const created = await first.request("session/new", { cwd: scratch, mcpServers: [] });
  assert.equal(typeof created.sessionId, "string");
  console.log(`session/new: ok sessionId=${created.sessionId}`);

  const prompt = await first.request("session/prompt", {
    sessionId: created.sessionId,
    prompt: [{ type: "text", text: "Reply with exactly: pong" }],
  });
  assert.equal(prompt.stopReason, "end_turn");
  assert.ok(first.chunks.length > 0);
  assert.equal(first.chunks.join("").trim(), "pong");
  modelUsed = prompt.model ?? created.model;
  assert.equal(typeof modelUsed, "string");
  console.log(
    `session/prompt: ok stopReason=end_turn chunks=${first.chunks.length} text=${JSON.stringify(first.chunks.join(""))}`,
  );
  console.log(`model: ${modelUsed}`);
  await first.close(created.sessionId);
  console.log("session/close: ok exitCode=0");

  const second = new ProbePeer();
  peers.push(second);
  await second.initialize();
  const loaded = await second.request("session/load", {
    sessionId: created.sessionId,
    cwd: scratch,
  });
  assert.equal(loaded.sessionId, created.sessionId);
  console.log("session/load: ok persistedThread=true excludeTurns=true");
  const resumed = await second.request("session/resume", {
    sessionId: created.sessionId,
    cwd: scratch,
  });
  assert.equal(resumed.sessionId, created.sessionId);
  console.log("session/resume: ok sameThread=true excludeTurns=true");
  await second.close(created.sessionId);
  console.log("resume bridge close: ok exitCode=0");

  console.log("STATUS PASS");
  console.log(`MODEL_USED ${modelUsed}`);
} catch (error) {
  console.error(error.stack || error);
  const stderr = peers
    .map((peer) => peer.stderr.trim())
    .filter(Boolean)
    .join("\n");
  if (stderr) console.error(`bridge stderr:\n${stderr}`);
  console.log("STATUS BLOCKED");
  console.log(`MODEL_USED ${modelUsed ?? "unknown"}`);
  process.exitCode = 1;
} finally {
  await Promise.allSettled(peers.map((peer) => peer.stop()));
  await rm(scratch, { recursive: true, force: true });
}
