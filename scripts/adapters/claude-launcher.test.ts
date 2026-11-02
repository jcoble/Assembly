import assert from "node:assert/strict";
import { spawn, execFileSync } from "node:child_process";
import { once } from "node:events";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createRequire } from "node:module";
import { createInterface } from "node:readline";
import { after, before, test } from "node:test";
import { pathToFileURL } from "node:url";

let root: string, bundle: string, entry: string, launcher: any;
before(async () => {
  root = mkdtempSync(join(tmpdir(), "assembly-claude-launcher-test-"));
  bundle = join(root, "bundle"); mkdirSync(bundle);
  // One isolated production dependency setup for all cases. No credentials or owner data.
  const seed = process.env.CLAUDE_LAUNCHER_TEST_BUNDLE;
  if (seed) {
    for (const file of ["package.json", "package-lock.json"]) copyFileSync(join(seed, file), join(bundle, file));
  } else {
    writeFileSync(join(bundle, "package.json"), JSON.stringify({ private: true, dependencies: {
      "@agentclientprotocol/claude-agent-acp": "0.99.1",
    } }));
    execFileSync("npm", ["install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund"], { cwd: bundle });
  }
  if (process.env.CLAUDE_LAUNCHER_TEST_INSTALLER === "pnpm") {
    execFileSync("pnpm", ["import"], { cwd: bundle });
    execFileSync("pnpm", ["install", "--prod", "--frozen-lockfile", "--ignore-scripts"], { cwd: bundle });
    assert.throws(() => createRequire(join(bundle, "entry.cjs")).resolve("@agentclientprotocol/sdk/experimental/v2"), { code: "MODULE_NOT_FOUND" });
  } else {
    execFileSync("npm", ["ci", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund"], { cwd: bundle });
  }
  entry = join(bundle, "claude-launcher.ts");
  copyFileSync(new URL("./claude-launcher.ts", import.meta.url), entry);
  launcher = await import(pathToFileURL(entry).href);
});
after(() => { if (root) rmSync(root, { recursive: true, force: true }); });

async function running(t: any, args: string[] = [], extraEnv = {}, imports: string[] = []) {
  const home = mkdtempSync(join(root, "home-"));
  const child = spawn(process.execPath, [...imports, entry, ...args], { cwd: home, env: {
    PATH: process.env.PATH, HOME: home, CLAUDE_CONFIG_DIR: join(home, "claude"),
    CLAUDE_AGENT_LOGS: join(home, "logs"), ...extraEnv,
  }, stdio: ["pipe", "pipe", "pipe"] });
  let stderr = "", stdout = "";
  child.stderr.on("data", data => { stderr += data; });
  child.stdout.on("data", data => { stdout += data; });
  child.stdin.on("error", () => {});
  const closed = once(child, "close");
  const messages: any[] = [], pending = new Map<string, (m: any) => void>();
  const lines = createInterface({ input: child.stdout });
  lines.on("line", line => {
    const message = JSON.parse(line); messages.push(message);
    pending.get(String(message.id))?.(message);
  });
  // Deadlines fail a test; successful shutdown always waits for this exact child.
  const timer = setTimeout(() => child.kill("SIGKILL"), 15_000);
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) child.kill("SIGTERM");
    await closed; clearTimeout(timer); lines.close();
  });
  function request(id: string, method: string, params: unknown) {
    return new Promise<any>((resolve, reject) => {
      pending.set(id, resolve);
      closed.then(() => reject(new Error("Exited before " + id + ": " + stderr)));
      child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    });
  }
  const initialize = (protocolVersion = 2) => request("init", "initialize", {
    protocolVersion, capabilities: {}, info: { name: "assembly-launcher-test", version: "1" },
  });
  return { child, closed, request, initialize, home, messages, stderr: () => stderr, stdout: () => stdout };
}
const prompt = [{ type: "text", text: "hello" }];
test("initialize v2 answers protocolVersion 2 with steering supported", async t => {
  const p = await running(t, [], { CLAUDE_AGENT_LOGS: "" });
  const reply = await p.initialize();
  assert.equal(reply.result.protocolVersion, 2);
  assert.deepEqual(reply.result._meta.steering, { supported: true });
  assert.equal(reply.result.info.version, "0.99.1");
  p.child.stdin.end(); assert.deepEqual(await p.closed, [0, null]);
});
test("initialize v1 is refused", async t => {
  const p = await running(t);
  const reply = await p.initialize(1);
  assert.ok(reply.error); assert.equal(reply.result, undefined);
  assert.equal(reply.error.code, -32022);
});
test("steer validates prompt, sessionId and idleBehavior before reaching the agent", async t => {
  const p = await running(t); await p.initialize();
  const invalid = [null, {}, { sessionId: "", prompt }, { sessionId: 1, prompt },
    { sessionId: "missing", prompt: "hello" }, { sessionId: "missing", prompt: [] },
    { sessionId: "missing", prompt, _meta: { steering: { idleBehavior: "startNewTurn" } } }];
  for (const [i, params] of invalid.entries()) {
    const reply = await p.request("bad-" + i, "_session/steering", params);
    assert.equal(reply.error?.code, -32602, JSON.stringify(reply));
  }
});
test("steer on an unknown session is session-not-found, not method-not-found", async t => {
  const p = await running(t); await p.initialize();
  for (const _meta of [undefined, { steering: { idleBehavior: "promptRequired" } }]) {
    const reply = await p.request("missing-" + String(_meta), "_session/steering", { sessionId: "assembly-missing", prompt, _meta });
    assert.equal(reply.error?.code, -32002, JSON.stringify(reply));
    assert.equal(reply.error.data.sessionId, "assembly-missing");
  }
});

