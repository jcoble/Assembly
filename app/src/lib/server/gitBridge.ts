/**
 * gitBridge.ts — the dev server's answer to "what does git say?", for the
 * browser.
 *
 * WHY THIS EXISTS
 * The source-control panel gets everything from Rust commands that only exist
 * inside the desktop app. Open the same shell in Chrome and every one of those
 * calls comes back empty, so the whole panel can only say "desktop app only" —
 * which makes it impossible to look at, style or debug in a browser. This file
 * runs the same `git` commands from the dev server instead, and hands back the
 * SAME SHAPES the Rust commands do, field for field. The panel therefore needs
 * no special case: it asks, something answers.
 *
 * IT ONLY READS
 * There is no route here that stages, commits, fetches, pulls or pushes. A page
 * served in a browser can be left open, refreshed, or clicked by accident, and
 * a repository with work in progress is not something to risk for the sake of a
 * preview. Anything that changes the repository stays in the desktop app, where
 * the user is deliberately driving. The panel says so in plain words rather
 * than pretending the buttons work.
 *
 * HOW IT RUNS GIT
 * `execFile`, never a shell — arguments are passed as a list, so nothing in a
 * path or a commit id can be read as a command. On top of that the same three
 * guards the Rust side uses are repeated here, because they are the reason a
 * bad argument cannot turn into a different git command:
 *   - the repository root must be a real folder,
 *   - a commit id may only contain the characters a commit id can contain, and
 *     may not start with `-` (which git would read as an option),
 *   - a path inside the repository must be relative and must not climb out
 *     of it with `..`.
 *
 * WHERE THE PARSING COMES FROM
 * Every parser below is a port of the one in `src-tauri/src/main.rs` — the
 * porcelain status reader, the badge letters, the plain-word status names, the
 * history record separated by unit separators, and the `--name-status` file
 * list. They are ported rather than shared because one is Rust and one is
 * TypeScript; `scripts/gitBridge.test.mjs` runs this side against the real
 * repository so the shapes stay honest.
 */

import { execFile } from 'node:child_process';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';

const runProcess = promisify(execFile);

/** Every route this bridge answers starts with this. */
export const GIT_BRIDGE_ROUTE_PREFIX = '/__mcb/git/';

/** How many commits the history returns when nobody says. Matches the Rust default. */
const DEFAULT_HISTORY_LIMIT = 24;

/** Room for a big `git show`; beyond this the answer is refused rather than truncated silently. */
const MAX_GIT_OUTPUT_BYTES = 24 * 1024 * 1024;

// ── the shapes, mirroring the Rust structs field for field ──────────────────

export interface GitBridgeFileStatus {
  relativePath: string;
  indexStatus: string;
  worktreeStatus: string;
  status: string;
  badge: string;
}

export interface GitBridgeStatus {
  branch: string | null;
  ahead: number;
  behind: number;
  hasUpstream: boolean;
  files: GitBridgeFileStatus[];
}

export interface GitBridgeCommit {
  shortSha: string;
  sha: string;
  subject: string;
  author: string;
  committedAt: string;
  refs: string;
  parentShas: string[];
  parentCount: number;
  taskID: string | null;
  taskSource: string | null;
}

export interface GitBridgeHistoryPage {
  root: string;
  relativePath: string | null;
  commits: GitBridgeCommit[];
  nextCursor: string | null;
  complete: boolean;
}

export interface GitBridgeCommitFile {
  relativePath: string;
  status: string;
  badge: string;
}

export interface GitBridgeDiff {
  relativePath: string;
  status: string;
  diff: string;
  isBinary: boolean;
  originalContent: string | null;
  modifiedContent: string | null;
}

const MAX_DIFF_MODEL_BYTES = 512 * 1024;

function boundedDiffModel(text: string): string | null {
  return Buffer.byteLength(text, 'utf8') <= MAX_DIFF_MODEL_BYTES && !text.includes('\0')
    ? text
    : null;
}

async function revisionTextOrEmpty(
  root: string,
  revision: string,
  relativePath: string
): Promise<string | null> {
  try {
    return boundedDiffModel(await runGit(root, ['show', `${revision}:${relativePath}`]));
  } catch {
    return '';
  }
}

// ── guards ──────────────────────────────────────────────────────────────────

