import { spawn, execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { appendFileSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";

const names = ["claude-agent-acp", "codex-acp"] as const;
type Versions = Record<(typeof names)[number], string>;
const targets = ["darwin-arm64", "darwin-x64", "linux-x64"];
const providerRepo = "jcoble/Assembly";
const entrypoint = (name: string) => "node_modules/@agentclientprotocol/" + name + "/dist/index.js";
const claudeLauncher = "claude-launcher.ts";

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

// A public read, not gh: Workbox Test has neither gh nor a GitHub login. One list call carries every release's assets.
// The token only raises the rate limit (CI's shared addresses); it goes on stdin so no process listing shows it.
export function latestProviderTag(target: string): string | undefined {
  if (!targets.includes(target)) throw new Error("Unsupported target: " + target);
  const auth = process.env.GH_TOKEN ? 'header = "Authorization: Bearer ' + process.env.GH_TOKEN + '"\n' : "";
  const releases = JSON.parse(execFileSync("curl", ["-fsSL", "--connect-timeout", "10", "--max-time", "60", "--config", "-",
    "-H", "Accept: application/vnd.github+json", "https://api.github.com/repos/" + providerRepo + "/releases?per_page=100"],
    { encoding: "utf8", input: auth, stdio: ["pipe", "pipe", "inherit"] }));
  const candidates = releases.filter((release: any) =>
    typeof release.tag_name === "string" && /^assembly-providers-\d+-\d+$/.test(release.tag_name) &&
    release.draft !== true && release.prerelease !== true)
    .sort((a: any, b: any) => Date.parse(b.published_at) - Date.parse(a.published_at));
  const required = ["assembly-adapters-" + target + ".tar.gz", "assembly-adapters-" + target + ".json"];
  for (const release of candidates) {
    const assets = new Set((release.assets ?? []).map((asset: any) => asset.name));
    if (required.every(name => assets.has(name))) return release.tag_name;
  }
  return undefined;
}

export function validateProviderAsset(manifestPath: string, archivePath: string, target: string) {
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  const file = "assembly-adapters-" + target + ".tar.gz";
  if (manifest.schemaVersion !== 1 || manifest.target !== target || manifest.archive?.file !== file ||
    manifest.entrypoints?.["claude-agent-acp"] !== claudeLauncher ||
    manifest.entrypoints?.["codex-acp"] !== entrypoint("codex-acp")) throw new Error("Mismatched provider manifest: " + target);
  const versions = validateVersions(manifest.versions);
  const checksum = createHash("sha256").update(readFileSync(archivePath)).digest("hex");
  if (manifest.archive.sha256 !== checksum) throw new Error("Corrupted provider archive: " + target);
  if (names.some(name => manifest.smoke?.[name]?.protocolVersion !== 2 ||
    manifest.smoke?.[name]?.info?.version !== versions[name] || manifest.smoke?.[name]?.canSteer !== true)) {
    throw new Error("Mismatched provider smoke proof: " + target);
  }
  return manifest;
}

export function validateHistoricalProviderAsset(manifestPath: string, archivePath: string, target: string) {
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  const file = "assembly-adapters-" + target + ".tar.gz";
  const claudeEntrypoint = manifest.entrypoints?.["claude-agent-acp"];
  if (manifest.schemaVersion !== 1 || manifest.target !== target || manifest.archive?.file !== file ||
    typeof claudeEntrypoint !== "string" || claudeEntrypoint.startsWith("/") ||
    claudeEntrypoint.split(/[\\/]/).some(part => part === ".." || part === "")) {
    throw new Error("Mismatched historical provider manifest: " + target);
  }
  const checksum = createHash("sha256").update(readFileSync(archivePath)).digest("hex");
  if (manifest.archive.sha256 !== checksum) throw new Error("Corrupted historical provider archive: " + target);
  return manifest as { entrypoints: Record<string, string> };
}

export function compareClaudeEntrypoints(previous: string | undefined, current: string, previousTag?: string, log = console.log) {
  if (!previous) {
    log("No previous successful provider release; skipping Claude dist/index.js diff (first publication)");
    return;
  }
  const comparison = spawnSync("diff", ["-u", previous, current], { encoding: "utf8" });
  if (comparison.error) throw comparison.error;
  if (comparison.status === 0) {
    log("Claude upstream dist/index.js matches " + previousTag);
    return;
  }
  if (comparison.status !== 1) throw new Error("Claude upstream diff failed: " + comparison.stderr);
  log("Claude upstream dist/index.js changes since " + previousTag + ":");
  log(comparison.stdout);
}

export function smokeAdapter(entry: string, home: string, requireSteering = true): Promise<Record<string, any>> {
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
        if (message.id === 1) {
          if (message.error || message.result?.protocolVersion !== 2) throw new Error("Invalid ACP initialize response: " + line);
          if (requireSteering && message.result?._meta?.steering?.supported !== true) throw new Error("Adapter does not advertise ACP v2 steering: " + line);
          result = message.result;
          if (requireSteering) {
            child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: 2, method: "_session/steering", params: {
              sessionId: "assembly-package-smoke-missing-session", prompt: [{ type: "text", text: "smoke" }],
              _meta: { steering: { idleBehavior: "promptRequired" } },
            } }) + "\n");
          } else stop();
        } else if (message.id === 2) {
          if (message.error?.code !== -32002 || message.error.code === -32601) throw new Error("Invalid ACP v2 steering route response: " + line);
          result.steeringSessionNotFound = true;
          stop();
        } else return;
      } catch (error) { failure = error as Error; stop(); }
    });
    child.on("close", code => {
      clearTimeout(deadline); lines.close();
      if (failure) rejectSmoke(failure);
      else if (!result) rejectSmoke(new Error("ACP exited before initialize (" + code + "): " + stderr));
      else if (requireSteering && result.steeringSessionNotFound !== true) rejectSmoke(new Error("ACP v2 steering route did not return session-not-found: " + stderr));
      else resolveSmoke(result);
    });
    child.stdin.write(JSON.stringify({
      jsonrpc: "2.0", id: 1, method: "initialize",
      params: { protocolVersion: 2, capabilities: {}, info: { name: "assembly-package-check", version: "1" } },
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
  readFileSync(join(bundle, claudeLauncher));
  const file = "assembly-adapters-" + target + ".tar.gz";
  const archive = join(output, file);
  if (existsSync(archive)) throw new Error("Refusing to overwrite " + archive);
  execFileSync("tar", ["-czf", archive, "-C", bundle, "package.json", "package-lock.json", claudeLauncher, "node_modules"]);
  const manifest = {
    schemaVersion: 1, target, nodeMajor: Number(process.versions.node.split(".")[0]), versions,
    entrypoints: { "claude-agent-acp": claudeLauncher, "codex-acp": entrypoint("codex-acp") },
    archive: { file, sha256: createHash("sha256").update(readFileSync(archive)).digest("hex") },
  };
  writeFileSync(join(output, "assembly-adapters-" + target + ".json"), JSON.stringify(manifest, null, 2) + "\n");
  return manifest;
}


function publishBundles(assetPath: string, versions: Versions, tag: string, commit: string) {
  if (!tag || !commit) throw new Error("Publish requires a tag and commit");
  const directory = resolve(assetPath);
  const expected = targets.flatMap(target => [".tar.gz", ".json"].map(extension => "assembly-adapters-" + target + extension)).sort();
  if (JSON.stringify(readdirSync(directory).sort()) !== JSON.stringify(expected)) throw new Error("Incomplete or unexpected release assets");
  for (const target of targets) {
    const file = "assembly-adapters-" + target + ".tar.gz";
    const manifest = JSON.parse(readFileSync(join(directory, "assembly-adapters-" + target + ".json"), "utf8"));
    if (manifest.schemaVersion !== 1 || manifest.target !== target || manifest.archive?.file !== file) throw new Error("Mismatched target manifest: " + target);
    const recorded = validateVersions(manifest.versions);
    if (names.some(name => recorded[name] !== versions[name])) throw new Error("Mismatched release versions: " + target);
    const hash = createHash("sha256").update(readFileSync(join(directory, file))).digest("hex");
    if (manifest.archive.sha256 !== hash) throw new Error("Corrupted release archive: " + target);
    if (names.some(name => manifest.smoke?.[name]?.protocolVersion !== 2 || manifest.smoke?.[name]?.info?.version !== versions[name] || manifest.smoke?.[name]?.canSteer !== true)) {
      throw new Error("Mismatched initialization proof: " + target);
    }
  }
  const gh = (args: string[]) => execFileSync("gh", ["release", ...args], { stdio: "inherit" });
  gh(["create", tag, "--target", commit, "--draft", "--title", "Provider adapters " + tag,
    "--notes", "Exact upstream versions: " + JSON.stringify(versions) + ". Archives contain production lockfiles; manifests contain SHA-256 and ACP initialization responses.", "--latest=false"]);
  gh(["upload", tag, ...expected.map(file => join(directory, file))]);
  gh(["edit", tag, "--draft=false", "--latest=false"]);
}

export function compareWithLastProviderRelease(target: string, currentBundle: string, scratch: string, log = console.log) {
  const tag = latestProviderTag(target);
  const current = join(currentBundle, entrypoint("claude-agent-acp"));
  if (!tag) {
    compareClaudeEntrypoints(undefined, current, undefined, log);
    return;
  }
  const asset = "assembly-adapters-" + target + ".tar.gz";
  const manifestFile = "assembly-adapters-" + target + ".json";
  const assets = join(scratch, "previous-assets");
  const unpacked = join(scratch, "previous-bundle");
  mkdirSync(assets); mkdirSync(unpacked);
  execFileSync("gh", ["release", "download", tag, "--repo", providerRepo,
    "--pattern", asset, "--pattern", manifestFile, "--dir", assets]);
  const manifest = validateHistoricalProviderAsset(join(assets, manifestFile), join(assets, asset), target);
  const members = execFileSync("tar", ["-tzf", join(assets, asset)], { encoding: "utf8" }).split("\n").filter(Boolean);
  if (members.some(name => name.startsWith("/") || name.split("/").some(part => part === ".."))) {
    throw new Error("Unsafe path in historical provider archive: " + tag);
  }
  const recordedEntrypoint = manifest.entrypoints["claude-agent-acp"];
  const upstreamEntrypoint = entrypoint("claude-agent-acp");
  if (!members.includes(recordedEntrypoint)) {
    throw new Error("Historical provider archive is missing its recorded Claude entrypoint: " + tag);
  }
  if (!members.includes(upstreamEntrypoint)) {
    throw new Error("Historical provider archive is missing the raw Claude upstream entrypoint: " + tag);
  }
  execFileSync("tar", ["-xzf", join(assets, asset), "-C", unpacked]);
  compareClaudeEntrypoints(join(unpacked, upstreamEntrypoint), current, tag, log);
}

async function main() {
  const [command, ...args] = process.argv.slice(2);
  if (command === "resolve") {
    const versions = Object.fromEntries(names.map(name => [name, JSON.parse(execFileSync(
      "npm", ["view", "@agentclientprotocol/" + name + "@latest", "version", "--json", "--registry=https://registry.npmjs.org"],
      { encoding: "utf8" },
    ))]));
    const result = JSON.stringify(validateVersions(versions));
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, "versions=" + result + "\n");
    console.log(result);
    return;
  }
  if (command === "provider-tag") {
    // agents-file.sh reports stderr's first error, so a failed lookup prints no Node stack dump.
    try {
      const tag = latestProviderTag(args[0]);
      if (!tag) throw new Error("No eligible published provider release contains the required platform assets");
      console.log(tag);
    } catch (error) {
      console.error(error instanceof Error ? error.message : String(error));
      process.exitCode = 1;
    }
    return;
  }
  if (command === "publish") {
    const [assetPath, versionsJson, tag, commit] = args;
    publishBundles(assetPath, validateVersions(JSON.parse(versionsJson)), tag, commit);
    return;
  }
  const [target, versionsJson, outputPath] = args;
  if (command !== "build" || !outputPath) throw new Error("Usage: package-adapters.ts resolve | provider-tag <target> | build <target> <versions-json> <output>");
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
    copyFileSync(new URL("./adapters/claude-launcher.ts", import.meta.url), join(bundle, claudeLauncher));
    const manifest = archiveBundle(bundle, output, target, versions);
    execFileSync("tar", ["-xzf", join(output, manifest.archive.file), "-C", unpacked]);
    const smoke: Record<string, any> = {};
    for (const name of names) smoke[name] = await smokeAdapter(join(unpacked, manifest.entrypoints[name]), join(scratch, "probe-" + name));
    for (const name of names) smoke[name] = { ...smoke[name], canSteer: smoke[name].steeringSessionNotFound === true };
    const stock = await smokeAdapter(join(unpacked, entrypoint("claude-agent-acp")), join(scratch, "probe-stock-claude"), false).catch(() => undefined);
    if (stock?._meta?.steering?.supported === true) console.log("launcher can be dropped (PR 1499 shipped)");
    else console.log("plain dist/index.js does not yet advertise steering");
    compareWithLastProviderRelease(target, bundle, scratch);
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