async function filter(inputChunks: Buffer[], outputChunks: Buffer[]) {
  const saved: Buffer[] = [];
  const pair = launcher.withSteeringMeta(new WritableStream({ write: bytes => { saved.push(Buffer.from(bytes)); } }),
    new ReadableStream({ start(c) { for (const bytes of inputChunks) c.enqueue(bytes); c.close(); } }));
  const observed: Buffer[] = [];
  const consume = (async () => { for await (const bytes of pair.input) observed.push(Buffer.from(bytes)); })();
  await consume; assert.deepEqual(Buffer.concat(observed), Buffer.concat(inputChunks));
  const writer = pair.output.getWriter();
  for (const chunk of outputChunks) await writer.write(chunk);
  await writer.close(); return Buffer.concat(saved);
}
test("withSteeringMeta leaves non-initialize bytes identical across chunks and EOF", async () => {
  const bytes = Buffer.from(' { "id":"other", "result":{"text":"é"} }\r\n\nnot-json\n{"method":"session/update"}\ntrailing');
  const result = await filter([Buffer.from('{"id":"init","method":"initialize"}\n')],
    Array.from(bytes, byte => Buffer.from([byte])));
  assert.deepEqual(result, bytes);
});
test("withSteeringMeta matches the initialize id, merges meta, and preserves errors", async () => {
  const other = ' { "id":0,"result":{} }\r\n';
  const result = await filter([Buffer.from('{"id":7,"meth'), Buffer.from('od":"initialize"}\n')],
    [Buffer.from(other + '{"id":7,"result":{"_meta":{"keep":42}}}\n {"id":7,"result":{"later":true}}\n')]);
  const lines = result.toString().split("\n");
  assert.equal(lines[0] + "\n", other);
  assert.deepEqual(JSON.parse(lines[1]).result._meta, { keep: 42, steering: { supported: true } });
  assert.equal(lines[2], ' {"id":7,"result":{"later":true}}');
  const error = Buffer.from('{"id":"init","error":{"code":-32602}}\n');
  assert.deepEqual(await filter([Buffer.from('{"id":"init","method":"initialize"}\n')], [error]), error);
});

