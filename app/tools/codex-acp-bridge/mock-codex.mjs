#!/usr/bin/env node

import { statSync } from "node:fs";
import process from "node:process";
import { createInterface } from "node:readline";

class OrderedWriter {
  constructor(stream) {
    this.stream = stream;
    this.tail = Promise.resolve();
  }

  write(message) {
    const line = `${JSON.stringify(message)}\n`;
    const current = this.tail.then(
      () =>
        new Promise((resolve, reject) => {
          this.stream.write(line, (error) => (error ? reject(error) : resolve()));
        }),
    );
    this.tail = current.catch(() => {});
    return current;
  }

  idle() {
    return this.tail;
  }
}

const scenario = process.env.BRIDGE_MOCK_SCENARIO || "normal";
const writer = new OrderedWriter(process.stdout);
let nextThread = 1;
let nextTurn = 1;
let initializeCount = 0;
let activeTurn;
let lastInput = [];
const approvalRequests = new Map();
const background = new Set();

function track(task) {
  background.add(task);
  task.finally(() => background.delete(task)).catch(() => {});
}

function response(id, result) {
  return writer.write({ id, result });
}

function error(id, code, message) {
  return writer.write({ id, error: { code, message } });
}

function notification(method, params) {
  return writer.write({ method, params });
}

function complete(threadId, turnId, status = "completed", turnError = null) {
  return notification("turn/completed", {
    threadId,
    turn: { id: turnId, status, error: turnError, items: [] },
  });
}

function promptText(params) {
  return (params?.input ?? [])
    .filter((item) => item?.type === "text")
    .map((item) => item.text)
    .join("\n");
}

function approvalMethodFor(text) {
  if (text.includes("V2_COMMAND")) return "item/commandExecution/requestApproval";
  if (text.includes("V2_FILE")) return "item/fileChange/requestApproval";
  if (text.includes("LEGACY_EXEC")) return "execCommandApproval";
  if (text.includes("LEGACY_PATCH")) return "applyPatchApproval";
  return undefined;
}

function expectedApproval(method, text) {
  const choice = text.includes("CANCEL") ? "cancel" : text.includes("REJECT") ? "reject" : "allow";
  if (method.startsWith("item/")) {
    return choice === "allow" ? "accept" : choice === "reject" ? "decline" : "cancel";
  }
  return choice === "allow"
    ? "approved"
    : choice === "reject"
      ? { denied: { rejection: "Rejected by user" } }
      : "abort";
}

