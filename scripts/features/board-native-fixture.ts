// SOURCE-DERIVED synthetic Board UI fixtures only. Never Engine outcomes, native/provider or handoff proof.
// Authoring this file does not authorize its execution. The native owner must stop Lane 9 first.
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { closeSync, existsSync, fsyncSync, lstatSync, openSync, readFileSync, readdirSync, readlinkSync, renameSync, statSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { backup, DatabaseSync } from 'node:sqlite';
import { safePath, takeLauncherLock } from '../seed-dev.ts';

const PROFILE = '/home/blackcolours/assembly-dev/worktrees/tsk-1649-engine-integration-fresh-f3e40988e73ec120';
const LANE = '/home/blackcolours/assembly-dotnet-tsk-1649-engine-integration-fresh/ui-check';
const MANIFEST = '/home/blackcolours/dev/work/reports/assembly-execution/tsk-1649-c2-board-database-source/closed-v2/board-proof-database.manifest.json';
const MANIFEST_SHA = 'd557c921f441f93315cc38640324466fbaf01a56c0ce80c3452f26add1ae6eff';
const MARKER = 'fixture.board-native.tsk-1649.v1';
const PREFIX = 'source-derived-board-native-tsk-1649-v1:';
const LAYOUT_KEY = 'shell.dock-layout';
const sha = (value: string | Buffer) => createHash('sha256').update(value).digest('hex');
type Options = { profile: string; backup: string; creator: string; underLock: boolean };

function parse(args: string[]): Options {
  const values = new Map<string, string>();
  let underLock = false;
  for (let index = 0; index < args.length; index++) {
    const key = args[index];
    if (key === '--under-backend-lock' && !underLock) { underLock = true; continue; }
    if (!['--profile', '--backup', '--creator-session'].includes(key) || values.has(key) || !args[index + 1] || args[index + 1].startsWith('--'))
      throw new Error(`Unknown/duplicate/incomplete argument: ${key}`);
    values.set(key, args[++index]);
  }
  const profile = values.get('--profile'); const destination = values.get('--backup'); const creator = values.get('--creator-session');
  if (!profile || !destination || !creator) throw new Error('--profile, --backup and --creator-session are required.');
  if (profile !== PROFILE || safePath(profile) !== PROFILE) throw new Error('Only the explicitly approved private Lane 9 profile is allowed.');
  const path = safePath(destination, false);
  if (dirname(path) !== join(PROFILE, 'board-fixture-backups') || !/^[a-zA-Z0-9_-]+\.db$/.test(basename(path)) || existsSync(path))
    throw new Error('Backup must be a NEW explicit .db file inside this profile/board-fixture-backups (create its private directory first).');
  if (!existsSync(dirname(path))) throw new Error('Native owner must create the private backup directory first.');
  if (!/^seed-[0-9a-f]{64}$/.test(creator)) throw new Error('Choose an explicit existing database-backed Board-proof seeded session; no default session is selected.');
  return { profile, backup: path, creator, underLock };
}

// An explicit PID assertion cannot establish offline ownership. Inspect only executable identities, never argv/env.
function isProtectedSystemChild(pid: string): boolean {
  try {
    const child = readFileSync(`/proc/${pid}/comm`, 'utf8').trim();
    const parent = /^PPid:\s+(\d+)$/m.exec(readFileSync(`/proc/${pid}/status`, 'utf8'))?.[1];
    if (parent === undefined) return false;
    const owner = readFileSync(`/proc/${parent}/comm`, 'utf8').trim();
    const ownerUid = statSync(`/proc/${parent}`).uid;
    const ssh = ['sshd', 'sshd-session'];
    if (ownerUid === 0) return (ssh.includes(child) && ssh.includes(owner)) ||
      ((child === 'smbd' || child.startsWith('smbd[')) && owner === 'smbd') || (child === 'systemd' && owner === 'systemd');
    if (ownerUid !== process.getuid!()) return false;
    const sshTransfer = child === 'sftp-server' && ssh.includes(owner);
    const userService = ['(sd-pam)', 'ssh-agent'].includes(child) && owner === 'systemd';
    if (!sshTransfer && !userService) return false;
    const grandparent = /^PPid:\s+(\d+)$/m.exec(readFileSync(`/proc/${parent}/status`, 'utf8'))?.[1];
    if (grandparent === undefined || statSync(`/proc/${grandparent}`).uid !== 0) return false;
    const ancestor = readFileSync(`/proc/${grandparent}/comm`, 'utf8').trim();
    return sshTransfer ? ssh.includes(ancestor) : ancestor === 'systemd';
  } catch { return false; } // Missing or restricted ancestry remains unknown and is refused.
}

function requireOffline(): void {
  if (process.platform !== 'linux') throw new Error('This fixture is limited to the approved Workbox Lane 9.');
  const configPath = safePath(join(LANE, 'backend/lane.json'));
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  if (config.home !== PROFILE) throw new Error('Owned lane.json no longer names the approved profile.');
  const executables = new Set([join(dirname(LANE), 'target/debug/assembly-desktop'), join(LANE, 'backend/Assembly.Desktop')]);
  for (const entry of readdirSync('/proc')) {
    if (!/^[0-9]+$/.test(entry)) continue;
    try {
      if (statSync(`/proc/${entry}`).uid !== process.getuid!()) continue;
      if (executables.has(readlinkSync(`/proc/${entry}/exe`).replace(/ \(deleted\)$/, '')))
        throw new Error(`Owned Lane 9 app/backend process ${entry} is still running; no process is stopped by this script.`);
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code === 'EACCES' && isProtectedSystemChild(entry)) continue;
      if (code !== 'ENOENT' && code !== 'ESRCH') throw error;
    }
  }
  const pidPath = safePath(join(PROFILE, 'backend.lock.pid'));
  const pid = Number(readFileSync(pidPath, 'utf8').trim());
  if (!Number.isSafeInteger(pid) || pid < 1) throw new Error('Cannot verify saved backend ownership.');
  try { process.kill(pid, 0); throw new Error(`Saved backend PID ${pid} is present; refuse even a possible PID reuse.`); }
  catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error; }
}

