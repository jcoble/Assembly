import { homedir } from 'node:os';
import { resolve, relative, isAbsolute, dirname, join } from 'node:path';
import { existsSync, realpathSync } from 'node:fs';

export const worktreeRoot = () => join(homedir(), 'dev/work/worktrees');
export function canonical(path: string): string {
  const resolved = resolve(path);
  return existsSync(resolved) ? realpathSync(resolved) : resolve(canonical(dirname(resolved)), resolved.slice(dirname(resolved).length + 1));
}
export function inside(path: string, root = worktreeRoot()) {
  const part = relative(canonical(root), canonical(path));
  return part !== '' && part !== '..' && !part.startsWith('../') && !isAbsolute(part);
}
// Split shell command boundaries while keeping quoted text and ${HOME} intact.
function tokens(command: string) {
  const result: { words: string[]; bare: string[] }[] = [{ words: [], bare: [] }];
  const heredocs: { delimiter: string; tabs: boolean; expands: boolean }[] = [];
  let lineStart = 0, malformedBody = false;
  let word = '', quote = '', started = false, quoted = false;
  const push = () => { if (started) { result.at(-1)!.words.push(word); if (!quoted) result.at(-1)!.bare.push(word); } word = ''; started = quoted = false; };
  for (let i = 0; i < command.length; i++) {
    const c = command[i];
    if (c === '\\' && quote !== "'") { word += command[++i] || ''; started = true; continue; }
    if (quote) { if (c === quote) quote = ''; else word += c; started = true; continue; }
    if (c === '"' || c === "'") { quote = c; started = quoted = true; continue; }
    if (command.startsWith('<<<', i)) { push(); i += 2; continue; }
    if (c === '<' && command[i + 1] === '<') {
      const match = /^<<(-)?[ \t]*((?:'[^']*'|"[^"]*"|\\.|[^\s;&|<>])+)/.exec(command.slice(i));
      if (match) {
        push(); heredocs.push({ delimiter: match[2].replace(/['"]|\\(?=.)/g, ''), tabs: !!match[1], expands: !/['"\\]/.test(match[2]) });
        i += match[0].length - 1; continue;
      }
    }
    if (c === '\n' && heredocs.length) {
      push();
      const shell = result.slice(lineStart).some(part => /(^|\/)(bash|sh|zsh|dash)$|^(source|\.)$/.test(part.words.find(word => !['command', 'env', 'sudo', 'exec', 'nohup', 'time'].includes(word) && !/^[A-Za-z_][A-Za-z_0-9]*=/.test(word)) || ''));
      for (const doc of heredocs.splice(0)) {
        let body = '', closed = false;
        while (i < command.length) {
          const end = command.indexOf('\n', i + 1), stop = end < 0 ? command.length : end;
          const line = command.slice(i + 1, stop); i = stop;
          if ((doc.tabs ? line.replace(/^\t+/, '') : line) === doc.delimiter) { closed = true; break; }
          body += line + '\n';
        }
        if (shell || (doc.expands && /\$\(|`/.test(body))) { const parsed = tokens(body); result.push(...parsed.commands); malformedBody ||= parsed.malformed || !closed; }
      }
      result.push({ words: [], bare: [] }); lineStart = result.length - 1; continue;
    }
    if (c === '$' && command[i + 1] === '(') { i++; push(); result.push({ words: [], bare: [] }); continue; }
    if (';&|\n()`'.includes(c)) { push(); if (result.at(-1)!.words.length) result.push({ words: [], bare: [] }); if (c === '\n') lineStart = result.length - 1; continue; }
    if (/\s/.test(c)) { push(); continue; }
    word += c; started = true;
  }
  push(); return { commands: result.filter(part => part.words.length), malformed: !!quote || malformedBody };
}
function expand(path: string) {
  return path.replace(/^~(?=\/|$)/, homedir()).replace(/^\$\{HOME\}(?=\/|$)|^\$HOME(?=\/|$)/, homedir());
}
export function checkWorktreeCommand(command: string, cwd: string): { deny?: string } {
  const parsed = tokens(command);
  let directory = cwd;
  const deny = () => ({ deny: 'Create or move worktrees only under ' + worktreeRoot() + '/<repo>/<name>; use agent-worktree create.' });
  commands: for (const original of parsed.commands) {
    const words = [...original.words];
    while (['command', 'env', 'sudo', '{', '}', '!', 'if', 'then', 'elif', 'else', 'do', 'while', 'until', 'time', 'nohup', 'exec', 'builtin', 'xargs'].includes(words[0]) || /^[A-Za-z_][A-Za-z_0-9]*=/.test(words[0] || '')) {
      if (words.shift() !== 'env') continue;
      while (words[0]?.startsWith('-')) {
        const option = words.shift();
        if (option === '--') break;
        if (option === '-i' || option === '--ignore-environment') continue;
        if (option?.startsWith('--unset=') || /^-u.+/.test(option || '')) continue;
        if (option?.startsWith('--chdir=') || /^-C.+/.test(option || '')) { directory = resolve(directory, expand(option!.replace(/^(--chdir=|-C)/, ''))); continue; }
        if ((option === '-C' || option === '--chdir') && words[0]) { directory = resolve(directory, expand(words.shift()!)); continue; }
        if ((option === '-u' || option === '--unset') && words.shift()) continue;
        if (/\bworktree\s+(add|move)\b/.test(original.words.join(' '))) return deny();
        continue commands;
      }
    }
    if (words[0] === 'eval' || /(^|\/)(bash|sh|zsh|dash)$/.test(words[0] || '')) {
      const evalText = words.shift() === 'eval', flag = evalText ? -1 : words.findIndex(word => /^-[^-]*c/.test(word));
      if (evalText || flag >= 0) {
        const text = evalText ? words.join(' ') : words[flag + 1];
        if (!text || parsed.malformed || checkWorktreeCommand(text, directory).deny) return deny();
        continue;
      }
    }
    const git = /(^|\/)git$/.test(words[0] || '');
    if (!git && original.bare.some((word, i, bare) => /git$/.test(word) && bare.slice(i + 1).some((next, j, rest) => next === 'worktree' && rest.slice(j + 1).some(op => op === 'add' || op === 'move')))) return deny();
    if (words[0] === 'cd') { directory = resolve(directory, expand(words[1] || homedir())); continue; }
    if (!git) continue;
    words.shift();
    let at = directory;
    let repositoryOverride = false;
    while (words[0]?.startsWith('-')) {
      const option = words.shift();
      if (/^-c(?:alias\.)/.test(option || '') && option!.includes('worktree')) return deny();
      if (option === '-C') at = resolve(at, expand(words.shift() || ''));
      else if (option === '-c' || option === '--git-dir' || option === '--work-tree') {
        if (option !== '-c') repositoryOverride = true;
        const value = words.shift();
        if (option === '-c' && /^alias\./.test(value || '') && value!.includes('worktree')) return deny();
      } else if (option?.startsWith('--git-dir=') || option?.startsWith('--work-tree=')) repositoryOverride = true;
      else if (option?.startsWith('-C')) at = resolve(at, expand(option.slice(2)));
    }
    if (words.shift() !== 'worktree') continue;
    const operation = words.shift();
    if (!['add', 'move'].includes(operation || '')) continue;
    if (parsed.malformed || repositoryOverride || /[\$`*?{}()]/.test(at)) return deny();
    const paths: string[] = [];
    let positional = false;
    for (let i = 0; i < words.length; i++) {
      const word = words[i];
      if (!positional && word === '--') { positional = true; continue; }
      if (!positional && ['-b', '-B', '--reason'].includes(word)) { i++; continue; }
      if (!positional && word.startsWith('-')) continue;
      paths.push(word);
    }
    const target = paths[operation === 'move' ? 1 : 0];
    if (!target) return deny();
    const expanded = expand(target);
    if (/[\$`*?{}()]/.test(expanded) || !inside(resolve(at, expanded))) return deny();
  }
  return {};
}