async function requireRepositoryFolder(root: string): Promise<string> {
  const trimmed = (root ?? '').trim();
  if (trimmed === '') throw new Error('A repository folder is required');
  let entry;
  try {
    entry = await stat(trimmed);
  } catch (error) {
    throw new Error(
      `Could not read the repository folder: ${error instanceof Error ? error.message : String(error)}`
    );
  }
  if (!entry.isDirectory()) throw new Error('The repository path is not a folder');
  return trimmed;
}

/**
 * The top of the repository the given folder belongs to.
 *
 * WHY EVERY READ GOES THROUGH THIS. `git show <sha> -- <path>` matches the path
 * against the CURRENT FOLDER, not against the top of the repository. Hand it a
 * repository-relative path while sitting in a subfolder and git matches
 * nothing, prints nothing, and reports success — an empty diff that looks like
 * "this file did not change" when the truth is "we looked in the wrong place".
 * A source-control panel pointed at a session's working folder hits this the
 * moment that folder is not the top, so the top is resolved once, here, and
 * every command runs from it.
 */
export async function repositoryTop(root: string): Promise<string> {
  const folder = await requireRepositoryFolder(root);
  const output = await runGit(folder, ['rev-parse', '--show-toplevel']);
  const top = output.trim();
  return top === '' ? folder : top;
}

/**
 * A commit id reaches git as a bare argument, so anything that could be read as
 * an option — or as a path — is refused instead of forwarded.
 */
function requireCommitId(sha: string): string {
  const trimmed = (sha ?? '').trim();
  const safe =
    trimmed !== '' &&
    !trimmed.startsWith('-') &&
    trimmed.length <= 200 &&
    /^[A-Za-z0-9_./^~-]+$/.test(trimmed);
  if (!safe) throw new Error('That is not a commit id this bridge will run');
  return trimmed;
}

/** A path inside the repository: relative, and staying inside it. */
function requireRelativePath(relativePath: string): string {
  const trimmed = (relativePath ?? '').trim();
  const safe =
    trimmed !== '' &&
    !trimmed.includes('\0') &&
    !path.isAbsolute(trimmed) &&
    !trimmed
      .split('/')
      .some((segment) => segment === '..' || segment === '.' || segment === '');
  if (!safe) throw new Error('Git reads only relative paths inside the repository');
  return trimmed;
}

// ── running git ─────────────────────────────────────────────────────────────

/**
 * Run one git command in `root` and return what it printed. A command that
 * fails hands back git's own message, the way the Rust side does, because
 * git's wording ("not a git repository", "unknown revision") is the wording the
 * panel already knows how to read.
 */
async function runGit(root: string, args: string[]): Promise<string> {
  try {
    // `core.quotepath=false` turns off git's habit of printing a non-ASCII file
    // name as octal escapes (`"\303\251.txt"`). With it off the name comes back
    // as the text it actually is, which is both readable on screen and usable
    // as an argument to the next git command.
    const { stdout } = await runProcess(
      'git',
      ['-C', root, '-c', 'core.quotepath=false', ...args],
      {
        maxBuffer: MAX_GIT_OUTPUT_BYTES,
        encoding: 'utf8'
      }
    );
    return stdout;
  } catch (error) {
    const failure = error as { stderr?: string; message?: string };
    const stderr = (failure.stderr ?? '').trim();
    throw new Error(stderr !== '' ? stderr : (failure.message ?? `git ${args.join(' ')} failed`));
  }
}

// ── parsers, ported from the Rust side ──────────────────────────────────────

/** git's one-letter code, said in a word. */
function statusName(code: string): string {
  switch (code) {
    case 'M':
      return 'modified';
    case 'A':
      return 'added';
    case 'D':
      return 'deleted';
    case 'R':
      return 'renamed';
    case 'C':
      return 'copied';
    case 'U':
      return 'unmerged';
    case '?':
      return 'untracked';
    case '!':
      return 'ignored';
    default:
      return '';
  }
}

/** The single letter shown at the end of a changed-file row. */
function statusBadge(indexCode: string, worktreeCode: string): string {
  const code = worktreeCode !== ' ' ? worktreeCode : indexCode;
  return 'MADRCU!?'.includes(code) ? code : '';
}

/** A rename prints `old -> new`; the file lives at the new path. */
function normalizeStatusPath(value: string): string {
  const parts = value.split(' -> ');
  return (parts[parts.length - 1] ?? value).trim().replace(/^"|"$/g, '');
}

