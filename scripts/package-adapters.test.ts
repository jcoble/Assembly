import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
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
