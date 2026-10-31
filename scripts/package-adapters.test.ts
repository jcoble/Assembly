import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { validateVersions, smokeAdapter, archiveBundle } from "./package-adapters.ts";

const versions = { "claude-agent-acp": "0.99.1", "codex-acp": "2.5.0" };
function fixture(run: (root: string) => unknown) {
  const root = mkdtempSync(join(tmpdir(), "assembly-adapter-test-"));
  return Promise.resolve().then(() => run(root)).finally(() => rmSync(root, { recursive: true, force: true }));
}
test("only exact stable versions of both supported adapters are accepted", () => {
  assert.deepEqual(validateVersions(versions), versions);
  for (const value of ["latest", "^2.5.0", "2.5.0-preview.1", "2.5", "2.5.0\n"]) {
    assert.throws(() => validateVersions({ ...versions, "codex-acp": value }), /exact stable/);
  }
  assert.throws(() => validateVersions({ "codex-acp": "2.5.0" }), /exact stable/);
  assert.throws(() => validateVersions({ ...versions, other: "1.0.0" }), /supported adapters/);
});
test("ACP smoke requires an actual matching initialize response", () => fixture(async root => {
  const entry = join(root, "adapter.cjs");
  writeFileSync(entry, String.raw`process.stdin.once("data", data => {
    require("node:fs").accessSync(process.env.CODEX_HOME);
    require("node:fs").accessSync(process.env.CLAUDE_CONFIG_DIR);
    const request = JSON.parse(String(data));
    if (request.method !== "initialize" || request.params.protocolVersion !== 1) process.exit(3);
    console.log(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { protocolVersion: 1, agentInfo: { name: "fixture", version: "1" } } }));
  });`);
  const result = await smokeAdapter(entry, root);
  assert.equal(result.protocolVersion, 1);
  assert.equal(result.agentInfo.name, "fixture");
}));
test("stdin stays open while the adapter initializes asynchronously", () => fixture(async root => {
  const entry = join(root, "async.cjs");
  writeFileSync(entry, String.raw`process.stdin.on("end", () => process.exit(0));
process.stdin.once("data", data => require("node:fs").readFile(__filename, () => {
  console.log(JSON.stringify({ id: JSON.parse(String(data)).id, result: { protocolVersion: 1 } }));
}));`);
  assert.equal((await smokeAdapter(entry, root)).protocolVersion, 1);
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
test("archive carries the production tree, lockfile, exact versions and matching checksum", () => fixture(root => {
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
        protocolVersion: 1, agentInfo: { version },
      }])),
    }));
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
  if (state.releases[tag].assets.length !== 6) throw new Error("Cannot publish incomplete assets");
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
test("publisher refuses corrupted archives before writing a release", () => fixture(root => {
  const f = releaseFixture(root);
  writeFileSync(join(f.assets, "assembly-adapters-linux-x64.tar.gz"), "corrupt");
  const run = f.run();
  assert.notEqual(run.status, 0);
  assert.match(run.stderr, /Corrupted release archive/);
  assert.deepEqual(f.state().calls, []);
}));
test("publisher refuses mismatched adapter proof before writing a release", () => fixture(root => {
  const f = releaseFixture(root), file = join(f.assets, "assembly-adapters-linux-x64.json");
  const manifest = JSON.parse(readFileSync(file, "utf8"));
  manifest.smoke["codex-acp"].agentInfo.version = "0.0.0";
  writeFileSync(file, JSON.stringify(manifest));
  const run = f.run();
  assert.notEqual(run.status, 0);
  assert.match(run.stderr, /Mismatched initialization proof/);
  assert.deepEqual(f.state().calls, []);
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
test("a complete release publishes all six assets", () => fixture(root => {
  const f = releaseFixture(root), run = f.run();
  assert.equal(run.status, 0, run.stderr);
  const release = f.state().releases["assembly-providers-fixture-1"];
  assert.equal(release.draft, false);
  assert.equal(release.assets.length, 6);
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