export interface GitBridgeBranchHeader {
  branch: string | null;
  ahead: number;
  behind: number;
  hasUpstream: boolean;
}

/** The `## branch...remote [ahead 1, behind 2]` line at the top of a status. */
export function parseGitBranchHeader(header: string): GitBridgeBranchHeader {
  let branchPart = header.trim();
  let ahead = 0;
  let behind = 0;

  const metadataIndex = branchPart.indexOf(' [');
  if (metadataIndex >= 0) {
    const metadata = branchPart.slice(metadataIndex + 2).replace(/\]$/, '');
    branchPart = branchPart.slice(0, metadataIndex);
    for (const item of metadata.split(',').map((value) => value.trim())) {
      if (item.startsWith('ahead ')) ahead = Number.parseInt(item.slice(6), 10) || 0;
      else if (item.startsWith('behind ')) behind = Number.parseInt(item.slice(7), 10) || 0;
    }
  }

  const [namePart, upstreamPart] = branchPart.split('...');
  const hasUpstream = (upstreamPart ?? '').trim() !== '';

  const noCommitsYet = branchPart.startsWith('No commits yet on ');
  const branchName = noCommitsYet
    ? branchPart.slice('No commits yet on '.length).split('...')[0].trim()
    : (namePart ?? '').trim();

  return { branch: branchName === '' ? null : branchName, ahead, behind, hasUpstream };
}

/** `git status --porcelain=v1 --branch`, read the same way Rust reads it. */
export function parseGitStatus(output: string): GitBridgeStatus {
  const status: GitBridgeStatus = {
    branch: null,
    ahead: 0,
    behind: 0,
    hasUpstream: false,
    files: []
  };

  for (const line of output.split('\n')) {
    if (line.trim() === '') continue;

    if (line.startsWith('## ')) {
      const header = parseGitBranchHeader(line.slice(3));
      status.branch = header.branch;
      status.ahead = header.ahead;
      status.behind = header.behind;
      status.hasUpstream = header.hasUpstream;
      continue;
    }

    if (line.length < 4) continue;
    const indexCode = line[0];
    const worktreeCode = line[1];
    const relativePath = normalizeStatusPath(line.slice(3));
    if (relativePath === '') continue;

    const indexStatus = statusName(indexCode);
    const worktreeStatus = statusName(worktreeCode);
    status.files.push({
      relativePath,
      indexStatus,
      worktreeStatus,
      status: worktreeStatus !== '' ? worktreeStatus : indexStatus,
      badge: statusBadge(indexCode, worktreeCode)
    });
  }

  return status;
}

