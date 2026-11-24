import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync, spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { validateVersions, smokeAdapter, archiveBundle, packagingDigest, releaseBehind } from "./package-adapters.ts";

const versions = { "claude-agent-acp": "0.99.1", "codex-acp": "2.5.0" };
test("local packaging can replace an earlier archive while release packaging refuses it", () => fixture(root => {
  const bundle = join(root, "bundle"), output = join(root, "output");
  mkdirSync(bundle); mkdirSync(output);
  for (const file of ["package.json", "package-lock.json"]) writeFileSync(join(bundle, file), "{}");
  for (const [name, version] of Object.entries(versions)) {
    const dir = join(bundle, "node_modules/@agentclientprotocol", name);
    mkdirSync(join(dir, "dist"), { recursive: true });
    writeFileSync(join(dir, "package.json"), JSON.stringify({ version }));
    writeFileSync(join(dir, "dist/index.js"), "first");
  }
  archiveBundle(bundle, output, "darwin-arm64", versions);
  assert.throws(() => archiveBundle(bundle, output, "darwin-arm64", versions), /Refusing to overwrite/);
  writeFileSync(join(bundle, "package.json"), '{"private":true}');
  const replaced = archiveBundle(bundle, output, "darwin-arm64", versions, true);
  assert.equal(execFileSync("tar", ["-xOf", join(output, replaced.archive.file), "package.json"], { encoding: "utf8" }), '{"private":true}');
}));
function fixture(run: (root: string) => unknown) {
  const root = mkdtempSync(join(tmpdir(), "assembly-adapter-test-"));
  return Promise.resolve().then(() => run(root)).finally(() => rmSync(root, { recursive: true, force: true }));
}
// GitHub's public releases list, the only read provider selection needs.
function fakeReleaseApi(root: string) {
  writeFileSync(join(root, "curl"), "#!" + process.execPath + "\n" + String.raw`
const fs = require("node:fs");
const url = process.argv.at(-1), download = url.match(/^https:\/\/github\.com\/jcoble\/Assembly\/releases\/download\/[^/]+\/([^/]+)$/);
if (download) { const from = process.env.RELEASE_DIR + "/" + download[1]; if (process.argv.includes("-o")) fs.copyFileSync(from, process.argv[process.argv.indexOf("-o") + 1]); else process.stdout.write(fs.readFileSync(from)); process.exit(0); }
if (!url.startsWith("https://api.github.com/repos/jcoble/Assembly/releases?")) throw Error("unexpected curl request");
console.log(JSON.stringify(JSON.parse(fs.readFileSync(process.env.RELEASES, "utf8")).map(release => ({ tag_name: release.tagName, draft: release.isDraft,
  prerelease: release.isPrerelease, published_at: release.publishedAt, assets: release.assets.map(name => ({ name })) }))));
`, { mode: 0o755 });
}

for (const [name, change, expected] of [
  ["matching versions and packaging inputs skip the build", {}, false],
  ["a newer upstream adapter builds", { versions: { ...versions, "codex-acp": "2.6.0" } }, true],
  ["changed packaging inputs build", { packaging: "older" }, true],
  ["a release from before packaging was recorded builds", { packaging: undefined }, true],
  ["no published release builds", { noRelease: true }, true],
] as const) test("scheduled comparison: " + name, () => fixture(async root => {
  fakeReleaseApi(root);
  const packaging = await packagingDigest();
  for (const target of ["darwin-arm64", "darwin-x64", "linux-x64"])
    writeFileSync(join(root, "assembly-adapters-" + target + ".json"), JSON.stringify({ versions: (change as any).versions ?? versions, packaging: "packaging" in change ? change.packaging : packaging }));
  writeFileSync(join(root, "releases"), JSON.stringify("noRelease" in change ? [] : [{ tagName: "assembly-providers-1-1", isDraft: false, isPrerelease: false, publishedAt: "2026-11-01T00:00:00Z",
    assets: ["darwin-arm64", "darwin-x64", "linux-x64"].flatMap(target => ["assembly-adapters-" + target + ".tar.gz", "assembly-adapters-" + target + ".json"]) }]));
  const before = { PATH: process.env.PATH, RELEASES: process.env.RELEASES, RELEASE_DIR: process.env.RELEASE_DIR };
  Object.assign(process.env, { PATH: root + ":" + process.env.PATH, RELEASES: join(root, "releases"), RELEASE_DIR: root });
  try { assert.equal(releaseBehind(versions, packaging, () => {}), expected); }
  finally { process.env.PATH = before.PATH; for (const key of ["RELEASES", "RELEASE_DIR"] as const) { if (before[key] === undefined) delete process.env[key]; else process.env[key] = before[key]; } }
}));