async function richScenario(threadId, turnId) {
  await notification("item/plan/delta", {
    threadId,
    turnId,
    itemId: "plan-delta",
    delta: "Inspect",
  });
  await notification("item/plan/delta", {
    threadId,
    turnId,
    itemId: "plan-delta",
    delta: " files",
  });
  await notification("turn/plan/updated", {
    threadId,
    turnId,
    plan: [
      { step: "Build bridge", status: "inProgress" },
      { content: "Verify bridge", status: "pending" },
    ],
  });
  await notification("item/started", {
    threadId,
    turnId,
    item: { id: "plan-lifecycle", type: "plan", text: "Lifecycle plan", status: "inProgress" },
  });
  await notification("item/completed", {
    threadId,
    turnId,
    item: { id: "plan-lifecycle", type: "plan", text: "Lifecycle plan", status: "completed" },
  });

  await notification("item/reasoning/summaryTextDelta", {
    threadId,
    turnId,
    itemId: "reasoning-delta",
    delta: "Thinking",
  });
  await notification("item/reasoning/textDelta", {
    threadId,
    turnId,
    itemId: "reasoning-delta",
    delta: " carefully",
  });
  await notification("item/completed", {
    threadId,
    turnId,
    item: {
      id: "reasoning-delta",
      type: "reasoning",
      summary: ["duplicate fallback"],
      content: [],
      status: "completed",
    },
  });
  await notification("item/completed", {
    threadId,
    turnId,
    item: {
      id: "reasoning-fallback",
      type: "reasoning",
      summary: ["Fallback thought"],
      content: [],
      status: "completed",
    },
  });

  await notification("item/started", {
    threadId,
    turnId,
    item: {
      id: "tool-command",
      type: "commandExecution",
      command: "printf hello",
      cwd: "/mock/work",
      status: "inProgress",
    },
  });
  await notification("item/commandExecution/outputDelta", {
    threadId,
    turnId,
    itemId: "tool-command",
    delta: "hello\n",
  });
  await notification("item/commandExecution/terminalInteraction", {
    threadId,
    turnId,
    itemId: "tool-command",
    stdin: "yes",
  });
  await notification("item/completed", {
    threadId,
    turnId,
    item: {
      id: "tool-command",
      type: "commandExecution",
      command: "printf hello",
      cwd: "/mock/work",
      aggregatedOutput: "hello\n",
      exitCode: 0,
      status: "completed",
    },
  });

  await notification("item/started", {
    threadId,
    turnId,
    item: { id: "tool-file", type: "fileChange", changes: [], status: "inProgress" },
  });
  await notification("item/fileChange/outputDelta", {
    threadId,
    turnId,
    itemId: "tool-file",
    delta: "preparing patch",
  });
  const changes = [{ kind: "update", path: "src/app.js", diff: "+const ready = true;" }];
  await notification("item/fileChange/patchUpdated", {
    threadId,
    turnId,
    itemId: "tool-file",
    changes,
  });
  await notification("item/completed", {
    threadId,
    turnId,
    item: { id: "tool-file", type: "fileChange", changes, status: "completed" },
  });

  await notification("item/started", {
    threadId,
    turnId,
    item: {
      id: "tool-mcp",
      type: "mcpToolCall",
      server: "mock-server",
      tool: "lookup",
      status: "inProgress",
    },
  });
  await notification("item/mcpToolCall/progress", {
    threadId,
    turnId,
    itemId: "tool-mcp",
    message: "halfway",
  });
  await notification("item/completed", {
    threadId,
    turnId,
    item: {
      id: "tool-mcp",
      type: "mcpToolCall",
      server: "mock-server",
      tool: "lookup",
      result: { content: "found" },
      status: "completed",
    },
  });

  await notification("item/started", {
    threadId,
    turnId,
    item: {
      id: "tool-dynamic",
      type: "dynamicToolCall",
      namespace: "mock",
      tool: "dynamic",
      status: "inProgress",
    },
  });
  await notification("item/completed", {
    threadId,
    turnId,
    item: {
      id: "tool-dynamic",
      type: "dynamicToolCall",
      namespace: "mock",
      tool: "dynamic",
      contentItems: [{ type: "text", text: "dynamic result" }],
      status: "completed",
    },
  });

  await notification("turn/diff/updated", {
    threadId,
    turnId,
    diff: "diff --git a/src/app.js b/src/app.js",
  });
  await notification("thread/compacted", { threadId, turnId, trigger: "auto" });
  await notification("item/agentMessage/delta", {
    threadId,
    turnId,
    itemId: "item-rich",
    delta: "rich",
  });
  await complete(threadId, turnId);
}

