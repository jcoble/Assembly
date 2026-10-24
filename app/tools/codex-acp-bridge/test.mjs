#!/usr/bin/env node

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { createInterface } from "node:readline";

const directory = path.dirname(fileURLToPath(import.meta.url));
const bridgePath = path.join(directory, "bridge.mjs");
const mockCodexPath = path.join(directory, "mock-codex.mjs");

class AcpPeer {
  constructor(scenario = "normal", permissionChoice = "allow", codexBin = mockCodexPath) {
    this.nextId = 1;
    this.pending = new Map();
    this.updates = [];
    this.updateWaiters = [];
    this.permissions = [];
    this.permissionWaiters = [];
    this.permissionChoice = permissionChoice;
    this.messages = [];
    this.stderr = "";
    this.child = spawn(process.execPath, [bridgePath], {
      env: {
        ...process.env,
        CODEX_BIN: codexBin,
        BRIDGE_MOCK_SCENARIO: scenario,
      },
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

  send(message) {
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  request(method, params, timeoutMs = 10_000) {
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
    this.send({ jsonrpc: "2.0", id, method, params });
    return response;
  }

  notify(method, params) {
    this.send({ jsonrpc: "2.0", method, params });
  }

  waitForUpdate(timeoutMs = 10_000) {
    if (this.updates.length > 0) return Promise.resolve(this.updates[this.updates.length - 1]);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("Timed out waiting for session/update")), timeoutMs);
      this.updateWaiters.push((message) => {
        clearTimeout(timer);
        resolve(message);
      });
    });
  }

  waitForPermission(timeoutMs = 10_000) {
    if (this.permissions.length > 0) {
      return Promise.resolve(this.permissions[this.permissions.length - 1]);
    }
    return new Promise((resolve, reject) => {
      const timer = setTimeout(
        () => reject(new Error("Timed out waiting for session/request_permission")),
        timeoutMs,
      );
      this.permissionWaiters.push((message) => {
        clearTimeout(timer);
        resolve(message);
      });
    });
  }

  respondToPermission(message) {
    if (this.permissionChoice === "defer") return;
    if (this.permissionChoice === "error") {
      this.send({
        jsonrpc: "2.0",
        id: message.id,
        error: { code: -32000, message: "permission UI closed" },
      });
      return;
    }
    if (this.permissionChoice === "cancel") {
      this.send({
        jsonrpc: "2.0",
        id: message.id,
        result: { outcome: { outcome: "cancelled" } },
      });
      return;
    }
    this.send({
      jsonrpc: "2.0",
      id: message.id,
      result: {
        outcome: { outcome: "selected", optionId: this.permissionChoice },
      },
    });
  }

  async read() {
    for await (const line of this.lines) {
      const message = JSON.parse(line);
      this.messages.push(message);
      if (message.method === "session/update") {
        this.updates.push(message);
        for (const waiter of this.updateWaiters.splice(0)) waiter(message);
        continue;
      }
      if (message.method === "session/request_permission") {
        this.permissions.push(message);
        for (const waiter of this.permissionWaiters.splice(0)) waiter(message);
        this.respondToPermission(message);
        continue;
      }
      if (Object.hasOwn(message, "id")) {
        const pending = this.pending.get(String(message.id));
        if (!pending) continue;
        this.pending.delete(String(message.id));
        if (message.error) pending.reject(Object.assign(new Error(message.error.message), message.error));
        else pending.resolve(message.result);
      }
    }
    for (const pending of this.pending.values()) pending.reject(new Error("Bridge transport closed"));
    this.pending.clear();
  }

  async initialize() {
    return this.request("initialize", { protocolVersion: 1 });
  }

  async newSession() {
    return this.request("session/new", { cwd: directory, mcpServers: [] });
  }

  async close(sessionId) {
    const result = await this.request("session/close", { sessionId });
    const exited = await this.exit;
    await this.reader;
    return { result, exited };
  }
}