/** `TSK-761` out of a branch name, a ref list or a commit subject. */
export function taskIdFromText(text: string): string | null {
  const lower = (text ?? '').toLowerCase();
  let index = lower.indexOf('tsk');
  while (index >= 0) {
    const digits = lower
      .slice(index + 3)
      .replace(/^[-_/#[ ]+/, '')
      .match(/^\d+/);
    if (digits) return `TSK-${digits[0]}`;
    index = lower.indexOf('tsk', index + 3);
  }
  return null;
}

/** One history record per line, fields separated by the unit separator. */
export function parseGitCommitHistory(output: string): GitBridgeCommit[] {
  const entries: GitBridgeCommit[] = [];

  for (const line of output.split('\n')) {
    if (line.trim() === '') continue;
    const parts = line.split('\u001f');
    const shortSha = (parts[0] ?? '').trim();
    const sha = (parts[1] ?? '').trim();
    const subject = (parts[2] ?? '').trim();
    const author = (parts[3] ?? '').trim();
    const committedAt = (parts[4] ?? '').trim();
    const refs = (parts[5] ?? '').trim();
    const parentShas = (parts[6] ?? '')
      .split(/\s+/)
      .map((value) => value.trim())
      .filter((value) => value !== '');

    if (shortSha === '' || sha === '') {
      throw new Error('Could not read a line of the commit history');
    }

    const fromRefs = taskIdFromText(refs);
    const fromSubject = fromRefs ? null : taskIdFromText(subject);
    entries.push({
      shortSha,
      sha,
      subject,
      author,
      committedAt,
      refs,
      parentShas,
      parentCount: parentShas.length,
      taskID: fromRefs ?? fromSubject,
      taskSource: fromRefs ? 'refs' : fromSubject ? 'subject' : null
    });
  }

  return entries;
}

function historyCursorOffset(cursor?: string | null): number {
  const trimmed = (cursor ?? '').trim();
  if (trimmed === '') return 0;
  if (!/^\d+$/.test(trimmed)) throw new Error('Git history cursor was not recognized');
  return Number.parseInt(trimmed, 10);
}

/**
 * `git show --name-status --format=` prints a status letter and a path per
 * line. A merge commit prints nothing at all, which is the honest answer: a
 * merge brings no changes of its own.
 */
export function parseGitCommitFileChanges(output: string): GitBridgeCommitFile[] {
  const files: GitBridgeCommitFile[] = [];

  for (const line of output.split('\n')) {
    if (line.trim() === '') continue;
    const fields = line.split('\t');
    const statusField = (fields[0] ?? '').trim();
    const statusCode = statusField[0];
    if (!statusCode) continue;
    // A rename or a copy prints the old path then the new one; the file lives
    // at the last path, so that is the one reported.
    const relativePath = normalizeStatusPath((fields[fields.length - 1] ?? '').trim());
    if (relativePath === '' || fields.length < 2) continue;

    files.push({
      relativePath,
      status: statusName(statusCode),
      badge: statusBadge(statusCode, ' ')
    });
  }

  return files;
}

/** Staged and unstaged changes shown one after the other, as the desktop does. */
function combineDiffs(stagedDiff: string, workingDiff: string): string {
  const staged = stagedDiff.trimEnd();
  const working = workingDiff.trimEnd();
  if (staged === '' && working === '') return '';
  if (working === '') return staged;
  if (staged === '') return working;
  return `## Staged\n${staged}\n\n## Working tree\n${working}`;
}

function looksBinary(diff: string): boolean {
  return diff.includes('Binary files ') || diff.includes('GIT binary patch');
}

// ── the four questions the panel asks ───────────────────────────────────────

/** Mirrors `project_git_status`. */
export async function readGitStatus(root: string): Promise<GitBridgeStatus> {
  const folder = await repositoryTop(root);
  const output = await runGit(folder, [
    'status',
    '--porcelain=v1',
    '--branch',
    '--untracked-files=all'
  ]);
  return parseGitStatus(output);
}

/** Mirrors `read_git_commit_history`. */
export async function readGitCommitHistory(
  root: string,
  cursor?: string | null,
  relativePath?: string | null
): Promise<GitBridgeHistoryPage> {
  const folder = await repositoryTop(root);
  const offset = historyCursorOffset(cursor);
  const pageRelativePath = relativePath?.trim() || null;

  try {
    const args = [
      'log',
      '--decorate=short',
      '--date=iso-strict',
      '--format=%h%x1f%H%x1f%s%x1f%an%x1f%cI%x1f%D%x1f%P',
      `--skip=${offset}`,
      `-n${DEFAULT_HISTORY_LIMIT + 1}`
    ];
    if (pageRelativePath) args.push('--follow', '--', pageRelativePath);
    const output = await runGit(folder, args);
    const commits = parseGitCommitHistory(output);
    const complete = commits.length <= DEFAULT_HISTORY_LIMIT;
    return {
      root: folder,
      relativePath: pageRelativePath,
      commits: complete ? commits : commits.slice(0, DEFAULT_HISTORY_LIMIT),
      nextCursor: complete ? null : String(offset + DEFAULT_HISTORY_LIMIT),
      complete
    };
  } catch (error) {
    // A brand-new repository has no commits yet. That is an empty list, not a
    // failure.
    const message = error instanceof Error ? error.message : String(error);
    if (message.includes('does not have any commits')) {
      return {
        root: folder,
        relativePath: pageRelativePath,
        commits: [],
        nextCursor: null,
        complete: true
      };
    }
    throw error;
  }
}

/** Mirrors `read_git_commit_files`. */
export async function readGitCommitFiles(
  root: string,
  sha: string
): Promise<GitBridgeCommitFile[]> {
  const folder = await repositoryTop(root);
  const commitId = requireCommitId(sha);
  const output = await runGit(folder, ['show', '--name-status', '--format=', commitId]);
  return parseGitCommitFileChanges(output);
}

/**
 * Mirrors `read_git_commit_file_diff`. Note the path is RELATIVE to the
 * repository here, while `readGitFileDiff` below takes a whole path on disk —
 * the desktop commands differ in exactly the same way.
 */
export async function readGitCommitFileDiff(
  root: string,
  sha: string,
  relativePath: string
): Promise<GitBridgeDiff> {
  const folder = await repositoryTop(root);
  const commitId = requireCommitId(sha);
  const filePath = requireRelativePath(relativePath);

  const nameStatus = await runGit(folder, [
    'show',
    '--name-status',
    '--format=',
    commitId,
    '--',
    filePath
  ]);
  const listed = parseGitCommitFileChanges(nameStatus)[0];

  const diff = await runGit(folder, [
    'show',
    '--no-ext-diff',
    '--format=',
    commitId,
    '--',
    filePath
  ]);

  const status = listed?.status || (diff.trim() === '' ? 'clean' : 'modified');
  const isBinary = looksBinary(diff);
  return {
    relativePath: filePath,
    status,
    diff,
    isBinary,
    originalContent: isBinary ? null : await revisionTextOrEmpty(folder, `${commitId}^`, filePath),
    modifiedContent: isBinary ? null : await revisionTextOrEmpty(folder, commitId, filePath)
  };
}

/** Mirrors `read_source_git_diff`, whole-path-on-disk and all. */
export async function readGitFileDiff(
  root: string,
  absolutePath: string
): Promise<GitBridgeDiff> {
  const folder = await repositoryTop(root);
  const target = (absolutePath ?? '').trim();
  if (target === '') throw new Error('A file path is required');

  let entry;
  try {
    entry = await stat(target);
  } catch (error) {
    throw new Error(
      `Could not read the file metadata: ${error instanceof Error ? error.message : String(error)}`
    );
  }
  if (!entry.isFile()) throw new Error('That path is not a file');

  const resolvedRoot = path.resolve(folder);
  const resolvedPath = path.resolve(target);
  if (resolvedPath !== resolvedRoot && !resolvedPath.startsWith(`${resolvedRoot}${path.sep}`)) {
    throw new Error('That file is outside the repository');
  }
  const relativePath = path.relative(resolvedRoot, resolvedPath).split(path.sep).join('/');

  const statusOutput = await runGit(folder, [
    'status',
    '--porcelain=v1',
    '--untracked-files=normal',
    '--',
    relativePath
  ]);
  const listed = parseGitStatus(statusOutput).files[0];

  const stagedDiff = await runGit(folder, [
    'diff',
    '--no-ext-diff',
    '--cached',
    '--',
    relativePath
  ]);
  const workingDiff = await runGit(folder, ['diff', '--no-ext-diff', '--', relativePath]);
  const diff = combineDiffs(stagedDiff, workingDiff);

  const status = listed?.status || (diff.trim() === '' ? 'clean' : 'modified');
  const isBinary = looksBinary(diff);
  return {
    relativePath,
    status,
    diff,
    isBinary,
    originalContent: isBinary ? null : await revisionTextOrEmpty(folder, 'HEAD', relativePath),
    modifiedContent: isBinary ? null : boundedDiffModel(await readFile(resolvedPath, 'utf8'))
  };
}

/** Mirrors `read_git_branch_diff`: one diff from the merge base of HEAD and
 * `origin/HEAD` to the working tree. */
export async function readGitBranchDiff(root: string): Promise<{ base: string; diff: string }> {
  const folder = await requireRepositoryFolder(root);
  let base: string;
  try {
    base = (await runGit(folder, ['rev-parse', '--abbrev-ref', 'origin/HEAD'])).trim();
  } catch {
    throw new Error('This repository has no origin/HEAD, so there is no default branch to compare with.');
  }
  const mergeBase = (await runGit(folder, ['merge-base', 'HEAD', base])).trim();
  return { base, diff: await runGit(folder, ['diff', '--no-ext-diff', mergeBase]) };
}

// ── the routes ──────────────────────────────────────────────────────────────

export interface GitBridgeResponse {
  /** False when the path is not one of ours, so the dev server carries on. */
  handled: boolean;
  statusCode: number;
  body: unknown;
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/**
 * Answer one bridge request. Kept separate from the HTTP plumbing so the test
 * can drive the routes without a socket.
 */
export async function handleGitBridgeRequest(
  pathname: string,
  body: Record<string, unknown>
): Promise<GitBridgeResponse> {
  const unhandled: GitBridgeResponse = { handled: false, statusCode: 404, body: null };
  if (!pathname.startsWith(GIT_BRIDGE_ROUTE_PREFIX)) return unhandled;
  const route = pathname.slice(GIT_BRIDGE_ROUTE_PREFIX.length);

  try {
    switch (route) {
      case 'status':
        return { handled: true, statusCode: 200, body: await readGitStatus(text(body.root)) };
      case 'history':
        return {
          handled: true,
          statusCode: 200,
          body: await readGitCommitHistory(
            text(body.root),
            text(body.cursor) || null,
            text(body.relativePath) || null
          )
        };
      case 'commit-files':
        return {
          handled: true,
          statusCode: 200,
          body: await readGitCommitFiles(text(body.root), text(body.sha))
        };
      case 'commit-file-diff':
        return {
          handled: true,
          statusCode: 200,
          body: await readGitCommitFileDiff(
            text(body.root),
            text(body.sha),
            text(body.relativePath)
          )
        };
      case 'branch-diff':
        return { handled: true, statusCode: 200, body: await readGitBranchDiff(text(body.root)) };
      case 'file-diff':
        return {
          handled: true,
          statusCode: 200,
          body: await readGitFileDiff(text(body.root), text(body.path))
        };
      default:
        // Everything that would CHANGE the repository lands here on purpose.
        return unhandled;
    }
  } catch (error) {
    return {
      handled: true,
      statusCode: 500,
      body: { error: error instanceof Error ? error.message : String(error) }
    };
  }
}

// ── plugging it into the dev server ─────────────────────────────────────────

/**
 * Minimal shapes for the node http objects and the vite plugin, written out
 * here so this file has no import of vite's types (it is imported by the vite
 * config, which is loaded before anything is built).
 */
interface BridgeRequest {
  url?: string;
  method?: string;
  [Symbol.asyncIterator](): AsyncIterableIterator<unknown>;
}

interface BridgeResponse {
  statusCode: number;
  setHeader(name: string, value: string): void;
  end(chunk?: string): void;
}

async function readJsonBody(request: BridgeRequest): Promise<Record<string, unknown>> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : (chunk as Buffer));
  }
  const raw = Buffer.concat(chunks).toString('utf8').trim();
  if (raw === '') return {};
  const parsed = JSON.parse(raw);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('The request body has to be a JSON object');
  }
  return parsed as Record<string, unknown>;
}