test("scheduled comparison: changing the launcher or the packager changes the recorded inputs", () => fixture(async root => {
  mkdirSync(join(root, "scripts"), { recursive: true });
  writeFileSync(join(root, "scripts/package-adapters.ts"), "packager");
  const first = await packagingDigest(root);
  assert.equal(await packagingDigest(root), first);
  writeFileSync(join(root, "scripts/package-adapters.ts"), "changed");
  assert.notEqual(await packagingDigest(root), first);
}));

test("only exact stable versions of both supported adapters are accepted", () => {
  assert.deepEqual(validateVersions(versions), versions);
  for (const value of ["latest", "^2.5.0", "2.5.0-preview.1", "2.5", "2.5.0\n"]) {
    assert.throws(() => validateVersions({ ...versions, "codex-acp": value }), /exact stable/);
  }
  assert.throws(() => validateVersions({ "codex-acp": "2.5.0" }), /exact stable/);
  assert.throws(() => validateVersions({ ...versions, other: "1.0.0" }), /supported adapters/);
});
test("provider selection ignores the designated app latest and chooses the newest complete provider release", () => fixture(root => {
  const releases = [
    { tagName: "assembly-app-2026.11", isLatest: true, isDraft: false, isPrerelease: false, publishedAt: "2026-11-05T00:00:00Z", assets: [] },
    { tagName: "assembly-providers-101-1", isLatest: false, isDraft: false, isPrerelease: false, publishedAt: "2026-11-01T00:00:00Z", assets: ["assembly-adapters-linux-x64.tar.gz", "assembly-adapters-linux-x64.json"] },
    { tagName: "assembly-providers-102-1", isLatest: false, isDraft: false, isPrerelease: false, publishedAt: "2026-11-02T00:00:00Z", assets: ["assembly-adapters-linux-x64.tar.gz", "assembly-adapters-linux-x64.json"] },
    { tagName: "assembly-providers-103-1", isLatest: false, isDraft: false, isPrerelease: false, publishedAt: "2026-11-04T00:00:00Z", assets: ["assembly-adapters-linux-x64.tar.gz"] },
    { tagName: "assembly-providers-104-1", isLatest: false, isDraft: true, isPrerelease: false, publishedAt: "2026-11-06T00:00:00Z", assets: ["assembly-adapters-linux-x64.tar.gz", "assembly-adapters-linux-x64.json"] },
    { tagName: "assembly-providers-105-1", isLatest: false, isDraft: false, isPrerelease: true, publishedAt: "2026-11-07T00:00:00Z", assets: ["assembly-adapters-linux-x64.tar.gz", "assembly-adapters-linux-x64.json"] },
  ];
  writeFileSync(join(root, "releases.json"), JSON.stringify(releases));
  writeFileSync(join(root, "gh"), "#!/bin/sh\necho 'gh: command not found' >&2\nexit 127\n", { mode: 0o755 });
  fakeReleaseApi(root);
  const run = spawnSync(process.execPath, [fileURLToPath(new URL("./package-adapters.ts", import.meta.url)), "provider-tag", "linux-x64"], {
    encoding: "utf8", env: { PATH: root + ":" + process.env.PATH, HOME: root, RELEASES: join(root, "releases.json") },
  });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout.trim(), "assembly-providers-102-1");
}));
test("provider-tag reports the request's first error alone, with no Node stack dump", () => fixture(root => {
  writeFileSync(join(root, "curl"), "#!/bin/sh\necho 'curl: (6) Could not resolve host: api.github.com' >&2\nexit 6\n", { mode: 0o755 });
  const run = spawnSync(process.execPath, [fileURLToPath(new URL("./package-adapters.ts", import.meta.url)), "provider-tag", "linux-x64"], {
    encoding: "utf8", env: { PATH: root + ":" + process.env.PATH, HOME: root },
  });
  assert.equal(run.status, 1);
  assert.equal(run.stderr.split("\n")[0], "curl: (6) Could not resolve host: api.github.com");
  assert.doesNotMatch(run.stderr, /\n\s+at |^}$/m);
}));
test("provider-tag sends a configured token on stdin, never on the command line", () => fixture(root => {
  writeFileSync(join(root, "curl"), "#!" + process.execPath + "\n" + String.raw`
const fs = require("node:fs");
fs.writeFileSync(process.env.SEEN, JSON.stringify({ args: process.argv.slice(2), stdin: fs.readFileSync(0, "utf8") }));
console.log(JSON.stringify([{ tag_name: "assembly-providers-1-1", draft: false, prerelease: false, published_at: "2026-11-01T00:00:00Z",
  assets: [{ name: "assembly-adapters-linux-x64.tar.gz" }, { name: "assembly-adapters-linux-x64.json" }] }]));
`, { mode: 0o755 });
  const seen = join(root, "seen.json");
  const run = spawnSync(process.execPath, [fileURLToPath(new URL("./package-adapters.ts", import.meta.url)), "provider-tag", "linux-x64"], {
    encoding: "utf8", env: { PATH: root + ":" + process.env.PATH, HOME: root, SEEN: seen, GH_TOKEN: "fixture-secret" },
  });
  assert.equal(run.status, 0, run.stderr);
  const call = JSON.parse(readFileSync(seen, "utf8"));
  assert.match(call.stdin, /Authorization: Bearer fixture-secret/);
  assert.ok(!call.args.join(" ").includes("fixture-secret"));
}));
test("smoke requires protocol 2 and steering", () => fixture(async root => {
  const entry = join(root, "adapter.cjs");
  writeFileSync(entry, String.raw`const lines = require("node:readline").createInterface({ input: process.stdin });
lines.on("line", line => {
    require("node:fs").accessSync(process.env.CODEX_HOME);
    require("node:fs").accessSync(process.env.CLAUDE_CONFIG_DIR);
    const request = JSON.parse(line);
    if (request.method === "initialize" && request.params.protocolVersion === 2) console.log(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { protocolVersion: 2, info: { name: "fixture", version: "1" }, _meta: { steering: { supported: true } } } }));
    else if (request.method === "_session/steering") console.log(JSON.stringify({ jsonrpc: "2.0", id: request.id, error: { code: -32002, data: { sessionId: request.params.sessionId } } }));
    else process.exit(3);
  });`);
  const result = await smokeAdapter(entry, root);
  assert.equal(result.protocolVersion, 2);
  assert.equal(result.info.name, "fixture");
  assert.equal(result.steeringSessionNotFound, true);
}));
test("smoke stops the bundle when an adapter cannot steer", () => fixture(async root => {
  const entry = join(root, "no-steering.cjs");
  writeFileSync(entry, String.raw`process.stdin.once("data", data => console.log(JSON.stringify({ id: JSON.parse(String(data)).id, result: { protocolVersion: 2, info: { version: "1" } } })));`);
  await assert.rejects(smokeAdapter(entry, root), /does not advertise ACP v2 steering/);
}));
test("smoke accepts prompt queueing without a steering route", () => fixture(async root => {
  const entry = join(root, "queueing.cjs"), seen = join(root, "methods");
  writeFileSync(entry, String.raw`const fs = require("node:fs");
require("node:readline").createInterface({ input: process.stdin }).on("line", line => {
  const request = JSON.parse(line);
  fs.appendFileSync(process.env.HOME + "/methods", request.method + "\n");
  if (request.method === "initialize") console.log(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { protocolVersion: 2, info: { version: "1" }, capabilities: { _meta: { claudeCode: { promptQueueing: true } } } } }));
});`);
  const result = await smokeAdapter(entry, root);
  assert.equal(result.capabilities._meta.claudeCode.promptQueueing, true);
  assert.equal(readFileSync(seen, "utf8"), "initialize\n");
}));
test("smoke accepts stock claude-agent-acp 0.110+ on ACP v2 without legacy promptQueueing flag", () => fixture(async root => {
  const entry = join(root, "claude-v2.cjs");
  writeFileSync(entry, String.raw`process.stdin.once("data", data => console.log(JSON.stringify({
    jsonrpc: "2.0",
    id: JSON.parse(String(data)).id,
    result: { protocolVersion: 2, info: { name: "@agentclientprotocol/claude-agent-acp", version: "0.110.1" }, capabilities: { session: { prompt: { image: {} } } } }
  })));`);
  const result = await smokeAdapter(entry, root);
  assert.equal(result.protocolVersion, 2);
  assert.equal(result.info.name, "@agentclientprotocol/claude-agent-acp");
}));
test("stdin stays open while the adapter initializes asynchronously", () => fixture(async root => {
  const entry = join(root, "async.cjs");
  writeFileSync(entry, String.raw`process.stdin.on("end", () => process.exit(0));
process.stdin.once("data", data => require("node:fs").readFile(__filename, () => {
  console.log(JSON.stringify({ id: JSON.parse(String(data)).id, result: { protocolVersion: 2, capabilities: { _meta: { claudeCode: { promptQueueing: true } } } } }));
}));`);
  assert.equal((await smokeAdapter(entry, root)).protocolVersion, 2);
}));
test("invalid ACP response fails instead of treating process startup as proof", () => fixture(async root => {
  const entry = join(root, "broken.cjs");
  writeFileSync(entry, 'process.stdin.once("data", () => console.log(JSON.stringify({id:1,result:{protocolVersion:0}})));');
  await assert.rejects(smokeAdapter(entry, root), /initialize/);
}));
test("an adapter that exits before initializing fails with its error", () => fixture(async root => {
  const entry = join(root, "broken.cjs");
  writeFileSync(entry, 'console.error("fixture cannot launch"); process.exit(7);');
  await assert.rejects(smokeAdapter(entry, root), /fixture cannot launch/);
}));
test("bundle manifest names the stock Claude entry", () => fixture(root => {
  const bundle = join(root, "bundle"), output = join(root, "out");
  mkdirSync(bundle); mkdirSync(output);
  writeFileSync(join(bundle, "package.json"), JSON.stringify({ private: true }));
  writeFileSync(join(bundle, "package-lock.json"), '{"lockfileVersion":3}');
  for (const [name, version] of Object.entries(versions)) {
    const dir = join(bundle, "node_modules", "@agentclientprotocol", name);
    mkdirSync(join(dir, "dist"), { recursive: true });
    writeFileSync(join(dir, "package.json"), JSON.stringify({ version }));
    writeFileSync(join(dir, "dist/index.js"), "// fixture entry");
  }
  const manifest = archiveBundle(bundle, output, "darwin-arm64", versions);
  const archive = join(output, manifest.archive.file);
  assert.equal(manifest.archive.sha256, createHash("sha256").update(readFileSync(archive)).digest("hex"));
  assert.deepEqual(manifest.versions, versions);
  const unpack = join(root, "unpacked");
  mkdirSync(unpack);
  execFileSync("tar", ["-xzf", archive, "-C", unpack]);
  assert.equal(readFileSync(join(unpack, "package-lock.json"), "utf8"), '{"lockfileVersion":3}');
  assert.ok(!existsSync(join(unpack, "claude-launcher.ts")));
  assert.equal(manifest.entrypoints["claude-agent-acp"], "node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js");
  assert.equal(manifest.entrypoints["codex-acp"], "node_modules/@agentclientprotocol/codex-acp/dist/index.js");
  for (const entry of Object.values(manifest.entrypoints)) assert.ok(readFileSync(join(unpack, entry)).length);
  assert.deepEqual(JSON.parse(readFileSync(join(output, "assembly-adapters-darwin-arm64.json"), "utf8")), manifest);
}));
test("missing launch entry fails before an archive can be published", () => fixture(root => {
  mkdirSync(join(root, "bundle")); mkdirSync(join(root, "out"));
  assert.throws(() => archiveBundle(join(root, "bundle"), join(root, "out"), "linux-x64", versions), /ENOENT/);
}));