async function startScenario(threadId, turnId, text) {
  if (scenario === "crash") {
    await writer.idle();
    process.exit(23);
  }
  if (scenario === "stdout-eof") {
    await writer.idle();
    process.on("SIGTERM", () => {});
    process.stdout.end();
    setInterval(() => {}, 1_000);
    return;
  }
  if (scenario === "cancel") {
    await notification("item/agentMessage/delta", {
      threadId,
      turnId,
      itemId: "item-cancel",
      delta: "working",
    });
    return;
  }
  if (scenario === "steer") {
    await notification("item/agentMessage/delta", {
      threadId,
      turnId,
      itemId: "item-steer",
      delta: "waiting",
    });
    return;
  }
  if (scenario === "huge") {
    await notification("item/agentMessage/delta", {
      threadId,
      turnId,
      itemId: "item-huge",
      delta: "start",
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    const delta = "x".repeat(16 * 1024);
    for (let index = 0; index < 512; index += 1) {
      await notification("item/agentMessage/delta", {
        threadId,
        turnId,
        itemId: "item-huge",
        delta,
      });
    }
    await complete(threadId, turnId);
    return;
  }
  if (scenario === "rich") {
    await richScenario(threadId, turnId);
    return;
  }
  if (scenario === "usage") {
    await writer.write({"method":"thread/tokenUsage/updated","params":{"threadId":"01a01d99-911a-75c3-90af-bfcc3217a2d9","turnId":"01a01d99-917d-7f43-b67a-f53bc4385351","tokenUsage":{"total":{"totalTokens":29427,"inputTokens":29422,"cachedInputTokens":6912,"cacheWriteInputTokens":0,"outputTokens":5,"reasoningOutputTokens":0},"last":{"totalTokens":29427,"inputTokens":29422,"cachedInputTokens":6912,"cacheWriteInputTokens":0,"outputTokens":5,"reasoningOutputTokens":0},"modelContextWindow":237500}},"emittedAtMs":1787203002168});
    await complete(threadId, turnId);
    return;
  }
  if (scenario === "image") {
    await notification("item/agentMessage/delta", {
      threadId,
      turnId,
      itemId: "item-image",
      delta: JSON.stringify(
        lastInput
          .filter((item) => item?.type === "localImage")
          .map((item) => ({
            path: item.path,
            byteLength: statSync(item.path, { throwIfNoEntry: false })?.size ?? -1,
          })),
      ),
    });
    await complete(threadId, turnId);
    return;
  }

  const approvalMethod = approvalMethodFor(text);
  if (approvalMethod) {
    const appRequestId = `mock-approval-${turnId}`;
    approvalRequests.set(appRequestId, {
      method: approvalMethod,
      expected: expectedApproval(approvalMethod, text),
      threadId,
      turnId,
    });
    const modern = approvalMethod.startsWith("item/");
    await writer.write({
      id: appRequestId,
      method: approvalMethod,
      params: modern
        ? {
            threadId,
            turnId,
            itemId: `item-${turnId}`,
            reason: "mock approval",
            cwd: "/mock/work",
            grantRoot: "/mock/grant",
            ...(approvalMethod.includes("commandExecution") ? { command: "echo hi" } : {}),
          }
        : {
            conversationId: threadId,
            callId: `call-${turnId}`,
            reason: "mock approval",
            ...(approvalMethod === "execCommandApproval"
              ? { approvalId: null, command: ["echo", "hi"], cwd: "/mock/work", parsedCmd: [] }
              : { fileChanges: { "/mock/file.txt": { type: "update" } }, grantRoot: null }),
          },
    });
    return;
  }

  await notification("mock/unknownNotification", { ignored: true });
  await notification("item/agentMessage/delta", {
    threadId,
    turnId,
    itemId: "item-normal",
    delta: "pong",
  });
  await complete(threadId, turnId);
}

async function handleMessage(message) {
  if (message.method === "initialized") return;
  if (!message.method && Object.hasOwn(message, "id")) {
    const pending = approvalRequests.get(String(message.id));
    if (!pending) return;
    approvalRequests.delete(String(message.id));
    if (JSON.stringify(message.result?.decision) !== JSON.stringify(pending.expected)) {
      await writer.idle();
      process.exit(41);
    }
    if (activeTurn?.turnId === pending.turnId) {
      await notification("item/agentMessage/delta", {
        threadId: pending.threadId,
        turnId: pending.turnId,
        itemId: `item-${pending.turnId}`,
        delta: "approved",
      });
      await complete(pending.threadId, pending.turnId);
      activeTurn = undefined;
    }
    return;
  }

  switch (message.method) {
    case "initialize":
      initializeCount += 1;
      if (initializeCount > 1) {
        await error(message.id, -32603, "initialize called more than once");
        break;
      }
      await response(message.id, {
        userAgent: "mock-codex-app-server/9.8.7",
        codexHome: "/mock/.codex",
        platformFamily: "unix",
        platformOs: "macos",
      });
      break;
    case "thread/start": {
      const threadId = scenario === "usage"
        ? "01a01d99-911a-75c3-90af-bfcc3217a2d9"
        : `mock-thread-${nextThread++}`;
      await response(message.id, {
        thread: { id: threadId },
        model: "gpt-5.6-sol",
        modelProvider: "mock",
        cwd: message.params?.cwd ?? process.cwd(),
      });
      break;
    }
    case "thread/resume":
      if (message.params?.excludeTurns !== true) {
        await error(message.id, -32602, "thread/resume must set excludeTurns=true");
        break;
      }
      await response(message.id, {
        thread: { id: message.params.threadId },
        model: "gpt-5.6-sol",
        modelProvider: "mock",
        cwd: message.params?.cwd ?? process.cwd(),
      });
      break;
    case "turn/start": {
      const turnId = scenario === "usage"
        ? "01a01d99-917d-7f43-b67a-f53bc4385351"
        : `mock-turn-${nextTurn++}`;
      activeTurn = { threadId: message.params.threadId, turnId };
      lastInput = message.params?.input ?? [];
      await response(message.id, {
        turn: { id: turnId, status: "inProgress", error: null, items: [] },
      });
      track(startScenario(message.params.threadId, turnId, promptText(message.params)));
      break;
    }
    case "turn/interrupt":
      if (activeTurn) {
        await complete(activeTurn.threadId, activeTurn.turnId, "interrupted");
        activeTurn = undefined;
      }
      await response(message.id, {});
      break;
    case "turn/steer":
      await response(message.id, { turnId: message.params.expectedTurnId });
      await notification("item/agentMessage/delta", {
        threadId: message.params.threadId,
        turnId: message.params.expectedTurnId,
        itemId: "item-steer",
        delta: "steered",
      });
      await complete(message.params.threadId, message.params.expectedTurnId);
      activeTurn = undefined;
      break;
    default:
      await error(message.id, -32601, `mock method not found: ${message.method}`);
  }
}

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
for await (const rawLine of lines) {
  const line = rawLine.trim();
  if (!line) continue;
  await handleMessage(JSON.parse(line));
}
await Promise.allSettled([...background]);
await writer.idle();
