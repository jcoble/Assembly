import { spawn, execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";

const names = ["claude-agent-acp", "codex-acp"] as const;
type Versions = Record<(typeof names)[number], string>;
const targets = ["darwin-arm64", "darwin-x64", "linux-x64"];
const entrypoint = (name: string) => "node_modules/@agentclientprotocol/" + name + "/dist/index.js";

export function validateVersions(value: unknown): Versions {
  if (!value || typeof value !== "object") throw new Error("Expected versions of both supported adapters");
  const data = value as Record<string, unknown>;
  if (Object.keys(data).some(key => !names.includes(key as typeof names[number]))) throw new Error("Only supported adapters are allowed");
  for (const name of names) {
    if (typeof data[name] !== "string" || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(data[name])) {
      throw new Error(name + " must have an exact stable version");
    }
  }
  return data as Versions;
}

export function smokeAdapter(entry: string, home: string): Promise<Record<string, any>> {
  return new Promise((resolveSmoke, rejectSmoke) => {
    for (const dir of [home, join(home, "codex"), join(home, "claude")]) mkdirSync(dir, { recursive: true });
    const child = spawn(process.execPath, [entry], {
      cwd: home, detached: true, stdio: ["pipe", "pipe", "pipe"],
      env: {
        PATH: process.env.PATH, HOME: home, TMPDIR: home,
        CODEX_HOME: join(home, "codex"), CLAUDE_CONFIG_DIR: join(home, "claude"),
      },
    });
    let result: Record<string, any> | undefined, failure: Error | undefined, stderr = "";
    const lines = createInterface({ input: child.stdout });
    // Only this saved process group, created for the probe, is ever stopped.
    const stop = () => {
      if (child.pid) {
        try { process.kill(-child.pid, "SIGKILL"); } catch (error) {
          if ((error as NodeJS.ErrnoException).code !== "ESRCH") failure = error as Error;
        }
      }
    };
    const deadline = setTimeout(() => { failure = new Error("ACP initialize timed out"); stop(); }, 20_000);
    child.stderr.on("data", chunk => { stderr = (stderr + chunk).slice(-4000); });
    child.stdin.on("error", () => {}); // Early exit is reported by close with its stderr.
    child.on("error", error => { failure = error; });
    lines.on("line", line => {
      try {
        const message = JSON.parse(line);
        if (message.id !== 1) return;
        if (message.error || message.result?.protocolVersion !== 1) throw new Error("Invalid ACP initialize response: " + line);
        result = message.result;
      } catch (error) { failure = error as Error; }
      stop();
    });
    child.on("close", code => {
      clearTimeout(deadline); lines.close();
      if (failure) rejectSmoke(failure);
      else if (!result) rejectSmoke(new Error("ACP exited before initialize (" + code + "): " + stderr));
      else resolveSmoke(result);
    });
    child.stdin.write(JSON.stringify({
      jsonrpc: "2.0", id: 1, method: "initialize",
      params: { protocolVersion: 1, clientCapabilities: {}, clientInfo: { name: "assembly-package-check", version: "1" } },
    }) + "\n");
  });
}

export function archiveBundle(bundle: string, output: string, target: string, versions: Versions) {
  validateVersions(versions);
  if (!targets.includes(target)) throw new Error("Unsupported target: " + target);
  for (const name of names) {
    const actual = JSON.parse(readFileSync(join(bundle, "node_modules/@agentclientprotocol", name, "package.json"), "utf8")).version;
    if (actual !== versions[name]) throw new Error(name + " version mismatch: " + actual);
    readFileSync(join(bundle, entrypoint(name)));
  }
  const file = "assembly-adapters-" + target + ".tar.gz";
  const archive = join(output, file);
  if (existsSync(archive)) throw new Error("Refusing to overwrite " + archive);
  execFileSync("tar", ["-czf", archive, "-C", bundle, "package.json", "package-lock.json", "node_modules"]);
  const manifest = {
    schemaVersion: 1, target, nodeMajor: Number(process.versions.node.split(".")[0]), versions,
    entrypoints: Object.fromEntries(names.map(name => [name, entrypoint(name)])),
    archive: { file, sha256: createHash("sha256").update(readFileSync(archive)).digest("hex") },
  };
  writeFileSync(join(output, "assembly-adapters-" + target + ".json"), JSON.stringify(manifest, null, 2) + "\n");
  return manifest;
}

async function main() {
  const [command, target, versionsJson, outputPath] = process.argv.slice(2);
  if (command === "resolve") {
    const versions = Object.fromEntries(names.map(name => [name, JSON.parse(execFileSync(
      "npm", ["view", "@agentclientprotocol/" + name + "@latest", "version", "--json", "--registry=https://registry.npmjs.org"],
      { encoding: "utf8" },
    ))]));
    console.log(JSON.stringify(validateVersions(versions)));
    return;
  }
  if (command !== "build" || !outputPath) throw new Error("Usage: package-adapters.ts resolve | build <target> <versions-json> <output>");
  if (!targets.includes(target) || target !== process.platform + "-" + process.arch) throw new Error("Build requires a native runner for " + target);
  const versions = validateVersions(JSON.parse(versionsJson));
  const output = resolve(outputPath);
  mkdirSync(output, { recursive: true });
  const scratch = mkdtempSync(join(output, ".package-"));
  try {
    const bundle = join(scratch, "bundle"), unpacked = join(scratch, "unpacked"), home = join(scratch, "npm-home");
    mkdirSync(bundle); mkdirSync(unpacked); mkdirSync(home);
    writeFileSync(join(bundle, "package.json"), JSON.stringify({
      private: true,
      dependencies: Object.fromEntries(names.map(name => ["@agentclientprotocol/" + name, versions[name]])),
    }, null, 2) + "\n");
    execFileSync("npm", ["install", "--omit=dev", "--ignore-scripts", "--no-audit", "--no-fund", "--registry=https://registry.npmjs.org"], {
      cwd: bundle, stdio: ["ignore", "inherit", "inherit"],
      env: { PATH: process.env.PATH, HOME: home, npm_config_cache: join(scratch, "cache"), npm_config_engine_strict: "true" },
    });
    const manifest = archiveBundle(bundle, output, target, versions);
    execFileSync("tar", ["-xzf", join(output, manifest.archive.file), "-C", unpacked]);
    const smoke = {};
    for (const name of names) smoke[name] = await smokeAdapter(join(unpacked, entrypoint(name)), join(scratch, "probe-" + name));
    const verified = { ...manifest, smoke };
    writeFileSync(join(output, "assembly-adapters-" + target + ".json"), JSON.stringify(verified, null, 2) + "\n");
    console.log(JSON.stringify(verified, null, 2));
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => { console.error(error); process.exitCode = 1; });
}