function releaseFixture(root: string) {
  const assets = join(root, "assets"); mkdirSync(assets);
  for (const target of ["darwin-arm64", "darwin-x64", "linux-x64"]) {
    const file = "assembly-adapters-" + target + ".tar.gz";
    const bytes = Buffer.from("fixture production archive " + target);
    writeFileSync(join(assets, file), bytes);
    writeFileSync(join(assets, "assembly-adapters-" + target + ".json"), JSON.stringify({
      schemaVersion: 1, target, versions,
      archive: { file, sha256: createHash("sha256").update(bytes).digest("hex") },
      smoke: Object.fromEntries(Object.entries(versions).map(([name, version]) => [name, {
        protocolVersion: 2, info: { version }, canSteer: true,
      }])),
    }));
    writeFileSync(join(assets, "assembly-adapters-" + target + ".json.sig"), "fixture signature");
  }
  const statePath = join(root, "release-state.json");
  writeFileSync(statePath, JSON.stringify({ calls: [], releases: {} }));
  // No network: the actual publisher CLI resolves gh to this owned executable.
  writeFileSync(join(root, "gh"), "#!" + process.execPath + "\n" + String.raw`
const fs = require("node:fs");
const state = JSON.parse(fs.readFileSync(process.env.RELEASE_STATE, "utf8"));
const [kind, stage, tag, ...args] = process.argv.slice(2);
if (kind !== "release") throw new Error("Unexpected gh operation");
state.calls.push({ stage, tag });
const save = () => fs.writeFileSync(process.env.RELEASE_STATE, JSON.stringify(state));
if (stage === "create") {
  if (!args.includes("--draft") || state.releases[tag]) throw new Error("Must create a fresh draft");
  if (process.env.FAIL_STAGE !== stage) state.releases[tag] = { draft: true, assets: [] };
} else if (stage === "upload") {
  state.releases[tag].assets = process.env.FAIL_STAGE === stage ? args.slice(0, 1) : args;
} else if (stage === "edit") {
  if (state.releases[tag].assets.length !== 9) throw new Error("Cannot publish incomplete assets");
  if (!args.includes("--draft=false")) throw new Error("Expected publish");
  if (process.env.FAIL_STAGE !== stage) state.releases[tag].draft = false;
} else throw new Error("Unexpected release stage");
save();
if (process.env.FAIL_STAGE === stage) process.exit(17);
`, { mode: 0o755 });
  return {
    assets,
    state: () => JSON.parse(readFileSync(statePath, "utf8")),
    run: (fail = "", tag = "assembly-providers-fixture-1") => spawnSync(process.execPath, [
      fileURLToPath(new URL("./package-adapters.ts", import.meta.url)), "publish",
      assets, JSON.stringify(versions), tag, "a".repeat(40),
    ], {
      encoding: "utf8",
      env: { PATH: root + ":" + process.env.PATH, HOME: root, GH_REPO: "fixture/no-network", RELEASE_STATE: statePath, FAIL_STAGE: fail },
    }),
  };
}
test("publisher refuses incomplete artifacts before writing a release", () => fixture(root => {
  const f = releaseFixture(root);
  rmSync(join(f.assets, "assembly-adapters-linux-x64.tar.gz"));
  const run = f.run();
  assert.notEqual(run.status, 0);
  assert.match(run.stderr, /Incomplete or unexpected release assets/);
  assert.deepEqual(f.state().calls, []);
}));
test("publisher refuses an unsigned manifest before writing a release", () => fixture(root => {
  const f = releaseFixture(root);
  rmSync(join(f.assets, "assembly-adapters-linux-x64.json.sig"));
  const run = f.run();
  assert.notEqual(run.status, 0);
  assert.match(run.stderr, /Incomplete or unexpected release assets/);
  assert.deepEqual(f.state().calls, []);
}));
test("publisher refuses corrupted archives before writing a release", () => fixture(root => {
  const f = releaseFixture(root);
  writeFileSync(join(f.assets, "assembly-adapters-linux-x64.tar.gz"), "corrupt");
  const run = f.run();
  assert.notEqual(run.status, 0);
  assert.match(run.stderr, /Corrupted release archive/);
  assert.deepEqual(f.state().calls, []);
}));
test("publish checks protocol 2 and info.version", () => fixture(async root => {
  for (const bad of [
    (proof: any) => { proof.protocolVersion = 1; },
    (proof: any) => { proof.info.version = "0.0.0"; },
  ]) await fixture(inner => {
    const f = releaseFixture(inner), file = join(f.assets, "assembly-adapters-linux-x64.json");
    const manifest = JSON.parse(readFileSync(file, "utf8"));
    bad(manifest.smoke["codex-acp"]);
    writeFileSync(file, JSON.stringify(manifest));
    const run = f.run();
    assert.notEqual(run.status, 0);
    assert.match(run.stderr, /Mismatched initialization proof/);
    assert.deepEqual(f.state().calls, []);
  });
}));
for (const stage of ["create", "upload", "edit"]) {
  test("failed release " + stage + " never publishes incomplete assets", () => fixture(root => {
    const f = releaseFixture(root);
    assert.notEqual(f.run(stage).status, 0);
    assert.ok(f.state().calls.some(call => call.stage === stage), "must exercise the actual publisher stage");
    assert.ok(Object.values(f.state().releases).every((release: any) => release.draft));
    if (stage !== "edit") assert.ok(!f.state().calls.some(call => call.stage === "edit"));
  }));
}
test("a complete release publishes all nine assets, signatures included", () => fixture(root => {
  const f = releaseFixture(root), run = f.run();
  assert.equal(run.status, 0, run.stderr);
  const release = f.state().releases["assembly-providers-fixture-1"];
  assert.equal(release.draft, false);
  assert.equal(release.assets.length, 9);
}));
test("a retry publishes a fresh release and preserves the failed draft", () => fixture(root => {
  const f = releaseFixture(root);
  assert.notEqual(f.run("upload").status, 0);
  const retry = f.run("", "assembly-providers-fixture-2");
  assert.equal(retry.status, 0, retry.stderr);
  assert.equal(f.state().releases["assembly-providers-fixture-1"].draft, true);
  assert.equal(f.state().releases["assembly-providers-fixture-2"].draft, false);
}));

