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
// Tokenize shell words without executing expansion, substitution, or quoted commands.
function tokens(command: string) {
  const result: string[][] = [[]];
  let word = '', quote = '', started = false;
  const push = () => { if (started) result.at(-1)!.push(word); word = ''; started = false; };
  for (let i = 0; i < command.length; i++) {
    const c = command[i];
    if (c === '\\' && quote !== "'") { word += command[++i] || ''; started = true; continue; }
    if (quote) { if (c === quote) quote = ''; else word += c; started = true; continue; }
    if (c === '"' || c === "'") { quote = c; started = true; continue; }
    if (';&|\n'.includes(c)) { push(); if (result.at(-1)!.length) result.push([]); continue; }
    if (/\s/.test(c)) { push(); continue; }
    word += c; started = true;
  }
  push(); return { commands: result.filter(words => words.length), malformed: !!quote };
}
function expand(path: string) {
  return path.replace(/^~(?=\/|$)/, homedir()).replace(/^\$\{HOME\}(?=\/|$)|^\$HOME(?=\/|$)/, homedir());
}
export function checkWorktreeCommand(command: string, cwd: string): { deny?: string } {
  const parsed = tokens(command);
  let directory = cwd;
  const deny = () => ({ deny: 'Create or move worktrees only under ' + worktreeRoot() + '/<repo>/<name>; use agent-worktree create.' });
  for (const original of parsed.commands) {
    const words = [...original];
    while (['command', 'env', 'sudo'].includes(words[0]) || /^[A-Za-z_][A-Za-z_0-9]*=/.test(words[0] || '')) words.shift();
    if (words[0] === 'cd') { directory = resolve(directory, expand(words[1] || homedir())); continue; }
    if (!/(^|\/)git$/.test(words.shift() || '')) continue;
    let at = directory;
    let repositoryOverride = false;
    while (words[0]?.startsWith('-')) {
      const option = words.shift();
      if (option === '-C') at = resolve(at, expand(words.shift() || ''));
      else if (option === '-c' || option === '--git-dir' || option === '--work-tree') {
        if (option !== '-c') repositoryOverride = true;
        words.shift();
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