/**
 * The middleware the dev server mounts. Anything that is not one of our routes
 * is passed straight on, so this can sit in front of everything else.
 */
export function createGitBridgeMiddleware() {
  return async (request: BridgeRequest, response: BridgeResponse, next: () => void) => {
    const pathname = (request.url ?? '').split('?')[0];
    if (!pathname.startsWith(GIT_BRIDGE_ROUTE_PREFIX)) {
      next();
      return;
    }

    const send = (statusCode: number, body: unknown) => {
      response.statusCode = statusCode;
      response.setHeader('content-type', 'application/json; charset=utf-8');
      response.end(JSON.stringify(body));
    };

    if (request.method !== 'POST') {
      send(405, { error: 'The git bridge answers POST requests' });
      return;
    }

    try {
      const body = await readJsonBody(request);
      const answer = await handleGitBridgeRequest(pathname, body);
      if (!answer.handled) {
        send(404, { error: 'That is not a git bridge route. It reads the repository only.' });
        return;
      }
      send(answer.statusCode, answer.body);
    } catch (error) {
      send(500, { error: error instanceof Error ? error.message : String(error) });
    }
  };
}

/** What a vite plugin looks like, without importing vite's types. */
interface BridgeServer {
  middlewares: { use(handler: unknown): void };
}

/**
 * The plugin to add to `vite.config.ts`. One line, and the browser can read the
 * repository:
 *
 *     plugins: [gitBridgePlugin(), localSourceBridgePlugin(), …]
 */
export function gitBridgePlugin() {
  return {
    name: 'mac-command-bar-git-bridge',
    configureServer(server: BridgeServer) {
      server.middlewares.use(createGitBridgeMiddleware());
    },
    configurePreviewServer(server: BridgeServer) {
      server.middlewares.use(createGitBridgeMiddleware());
    }
  };
}