function requireBackendLock(): void {
  const info = statSync(safePath(join(PROFILE, 'backend.lock')), { bigint: true });
  const major = ((info.dev & 0xfff00n) >> 8n) | ((info.dev & 0xfffff00000000000n) >> 32n);
  const minor = (info.dev & 0xffn) | ((info.dev & 0xffffff00000n) >> 12n);
  const held = readFileSync('/proc/locks', 'utf8').split('\n').some(line => {
    const fields = line.trim().split(/\s+/);
    const identity = fields[5]?.split(':');
    return fields[1] === 'FLOCK' && fields[3] === 'WRITE' && fields[4] === String(process.pid) && identity?.length === 3 &&
      BigInt('0x' + identity[0]) === major && BigInt('0x' + identity[1]) === minor && identity[2] === String(info.ino);
  });
  if (!held) throw new Error('This process does not hold the actual backend flock; internal mode cannot bypass it.');
}

type Panel = { panel_name: string; children: Panel[]; info: { panel?: unknown; tabs?: { active_index: number } } };
const leaf = (name: string, value: unknown = null): Panel => ({ panel_name: name, children: [], info: { panel: value } });
const tabs = (children: Panel[]): Panel => ({ panel_name: 'TabPanel', children, info: { tabs: { active_index: 0 } } });
function legacyLayout(saved: string | undefined, creator: string): unknown {
  const dock = (names: string[], placement: string, size: number, open: boolean) => ({ panel: tabs(names.map(name => leaf(name))), placement, size, open });
  const layout = saved === undefined ? {
    version: 2, center: tabs([leaf('Chat')]),
    left_dock: dock(['Sessions', 'Files', 'Search'], 'left', 326, true),
    right_dock: dock(['Source control', 'Agents', 'Tasks', 'Worktrees', 'Run', 'Context', 'History'], 'right', 320, false),
    bottom_dock: dock(['Problems', 'Terminal'], 'bottom', 180, false),
  } : JSON.parse(saved);
  if (layout.version !== 2 || layout.center?.panel_name !== 'TabPanel' || !Array.isArray(layout.center.children) ||
      !Number.isSafeInteger(layout.center.info?.tabs?.active_index)) throw new Error('Existing saved layout is not the supported Rust version2 center tab group; preserve it unchanged.');
  const center: Panel[] = layout.center.children;
  // Unrelated existing tabs, payloads and active indices are never changed. Fresh fixture layout only gets Chat/File.
  if (saved === undefined) center.push(leaf('File', { session: creator, path: 'SEED-FIXTURE.txt' }));
  if (!center.some(panel => panel.panel_name === 'Build Board')) center.push(leaf('Build Board'));
  return layout;
}