// Test-only module boundaries observe the actual launcher process without editing vendor files.
function setupObserver() {
  const agentModule = pathToFileURL(join(bundle, "node_modules/@agentclientprotocol/claude-agent-acp/dist/v2/agent.js")).href;
  const wrapper = join(root, "observe-agent.mjs"), policy = join(root, "observe-policy.mjs"), preload = join(root, "observe.mjs");
  writeFileSync(policy, 'export async function applyManagedPolicyEnv() { process.env.A4_POLICY="applied"; }');
  writeFileSync(wrapper, `import { v2AgentApp as stock } from ${JSON.stringify(agentModule)};
import { appendFileSync } from "node:fs";
export function v2AgentApp(logger, onAgent) { return stock(logger, agent => {
  if (process.env.A4_POLICY !== "applied" || process.stdin.isPaused()) throw Error("setup missing");
  console.log("a4-log"); console.info("a4-info"); console.debug("a4-debug");
  logger.log("a4-file-log"); logger.error(new Error("a4-file-error"));
  const dispose = agent.dispose.bind(agent);
  agent.dispose = async () => { await dispose(); appendFileSync(process.env.A4_DISPOSE, "disposed\\n"); };
  onAgent(agent);
}); }`);
  writeFileSync(preload, `import { registerHooks } from "node:module";
registerHooks({ resolve(specifier, context, next) {
 if (specifier === "@agentclientprotocol/claude-agent-acp/dist/v2/agent.js") return { url: ${JSON.stringify(pathToFileURL(wrapper).href)}, shortCircuit:true };
 if (specifier === "@agentclientprotocol/claude-agent-acp/dist/managed-policy.js") return { url: ${JSON.stringify(pathToFileURL(policy).href)}, shortCircuit:true };
 return next(specifier, context);
} });`);
  return preload;
}
for (const ending of ["EOF", "SIGINT", "SIGTERM"]) test(ending + " disposes the agent; setup keeps diagnostics off stdout", async t => {
  const disposed = join(root, "disposed-" + ending);
  const p = await running(t, [], { A4_DISPOSE: disposed }, ["--import", setupObserver()]);
  const reply = await p.initialize(); assert.equal(reply.result.protocolVersion, 2);
  if (ending === "EOF") p.child.stdin.end(); else p.child.kill(ending as NodeJS.Signals);
  assert.deepEqual(await p.closed, [0, null]);
  assert.equal(readFileSync(disposed, "utf8"), "disposed\n");
  for (const name of ["a4-log", "a4-info", "a4-debug", "a4-file-error"]) assert.ok(p.stderr().includes(name), p.stderr());
  assert.equal(p.messages.length, 1);
  const log = readFileSync(join(p.home, "logs/agent.log"), "utf8");
  assert.match(log, /\d{4}-\d\d-\d\dT.* pid=\d+ Claude ACP started\n/);
  assert.match(log, /a4-file-log/); assert.match(log, /Error: a4-file-error/);
});
test("--cli forwards flags, stdio and the child exit code", async t => {
  const cli = join(root, "cli");
  writeFileSync(cli, '#!' + process.execPath + '\nprocess.stdin.once("data", input => { console.log(JSON.stringify({args: process.argv.slice(2), input: String(input)})); console.error("cli-stderr"); process.exit(23); });', { mode: 0o755 });
  // CLI output is deliberately not ACP, so use its own process harness.
  const child = spawn(process.execPath, [entry, "--cli", "--version", "--cli", "argument"], {
    env: { PATH: process.env.PATH, HOME: root, CLAUDE_CONFIG_DIR: root, CLAUDE_CODE_EXECUTABLE: cli },
  });
  const closed = once(child, "close"); let stdout = "", stderr = "";
  child.stdout.on("data", d => { stdout += d; }); child.stderr.on("data", d => { stderr += d; });
  const timer = setTimeout(() => child.kill("SIGKILL"), 15_000);
  t.after(async () => { if (child.exitCode === null && child.signalCode === null) child.kill("SIGTERM"); await closed; clearTimeout(timer); });
  child.stdin.write("cli-input");
  assert.deepEqual(await closed, [23, null]);
  assert.deepEqual(JSON.parse(stdout), { args: ["--version", "argument"], input: "cli-input" }); assert.ok(stderr.includes("cli-stderr\n"), stderr);
});

for (const signal of ["SIGINT", "SIGTERM"] as const) test("--cli forwards " + signal + " and re-raises the child signal", async t => {
  const cli = join(root, "cli-" + signal);
  writeFileSync(cli, '#!' + process.execPath + '\nconsole.log(process.pid); process.stdin.resume();', { mode: 0o755 });
  const child = spawn(process.execPath, [entry, "--cli"], {
    env: { PATH: process.env.PATH, HOME: root, CLAUDE_CONFIG_DIR: root, CLAUDE_CODE_EXECUTABLE: cli },
  });
  const closed = once(child, "close"), lines = createInterface({ input: child.stdout });
  const timer = setTimeout(() => child.kill("SIGKILL"), 15_000);
  t.after(async () => { if (child.exitCode === null && child.signalCode === null) child.kill("SIGTERM"); await closed; clearTimeout(timer); lines.close(); });
  const ready = await Promise.race([once(lines, "line"), closed.then(() => { throw Error("CLI exited before ready"); })]);
  const cliPid = Number(ready[0]); assert.ok(cliPid > 0);
  child.kill(signal); assert.deepEqual(await closed, [null, signal]);
  assert.throws(() => process.kill(cliPid, 0), { code: "ESRCH" });
});
