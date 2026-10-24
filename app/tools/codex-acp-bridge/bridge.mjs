#!/usr/bin/env node

import { spawn } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import process from "node:process";
import { createInterface } from "node:readline";

const BRIDGE_VERSION = "0.2.0";
const CODEX_BIN = process.env.CODEX_BIN?.trim() || "codex";
const METHOD_NOT_FOUND = -32601;
const INVALID_PARAMS = -32602;
const INTERNAL_ERROR = -32603;
const CHILD_EXITED = -32098;
const FALLBACK_MODELS = Object.freeze(["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"]);
const AVAILABLE_EFFORTS = Object.freeze(["low", "medium", "high", "xhigh", "max"]);
const AVAILABLE_APPROVAL_POLICIES = Object.freeze(["untrusted", "on-request", "never"]);

class RpcFailure extends Error {
  constructor(code, message, data) {
    super(message);
    this.code = code;
    this.data = data;
  }
}

// A single promise chain preserves NDJSON message order and makes pipe
// backpressure propagate all the way to the app-server reader.
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
          if (this.stream.destroyed || !this.stream.writable) {
            reject(new Error("NDJSON output stream is closed"));
            return;
          }
          this.stream.write(line, (error) => {
            if (error) reject(error);
            else resolve();
          });
        }),
    );
    this.tail = current.catch(() => {});
    return current;
  }

  idle() {
    return this.tail;
  }
}

const child = spawn(CODEX_BIN, ["app-server"], {
  env: process.env,
  stdio: ["pipe", "pipe", "pipe"],
});
const acpWriter = new OrderedWriter(process.stdout);
const appWriter = new OrderedWriter(child.stdin);

let nextAppRequestId = 1;
let nextPermissionRequestId = 1;
let appInitializePromise;
let availableModelsPromise;
let closing = false;
let fatal = false;
let childClosed = false;
let childCloseResult;
let resolveChildClose;
const childClose = new Promise((resolve) => {
  resolveChildClose = resolve;
});

const appPending = new Map();
const sessions = new Map();
const activePrompts = new Map();
const permissionRequests = new Map();
const activeAcpTasks = new Set();

child.stderr.on("data", (chunk) => {
  if (!process.stderr.destroyed) process.stderr.write(chunk);
});

function track(task) {
  activeAcpTasks.add(task);
  task.finally(() => activeAcpTasks.delete(task)).catch(() => {});
}

function normalizeError(error, fallbackCode = INTERNAL_ERROR) {
  if (error instanceof RpcFailure) return error;
  return new RpcFailure(fallbackCode, error instanceof Error ? error.message : String(error));
}

function appRequest(method, params) {
  if (childClosed || fatal) {
    return Promise.reject(new RpcFailure(CHILD_EXITED, "codex app-server is not running"));
  }

  const id = nextAppRequestId++;
  let resolveRequest;
  let rejectRequest;
  const response = new Promise((resolve, reject) => {
    resolveRequest = resolve;
    rejectRequest = reject;
  });
  appPending.set(String(id), { method, resolve: resolveRequest, reject: rejectRequest });
  appWriter.write({ id, method, ...(params === undefined ? {} : { params }) }).catch((error) => {
    const pending = appPending.get(String(id));
    if (!pending) return;
    appPending.delete(String(id));
    pending.reject(normalizeError(error, CHILD_EXITED));
  });
  return response;
}

function appNotify(method, params) {
  if (childClosed || fatal) {
    return Promise.reject(new RpcFailure(CHILD_EXITED, "codex app-server is not running"));
  }
  return appWriter.write({ method, ...(params === undefined ? {} : { params }) });
}

function appRespond(id, result) {
  if (childClosed || fatal) return Promise.resolve();
  return appWriter.write({ id, result });
}

function appRespondError(id, code, message) {
  if (childClosed || fatal) return Promise.resolve();
  return appWriter.write({ id, error: { code, message } });
}

function acpRespond(id, result) {
  return acpWriter.write({ jsonrpc: "2.0", id, result });
}

function acpRespondError(id, error) {
  const normalized = normalizeError(error);
  return acpWriter.write({
    jsonrpc: "2.0",
    id: id ?? null,
    error: {
      code: normalized.code,
      message: normalized.message,
      ...(normalized.data === undefined ? {} : { data: normalized.data }),
    },
  });
}

function requireString(value, name) {
  if (typeof value !== "string" || value.length === 0) {
    throw new RpcFailure(INVALID_PARAMS, `${name} must be a non-empty string`);
  }
  return value;
}

function sessionIdFrom(params) {
  return requireString(params?.sessionId ?? params?.session_id, "sessionId");
}

function optionalConfigString(params, key) {
  if (!Object.hasOwn(params ?? {}, key) || params[key] === null || params[key] === undefined) {
    return undefined;
  }
  return requireString(params[key], key);
}

function distinctStrings(values) {
  return [...new Set(values.filter((value) => typeof value === "string" && value.length > 0))];
}

async function listAvailableModels() {
  if (!availableModelsPromise) {
    availableModelsPromise = (async () => {
      const models = [];
      let cursor;
      do {
        const result = await appRequest("model/list", {
          ...(cursor ? { cursor } : {}),
        });
        models.push(...(Array.isArray(result?.data) ? result.data.map((entry) => entry?.model) : []));
        cursor = typeof result?.nextCursor === "string" && result.nextCursor ? result.nextCursor : undefined;
      } while (cursor);
      return distinctStrings(models);
    })().catch((error) => {
      if (error instanceof RpcFailure && error.code === METHOD_NOT_FOUND) {
        return [...FALLBACK_MODELS];
      }
      availableModelsPromise = undefined;
      throw error;
    });
  }
  return availableModelsPromise;
}

