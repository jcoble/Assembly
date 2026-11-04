import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { hostname, tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';

// Full launcher lifecycle using owned fake build/install processes. No remote service, registry or provider conversation.
test('a second managed dev launch runs no build, install or seed process, and neither launch touches the remote', { timeout: 25000 }, () => {
  const top = mkdtempSync(join(realpathSync(tmpdir()), 'managed-launch-'));
  const home = join(top, 'user'), root = join(home, 'dev/work/worktrees/Assembly/example'), log = join(top, 'calls'), marker = join(top, 'remote-checked');
  const write = (file: string, text: string, mode = 0o600) => { mkdirSync(dirname(file), { recursive: true }); writeFileSync(file, text, { mode }); };
  try {
    for (const file of ['app-dev.ts', 'app-dev-managed.ts', 'seed-dev.ts', 'dev-seed-cohort.ts', 'dev-build-cache.ts', 'dev-toolchain.ts', 'dev-lane-clean.ts', 'dev-remote-identity.ts', 'dev-remote-process.ts']) {
      mkdirSync(join(root, 'scripts'), { recursive: true }); copyFileSync(new URL(file, import.meta.url), join(root, 'scripts', file));
    }
    const owner = { version: 1, name: 'example', number: 2, owner: randomUUID(), host: hostname(), worktree: root };
    write(join(home, 'assembly-dev/lanes/example.json'), JSON.stringify(owner));
    write(join(root, '.lane'), `LANE_NAME=example\nLANE_NUMBER=2\nLANE_OWNER=${owner.owner}\n`);
    write(join(root, '.gitignore'), '.lane\n/desktop/target\n/dotnet/*/bin\n/dotnet/*/obj\n');
    write(join(root, 'desktop/Cargo.toml'), 'native fixture');
    write(join(root, 'dotnet/Assembly.Desktop/Assembly.Desktop.csproj'), '<Project/>');
    write(join(root, 'dotnet/Assembly.Remote/Assembly.Remote.csproj'), '<Project/>');
    write(join(root, 'scripts/adapters/claude-launcher.ts'), 'launcher');
    write(join(root, 'scripts/lane-env.sh'), `DATA="$HOME/assembly-dotnet-example"\nDESKTOP_PORT=18817\nTOKEN_FILE=test\nBOX_PORT=9817\nBOX_TOKEN_FILE=test\nLANE_NAME=example\n`);
    write(join(root, 'dotnet/scripts/workbox.sh'), '#!/bin/sh\necho remote-check >> "$MANAGED_TEST_LOG"\necho "remote is right; no deploy"\ntouch "$MANAGED_TEST_MARKER"\n');
    const loader = join(top, 'registry-fixture.ts');
    write(loader, `globalThis.fetch=async()=>new Response(JSON.stringify({version:'1.2.3'}),{status:200});`);
    const backup = join(top, 'designated-test-backup.db'); write(backup, 'artificial backup bytes: importer process is faked in this launcher test');
    const manifest = join(top, 'private-manifest.json'); write(manifest, JSON.stringify({ version: 1, originId: 'launcher-test', kind: 'real-recorded', sources: [{ format: 'database', path: backup, sha256: createHash('sha256').update(readFileSync(backup)).digest('hex') }] }));
    write(join(root, 'bin/dotnet'), `#!${process.execPath}
const fs=require('node:fs'),path=require('node:path'),args=process.argv.slice(2);
const write=(file,text)=>{fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,text)};
fs.appendFileSync(process.env.MANAGED_TEST_LOG,'dotnet '+args[0]+'\\n');
if(args[0]==='publish'){const out=args[args.indexOf('--output')+1];write(path.join(out,'Assembly.Desktop.dll'),'backend');write(path.join(out,'Assembly.Infrastructure.dll'),'backend')}
else if(args[0]==='build'){write(path.join(args[args.indexOf('--output')+1],'Assembly.DevSeed.dll'),'helper')}
else {let text='';process.stdin.on('data',data=>text+=data);process.stdin.on('end',()=>{const request=JSON.parse(text);write(path.join(request.computedHome,'assembly.db'),'fake importer output; real SQL parity covered separately')})}
`, 0o755);
    write(join(root, 'bin/npm'), `#!${process.execPath}
const fs=require('node:fs'),path=require('node:path'),args=process.argv.slice(2),prefix=args[args.indexOf('--prefix')+1];
fs.appendFileSync(process.env.MANAGED_TEST_LOG,'npm install\\n');
for(const spec of args.filter(value=>value.startsWith('@'))){const split=spec.lastIndexOf('@'),name=spec.slice(0,split),version=spec.slice(split+1),file=path.join(prefix,'node_modules',name,'package.json');fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,JSON.stringify({version}))}
for(const id of ['claude','codex']){const file=path.join(prefix,'node_modules/.bin',id);fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,'cli')}
`, 0o755);
    write(join(root, 'scripts/package-adapters.ts'), `import fs from 'node:fs';import path from 'node:path';const [,target,,out]=process.argv.slice(2);fs.appendFileSync(process.env.MANAGED_TEST_LOG!,'adapter build\\n');fs.mkdirSync(out,{recursive:true});fs.writeFileSync(path.join(out,'assembly-adapters-'+target+'.json'),'{}');`);
    write(join(root, 'dotnet/scripts/agents-file.sh'), `#!/bin/sh\nexec "${process.execPath}" "${root}/scripts/installer-fixture.ts" "$@"\n`);
    write(join(root, 'scripts/installer-fixture.ts'), `import fs from 'node:fs';import path from 'node:path';
const [adapters,descriptor]=process.argv.slice(2),bundle=path.join(adapters,'bundles','test'),bin=process.env.PATH!.split(':')[0];
const write=(file:string,text:string)=>{fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,text)};
write(path.join(bundle,'claude-launcher.ts'),'launcher');write(path.join(bundle,'node_modules/@agentclientprotocol/codex-acp/dist/index.js'),'adapter');
for(const name of ['claude-agent-acp','codex-acp'])write(path.join(bundle,'node_modules/@agentclientprotocol',name,'package.json'),JSON.stringify({version:'1.2.3'}));
write(descriptor,JSON.stringify([{id:'claude',cli:path.join(bin,'claude'),args:[path.join(bundle,'claude-launcher.ts')]},{id:'codex',cli:path.join(bin,'codex'),args:[path.join(bundle,'node_modules/@agentclientprotocol/codex-acp/dist/index.js')]}]));
`);
    const native = `#!${process.execPath}\nconst fs=require('node:fs'),path=require('node:path'),marker=process.env.MANAGED_TEST_MARKER;fs.appendFileSync(process.env.MANAGED_TEST_LOG,'native launch\\n');\n`;
    write(join(top, 'native-template'), native, 0o755);
    write(join(root, 'bin/cargo'), `#!/bin/sh\necho 'cargo build' >> "$MANAGED_TEST_LOG"\nmkdir -p target/debug\ncp "$MANAGED_TEST_NATIVE" target/debug/assembly-desktop\nchmod +x target/debug/assembly-desktop\n`, 0o755);
    execFileSync('git', ['init', '-q'], { cwd: root }); execFileSync('git', ['add', '.'], { cwd: root });
    const run = (env: object = {}) => {
      const started = performance.now();
      const result = spawnSync(process.execPath, ['--import', loader, join(root, 'scripts/app-dev.ts')], { cwd: root, encoding: 'utf8', timeout: 15000,
        env: { ...process.env, HOME: home, PATH: join(root, 'bin') + ':' + process.env.PATH, ASSEMBLY_REMOTE: undefined, ASSEMBLY_DEV_SEED_MANIFEST: manifest,
          MANAGED_TEST_LOG: log, MANAGED_TEST_MARKER: marker, MANAGED_TEST_NATIVE: join(top, 'native-template'), ...env } });
      assert.equal(result.status, 0, result.stderr + result.error);
      return performance.now() - started;
    };
    const first = run(); assert.match(readFileSync(log, 'utf8'), /dotnet publish/); assert.match(readFileSync(log, 'utf8'), /cargo build/); assert.doesNotMatch(readFileSync(log, 'utf8'), /remote-check/);
    write(log, '');
    const second = run(), calls = readFileSync(log, 'utf8');
    assert.doesNotMatch(calls, /dotnet|cargo|npm|adapter build/);
    assert.doesNotMatch(calls, /remote-check/); assert.match(calls, /native launch/);
    // A hand-made or copied .lane still opens, with no dependency check, seed or request to the shared remote.
    write(join(root, '.lane'), 'LANE_NAME=example\nLANE_NUMBER=2\n');
    write(log, '');
    run();
    const copied = readFileSync(log, 'utf8');
    assert.doesNotMatch(copied, /npm|dotnet build/);
    assert.match(copied, /native launch/);
    console.log(JSON.stringify({ measured: 'owned fake native launcher lifecycle', coldMs: first, warmMs: second, noBuildInstallOrSeed: true }));
  } finally { rmSync(top, { recursive: true, force: true }); }
});