const COLUMNS: Record<string, string> = {
  board_tasks: 'machine_id,task_key,creator_session_id,project,title,summary,state,task_url,pr_url,started_at,ended_at,updated_at,revision',
  board_questions: 'machine_id,task_key,question_key,generation,kind,prompt,recommendation,asked_at,answer_json,answered_at,answer_key,delivery_client_id,delivery_turn_id,delivery_state,accepted_revision',
  board_events: 'machine_id,seq,task_key,kind,occurred_at,payload_json', preferences: 'key,value',
};
function requireSchema(db: DatabaseSync): void {
  for (const [table, columns] of Object.entries(COLUMNS)) {
    const actual = db.prepare(`PRAGMA table_info(${table})`).all().map(row => row.name).join(',');
    if (actual !== columns) throw new Error(`Current ${table} schema differs; no migration is performed.`);
  }
}
function fixtureRows(db: DatabaseSync): unknown[] {
  return ['board_tasks', 'board_questions', 'board_events'].map(table =>
    db.prepare(`SELECT * FROM ${table} WHERE machine_id='local' AND substr(task_key,1,?)=? ORDER BY ${table === 'board_events' ? 'seq' : table === 'board_questions' ? 'task_key,question_key' : 'task_key'}`)
      .all(PREFIX.length, PREFIX));
}
function addFixture(db: DatabaseSync, creator: string, now: number): { tasks: number; questions: number; events: number } {
  const task = db.prepare(`INSERT INTO board_tasks(${COLUMNS.board_tasks}) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)`);
  const question = db.prepare(`INSERT INTO board_questions(${COLUMNS.board_questions}) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`);
  const event = db.prepare(`INSERT INTO board_events(${COLUMNS.board_events}) VALUES(?,?,?,?,?,?)`);
  let seq = Number((db.prepare("SELECT COALESCE(MAX(seq),0) AS seq FROM board_events WHERE machine_id='local'").get()!).seq);
  if (!Number.isSafeInteger(seq) || seq < 0) throw new Error('Unsafe existing Board sequence.');
  let events = 0; let questions = 0;
  const items = [
    { key: 'running', title: 'Inspect reconnect history', summary: 'SOURCE-DERIVED UI FIXTURE: compare restored rows and keep the reader position stable.', state: 'running', ended: null },
    { key: 'waiting', title: 'Review the rollout checklist', summary: 'SOURCE-DERIVED UI FIXTURE: inspect two questions and their recommendations. No real approval or Engine work is represented.', state: 'waiting', ended: null },
    { key: 'completed', title: 'Review a terminal card', summary: 'SOURCE-DERIVED UI FIXTURE: static completed-card typography and duration; not an accepted Engine outcome.', state: 'completed', ended: now - 60_000 },
  ];
  for (const [index, item] of items.entries()) {
    const key = PREFIX + item.key; const started = now - (index + 2) * 60_000;
    const record = { machineId: 'local', taskKey: key, creatorSessionId: creator, project: 'Source-derived UI fixtures',
      title: `[UI fixture] ${item.title}`, summary: item.summary, state: item.state, taskUrl: null, prUrl: null,
      startedAt: started, endedAt: item.ended, updatedAt: now - index * 1000, revision: 1 };
    task.run('local', key, creator, record.project, record.title, record.summary, record.state, null, null, started, item.ended, record.updatedAt, 1);
    const steps = [
      { index: 0, text: 'Inspect source-derived fixture context', tool: null, detail: 'Static UI sample, not a tool execution.', state: 'done', badge: null, explanation: null, progress: 100,
        startedAt: new Date(started).toISOString(), endedAt: new Date(started + 20_000).toISOString(), attempt: 1, maxAttempts: 1, questionKey: null, requiredScope: null, scopeGranted: null },
      { index: 1, text: 'Review the visible Board detail', tool: null, detail: 'No provider, network, Engine transition or handoff is run by this fixture.', state: item.state === 'completed' ? 'done' : 'running',
        badge: null, explanation: null, progress: item.state === 'completed' ? 100 : 40, startedAt: new Date(started + 20_000).toISOString(), endedAt: null,
        attempt: 1, maxAttempts: 1, questionKey: item.state === 'waiting' ? 'fixture-approval' : null, requiredScope: null, scopeGranted: null },
    ];
    const emit = (kind: string, qs: unknown[], at: number) => {
      seq++; if (!Number.isSafeInteger(seq)) throw new Error('Board sequence overflow.');
      const payload = { machineId: 'local', seq, taskKey: key, kind, occurredAt: at, task: record, questions: qs,
        changedPaths: ['SOURCE-DERIVED-UI-FIXTURE.txt'], steps };
      event.run('local', seq, key, kind, at, JSON.stringify(payload)); events++;
    };
    emit('task_reported', [], record.updatedAt);
    if (item.state !== 'waiting') continue;
    for (const [offset, kind] of ['approval', 'text'].entries()) {
      const q = { machineId: 'local', taskKey: key, questionKey: kind === 'approval' ? 'fixture-approval' : 'fixture-text', generation: 1, kind,
        prompt: kind === 'approval' ? '[UI fixture] Is this static checklist clear enough to review?' : '[UI fixture] Which sample detail should the reviewer inspect?',
        recommendation: 'SOURCE-DERIVED UI ONLY. Do not treat an answer as provider, task, Engine or delivery proof.', askedAt: now + offset,
        answer: null, answeredAt: null, answerKey: null, deliveryClientId: null, deliveryTurnId: null, deliveryState: 'none', acceptedRevision: null };
      question.run('local', key, q.questionKey, 1, kind, q.prompt, q.recommendation, q.askedAt, null, null, null, null, null, 'none', null);
      emit('question_asked', [q], q.askedAt); questions++;
    }
  }
  return { tasks: items.length, questions, events };
}