function configState(session) {
  return {
    model: session.model ?? null,
    availableModels: [...session.availableModels],
    reasoningEffort: session.reasoningEffort ?? null,
    availableEfforts: [...AVAILABLE_EFFORTS],
    approvalPolicy: session.approvalPolicy ?? null,
    availableApprovalPolicies: [...AVAILABLE_APPROVAL_POLICIES],
  };
}

function sessionResponse(sessionId, session) {
  return {
    sessionId,
    ...(session.model ? { model: session.model } : {}),
    _meta: configState(session),
  };
}

async function sessionFromOpened(opened, cwd, existing) {
  const model = existing?.model ?? optionalConfigString(opened, "model") ?? null;
  const advertisedModels = existing?.availableModels ?? (await listAvailableModels());
  const availableModels = distinctStrings([
    ...advertisedModels,
    ...(model ? [model] : []),
  ]);
  return {
    cwd: opened?.cwd ?? cwd,
    model,
    availableModels,
    reasoningEffort:
      existing?.reasoningEffort ?? optionalConfigString(opened, "reasoningEffort") ?? null,
    approvalPolicy:
      existing?.approvalPolicy ?? optionalConfigString(opened, "approvalPolicy") ?? null,
  };
}

function validateConfigChoice(value, available, label) {
  if (value !== undefined && !available.includes(value)) {
    throw new RpcFailure(INVALID_PARAMS, `${label} is not available for this session`);
  }
}

// The app-server takes images as files on disk, while ACP carries them as
// base64. Each prompt writes its images into a directory this process owns and
// deletes them again when the turn settles.
const IMAGE_EXTENSIONS = Object.freeze({
  "image/png": ".png",
  "image/jpeg": ".jpg",
  "image/gif": ".gif",
  "image/webp": ".webp",
  "image/bmp": ".bmp",
  "image/tiff": ".tiff",
});
let imageRoot;
let nextImageName = 1;

function imageDirectory() {
  if (!imageRoot) imageRoot = mkdtempSync(path.join(tmpdir(), "codex-acp-bridge-images-"));
  return imageRoot;
}

function writePromptImage(block) {
  if (typeof block.data !== "string" || block.data.length === 0) {
    throw new RpcFailure(INVALID_PARAMS, "image block has no data");
  }
  const extension = IMAGE_EXTENSIONS[block.mimeType] ?? ".png";
  const file = path.join(imageDirectory(), `prompt-${nextImageName++}${extension}`);
  writeFileSync(file, Buffer.from(block.data, "base64"));
  return file;
}

function removePromptImages(imageFiles) {
  for (const file of imageFiles.splice(0)) rmSync(file, { force: true });
}

function promptInput(blocks, imageFiles) {
  if (!Array.isArray(blocks)) throw new RpcFailure(INVALID_PARAMS, "prompt must be an array");
  const input = [];
  for (const block of blocks) {
    if (block?.type === "text" && typeof block.text === "string") {
      input.push({ type: "text", text: block.text, text_elements: [] });
    } else if (block?.type === "image") {
      const file = writePromptImage(block);
      imageFiles.push(file);
      input.push({ type: "localImage", path: file });
    }
  }
  if (input.length === 0) throw new RpcFailure(INVALID_PARAMS, "prompt must contain text or an image");
  return input;
}

function findPrompt(threadId, turnId) {
  const prompt = activePrompts.get(threadId);
  if (!prompt || prompt.settled) return undefined;
  if (turnId && prompt.turnId && turnId !== prompt.turnId) return undefined;
  return prompt;
}

function promptForEvent(params) {
  const prompt = findPrompt(params?.threadId, params?.turnId);
  if (!prompt) return undefined;
  if (!prompt.turnId && typeof params?.turnId === "string") prompt.turnId = params.turnId;
  return prompt;
}

function clearPromptTurnState(prompt) {
  prompt.itemStates.clear();
  prompt.diffTool = undefined;
}

function settlePrompt(prompt, outcome) {
  if (prompt.settled) return false;
  prompt.settled = true;
  clearPromptTurnState(prompt);
  removePromptImages(prompt.imageFiles);
  if (activePrompts.get(prompt.threadId) === prompt) activePrompts.delete(prompt.threadId);
  if (outcome.error) prompt.reject(outcome.error);
  else prompt.resolve(outcome.result);
  return true;
}

function promptResult(prompt, stopReason) {
  return {
    ...(prompt.turnId ? { turnId: prompt.turnId } : {}),
    stopReason,
    ...(sessions.get(prompt.threadId)?.model ? { model: sessions.get(prompt.threadId).model } : {}),
  };
}

async function emitSessionUpdate(prompt, update) {
  const annotatedUpdate = prompt.replay
    ? { ...update, _meta: { ...(update._meta ?? {}), replay: true } }
    : update;
  await acpWriter.write({
    jsonrpc: "2.0",
    method: "session/update",
    params: { sessionId: prompt.threadId, update: annotatedUpdate },
  });
}

async function emitUserText(prompt, turnId, itemId, text) {
  if (typeof text !== "string" || text.length === 0) return;
  await emitSessionUpdate(prompt, {
    sessionUpdate: "user_message_chunk",
    turnId,
    messageId: itemId,
    content: { type: "text", text },
  });
}

async function emitText(prompt, turnId, itemId, text) {
  if (typeof text !== "string" || text.length === 0) return;
  await emitSessionUpdate(prompt, {
    sessionUpdate: "agent_message_chunk",
    turnId,
    messageId: itemId,
    content: { type: "text", text },
  });
}

