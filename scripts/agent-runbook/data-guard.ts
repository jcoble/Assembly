import { homedir } from 'node:os';
import { resolve, basename, dirname, isAbsolute, join } from 'node:path';
import { canonical } from './guard.ts';

// Jesse's live data folder: agents may read and copy out of it, never write to it.
export const DATA_DENY = "~/assembly-dotnet is Jesse's live data. Reads and copies out are allowed; writes are not. Make a test copy elsewhere and work on that.";
const NAME = 'assembly-dotnet';
const deny = () => ({ deny: DATA_DENY });

type Word = { text: string; dynamic: boolean };
type Redirect = { op: string; target?: Word; dup?: boolean };
type Simple = { words: Word[]; redirects: Redirect[]; piped: boolean; stdin: string };
type State = { dir: string; unknown: boolean; strict: boolean };

export function namesProtectedFolder(command: string) {
  return command.replace(/['"\\]/g, '').toLowerCase().includes(NAME);
}

// ---- Parsing: shell words without executing anything -------------------------------------------
function parse(command: string) {
  const home = homedir();
  const commands: Simple[] = [];
  const blank = (piped: boolean): Simple => ({ words: [], redirects: [], piped, stdin: '' });
  let cur = blank(false);
  let text = '', dynamic = false, started = false, quote = '', tilde = false, quoted = false, open = 0, close = 0;
  let pending: Redirect | undefined;
  let substitution = false, malformed = false;
  const heredocs: { delimiter: string; quoted: boolean; strip: boolean; into: Simple }[] = [];
  const reset = () => { text = ''; dynamic = false; started = false; tilde = false; quoted = false; open = 0; close = 0; };
  const pushWord = () => {
    if (!started) return;
    let t = text, dyn = dynamic;
    if (tilde) {
      const user = /^~([^/]*)/.exec(t)![1];
      if (user === '+' || user === '-') dyn = true; else t = home + t.slice(1 + user.length);
    }
    t = t.replace(/([=:])~(?=\/|$)/g, (_all, sign) => sign + home);
    if (open && close) dyn = true;
    const word: Word = { text: t, dynamic: dyn };
    if (pending) {
      pending.target = word;
      if (['>&', '<&'].includes(pending.op) && /^(\d+|-)$/.test(t)) pending.dup = true;
      if (pending.op === '<<' || pending.op === '<<-') heredocs.push({ delimiter: t, quoted, strip: pending.op === '<<-', into: cur });
      if (pending.op === '<<<') cur.stdin += t + '\n';
      pending = undefined;
    } else cur.words.push(word);
    reset();
  };
  const endCommand = (pipe: boolean) => {
    pushWord();
    if (pending) { malformed = true; pending = undefined; }
    if (cur.words.length || cur.redirects.length) { commands.push(cur); cur = blank(pipe); } else if (pipe) cur.piped = true;
  };
  const dollar = (i: number) => {
    const rest = command.slice(i + 1);
    started = true;
    if (rest.startsWith('(')) { substitution = true; dynamic = true; text += '$'; return i; }
    const home_ = /^\{HOME\}/.exec(rest) || /^HOME(?![A-Za-z0-9_])/.exec(rest);
    if (home_) { text += home; return i + home_[0].length; }
    dynamic = true; text += '$'; return i;
  };
  const operators = ['&>>', '&>', '<<<', '<<-', '<<', '<>', '<&', '>>', '>&', '>|', '>', '<'];
  for (let i = 0; i < command.length; i++) {
    const c = command[i];
    if (quote === "'") { if (c === "'") quote = ''; else text += c; continue; }
    if (quote === '"') {
      if (c === '"') quote = '';
      else if (c === '\\') {
        const n = command[i + 1];
        if (n && '$`"\\\n'.includes(n)) { if (n !== '\n') text += n; i++; } else text += c;
      } else if (c === '$') i = dollar(i);
      else if (c === '`') { substitution = true; dynamic = true; text += c; }
      else text += c;
      continue;
    }
    if (c === '#' && !started) { while (i + 1 < command.length && command[i + 1] !== '\n') i++; continue; }
    if (c === '\\') {
      const n = command[i + 1];
      if (n === undefined) { malformed = true; break; }
      if (n !== '\n') { text += n; started = true; quoted = true; }
      i++; continue;
    }
    if (c === "'" || c === '"') { quote = c; started = true; quoted = true; continue; }
    if (c === '`') { substitution = true; dynamic = true; started = true; text += c; continue; }
    if (c === '$') { i = dollar(i); continue; }
    if (c === '<' || c === '>' || (c === '&' && command[i + 1] === '>')) {
      if (started && !quoted && c !== '&' && /^\d+$/.test(text) && !pending) reset(); else pushWord();
      if (pending) { malformed = true; pending = undefined; }
      const op = operators.find(o => command.startsWith(o, i))!;
      if (op.length === 1 && command[i + 1] === '(') substitution = true; // process substitution
      i += op.length - 1;
      pending = { op }; cur.redirects.push(pending);
      continue;
    }
    if (c === '\n') {
      endCommand(false);
      let position = i + 1;
      for (const doc of heredocs.splice(0)) {
        let body = '', found = false;
        while (position <= command.length && !found) {
          const end = command.indexOf('\n', position);
          const line = command.slice(position, end < 0 ? command.length : end);
          position = end < 0 ? command.length + 1 : end + 1;
          if ((doc.strip ? line.replace(/^\t+/, '') : line) === doc.delimiter) found = true; else body += line + '\n';
          if (end < 0) break;
        }
        if (!found) malformed = true;
        doc.into.stdin += body;
        if (!doc.quoted && /\$\(|`/.test(body)) substitution = true;
      }
      i = position - 1;
      continue;
    }
    if (c === ';' || c === '&' || c === '(' || c === ')') { endCommand(false); continue; }
    if (c === '|') {
      if (command[i + 1] === '|') { i++; endCommand(false); } else { if (command[i + 1] === '&') i++; endCommand(true); }
      continue;
    }
    if (/\s/.test(c)) { pushWord(); continue; }
    if (c === '*' || c === '?' || c === '[') dynamic = true;
    if (c === '{') open++;
    if (c === '}') close++;
    if (!started && c === '~') tilde = true;
    text += c; started = true;
  }
  if (quote) malformed = true;
  endCommand(false);
  if (heredocs.length) malformed = true;
  return { commands, malformed, substitution };
}

// ---- Paths -------------------------------------------------------------------------------------
function safeCanonical(path: string) { try { return canonical(path); } catch { return resolve(path); } }
function roots() {
  const home = homedir();
  return [...new Set([join(home, NAME), join(safeCanonical(home), NAME), safeCanonical(join(home, NAME))].map(r => r.toLowerCase()))];
}
function insideFolder(path: string) {
  const forms = [resolve(path).toLowerCase(), safeCanonical(path).toLowerCase()];
  return roots().some(root => forms.some(form => form === root || form.startsWith(root + '/')));
}
function globMatches(segment: string, name: string) {
  if (/[${`]/.test(segment)) return true;
  let source = '';
  for (let i = 0; i < segment.length; i++) {
    const c = segment[i];
    if (c === '*') source += '.*';
    else if (c === '?') source += '.';
    else if (c === '[') {
      const end = segment.indexOf(']', i + 2);
      if (end < 0) source += '\\['; else { source += '[' + segment.slice(i + 1, end).replace(/^!/, '^').replaceAll('\\', '\\\\') + ']'; i = end; }
    } else source += c.replace(/[.+^${}()|\\\]]/g, '\\$&');
  }
  try { return new RegExp('^' + source + '$', 'i').test(name); } catch { return true; }
}
// How a concrete path relates to a protected root: inside it, an ancestor of it, or neither.
function relation(path: string, root: string) {
  for (const form of [resolve(path).toLowerCase(), safeCanonical(path).toLowerCase()]) {
    if (form === root || form.startsWith(root + '/')) return { kind: 'inside', form };
    if (form === '/' || root.startsWith(form + '/')) return { kind: 'ancestor', form };
  }
  return { kind: 'none', form: '' };
}
// Could this glob path reach the protected folder, an ancestor of it, or something inside it?
function globReaches(text: string, start: string, strict: boolean) {
  return roots().some(root => {
    let current = start, globbed = false;
    for (const segment of text.split('/')) {
      if (segment === '') continue;
      if (/[*?[\]${`]/.test(segment)) {
        if (!strict && !/[*?[]/.test(segment)) return false; // a bare variable only counts once the command names the folder
        const { kind, form } = relation(current, root);
        if (kind === 'inside') return true;
        if (kind === 'none') return false;
        const next = root.slice(form === '/' ? 1 : form.length + 1).split('/')[0];
        if (!globMatches(segment, next)) return false;
        current = join(form, next); globbed = true;
      } else current = resolve(current, segment);
    }
    const { kind } = relation(current, root);
    return kind === 'inside' || (kind === 'ancestor' && globbed);
  });
}
// Does this word name a path inside the protected folder (or one a glob could reach)?
function refers(word: Word, state: State) {
  const candidates = [word.text];
  const host = /^[A-Za-z0-9_.@-]+:(.+)$/.exec(word.text);
  if (host) candidates.push(host[1]);
  return candidates.some(text => {
    const absolute = isAbsolute(text);
    if (!absolute && state.unknown) return state.strict;
    if (!word.dynamic) return insideFolder(absolute ? text : resolve(state.dir, text));
    return globReaches(text, absolute ? '/' : state.dir, state.strict);
  });
}
// The folder named inside a non-path word, such as an ssh command string or a quoted message.
function embeds(text: string) {
  const home = homedir().toLowerCase().replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp('(?:^|[^\\w.-])(?:~|\\$\\{?home\\}?|' + home + ')/' + NAME + '(?![\\w.-])', 'i').test(text);
}
const mentions = (word: Word, state: State) => refers(word, state) || embeds(word.text);
const outside = (word: Word, state: State) => !word.dynamic && !refers(word, state);

// ---- Command classes ---------------------------------------------------------------------------
const ASSIGN = /^[A-Za-z_][A-Za-z0-9_]*\+?=/;
const WRAPPERS = new Set(['command', 'builtin', 'env', 'nohup', 'time', 'exec', '!', '{', 'if', 'then', 'else', 'elif', 'do', 'while', 'until']);
const BLOCKED = /^(eval|xargs|source|\.|parallel|ba?sh|z?sh|dash|ksh|fish|c?sh|tcsh|python[\d.]*|node(js)?|perl|ruby|php|deno|bun|osascript)$/;
const READ_ONLY = /^(ls|cat|head|tail|stat|du|file|wc|grep|rg|shasum|sha256sum|md5|md5sum)$/;
const DECLARE = /^(export|declare|typeset|local|readonly)$/;

function lead(cmd: Simple) {
  const assigns: Word[] = [];
  const words = [...cmd.words];
  while (words.length) {
    if (ASSIGN.test(words[0].text)) assigns.push(words.shift()!);
    else if (WRAPPERS.has(words[0].text)) words.shift();
    else break;
  }
  return { assigns, rest: words };
}

// Splits an option list into flags, positionals and values taken by value-taking options.
function options(args: Word[], shortValue: string, longValue: Set<string>) {
  const flags: string[] = [], positionals: Word[] = [], values: Word[] = [];
  let ended = false;
  for (let i = 0; i < args.length; i++) {
    const word = args[i], text = word.text;
    if (ended || text === '-' || !text.startsWith('-')) { positionals.push(word); continue; }
    if (text === '--') { ended = true; continue; }
    if (text.startsWith('--')) {
      const [name, ...rest] = text.split('=');
      flags.push(name);
      if (rest.length) values.push({ text: rest.join('='), dynamic: word.dynamic });
      else if (longValue.has(name) && args[i + 1]) values.push(args[++i]);
      continue;
    }
    for (let j = 1; j < text.length; j++) {
      flags.push('-' + text[j]);
      if (shortValue.includes(text[j])) {
        if (j + 1 < text.length) values.push({ text: text.slice(j + 1), dynamic: word.dynamic });
        else if (args[i + 1]) values.push(args[++i]);
        break;
      }
    }
  }
  return { flags, positionals, values };
}

const anyIn = (flags: string[], list: (string | RegExp)[]) => flags.some(flag => list.some(item => typeof item === 'string' ? flag === item : item.test(flag)));

function checkReadOnly(name: string, args: Word[]) {
  if (name === 'rg') return args.some(a => /^--(pre|hostname-bin)(=|$)/.test(a.text));
  if (name === 'file') return args.some(a => a.text === '--compile' || /^-[a-zA-Z]*C/.test(a.text));
  return false;
}
function checkFind(args: Word[]) {
  return args.some(a => ['-delete', '-exec', '-execdir', '-ok', '-okdir', '-fprint', '-fprint0', '-fprintf', '-fls'].includes(a.text));
}
// cp: the protected folder may only be a source.
function checkCopy(args: Word[], state: State) {
  const { flags, positionals, values } = options(args, 'tS', new Set(['--target-directory', '--suffix']));
  if (anyIn(flags, [/^-[ls]$/, '--link', '--symbolic-link'])) return true;
  const target = anyIn(flags, ['-t', '--target-directory']) ? values[0] : undefined;
  const destination = target || (positionals.length >= 2 ? positionals[positionals.length - 1] : undefined);
  if (!destination || !outside(destination, state)) return true;
  const sources = target ? positionals : positionals.slice(0, -1);
  if (!sources.length) return true;
  return values.filter(v => v !== target).some(v => mentions(v, state)) || sources.some(s => !refers(s, state) && embeds(s.text));
}
const RSYNC_VALUE = new Set(['--rsh', '--rsync-path', '--exclude', '--include', '--exclude-from', '--include-from', '--files-from', '--filter', '--log-file', '--partial-dir', '--temp-dir', '--backup-dir', '--compare-dest', '--copy-dest', '--out-format', '--port', '--timeout', '--contimeout', '--address', '--sockopts', '--chmod', '--usermap', '--groupmap', '--chown', '--max-size', '--min-size', '--suffix', '--read-batch', '--write-batch', '--only-write-batch', '--password-file', '--iconv', '--checksum-choice', '--skip-compress', '--info', '--debug', '--block-size', '--bwlimit', '--modify-window', '--max-delete', '--compress-level', '--compress-choice']);
function checkRsync(args: Word[], state: State) {
  const { flags, positionals, values } = options(args, 'efTBM@', RSYNC_VALUE);
  if (anyIn(flags, ['-e', /^--(rsh|rsync-path|remove-source-files|remove-sent-files|delete.*|link-dest|daemon|server)$/])) return true;
  if (positionals.some(p => p.text.startsWith('rsync://'))) return true;
  if (values.some(v => mentions(v, state))) return true;
  if (positionals.length < 2) return false;
  const destination = positionals[positionals.length - 1];
  return /^[^/\s]+:/.test(destination.text) || !outside(destination, state);
}
const TAR_VALUE = 'fCTXINLbsF';
function checkTar(args: Word[], state: State) {
  const flags: string[] = [], directories: Word[] = [];
  let archive: Word | undefined;
  const take = (letter: string, word: Word) => { if (letter === 'f') archive = word; if (letter === 'C') directories.push(word); };
  const longValue: Record<string, string> = { '--file': 'f', '--directory': 'C' };
  let i = 0;
  if (args[0] && !args[0].text.startsWith('-') && /^[A-Za-z]+$/.test(args[0].text)) {
    // Old style: each value letter consumes the following words in order.
    for (const letter of args[0].text) { flags.push('-' + letter); if (TAR_VALUE.includes(letter) && args[i + 1]) take(letter, args[++i]); }
    i++;
  }
  for (; i < args.length; i++) {
    const text = args[i].text;
    if (text === '--') break;
    if (!text.startsWith('-') || text === '-') continue;
    if (text.startsWith('--')) {
      const [name, ...rest] = text.split('=');
      flags.push(name);
      const value = rest.length ? { text: rest.join('='), dynamic: args[i].dynamic } : longValue[name] ? args[++i] : undefined;
      if (value && longValue[name]) take(longValue[name], value);
      continue;
    }
    for (let j = 1; j < text.length; j++) {
      flags.push('-' + text[j]);
      if (TAR_VALUE.includes(text[j])) {
        const value = j + 1 < text.length ? { text: text.slice(j + 1), dynamic: args[i].dynamic } : args[++i];
        if (value) take(text[j], value);
        break;
      }
    }
  }
  return tarJudge(flags, archive, directories, state);
}
function tarJudge(flags: string[], archive: Word | undefined, directories: Word[], state: State) {
  const create = anyIn(flags, ['-c', '--create']);
  if (!create || anyIn(flags, ['-x', '-r', '-u', '-A', '-d', '-I', '-F', '-t', '--extract', '--get', '--append', '--update', '--catenate', '--concatenate', '--diff', '--compare', '--delete', '--remove-files', '--use-compress-program', '--to-command', '--checkpoint-action', '--info-script', '--new-volume-script', '--rsh-command'])) return true;
  if (!archive) return true;
  if (archive.text === '-') return false;
  if (!outside(archive, state)) return true;
  // -C changes where members are read from, and could change where a relative archive lands.
  return directories.some(directory => !isAbsolute(archive.text) && !outside({ text: join(directory.text, archive.text), dynamic: directory.dynamic }, state));
}

const ALLOWED_DOT = new Set(['.backup', '.dump', '.schema', '.tables', '.indexes', '.headers', '.header', '.mode', '.databases', '.fullschema', '.show', '.width', '.separator', '.nullvalue', '.timeout', '.stats', '.dbinfo', '.help', '.quit', '.exit', '.print', '.echo', '.bail', '.eqp']);
const SQL_WRITE = /\b(insert|update|delete|drop|create|alter|attach|detach|vacuum|reindex|analyze|savepoint|release|load_extension|replace(?!\s*\())\b/i;
const SQLITE_FLAGS = new Set(['-readonly', '-batch', '-bail', '-echo', '-header', '-noheader', '-csv', '-column', '-json', '-line', '-list', '-html', '-markdown', '-table', '-box', '-quote', '-tabs', '-ascii', '-interactive', '-stats', '-version', '-help', '-nofollow', '-memdb', '-utf8']);
const SQLITE_VALUE = new Set(['-separator', '-newline', '-nullvalue', '-vfs', '-mmap', '-maxsize', '-lookaside']);
function sqlSafe(sql: string, state: State) {
  for (const raw of sql.split('\n')) {
    const line = raw.trim();
    if (!line) continue;
    if (!line.startsWith('.')) {
      if (SQL_WRITE.test(line) || (/\bpragma\b/i.test(line) && line.includes('='))) return false;
      continue;
    }
    const parts = [...line.matchAll(/"([^"]*)"|'([^']*)'|(\S+)/g)].map(m => m[1] ?? m[2] ?? m[3]);
    const dot = parts[0].toLowerCase();
    if (!ALLOWED_DOT.has(dot)) return false;
    if (dot === '.backup') {
      const files = parts.slice(1).filter(p => !p.startsWith('--'));
      const file = files[files.length - 1];
      if (!file) return false;
      const home = homedir();
      const text = file.replace(/^~(?=\/|$)/, home).replace(/^\$\{?HOME\}?(?=\/|$)/, home);
      if (!outside({ text, dynamic: /[*?[${`]/.test(text) }, state)) return false;
    }
  }
  return true;
}
function checkSqlite(cmd: Simple, args: Word[], state: State) {
  let readonly = false;
  const positionals: Word[] = [], sql: Word[] = [];
  let ended = false;
  for (let i = 0; i < args.length; i++) {
    const text = args[i].text;
    if (!ended && text === '--') { ended = true; continue; }
    if (ended || !text.startsWith('-') || text === '-') { positionals.push(args[i]); continue; }
    const flag = text.replace(/^--/, '-');
    if (flag === '-readonly') readonly = true;
    else if (flag === '-cmd') { if (!args[i + 1]) return true; sql.push(args[++i]); }
    else if (SQLITE_VALUE.has(flag)) i++;
    else if (!SQLITE_FLAGS.has(flag)) return true;
  }
  if (!readonly) return true;
  sql.push(...positionals.slice(1));
  if (sql.some(w => w.dynamic)) return true;
  const hasStdin = cmd.stdin !== '';
  if (positionals.length < 2 && !hasStdin && !sql.length && (cmd.piped || cmd.redirects.some(r => r.op === '<'))) return true;
  if (cmd.redirects.some(r => r.op === '<' || r.op === '<>')) return true;
  return ![...sql.map(w => w.text), cmd.stdin].every(text => sqlSafe(text, state));
}

// ---- Entry point -------------------------------------------------------------------------------
export function checkDataCommand(command: string, cwd: string): { deny?: string } {
  const named = namesProtectedFolder(command) || (!!cwd && insideFolder(cwd));
  if (named) return evaluate(command, cwd, true).result;
  // $? is a number, so it can't reach the folder even though it contains a glob character.
  const globbed = command.replaceAll('$?', '0');
  if (!/[*?[]/.test(globbed)) return {};
  // A glob can reach the folder without naming it. Only a command it reaches gets the full strict check.
  const probe = evaluate(globbed, cwd, false);
  return probe.result.deny || !probe.touched ? probe.result : evaluate(globbed, cwd, true).result;
}
function evaluate(command: string, cwd: string, strict: boolean): { result: { deny?: string }; touched: boolean } {
  const blocked = { result: deny(), touched: true };
  const parsed = parse(command);
  if (strict && (parsed.malformed || parsed.substitution)) return blocked;
  const state: State = { dir: cwd || process.cwd(), unknown: false, strict };
  const leads = parsed.commands.map(lead);
  if (strict) for (const { rest } of leads) {
    if (rest.length && BLOCKED.test(basename(rest[0].text))) return blocked;
  }
  let touched = false;
  for (const [index, cmd] of parsed.commands.entries()) {
    const { assigns, rest } = leads[index];
    if (assigns.some(a => namesProtectedFolder(a.text))) return blocked;
    const writes = cmd.redirects.filter(r => r.target && !r.dup && !['<', '<&', '<<', '<<-', '<<<'].includes(r.op));
    if (writes.some(r => refers(r.target!, state))) return blocked;
    if (!rest.length) continue;
    const [first, ...args] = rest;
    const name = basename(first.text);
    if (['cd', 'pushd'].includes(first.text)) {
      const target = args.find(a => !['-L', '-P', '-e', '-@', '--'].includes(a.text));
      if (!target) { state.dir = homedir(); state.unknown = false; }
      else if (target.dynamic || target.text === '-') state.unknown = true;
      else if (isAbsolute(target.text)) { state.dir = target.text; state.unknown = false; }
      else if (!state.unknown) state.dir = resolve(state.dir, target.text);
      continue;
    }
    if (first.text === 'popd') { state.unknown = true; continue; }
    if (DECLARE.test(first.text) && args.some(a => ASSIGN.test(a.text) && namesProtectedFolder(a.text))) return blocked;
    const redirectTargets = cmd.redirects.filter(r => r.target && !r.dup && !['<<', '<<-', '<<<'].includes(r.op)).map(r => r.target!);
    const touches = (strict && state.unknown) || insideFolder(state.dir) || rest.some(w => mentions(w, state)) || redirectTargets.some(w => mentions(w, state));
    if (!touches) continue;
    touched = true;
    if (first.dynamic || (first.text.includes('/') && refers(first, state))) return blocked;
    if (writes.some(r => r.target!.dynamic)) return blocked;
    if (['for', 'select'].includes(first.text)) return blocked;
    let bad: boolean;
    if (READ_ONLY.test(name)) bad = checkReadOnly(name, args);
    else if (name === 'find') bad = checkFind(args);
    else if (name === 'cp') bad = checkCopy(args, state);
    else if (name === 'rsync') bad = checkRsync(args, state);
    else if (name === 'tar') bad = checkTar(args, state);
    else if (name === 'sqlite3') bad = checkSqlite(cmd, args, state);
    else bad = true;
    if (bad) return blocked;
  }
  return { result: {}, touched };
}