async function apply(options: Options): Promise<void> {
  requireBackendLock(); requireOffline();
  const launcher = JSON.parse(readFileSync(safePath(join(PROFILE, 'launcher.lock')), 'utf8'));
  if (launcher.launcher !== process.ppid || launcher.purpose !== 'dev-seed') throw new Error('The parent does not own the existing seed launcher guard.');
  const manifestBytes = readFileSync(safePath(MANIFEST));
  if (sha(manifestBytes) !== MANIFEST_SHA) throw new Error('Board-proof database manifest changed; refuse another source.');
  const manifest = JSON.parse(manifestBytes.toString('utf8'));
  // DevSeed.SessionId: compact [origin, key] JSON, UTF-8 SHA256. This pinned database manifest has explicit ASCII sessionKeys.
  const acceptedCreators = new Set(manifest.sources.flatMap((source: { sessionKeys: string[] }) => source.sessionKeys.map(key =>
    'seed-' + sha(JSON.stringify([manifest.originId, key])))));
  if (!acceptedCreators.has(options.creator)) throw new Error('Creator is not a pinned database-backed Board-proof seed session.');
  const path = safePath(join(PROFILE, 'assembly.db'));
  if (!lstatSync(path).isFile() || lstatSync(path).nlink !== 1) throw new Error('Database must be the existing unaliased private profile file.');
  const db = new DatabaseSync(path, { timeout: 0 });
  try {
    requireSchema(db);
    const creator = db.prepare('SELECT session_id,repo_url,machine,acp_session_id FROM session_info WHERE session_id=?').get(options.creator);
    if (!creator || creator.machine !== null || creator.acp_session_id !== null || typeof creator.repo_url !== 'string')
      throw new Error('Creator must be an existing local archival database-backed Board-proof seed with no provider resume identity.');
    const folder = safePath(creator.repo_url);
    if (!folder.startsWith(join(PROFILE, 'sessions') + '/') || !lstatSync(safePath(join(folder, 'SEED-FIXTURE.txt'))).isFile())
      throw new Error('Creator does not have the existing accepted seed fixture surroundings.');
    // A SQLite backup, not a raw copy of an open/WAL database. Publish the explicit private backup atomically.
    const part = options.backup + '.partial'; safePath(part, false);
    const fd = openSync(part, 'wx', 0o600); closeSync(fd);
    await backup(db, part);
    const sync = openSync(part, 'r'); try { fsyncSync(sync); } finally { closeSync(sync); }
    renameSync(part, options.backup);
    requireOffline(); requireBackendLock();
    db.exec('BEGIN IMMEDIATE');
    try {
      const saved = db.prepare('SELECT value FROM preferences WHERE key=?').get(LAYOUT_KEY)?.value;
      if (saved !== undefined && typeof saved !== 'string') throw new Error('Saved layout has an unexpected representation.');
      const previous = db.prepare('SELECT value FROM preferences WHERE key=?').get(MARKER)?.value;
      if (previous !== undefined) {
        const receipt = JSON.parse(String(previous));
        if (typeof saved !== 'string' || receipt.creator !== options.creator || receipt.rowsSha256 !== sha(JSON.stringify(fixtureRows(db))) || receipt.layoutSha256 !== sha(saved))
          throw new Error('Existing fixture or layout changed; preserve it without overwrite or cleanup.');
        db.exec('ROLLBACK');
        console.log(JSON.stringify({ unchanged: true, backup: options.backup, sourceDerivedUiOnly: true })); return;
      }
      if (fixtureRows(db).some(rows => (rows as unknown[]).length !== 0)) throw new Error('Marked Board identities exist without their fixture receipt; no overwrite.');
      const layout = JSON.stringify(legacyLayout(saved as string | undefined, options.creator));
      const counts = addFixture(db, options.creator, Date.now());
      db.prepare('INSERT INTO preferences(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value').run(LAYOUT_KEY, layout);
      const receipt = { version: 1, creator: options.creator, manifestSha256: MANIFEST_SHA, sourceDerivedUiOnly: true,
        layoutBeforeSha256: saved === undefined ? null : sha(saved as string), layoutSha256: sha(layout), rowsSha256: sha(JSON.stringify(fixtureRows(db))), counts };
      db.prepare('INSERT INTO preferences(key,value) VALUES(?,?)').run(MARKER, JSON.stringify(receipt));
      if ((db.prepare('PRAGMA integrity_check').get()!).integrity_check !== 'ok' || db.prepare('PRAGMA foreign_key_check').all().length)
        throw new Error('Relational integrity refused; all related writes roll back.');
      db.exec('COMMIT');
      console.log(JSON.stringify({ backup: options.backup, counts, sourceDerivedUiOnly: true, layoutKey: LAYOUT_KEY,
        limitation: 'Synthetic Board UI and saved-layout fixture only; no Engine/provider/answer-delivery/native acceptance proof.' }));
    } catch (error) { if (db.isTransaction) db.exec('ROLLBACK'); throw error; }
  } finally { db.close(); }
}

export async function main(args: string[]): Promise<void> {
  if (args.length === 1 && args[0] === '--help') {
    console.log('SOURCE-ONLY until Main authorizes execution. Stop owned Lane9 app/backend first.\nnode scripts/features/board-native-fixture.ts --profile EXACT_LANE9_PROFILE --creator-session EXPLICIT_SEED_ID --backup NEW_PRIVATE_BACKUP.db\nAdds marked SOURCE-DERIVED Board/layout fixtures in one transaction after SQLite backup; no cleanup, build, provider, network or Engine outcomes.');
    return;
  }
  const options = parse(args); requireOffline();
  if (options.underLock) { await apply(options); return; }
  const release = takeLauncherLock(PROFILE);
  try {
    const child = spawn('flock', ['--nonblock', '--no-fork', safePath(join(PROFILE, 'backend.lock')), process.execPath,
      fileURLToPath(import.meta.url), ...args, '--under-backend-lock'], { stdio: 'inherit' });
    const code = await new Promise<number>((done, reject) => { child.once('error', reject); child.once('close', value => done(value ?? 1)); });
    if (code !== 0) throw new Error(`Owned offline fixture process failed (${code}); existing data/backup retained.`);
  } finally { release(); }
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
}