async function emitThought(prompt, turnId, itemId, text) {
  if (typeof text !== "string" || text.length === 0) return;
  await emitSessionUpdate(prompt, {
    sessionUpdate: "agent_thought_chunk",
    turnId,
    messageId: itemId,
    content: { type: "text", text },
  });
}

function normalizePlanStatus(status) {
  if (status === "inProgress" || status === "in-progress") return "in_progress";
  return typeof status === "string" && status ? status : "pending";
}

async function emitPlan(prompt, turnId, planId, entries) {
  await emitSessionUpdate(prompt, {
    sessionUpdate: "plan",
    turnId,
    planId,
    entries: entries.map((entry) => ({
      content: entry.content ?? entry.step ?? "",
      status: normalizePlanStatus(entry.status),
    })),
  });
}

function truncate(text, limit = 240) {
  const value = String(text ?? "");
  return value.length <= limit ? value : `${value.slice(0, limit - 1)}…`;
}

function displayJson(value, limit = 8_000) {
  if (value === undefined || value === null) return "";
  if (typeof value === "string") return value;
  try {
    return truncate(JSON.stringify(value), limit);
  } catch {
    return truncate(String(value), limit);
  }
}

function itemKind(item) {
  switch (item?.type) {
    case "commandExecution":
      return "command";
    case "fileChange":
      return "file_change";
    case "mcpToolCall":
      return "mcp";
    case "dynamicToolCall":
      return "dynamic_tool";
    default:
      return "tool";
  }
}

function toolTitle(item) {
  switch (item?.type) {
    case "commandExecution":
      return truncate(item.command || "Run shell command", 160);
    case "fileChange":
      return "Apply file changes";
    case "mcpToolCall":
      return truncate(`${item.server || "MCP"}: ${item.tool || "tool"}`, 160);
    case "dynamicToolCall":
      return truncate(
        `${item.namespace ? `${item.namespace}: ` : ""}${item.tool || "Dynamic tool"}`,
        160,
      );
    default:
      return "Tool";
  }
}

function toolStatus(item, lifecycle) {
  const status = item?.status;
  if (status === "completed") return "completed";
  if (status === "failed" || status === "declined") return "failed";
  if (status === "inProgress" || status === "in_progress") return "in_progress";
  return lifecycle === "completed" ? "completed" : "in_progress";
}

function fileChangeContent(changes) {
  if (!Array.isArray(changes)) return { content: [], locations: [] };
  return {
    content: changes.map((change) => ({
      type: "text",
      text: `${change.kind || "change"}: ${change.path || "unknown path"}${
        change.diff ? `\n${change.diff}` : ""
      }`,
    })),
    locations: changes
      .filter((change) => typeof change?.path === "string")
      .map((change) => ({ path: change.path })),
  };
}

function toolContent(item, state, lifecycle) {
  switch (item?.type) {
    case "commandExecution": {
      const output = !state.outputSeen ? item.aggregatedOutput : "";
      const detail = [
        output,
        item.exitCode === null || item.exitCode === undefined ? "" : `exit code: ${item.exitCode}`,
      ]
        .filter(Boolean)
        .join("\n");
      return {
        content: detail ? [{ type: "text", text: detail }] : [],
        locations: item.cwd ? [{ path: item.cwd }] : [],
      };
    }
    case "fileChange":
      return fileChangeContent(item.changes);
    case "mcpToolCall": {
      const detail = item.error
        ? displayJson(item.error)
        : item.result
          ? displayJson(item.result)
          : lifecycle === "completed"
            ? "MCP tool completed"
            : "";
      return { content: detail ? [{ type: "text", text: detail }] : [], locations: [] };
    }
    case "dynamicToolCall": {
      const detail = displayJson(item.contentItems);
      return { content: detail ? [{ type: "text", text: detail }] : [], locations: [] };
    }
    default:
      return { content: [], locations: [] };
  }
}

function isCoreToolItem(item) {
  return ["commandExecution", "fileChange", "mcpToolCall", "dynamicToolCall"].includes(
    item?.type,
  );
}

function getItemState(prompt, itemId, item) {
  let state = prompt.itemStates.get(itemId);
  if (!state) {
    state = {
      started: false,
      textSeen: false,
      thoughtSeen: false,
      outputSeen: false,
      planText: "",
      item: {},
    };
    prompt.itemStates.set(itemId, state);
  }
  if (item) state.item = { ...state.item, ...item };
  return state;
}

async function emitToolStart(prompt, turnId, item, state) {
  if (state.started) return;
  state.started = true;
  state.item = { ...state.item, ...item };
  const detail = toolContent(state.item, state, "started");
  await emitSessionUpdate(prompt, {
    sessionUpdate: "tool_call",
    turnId,
    toolCallId: state.item.id,
    title: toolTitle(state.item),
    kind: itemKind(state.item),
    status: "in_progress",
    ...detail,
  });
}

async function emitToolUpdate(prompt, turnId, item, state, overrides = {}) {
  state.item = { ...state.item, ...item };
  await emitToolStart(prompt, turnId, state.item, state);
  const detail = toolContent(state.item, state, overrides.lifecycle || "updated");
  await emitSessionUpdate(prompt, {
    sessionUpdate: "tool_call_update",
    turnId,
    toolCallId: state.item.id,
    title: toolTitle(state.item),
    kind: itemKind(state.item),
    status: overrides.status || toolStatus(state.item, overrides.lifecycle),
    ...detail,
    ...(overrides.content === undefined ? {} : { content: overrides.content }),
    ...(overrides.locations === undefined ? {} : { locations: overrides.locations }),
  });
}