for (const field of ["target", "archive.file", "versions"]) {
  test("publisher rejects cross-wired manifest " + field, () => fixture(root => {
    const f = releaseFixture(root), path = join(f.assets, "assembly-adapters-linux-x64.json");
    const manifest = JSON.parse(readFileSync(path, "utf8"));
    if (field === "target") manifest.target = "darwin-x64";
    else if (field === "archive.file") manifest.archive.file = "assembly-adapters-darwin-x64.tar.gz";
    else manifest.versions["codex-acp"] = "1.0.0";
    writeFileSync(path, JSON.stringify(manifest));
    const run = f.run();
    assert.notEqual(run.status, 0);
    assert.match(run.stderr, /Mismatched/);
    assert.deepEqual(f.state().calls, []);
  }));
}
for (const fail of [false, true]) {
  test(fail ? "registry failure stops resolution and writes no workflow output" : "successful resolution writes both exact versions to the workflow output", () => fixture(root => {
    const output = join(root, "github-output");
    writeFileSync(join(root, "npm"), "#!" + process.execPath + "\n" + String.raw`
if (process.env.FAIL_REGISTRY) { console.error("fixture registry unavailable"); process.exit(17); }
console.log(JSON.stringify(process.argv.some(arg => arg.includes("claude-agent-acp")) ? "0.99.1" : "2.5.0"));
`, { mode: 0o755 });
    const run = spawnSync(process.execPath, [
      fileURLToPath(new URL("./package-adapters.ts", import.meta.url)), "resolve",
    ], {
      encoding: "utf8",
      env: { PATH: root + ":" + process.env.PATH, HOME: root, GITHUB_OUTPUT: output, FAIL_REGISTRY: fail ? "1" : "" },
    });
    if (fail) {
      assert.notEqual(run.status, 0);
      assert.match(run.stderr, /fixture registry unavailable/);
      assert.equal(existsSync(output), false);
    } else {
      assert.equal(run.status, 0, run.stderr);
      assert.deepEqual(JSON.parse(readFileSync(output, "utf8").trim().slice("versions=".length)), versions);
    }
  }));
}