async function withSession(scenario, callback, permissionChoice = "allow") {
  const peer = new AcpPeer(scenario, permissionChoice);
  const initialized = await peer.initialize();
  assert.equal(initialized.protocolVersion, 1);
  const session = await peer.newSession();
  try {
    await callback(peer, session);
  } finally {
    if (peer.child.exitCode === null && peer.child.signalCode === null) {
      const closed = await peer.close(session.sessionId);
      assert.deepEqual(closed.result, {});
      assert.equal(closed.exited.code, 0, peer.stderr);
    }
  }
}

function updatesOf(peer, kind) {
  return peer.updates
    .map((message) => message.params.update)
    .filter((update) => update.sessionUpdate === kind);
}

function createReplayMock() {
  const root = mkdtempSync(path.join(tmpdir(), "mcb-bridge-replay-"));
  const executable = path.join(root, "mock-replay.mjs");
  const turns = [
    {
      id: "history-turn-1",
      status: "completed",
      items: [
        {
          id: "history-user-1",
          type: "userMessage",
          content: [{ type: "text", text: "First question" }],
        },
        {
          id: "history-thought-1",
          type: "reasoning",
          summary: ["First thought"],
          content: [],
        },
        {
          id: "history-tool-1",
          type: "commandExecution",
          command: "printf first",
          cwd: "/mock/work",
          aggregatedOutput: "first",
          exitCode: 0,
          status: "completed",
        },
        { id: "history-agent-1", type: "agentMessage", text: "First answer" },
      ],
    },
    {
      id: "history-turn-2",
      status: "completed",
      items: [
        {
          id: "history-user-2",
          type: "userMessage",
          content: [{ type: "text", text: "Second question" }],
        },
        {
          id: "history-tool-2",
          type: "mcpToolCall",
          server: "mock-server",
          tool: "lookup",
          arguments: {},
          result: { content: "found" },
          status: "completed",
        },
        { id: "history-agent-2", type: "agentMessage", text: "Second answer" },
      ],
    },
    {
      id: "history-turn-in-flight",
      status: "inProgress",
      items: [
        {
          id: "history-user-in-flight",
          type: "userMessage",
          content: [{ type: "text", text: "Do not replay" }],
        },
        { id: "history-agent-in-flight", type: "agentMessage", text: "Partial answer" },
      ],
    },
  ];
  writeFileSync(
    executable,
    `#!/usr/bin/env node
import { createInterface } from "node:readline";
const turns = ${JSON.stringify(turns)};
const send = (message) => process.stdout.write(JSON.stringify(message) + "\\n");
for await (const line of createInterface({ input: process.stdin, crlfDelay: Infinity })) {
  const message = JSON.parse(line);
  if (message.method === "initialized") continue;
  if (message.method === "initialize") {
    send({ id: message.id, result: { userAgent: "mock-replay/1.0.0" } });
  } else if (message.method === "thread/resume") {
    if (Object.hasOwn(message.params ?? {}, "excludeTurns")) {
      send({ id: message.id, error: { code: -32602, message: "excludeTurns must be omitted" } });
    } else {
      send({ id: message.id, result: {
        thread: { id: message.params.threadId, turns },
        model: "gpt-5.6-sol",
        cwd: message.params.cwd
      } });
    }
  } else {
    send({ id: message.id, error: { code: -32601, message: "unsupported mock method" } });
  }
}
`,
  );
  chmodSync(executable, 0o755);
  return { executable, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

function createConfigMock() {
  const root = mkdtempSync(path.join(tmpdir(), "mcb-bridge-config-"));
  const executable = path.join(root, "mock-config.mjs");
  const log = path.join(root, "frames.jsonl");
  writeFileSync(
    executable,
    `#!/usr/bin/env node
import { appendFileSync } from "node:fs";
import { createInterface } from "node:readline";
const log = ${JSON.stringify(log)};
const send = (message) => process.stdout.write(JSON.stringify(message) + "\\n");
let nextTurn = 1;
for await (const line of createInterface({ input: process.stdin, crlfDelay: Infinity })) {
  const message = JSON.parse(line);
  appendFileSync(log, JSON.stringify(message) + "\\n");
  if (message.method === "initialized") continue;
  if (message.method === "initialize") {
    send({ id: message.id, result: { userAgent: "mock-config/1.0.0" } });
  } else if (message.method === "model/list") {
    if (message.params?.cursor === "page-2") {
      send({ id: message.id, result: { data: [{ model: "gpt-5.6-luna" }], nextCursor: null } });
    } else {
      send({ id: message.id, result: {
        data: [{ model: "gpt-5.6-sol" }, { model: "gpt-5.6-terra" }],
        nextCursor: "page-2"
      } });
    }
  } else if (message.method === "thread/start") {
    send({ id: message.id, result: {
      thread: { id: "config-thread", turns: [] }, cwd: message.params.cwd,
      model: "gpt-5.6-sol", reasoningEffort: "high", approvalPolicy: "on-request",
      serviceTier: "priority"
    } });
  } else if (message.method === "thread/resume") {
    send({ id: message.id, result: {
      thread: { id: message.params.threadId, turns: [] }, cwd: message.params.cwd,
      model: "gpt-5.6-sol", reasoningEffort: "low", approvalPolicy: "untrusted",
      serviceTier: "priority"
    } });
  } else if (message.method === "turn/start") {
    const turnId = "config-turn-" + nextTurn++;
    send({ id: message.id, result: { turn: { id: turnId, status: "inProgress", items: [] } } });
    send({ method: "item/agentMessage/delta", params: {
      threadId: message.params.threadId, turnId, itemId: "config-answer", delta: "configured"
    } });
    send({ method: "turn/completed", params: {
      threadId: message.params.threadId,
      turn: { id: turnId, status: "completed", error: null, items: [] }
    } });
  } else {
    send({ id: message.id, error: { code: -32601, message: "unsupported mock method" } });
  }
}
`,
  );
  chmodSync(executable, 0o755);
  return {
    executable,
    frames: () => readFileSync(log, "utf8").trim().split("\n").filter(Boolean).map(JSON.parse),
    cleanup: () => rmSync(root, { recursive: true, force: true }),
  };
}

function replayUpdatesBeforeResponse(messages) {
  assert(messages.length > 1, "replay must emit updates before its response");
  const response = messages.at(-1);
  assert.equal(Object.hasOwn(response, "id"), true, "resume result must be last");
  const updates = messages.slice(0, -1);
  assert(updates.every((message) => message.method === "session/update"));
  return updates.map((message) => message.params.update);
}

function replayItemId(update) {
  return update.messageId ?? update.toolCallId ?? update.planId;
}

const tests = [];

tests.push([
  "protocol v1 validation, memoized child initialization, and real child version",
  async () => {
    const peer = new AcpPeer("normal");
    await assert.rejects(
      peer.request("initialize", { protocolVersion: 2 }),
      (error) => error.code === -32602,
    );
    const [first, second] = await Promise.all([peer.initialize(), peer.initialize()]);
    assert.equal(first.protocolVersion, 1);
    assert.equal(first.agentInfo.version, "9.8.7");
    assert.deepEqual(second, first);
    assert.equal(first.agentCapabilities.plans, true);
    assert.equal(first.agentCapabilities.promptCapabilities.image, true);
    const session = await peer.newSession();
    const closed = await peer.close(session.sessionId);
    assert.equal(closed.exited.code, 0, peer.stderr);
  },
]);

tests.push([
  "handshake, text delta, model, unknown ACP method, and clean shutdown",
  async () => {
    await withSession("normal", async (peer, session) => {
      assert.equal(session.model, "gpt-5.6-sol");
      await assert.rejects(
        peer.request("bridge/unknown", {}),
        (error) => error.code === -32601,
      );
      const prompt = await peer.request("session/prompt", {
        sessionId: session.sessionId,
        prompt: [{ type: "text", text: "Reply with exactly: pong" }],
      });
      assert.equal(prompt.stopReason, "end_turn");
      assert.equal(prompt.model, "gpt-5.6-sol");
      assert.equal(
        updatesOf(peer, "agent_message_chunk").map((update) => update.content.text).join(""),
        "pong",
      );
      assert.deepEqual(
        await peer.request("session/set_config_option", {
          sessionId: session.sessionId,
          model: "gpt-5.6-terra",
        }),
        {
          model: "gpt-5.6-terra",
          availableModels: ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"],
          reasoningEffort: null,
          availableEfforts: ["low", "medium", "high", "xhigh", "max"],
          approvalPolicy: null,
          availableApprovalPolicies: ["untrusted", "on-request", "never"],
        },
      );
    });
  },
]);

tests.push([
  "token usage notification emits an ACP usage update",
  async () => {
    await withSession("usage", async (peer, session) => {
      const prompt = await peer.request("session/prompt", {
        sessionId: session.sessionId,
        prompt: [{ type: "text", text: "Reply with the single word OK" }],
      });
      assert.equal(prompt.stopReason, "end_turn");
      assert.deepEqual(updatesOf(peer, "usage_update"), [
        {
          sessionUpdate: "usage_update",
          usedTokens: 29427,
          contextWindow: 237500,
          inputTokens: 29422,
          outputTokens: 5,
        },
      ]);
    });
  },
]);

tests.push([
  "session configuration is discovered, stored, applied to turn/start, and echoed by load/resume",
  async () => {
    const mock = createConfigMock();
    const peer = new AcpPeer("config", "allow", mock.executable);
    try {
      await peer.initialize();
      const session = await peer.newSession();
      assert.equal(mock.frames().find((frame) => frame.method === "thread/start")?.params.sandbox, "workspace-write");
      assert.deepEqual(session._meta, {
        model: "gpt-5.6-sol",
        availableModels: ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"],
        reasoningEffort: "high",
        availableEfforts: ["low", "medium", "high", "xhigh", "max"],
        approvalPolicy: "on-request",
        availableApprovalPolicies: ["untrusted", "on-request", "never"],
      });

      const configured = await peer.request("session/set_config_option", {
        sessionId: session.sessionId,
        model: "gpt-5.6-terra",
        reasoningEffort: "xhigh",
        approvalPolicy: "never",
      });
      assert.deepEqual(configured, {
        ...session._meta,
        model: "gpt-5.6-terra",
        reasoningEffort: "xhigh",
        approvalPolicy: "never",
      });

      assert.equal(
        (
          await peer.request("session/prompt", {
            sessionId: session.sessionId,
            prompt: [{ type: "text", text: "use configured values" }],
          })
        ).stopReason,
        "end_turn",
      );
      const firstTurn = mock.frames().find((frame) => frame.method === "turn/start");
      assert.equal(firstTurn.params.model, "gpt-5.6-terra");
      assert.equal(firstTurn.params.effort, "xhigh");
      assert.equal(firstTurn.params.approvalPolicy, "never");

      const loaded = await peer.request("session/load", {
        sessionId: session.sessionId,
        cwd: directory,
      });
      assert.deepEqual(loaded._meta, configured);
      const resumed = await peer.request("session/resume", {
        sessionId: session.sessionId,
        cwd: directory,
      });
      assert.deepEqual(resumed._meta, configured);

      const resumeFrames = mock.frames().filter((frame) => frame.method === "thread/resume");
      assert.equal(resumeFrames.length, 2);
      assert(resumeFrames.every((frame) => frame.params.model === "gpt-5.6-terra"));
      assert(resumeFrames.every((frame) => frame.params.approvalPolicy === "never"));
      assert(resumeFrames.every((frame) => frame.params.sandbox === "workspace-write"));

      const closed = await peer.close(session.sessionId);
      assert.equal(closed.exited.code, 0, peer.stderr);
    } finally {
      if (peer.child.exitCode === null && peer.child.signalCode === null) peer.child.kill("SIGTERM");
      mock.cleanup();
    }
  },
]);

tests.push([
  "session_new_and_resume_forward_mcp_server",
  async () => {
    const mock = createConfigMock();
    const peer = new AcpPeer("normal", "allow", mock.executable);
    const mcpServers = [{ name: "assembly", command: "python3", args: ["dummy.py"], env: [{ name: "TOKEN", value: "test-only" }] }];
    const expected = { mcp_servers: { assembly: { command: "python3", args: ["dummy.py"], env: { TOKEN: "test-only" } } } };
    try {
      await peer.initialize();
      const session = await peer.request("session/new", { cwd: directory, mcpServers });
      await peer.request("session/load", { sessionId: session.sessionId, cwd: directory, mcpServers });
      await peer.request("session/resume", { sessionId: session.sessionId, cwd: directory, mcpServers });
      const frames = mock.frames().filter((frame) => ["thread/start", "thread/resume"].includes(frame.method));
      assert.equal(frames.length, 3);
      for (const frame of frames) assert.deepEqual(frame.params.config, expected);
      await peer.close(session.sessionId);
    } finally {
      if (peer.child.exitCode === null && peer.child.signalCode === null) peer.child.kill("SIGTERM");
      mock.cleanup();
    }
  },
]);

tests.push([
  "session/load and session/resume replay completed turns before their results with stable ids",
  async () => {
    const mock = createReplayMock();
    const peer = new AcpPeer("replay", "allow", mock.executable);
    try {
      await peer.initialize();

      const loadStart = peer.messages.length;
      const loaded = await peer.request("session/load", {
        sessionId: "persisted-thread",
        cwd: directory,
      });
      assert.equal(loaded.sessionId, "persisted-thread");
      assert.equal(loaded.model, "gpt-5.6-sol");
      const loadUpdates = replayUpdatesBeforeResponse(peer.messages.slice(loadStart));

      const resumeStart = peer.messages.length;
      const resumed = await peer.request("session/resume", {
        sessionId: "persisted-thread",
        cwd: directory,
      });
      assert.equal(resumed.sessionId, "persisted-thread");
      const resumeUpdates = replayUpdatesBeforeResponse(peer.messages.slice(resumeStart));

      const expectedKinds = [
        "user_message_chunk",
        "agent_thought_chunk",
        "tool_call",
        "tool_call_update",
        "agent_message_chunk",
        "user_message_chunk",
        "tool_call",
        "tool_call_update",
        "agent_message_chunk",
      ];
      assert.deepEqual(
        loadUpdates.map((update) => update.sessionUpdate),
        expectedKinds,
      );
      assert.deepEqual(
        resumeUpdates.map((update) => update.sessionUpdate),
        expectedKinds,
      );
      assert(loadUpdates.every((update) => update._meta?.replay === true));
      assert(resumeUpdates.every((update) => update._meta?.replay === true));
      assert.equal(loadUpdates.some((update) => replayItemId(update)?.includes("in-flight")), false);

      const loadIds = loadUpdates.map(replayItemId);
      const resumeIds = resumeUpdates.map(replayItemId);
      assert.deepEqual(resumeIds, loadIds, "history item ids must remain stable across reloads");
      const clientItems = new Map();
      for (const update of loadUpdates) clientItems.set(replayItemId(update), update);
      const firstReplaySize = clientItems.size;
      for (const update of resumeUpdates) clientItems.set(replayItemId(update), update);
      assert.equal(clientItems.size, firstReplaySize, "a second replay must replace the same items");
      assert.equal(firstReplaySize, 7);

      const closed = await peer.close(resumed.sessionId);
      assert.equal(closed.exited.code, 0, peer.stderr);
    } finally {
      if (peer.child.exitCode === null && peer.child.signalCode === null) peer.child.kill("SIGTERM");
      mock.cleanup();
    }
  },
]);

tests.push([
  "approval allow, reject, cancel, error, and rich permission metadata",
  async () => {
    const cases = [
      {
        prompt: "V2_COMMAND",
        title: "echo hi",
        description: "mock approval\n/mock/work",
        kind: "command",
        idPrefix: "item-",
      },
      {
        prompt: "V2_FILE",
        title: "Apply file changes",
        description: "mock approval\n/mock/grant",
        kind: "file_change",
        idPrefix: "item-",
      },
      {
        prompt: "LEGACY_EXEC",
        title: "echo hi",
        description: "mock approval\n/mock/work",
        kind: "command",
        idPrefix: "call-",
      },
      {
        prompt: "LEGACY_PATCH",
        title: "Apply file changes",
        description: "mock approval\n/mock/file.txt",
        kind: "file_change",
        idPrefix: "call-",
      },
    ];
    for (const permissionChoice of ["allow", "reject", "cancel", "error"]) {
      for (const approvalCase of cases) {
        const suffix =
          permissionChoice === "allow"
            ? ""
            : permissionChoice === "reject"
              ? " REJECT"
              : " CANCEL";
        await withSession(
          "approval",
          async (peer, session) => {
            const result = await peer.request("session/prompt", {
              sessionId: session.sessionId,
              prompt: [{ type: "text", text: `${approvalCase.prompt}${suffix}` }],
            });
            assert.equal(result.stopReason, "end_turn");
            assert.equal(result.turnId, "mock-turn-1");
            assert.equal(peer.permissions.length, 1);
            const permission = peer.permissions[0].params;
            assert.equal(permission.title, approvalCase.title);
            assert.equal(permission.description, approvalCase.description);
            assert.equal(permission.toolCall.title, approvalCase.title);
            assert.equal(permission.toolCall.kind, approvalCase.kind);
            assert.match(permission.toolCall.toolCallId, new RegExp(`^${approvalCase.idPrefix}`));
            assert.deepEqual(permission.options, [
              { optionId: "allow", name: "Allow", kind: "allow_once" },
              { optionId: "reject", name: "Reject", kind: "reject_once" },
            ]);
            assert.equal(
              updatesOf(peer, "agent_message_chunk").map((update) => update.content.text).join(""),
              "approved",
              "the app-server turn must continue after its approval response",
            );
          },
          permissionChoice,
        );
      }
    }
  },
]);

tests.push([
  "session/cancel drains current and legacy approvals with cancel outcomes",
  async () => {
    for (const promptText of ["V2_COMMAND CANCEL", "LEGACY_PATCH CANCEL"]) {
      await withSession(
        "approval",
        async (peer, session) => {
          const prompt = peer.request("session/prompt", {
            sessionId: session.sessionId,
            prompt: [{ type: "text", text: promptText }],
          });
          await peer.waitForPermission();
          peer.notify("session/cancel", { sessionId: session.sessionId });
          assert.equal((await prompt).stopReason, "cancelled");
        },
        "defer",
      );
    }
  },
]);

tests.push([
  "plan, reasoning, four tool kinds, output, progress, patch, and turn diff mappings",
  async () => {
    await withSession("rich", async (peer, session) => {
      const runRichPrompt = () =>
        peer.request("session/prompt", {
          sessionId: session.sessionId,
          prompt: [{ type: "text", text: "rich" }],
        });

      assert.equal((await runRichPrompt()).stopReason, "end_turn");
      const plans = updatesOf(peer, "plan");
      assert.deepEqual(
        plans.filter((update) => update.planId === "plan-delta").map((update) => update.entries[0]),
        [
          { content: "Inspect", status: "in_progress" },
          { content: "Inspect files", status: "in_progress" },
        ],
      );
      assert.deepEqual(
        plans.find((update) => update.planId.startsWith("plan-mock-turn-")).entries,
        [
          { content: "Build bridge", status: "in_progress" },
          { content: "Verify bridge", status: "pending" },
        ],
      );
      assert.deepEqual(
        plans
          .filter((update) => update.planId === "plan-lifecycle")
          .map((update) => update.entries[0].status),
        ["in_progress", "completed"],
      );

      assert.deepEqual(
        updatesOf(peer, "agent_thought_chunk").map((update) => update.content.text),
        ["Thinking", " carefully", "Fallback thought"],
      );

      // A compaction is reported outright, because nothing else in the stream
      // shows one: no context occupancy is sent at all.
      assert.deepEqual(
        updatesOf(peer, "context_compaction").map((update) => update.trigger),
        ["auto"],
      );

      const starts = updatesOf(peer, "tool_call");
      const toolIds = ["tool-command", "tool-file", "tool-mcp", "tool-dynamic"];
      for (const toolId of toolIds) {
        assert.equal(starts.filter((update) => update.toolCallId === toolId).length, 1);
      }
      assert.deepEqual(
        toolIds.map((toolId) => starts.find((update) => update.toolCallId === toolId).kind),
        ["command", "file_change", "mcp", "dynamic_tool"],
      );

      const toolUpdates = updatesOf(peer, "tool_call_update");
      for (const toolId of toolIds) {
        assert.equal(
          toolUpdates.some((update) => update.toolCallId === toolId && update.status === "completed"),
          true,
        );
      }
      assert.equal(
        toolUpdates.some(
          (update) =>
            update.toolCallId === "tool-command" &&
            update.content?.some((content) => content.text === "hello\n"),
        ),
        true,
      );
      assert.equal(
        toolUpdates.some(
          (update) =>
            update.toolCallId === "tool-command" &&
            update.content?.some((content) => content.text === "stdin: yes"),
        ),
        true,
      );
      assert.equal(
        toolUpdates.some(
          (update) =>
            update.toolCallId === "tool-file" &&
            update.locations?.some((location) => location.path === "src/app.js"),
        ),
        true,
      );
      assert.equal(
        toolUpdates.some(
          (update) =>
            update.toolCallId === "tool-mcp" &&
            update.content?.some((content) => content.text === "halfway"),
        ),
        true,
      );
      assert.equal(
        toolUpdates.some(
          (update) =>
            update.toolCallId === "tool-dynamic" &&
            update.content?.some((content) => content.text.includes("dynamic result")),
        ),
        true,
      );
      assert.equal(
        starts.some((update) => update.toolCallId === "diff-mock-turn-1"),
        true,
      );
      assert.equal(
        toolUpdates.some(
          (update) =>
            update.toolCallId === "diff-mock-turn-1" &&
            update.content?.some((content) => content.text.startsWith("diff --git")),
        ),
        true,
      );
      assert.equal(
        toolUpdates.some(
          (update) => update.toolCallId === "diff-mock-turn-1" && update.status === "completed",
        ),
        true,
      );

      assert.equal((await runRichPrompt()).stopReason, "end_turn");
      assert.equal(
        updatesOf(peer, "tool_call").filter((update) => update.toolCallId === "tool-command").length,
        2,
      );
    });
  },
]);

tests.push([
  "cancel racing turn completion resolves the pending prompt once as cancelled",
  async () => {
    await withSession("cancel", async (peer, session) => {
      const prompt = peer.request("session/prompt", {
        sessionId: session.sessionId,
        prompt: [{ type: "text", text: "wait" }],
      });
      await peer.waitForUpdate();
      peer.notify("session/cancel", { sessionId: session.sessionId });
      const result = await prompt;
      assert.equal(result.stopReason, "cancelled");
      assert.equal(result.turnId, "mock-turn-1");
    });
  },
]);

tests.push([
  "session/steer uses the active app-server turn",
  async () => {
    await withSession("steer", async (peer, session) => {
      const prompt = peer.request("session/prompt", {
        sessionId: session.sessionId,
        prompt: [{ type: "text", text: "wait" }],
      });
      await peer.waitForUpdate();
      assert.deepEqual(
        await peer.request("session/steer", {
          sessionId: session.sessionId,
          prompt: [{ type: "text", text: "continue" }],
        }),
        {},
      );
      assert.equal((await prompt).stopReason, "end_turn");
    });
  },
]);

tests.push([
  "an image block reaches the app-server as a file that is deleted when the turn settles",
  async () => {
    await withSession("image", async (peer, session) => {
      const pixel = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
      const result = await peer.request("session/prompt", {
        sessionId: session.sessionId,
        prompt: [
          { type: "text", text: "look" },
          { type: "image", mimeType: "image/png", data: pixel.toString("base64") },
        ],
      });
      assert.equal(result.stopReason, "end_turn");
      const seen = JSON.parse(
        updatesOf(peer, "agent_message_chunk").map((update) => update.content.text).join(""),
      );
      assert.equal(seen.length, 1);
      assert.equal(seen[0].byteLength, pixel.length, "the app-server could read the written image");
      assert.equal(path.extname(seen[0].path), ".png");
      assert.equal(existsSync(seen[0].path), false, "the image file is deleted when the turn settles");
    });
  },
]);

tests.push([
  "an image-only prompt is accepted and a prompt with no usable block is refused",
  async () => {
    await withSession("image", async (peer, session) => {
      const result = await peer.request("session/prompt", {
        sessionId: session.sessionId,
        prompt: [{ type: "image", mimeType: "image/png", data: Buffer.from("shot").toString("base64") }],
      });
      assert.equal(result.stopReason, "end_turn");
      assert.equal(JSON.parse(updatesOf(peer, "agent_message_chunk")[0].content.text).length, 1);
      await assert.rejects(
        peer.request("session/prompt", {
          sessionId: session.sessionId,
          prompt: [{ type: "resource_link", uri: "file:///nope" }],
        }),
        (error) => error.code === -32602,
      );
    });
  },
]);

tests.push([
  "child crash mid-turn fails the prompt and exits the bridge with code 1",
  async () => {
    const peer = new AcpPeer("crash");
    await peer.initialize();
    const session = await peer.newSession();
    const prompt = peer.request("session/prompt", {
      sessionId: session.sessionId,
      prompt: [{ type: "text", text: "crash" }],
    });
    await assert.rejects(prompt, /app-server (?:exited|stdout closed)|transport closed/i);
    const exited = await peer.exit;
    assert.equal(exited.code, 1, peer.stderr);
    await peer.reader;
  },
]);

tests.push([
  "app-server stdout EOF while child stays alive is fatal and the child is reaped",
  async () => {
    const peer = new AcpPeer("stdout-eof");
    await peer.initialize();
    const session = await peer.newSession();
    const prompt = peer.request("session/prompt", {
      sessionId: session.sessionId,
      prompt: [{ type: "text", text: "close stdout" }],
    });
    await assert.rejects(prompt, /stdout closed unexpectedly|transport closed/i);
    const exited = await peer.exit;
    assert.equal(exited.code, 1, peer.stderr);
    assert.equal(exited.signal, null);
    await peer.reader;
  },
]);

tests.push([
  "huge assistant output survives downstream backpressure without data loss",
  async () => {
    await withSession("huge", async (peer, session) => {
      const prompt = peer.request(
        "session/prompt",
        {
          sessionId: session.sessionId,
          prompt: [{ type: "text", text: "huge" }],
        },
        30_000,
      );
      await peer.waitForUpdate();
      peer.child.stdout.pause();
      await new Promise((resolve) => setTimeout(resolve, 100));
      assert.equal(peer.child.exitCode, null);
      peer.child.stdout.resume();
      assert.equal((await prompt).stopReason, "end_turn");
      const textLength = updatesOf(peer, "agent_message_chunk").reduce(
        (total, update) => total + update.content.text.length,
        0,
      );
      assert.equal(textLength, 5 + 512 * 16 * 1024);
    });
  },
]);

let failures = 0;
for (const [name, run] of tests) {
  try {
    await run();
    console.log(`PASS ${name}`);
  } catch (error) {
    failures += 1;
    console.error(`FAIL ${name}`);
    console.error(error.stack || error);
  }
}

if (failures > 0) {
  console.error(`STATUS FAIL (${failures}/${tests.length})`);
  process.exit(1);
}
console.log(`STATUS PASS (${tests.length}/${tests.length})`);