async function mapThreadItem(prompt, turnId, item, lifecycle, includeUserMessages = false) {
  const state = getItemState(prompt, item.id, item);
  const completed = lifecycle === "completed";
  if (item.type === "userMessage") {
    if (completed && includeUserMessages) {
      const text = Array.isArray(item.content)
        ? item.content
            .filter((content) => content?.type === "text" && typeof content.text === "string")
            .map((content) => content.text)
            .join("\n")
        : "";
      await emitUserText(prompt, turnId, item.id, text);
    }
    return;
  }
  if (item.type === "agentMessage") {
    if (completed && !state.textSeen) await emitText(prompt, turnId, item.id, item.text);
    return;
  }
  if (item.type !== "plan" && item.type !== "reasoning" && !isCoreToolItem(item)) return;

  if (item.type === "reasoning") {
    if (completed && !state.thoughtSeen) {
      const summary = Array.isArray(item.summary) ? item.summary.join("\n") : "";
      const content = Array.isArray(item.content) ? item.content.join("\n") : "";
      await emitThought(prompt, turnId, item.id, summary || content);
    }
    return;
  }
  if (item.type === "plan") {
    if (typeof item.text === "string" && item.text) state.planText = item.text;
    if (state.planText) {
      await emitPlan(prompt, turnId, item.id, [
        { content: state.planText, status: completed ? "completed" : "in_progress" },
      ]);
    }
    return;
  }

  if (completed) {
    await emitToolUpdate(prompt, turnId, item, state, {
      lifecycle: "completed",
      status: toolStatus(item, "completed"),
    });
  } else {
    await emitToolStart(prompt, turnId, item, state);
  }
}

async function mapItemLifecycle(method, params) {
  const { turnId, item } = params ?? {};
  const prompt = promptForEvent(params);
  if (!prompt || !item || typeof item.id !== "string" || typeof turnId !== "string") return;

  const completed = method === "item/completed";
  try {
    await mapThreadItem(prompt, turnId, item, completed ? "completed" : "started");
  } finally {
    if (completed) prompt.itemStates.delete(item.id);
  }
}

async function replayCompletedTurns(threadId, turns) {
  const replay = {
    threadId,
    replay: true,
    itemStates: new Map(),
  };
  for (const turn of Array.isArray(turns) ? turns : []) {
    if (turn?.status !== "completed" || typeof turn.id !== "string") continue;
    for (const item of Array.isArray(turn.items) ? turn.items : []) {
      if (!item || typeof item.id !== "string") continue;
      try {
        await mapThreadItem(replay, turn.id, item, "completed", true);
      } finally {
        replay.itemStates.delete(item.id);
      }
    }
  }
}

async function mapToolDelta(method, params) {
  const { turnId, itemId } = params ?? {};
  const prompt = promptForEvent(params);
  if (!prompt || typeof turnId !== "string" || typeof itemId !== "string") return;
  const type = method.startsWith("item/fileChange/")
    ? "fileChange"
    : method.startsWith("item/mcpToolCall/")
      ? "mcpToolCall"
      : "commandExecution";
  const state = getItemState(prompt, itemId, { id: itemId, type, status: "inProgress" });
  state.outputSeen = true;
  let text = params.delta ?? params.message ?? params.stdin ?? "";
  if (method.endsWith("terminalInteraction") && text) text = `stdin: ${text}`;
  await emitToolUpdate(prompt, turnId, state.item, state, {
    status: "in_progress",
    content: text ? [{ type: "text", text }] : [],
  });
}

async function mapFilePatch(params) {
  const { turnId, itemId, changes } = params ?? {};
  const prompt = promptForEvent(params);
  if (!prompt || typeof turnId !== "string" || typeof itemId !== "string") return;
  const item = { id: itemId, type: "fileChange", changes, status: "inProgress" };
  const state = getItemState(prompt, itemId, item);
  const detail = fileChangeContent(changes);
  await emitToolUpdate(prompt, turnId, item, state, { status: "in_progress", ...detail });
}

async function mapTurnDiff(params) {
  const { turnId, diff } = params ?? {};
  const prompt = promptForEvent(params);
  if (!prompt || typeof turnId !== "string" || typeof diff !== "string") return;
  const item = { id: `diff-${turnId}`, type: "fileChange", changes: [] };
  prompt.diffTool = { turnId, itemId: item.id };
  const state = getItemState(prompt, item.id, item);
  await emitToolUpdate(prompt, turnId, item, state, {
    status: "in_progress",
    content: diff ? [{ type: "text", text: diff }] : [],
  });
}

async function completeDiffTool(prompt, completionStatus) {
  const diffTool = prompt.diffTool;
  if (!diffTool) return;
  prompt.diffTool = undefined;
  const state = getItemState(prompt, diffTool.itemId, {
    id: diffTool.itemId,
    type: "fileChange",
    changes: [],
  });
  await emitToolUpdate(prompt, diffTool.turnId, state.item, state, {
    lifecycle: "completed",
    status: completionStatus === "failed" ? "failed" : "completed",
  });
  prompt.itemStates.delete(diffTool.itemId);
}

async function requestInterrupt(prompt) {
  if (prompt.settled || !prompt.cancelRequested || !prompt.turnId || prompt.interruptSent) return;
  prompt.interruptSent = true;
  try {
    await appRequest("turn/interrupt", { threadId: prompt.threadId, turnId: prompt.turnId });
    await completeDiffTool(prompt, "cancelled");
    settlePrompt(prompt, { result: promptResult(prompt, "cancelled") });
  } catch (error) {
    if (!prompt.settled) {
      await completeDiffTool(prompt, "failed");
      settlePrompt(prompt, { error: normalizeError(error) });
    }
  }
}