// The desktop release uses releases/latest/download/latest.json, so an adapter release must never take GitHub's Latest.
test("publishing adapters never takes GitHub's Latest", () => fixture(root => {
  const assets = join(root, "assets"), log = join(root, "gh-calls");
  mkdirSync(assets);
  for (const target of ["darwin-arm64", "darwin-x64", "linux-x64"]) {
    const file = "assembly-adapters-" + target + ".tar.gz";
    writeFileSync(join(assets, file), target);
    writeFileSync(join(assets, "assembly-adapters-" + target + ".json.sig"), "signature");
    writeFileSync(join(assets, "assembly-adapters-" + target + ".json"), JSON.stringify({ schemaVersion: 1, target, versions,
      archive: { file, sha256: createHash("sha256").update(target).digest("hex") },
      smoke: Object.fromEntries(Object.entries(versions).map(([name, version]) => [name, { protocolVersion: 2, canSteer: true, info: { version } }])) }));
  }
  writeFileSync(join(root, "gh"), "#!/bin/sh\necho \"$*\" >> \"$GH_LOG\"\n", { mode: 0o755 });
  const run = spawnSync(process.execPath, [fileURLToPath(new URL("./package-adapters.ts", import.meta.url)), "publish", assets, JSON.stringify(versions), "assembly-providers-9-1", "abc"], {
    encoding: "utf8", env: { PATH: root + ":" + process.env.PATH, GH_LOG: log, HOME: root } });
  assert.equal(run.status, 0, run.stderr);
  const calls = readFileSync(log, "utf8").trim().split("\n");
  assert.match(calls[0], /^release create .*--draft/);
  assert.match(calls.at(-1)!, /^release edit assembly-providers-9-1 --draft=false --latest=false$/);
  assert.ok(!calls.some(call => /--latest(=true)?( |$)/.test(call)));
}));