function approvalResult(method, choice) {
  switch (method) {
    case "item/commandExecution/requestApproval":
    case "item/fileChange/requestApproval":
      return {
        decision: choice === "allow" ? "accept" : choice === "reject" ? "decline" : "cancel",
      };
    case "execCommandApproval":
    case "applyPatchApproval":
      return {
        decision:
          choice === "allow"
            ? "approved"
            : choice === "reject"
              ? { denied: { rejection: "Rejected by user" } }
              : "abort",
      };
    default:
      throw new RpcFailure(METHOD_NOT_FOUND, `Unsupported app-server approval method: ${method}`);
  }
}

function approvalDetails(method, params) {
  if (method === "item/commandExecution/requestApproval") {
    return {
      sessionId: params.threadId,
      title: truncate(params.command || "Run shell command", 160),
      description: [params.reason, params.cwd].filter(Boolean).join("\n"),
      toolCallId: params.itemId,
      kind: "command",
    };
  }
  if (method === "item/fileChange/requestApproval") {
    return {
      sessionId: params.threadId,
      title: "Apply file changes",
      description: [params.reason, params.grantRoot].filter(Boolean).join("\n"),
      toolCallId: params.itemId,
      kind: "file_change",
    };
  }
  if (method === "execCommandApproval") {
    return {
      sessionId: params.conversationId,
      title: truncate(Array.isArray(params.command) ? params.command.join(" ") : "Run command", 160),
      description: [params.reason, params.cwd].filter(Boolean).join("\n"),
      toolCallId: params.callId,
      kind: "command",
    };
  }
  const files = params.fileChanges ? Object.keys(params.fileChanges) : [];
  return {
    sessionId: params.conversationId,
    title: "Apply file changes",
    description: [params.reason, files.join("\n")].filter(Boolean).join("\n"),
    toolCallId: params.callId,
    kind: "file_change",
  };
}

function permissionChoice(message) {
  if (message.error) return "cancel";
  const outcome = message.result?.outcome;
  if (outcome?.outcome !== "selected") return "cancel";
  return outcome.optionId === "allow" ? "allow" : "reject";
}

async function handleAppRequest(message) {
  const approvalMethods = new Set([
    "item/commandExecution/requestApproval",
    "item/fileChange/requestApproval",
    "execCommandApproval",
    "applyPatchApproval",
  ]);
  if (!approvalMethods.has(message.method)) {
    await appRespondError(message.id, METHOD_NOT_FOUND, `Method not found: ${message.method}`);
    return;
  }

  const details = approvalDetails(message.method, message.params ?? {});
  const sessionId =
    details.sessionId ?? activePrompts.keys().next().value ?? sessions.keys().next().value;
  const permissionId = `bridge-permission-${nextPermissionRequestId++}`;
  permissionRequests.set(permissionId, {
    appId: message.id,
    method: message.method,
    sessionId,
  });
  try {
    await acpWriter.write({
      jsonrpc: "2.0",
      id: permissionId,
      method: "session/request_permission",
      params: {
        ...(sessionId ? { sessionId } : {}),
        title: details.title,
        ...(details.description ? { description: details.description } : {}),
        toolCall: {
          toolCallId: details.toolCallId,
          title: details.title,
          kind: details.kind,
        },
        options: [
          { optionId: "allow", name: "Allow", kind: "allow_once" },
          { optionId: "reject", name: "Reject", kind: "reject_once" },
        ],
      },
    });
  } catch (error) {
    permissionRequests.delete(permissionId);
    throw error;
  }
}

async function handlePermissionResponse(message) {
  const pending = permissionRequests.get(String(message.id));
  if (!pending) return;
  permissionRequests.delete(String(message.id));
  await appRespond(pending.appId, approvalResult(pending.method, permissionChoice(message)));
}

async function handleAppNotification(message) {
  const params = message.params ?? {};
  if (message.method === "item/agentMessage/delta") {
    const prompt = promptForEvent(params);
    if (!prompt || typeof params.itemId !== "string") return;
    const state = getItemState(prompt, params.itemId);
    state.textSeen = true;
    await emitText(prompt, params.turnId, params.itemId, params.delta);
    return;
  }

  if (
    message.method === "item/reasoning/summaryTextDelta" ||
    message.method === "item/reasoning/textDelta"
  ) {
    const prompt = promptForEvent(params);
    if (!prompt || typeof params.itemId !== "string") return;
    const state = getItemState(prompt, params.itemId);
    state.thoughtSeen = true;
    await emitThought(prompt, params.turnId, params.itemId, params.delta);
    return;
  }

  if (message.method === "item/plan/delta") {
    const prompt = promptForEvent(params);
    if (!prompt || typeof params.itemId !== "string") return;
    const state = getItemState(prompt, params.itemId);
    state.planText += params.delta || "";
    await emitPlan(prompt, params.turnId, params.itemId, [
      { content: state.planText, status: "in_progress" },
    ]);
    return;
  }

  if (message.method === "turn/plan/updated") {
    const prompt = promptForEvent(params);
    if (!prompt) return;
    await emitPlan(prompt, params.turnId, `plan-${params.turnId}`, params.plan || []);
    return;
  }

  if (message.method === "item/started" || message.method === "item/completed") {
    await mapItemLifecycle(message.method, params);
    return;
  }

  if (
    message.method === "item/commandExecution/outputDelta" ||
    message.method === "item/commandExecution/terminalInteraction" ||
    message.method === "item/fileChange/outputDelta" ||
    message.method === "item/mcpToolCall/progress"
  ) {
    await mapToolDelta(message.method, params);
    return;
  }

  if (message.method === "item/fileChange/patchUpdated") {
    await mapFilePatch(params);
    return;
  }

  if (message.method === "turn/diff/updated") {
    await mapTurnDiff(params);
    return;
  }

  if (message.method === "turn/started") {
    const turnId = params.turn?.id;
    const prompt = findPrompt(params.threadId, turnId);
    if (!prompt || typeof turnId !== "string") return;
    prompt.turnId = turnId;
    if (prompt.cancelRequested) track(requestInterrupt(prompt));
    return;
  }

  if (message.method === "thread/tokenUsage/updated") {
    const prompt = promptForEvent(params);
    if (!prompt) return;
    await emitSessionUpdate(prompt, {
      sessionUpdate: "usage_update",
      usedTokens: params.tokenUsage?.total?.totalTokens,
      contextWindow: params.tokenUsage?.modelContextWindow,
      inputTokens: params.tokenUsage?.last?.inputTokens,
      outputTokens: params.tokenUsage?.last?.outputTokens,
    });
    return;
  }

  // The agent threw away the older part of the thread to make room. Codex
  // reports no context occupancy at all, so this notification is the only sign
  // a compaction happened; without it the transcript has a silent gap.
  if (message.method === "thread/compacted") {
    const prompt = promptForEvent(params);
    if (!prompt) return;
    await emitSessionUpdate(prompt, {
      sessionUpdate: "context_compaction",
      ...(typeof params.turnId === "string" ? { turnId: params.turnId } : {}),
      ...(typeof params.trigger === "string" ? { trigger: params.trigger } : {}),
      ...(Number.isFinite(params.preTokens) ? { preTokens: params.preTokens } : {}),
      ...(Number.isFinite(params.postTokens) ? { postTokens: params.postTokens } : {}),
    });
    return;
  }

  if (message.method === "model/rerouted") {
    const session = sessions.get(params.threadId);
    if (session && typeof params.toModel === "string") {
      session.model = params.toModel;
      session.availableModels = distinctStrings([...session.availableModels, params.toModel]);
    }
    return;
  }

  if (message.method === "thread/settings/updated") {
    const session = sessions.get(params.threadId);
    if (session) {
      const settings = params.threadSettings ?? {};
      if (typeof settings.model === "string") {
        session.model = settings.model;
        session.availableModels = distinctStrings([...session.availableModels, settings.model]);
      }
      if (typeof settings.reasoningEffort === "string") {
        session.reasoningEffort = settings.reasoningEffort;
      }
      if (typeof settings.approvalPolicy === "string") {
        session.approvalPolicy = settings.approvalPolicy;
      }
    }
    return;
  }

  if (message.method === "turn/completed") {
    const turn = params.turn ?? {};
    const prompt = findPrompt(params.threadId, turn.id);
    if (!prompt) return;
    if (!prompt.turnId && typeof turn.id === "string") prompt.turnId = turn.id;
    await completeDiffTool(prompt, turn.status);
    if (prompt.cancelRequested || turn.status === "interrupted") {
      settlePrompt(prompt, { result: promptResult(prompt, "cancelled") });
    } else if (turn.status === "failed") {
      settlePrompt(prompt, {
        error: new RpcFailure(-32001, turn.error?.message || "codex turn failed", turn.error),
      });
    } else {
      settlePrompt(prompt, { result: promptResult(prompt, "end_turn") });
    }
    return;
  }

  if (message.method === "error" && params.willRetry !== true) {
    const prompt = findPrompt(params.threadId, params.turnId);
    if (prompt) {
      await completeDiffTool(prompt, "failed");
      settlePrompt(prompt, {
        error: new RpcFailure(-32001, params.error?.message || "codex turn failed", params.error),
      });
    }
  }
  // Unknown app-server notifications are intentionally ignored.
}

async function handleAppMessage(message) {
  if (!message || typeof message !== "object") {
    throw new RpcFailure(INTERNAL_ERROR, "app-server emitted a non-object message");
  }
  if (message.method && Object.hasOwn(message, "id")) {
    await handleAppRequest(message);
    return;
  }
  if (message.method) {
    await handleAppNotification(message);
    return;
  }
  if (Object.hasOwn(message, "id")) {
    const pending = appPending.get(String(message.id));
    if (!pending) return;
    appPending.delete(String(message.id));
    if (message.error) {
      pending.reject(
        new RpcFailure(
          typeof message.error.code === "number" ? message.error.code : INTERNAL_ERROR,
          message.error.message || `${pending.method} failed`,
          message.error.data,
        ),
      );
    } else {
      pending.resolve(message.result);
    }
  }
}

async function initializeAppServer() {
  if (!appInitializePromise) {
    appInitializePromise = (async () => {
      const result = await appRequest("initialize", {
        clientInfo: {
          name: "mac_command_bar_acp_bridge",
          title: "Mac Command Bar ACP Bridge",
          version: BRIDGE_VERSION,
        },
        capabilities: { experimentalApi: true },
      });
      await appNotify("initialized");
      return result;
    })();
  }
  return appInitializePromise;
}

function appServerVersion(initializeResult) {
  const match = String(initializeResult?.userAgent ?? "").match(/\/([^\s]+)/);
  return match?.[1] || "unknown";
}

function mcpConfig(params) {
  if (!params?.mcpServers?.length) return {};
  return { config: { mcp_servers: Object.fromEntries(params.mcpServers.map((server) => [server.name, {
    command: server.command,
    args: server.args,
    env: Object.fromEntries((server.env ?? []).map(({ name, value }) => [name, value])),
  }])) } };
}

async function newSession(params) {
  const cwd = typeof params?.cwd === "string" && params.cwd ? params.cwd : process.cwd();
  const opened = await appRequest("thread/start", { cwd, sandbox: "workspace-write", ...mcpConfig(params) });
  const sessionId = requireString(opened?.thread?.id, "thread/start result.thread.id");
  const session = await sessionFromOpened(opened, cwd);
  sessions.set(sessionId, session);
  return sessionResponse(sessionId, session);
}

async function resumeSession(params) {
  const requestedSessionId = sessionIdFrom(params);
  const existing = sessions.get(requestedSessionId);
  const request = {
    threadId: requestedSessionId,
    ...mcpConfig(params),
    ...(typeof params?.cwd === "string" && params.cwd ? { cwd: params.cwd } : {}),
    ...(existing?.model ? { model: existing.model } : {}),
    ...(existing?.approvalPolicy ? { approvalPolicy: existing.approvalPolicy } : {}),
    sandbox: "workspace-write",
  };
  const opened = await appRequest("thread/resume", request);
  const sessionId = requireString(opened?.thread?.id, "thread/resume result.thread.id");
  const session = await sessionFromOpened(opened, params?.cwd ?? process.cwd(), existing);
  sessions.set(sessionId, session);
  if (sessionId !== requestedSessionId) sessions.delete(requestedSessionId);
  await replayCompletedTurns(sessionId, opened?.thread?.turns);
  return sessionResponse(sessionId, session);
}

async function startPrompt(params) {
  const threadId = sessionIdFrom(params);
  const session = sessions.get(threadId);
  if (!session) throw new RpcFailure(INVALID_PARAMS, `Unknown session: ${threadId}`);
  if (activePrompts.has(threadId)) {
    throw new RpcFailure(-32002, `Session ${threadId} already has an active turn`);
  }

  let resolvePrompt;
  let rejectPrompt;
  const done = new Promise((resolve, reject) => {
    resolvePrompt = resolve;
    rejectPrompt = reject;
  });
  const prompt = {
    threadId,
    turnId: undefined,
    cancelRequested: false,
    interruptSent: false,
    settled: false,
    itemStates: new Map(),
    diffTool: undefined,
    imageFiles: [],
    resolve: resolvePrompt,
    reject: rejectPrompt,
  };
  activePrompts.set(threadId, prompt);

  try {
    const started = await appRequest("turn/start", {
      threadId,
      input: promptInput(params?.prompt, prompt.imageFiles),
      ...(session.model ? { model: session.model } : {}),
      ...(session.reasoningEffort ? { effort: session.reasoningEffort } : {}),
      ...(session.approvalPolicy ? { approvalPolicy: session.approvalPolicy } : {}),
    });
    if (!prompt.settled && typeof started?.turn?.id === "string") {
      prompt.turnId = started.turn.id;
    }
    if (prompt.cancelRequested) track(requestInterrupt(prompt));
  } catch (error) {
    await completeDiffTool(prompt, "failed");
    settlePrompt(prompt, { error: normalizeError(error) });
  }
  return done;
}

async function setSessionConfig(params) {
  const threadId = sessionIdFrom(params);
  const session = sessions.get(threadId);
  if (!session) throw new RpcFailure(INVALID_PARAMS, `Unknown session: ${threadId}`);

  const model = optionalConfigString(params, "model");
  const reasoningEffort = optionalConfigString(params, "reasoningEffort");
  const approvalPolicy = optionalConfigString(params, "approvalPolicy");
  validateConfigChoice(model, session.availableModels, "model");
  validateConfigChoice(reasoningEffort, AVAILABLE_EFFORTS, "reasoningEffort");
  validateConfigChoice(approvalPolicy, AVAILABLE_APPROVAL_POLICIES, "approvalPolicy");
  if (model !== undefined) session.model = model;
  if (reasoningEffort !== undefined) session.reasoningEffort = reasoningEffort;
  if (approvalPolicy !== undefined) session.approvalPolicy = approvalPolicy;
  return configState(session);
}

async function cancelSession(params) {
  const threadId = sessionIdFrom(params);
  const prompt = activePrompts.get(threadId);
  if (prompt && !prompt.settled) {
    prompt.cancelRequested = true;
    track(requestInterrupt(prompt));
  }

  const cancellations = [];
  for (const [permissionId, permission] of permissionRequests) {
    if (permission.sessionId !== threadId) continue;
    permissionRequests.delete(permissionId);
    cancellations.push(appRespond(permission.appId, approvalResult(permission.method, "cancel")));
  }
  await Promise.allSettled(cancellations);
  return {};
}

async function steerSession(params) {
  const threadId = sessionIdFrom(params);
  const prompt = activePrompts.get(threadId);
  if (!prompt?.turnId || prompt.settled) return {};
  // Steering images belong to the turn already running, so they are deleted
  // with the rest of that turn's images when it settles.
  await appRequest("turn/steer", {
    threadId,
    expectedTurnId: prompt.turnId,
    input: promptInput(params?.prompt, prompt.imageFiles),
  });
  return {};
}

async function dispatchAcpMethod(method, params) {
  switch (method) {
    case "initialize": {
      if (params?.protocolVersion !== 1) {
        throw new RpcFailure(INVALID_PARAMS, "Only ACP protocolVersion 1 is supported");
      }
      const initialized = await initializeAppServer();
      return {
        protocolVersion: 1,
        agentInfo: { name: "codex-app-server-bridge", version: appServerVersion(initialized) },
        agentCapabilities: {
          loadSession: true,
          promptCapabilities: { image: true },
          sessionCapabilities: {
            list: false,
            resume: true,
            close: true,
            steering: true,
          },
          plans: true,
        },
      };
    }
    case "session/new":
      return newSession(params);
    case "session/load":
    case "session/resume":
      return resumeSession(params);
    case "session/prompt":
      return startPrompt(params);
    case "session/cancel":
      return cancelSession(params);
    case "session/set_config_option":
      return setSessionConfig(params);
    case "session/steer":
      return steerSession(params);
    default:
      throw new RpcFailure(METHOD_NOT_FOUND, `Method not found: ${method}`);
  }
}

async function stopChild() {
  if (childClosed) return childCloseResult;
  await appWriter.idle().catch(() => {});
  if (!child.stdin.destroyed) child.stdin.end();

  const graceful = await Promise.race([
    childClose.then(() => true),
    new Promise((resolve) => setTimeout(() => resolve(false), 1_000)),
  ]);
  if (!graceful && !childClosed) child.kill("SIGTERM");
  const terminated = await Promise.race([
    childClose.then(() => true),
    new Promise((resolve) => setTimeout(() => resolve(false), 1_000)),
  ]);
  if (!terminated && !childClosed) child.kill("SIGKILL");
  return childClose;
}

async function settleOpenPrompts(stopReason) {
  for (const prompt of [...activePrompts.values()]) {
    prompt.cancelRequested = stopReason === "cancelled";
    await completeDiffTool(prompt, stopReason === "cancelled" ? "cancelled" : "failed");
    if (stopReason === "cancelled") {
      settlePrompt(prompt, { result: promptResult(prompt, "cancelled") });
    }
  }
}

async function cleanShutdown(exitCode = 0) {
  if (closing) return;
  closing = true;
  await settleOpenPrompts("cancelled");
  process.stdin.destroy();
  await stopChild().catch(() => {});
  await new Promise((resolve) => setImmediate(resolve));
  await acpWriter.idle().catch(() => {});
  process.exit(exitCode);
}

async function fatalShutdown(reason) {
  if (fatal || closing) return;
  fatal = true;
  const failure = normalizeError(reason, CHILD_EXITED);
  for (const pending of appPending.values()) pending.reject(failure);
  appPending.clear();
  for (const prompt of [...activePrompts.values()]) {
    await completeDiffTool(prompt, "failed").catch(() => {});
    settlePrompt(prompt, { error: failure });
  }
  permissionRequests.clear();
  process.stdin.destroy();
  await stopChild().catch(() => {});
  await new Promise((resolve) => setImmediate(resolve));
  await acpWriter.idle().catch(() => {});
  process.exit(1);
}

async function handleAcpMessage(message) {
  if (!message || typeof message !== "object") {
    await acpRespondError(null, new RpcFailure(-32600, "Invalid JSON-RPC message"));
    return;
  }
  if (!message.method && Object.hasOwn(message, "id")) {
    await handlePermissionResponse(message);
    return;
  }
  if (typeof message.method !== "string") {
    await acpRespondError(message.id ?? null, new RpcFailure(-32600, "Invalid JSON-RPC message"));
    return;
  }

  if (message.method === "session/close") {
    if (!Object.hasOwn(message, "id")) {
      track(cleanShutdown(0));
      return;
    }
    closing = true;
    await settleOpenPrompts("cancelled");
    await acpRespond(message.id, {});
    process.stdin.destroy();
    await stopChild().catch(() => {});
    await new Promise((resolve) => setImmediate(resolve));
    await acpWriter.idle().catch(() => {});
    process.exit(0);
  }

  try {
    const result = await dispatchAcpMethod(message.method, message.params ?? {});
    if (Object.hasOwn(message, "id")) await acpRespond(message.id, result);
  } catch (error) {
    if (Object.hasOwn(message, "id")) await acpRespondError(message.id, error);
  }
}

async function readAppOutput() {
  const lines = createInterface({ input: child.stdout, crlfDelay: Infinity });
  for await (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line) continue;
    let message;
    try {
      message = JSON.parse(line);
    } catch (error) {
      throw new RpcFailure(INTERNAL_ERROR, `Invalid app-server NDJSON: ${error.message}`);
    }
    await handleAppMessage(message);
  }
  if (!closing && !fatal && !childClosed) {
    throw new RpcFailure(CHILD_EXITED, "codex app-server stdout closed unexpectedly");
  }
}

async function readAcpInput() {
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
  for await (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line) continue;
    let message;
    try {
      message = JSON.parse(line);
    } catch (error) {
      await acpRespondError(null, new RpcFailure(-32700, `Parse error: ${error.message}`));
      continue;
    }
    const task = handleAcpMessage(message);
    track(task);
  }
  if (!closing && !fatal) await cleanShutdown(0);
}

child.on("error", (error) => {
  track(fatalShutdown(new RpcFailure(CHILD_EXITED, `Failed to start codex app-server: ${error.message}`)));
});

child.on("close", (code, signal) => {
  childClosed = true;
  childCloseResult = { code, signal };
  resolveChildClose(childCloseResult);
  if (!closing && !fatal) {
    track(
      fatalShutdown(
        new RpcFailure(
          CHILD_EXITED,
          `codex app-server exited unexpectedly (${signal ? `signal ${signal}` : `code ${code}`})`,
        ),
      ),
    );
  }
});

process.on("SIGINT", () => track(cleanShutdown(0)));
process.on("SIGTERM", () => track(cleanShutdown(0)));
process.stdout.on("error", (error) => {
  if (error.code === "EPIPE") track(cleanShutdown(0));
  else track(fatalShutdown(error));
});

track(readAppOutput().catch((error) => fatalShutdown(error)));
track(readAcpInput().catch((error) => fatalShutdown(error)));
