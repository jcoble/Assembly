/**
 * commitSuggestion.ts — a first line for the commit box, worked out from the
 * staged files alone.
 *
 * THIS IS NOT A WRITTEN COMMIT MESSAGE AND DOES NOT PRETEND TO BE. It reads the
 * names and the status letters of what is staged and says what git itself can
 * see: what happened, to how many files, and where. No model is asked, nothing
 * leaves the machine, and it costs nothing — which is exactly why it can run on
 * every keystroke-free click of "Suggest" without anybody thinking about it.
 *
 * The panel already has a separate button that asks the running agent to write
 * a real message from the diff. This is the cheap one next to it, and the copy
 * around it says so: it is a starting point to edit, not a description.
 *
 * Pure: takes the staged files, returns a string. The test reads every branch.
 */

import type { ProjectGitFileStatus } from '$lib/tauriSource';

/** Git's subject line stops being readable in a log past about here. */
export const COMMIT_SUBJECT_LIMIT = 72;

/** Folders whose contents describe the change better than "update files" does. */
const TOPIC_FOLDERS: ReadonlyArray<[RegExp, string]> = [
  [/(^|\/)docs?(\/|$)/i, 'the docs'],
  [/(^|\/)tests?(\/|$)/i, 'the tests'],
  [/(^|\/)__tests__(\/|$)/i, 'the tests'],
  [/\.test\.[a-z]+$/i, 'the tests'],
  [/\.spec\.[a-z]+$/i, 'the tests']
];

function isStaged(file: ProjectGitFileStatus): boolean {
  return Boolean(file.indexStatus) && file.badge !== '?';
}

/** The verb the mix of status letters justifies. Mixed work is always "Update". */
export function suggestCommitVerb(files: ProjectGitFileStatus[]): string {
  if (files.length === 0) return 'Update';
  const badges = new Set(files.map((file) => file.badge));
  if (badges.size === 1) {
    const only = [...badges][0];
    if (only === 'A') return 'Add';
    if (only === 'D') return 'Remove';
    if (only === 'R') return 'Rename';
    if (only === 'C') return 'Copy';
  }
  return 'Update';
}

/** The deepest folder every staged file sits under, or '' when they are spread. */
export function commonPathPrefix(paths: string[]): string {
  if (paths.length === 0) return '';
  const split = paths.map((path) => path.split('/').slice(0, -1));
  let prefix = split[0];
  for (const parts of split.slice(1)) {
    let index = 0;
    while (index < prefix.length && index < parts.length && prefix[index] === parts[index]) index++;
    prefix = prefix.slice(0, index);
  }
  return prefix.join('/');
}

function topicFor(paths: string[]): string {
  for (const [pattern, topic] of TOPIC_FOLDERS) {
    if (paths.every((path) => pattern.test(path))) return topic;
  }
  return '';
}

function shorten(subject: string): string {
  if (subject.length <= COMMIT_SUBJECT_LIMIT) return subject;
  return `${subject.slice(0, COMMIT_SUBJECT_LIMIT - 1).trimEnd()}…`;
}

/**
 * The suggested subject line, or '' when nothing is staged — an empty string is
 * the honest answer there, and it leaves the commit box alone.
 */
export function suggestCommitMessage(files: ProjectGitFileStatus[]): string {
  const staged = files.filter(isStaged);
  if (staged.length === 0) return '';

  const paths = staged.map((file) => file.relativePath);
  const verb = suggestCommitVerb(staged);
  const topic = topicFor(paths);

  if (topic) {
    return shorten(`${verb} ${topic}`);
  }
  if (staged.length === 1) {
    return shorten(`${verb} ${paths[0]}`);
  }

  const prefix = commonPathPrefix(paths);
  const many = `${staged.length} files`;
  return shorten(prefix ? `${verb} ${many} in ${prefix}` : `${verb} ${many}`);
}

/** The sentence under the button, so nobody mistakes this for a description. */
export function describeCommitSuggestion(files: ProjectGitFileStatus[]): string {
  const staged = files.filter(isStaged);
  if (staged.length === 0) return 'Stage something first — a suggestion is read from the staged files.';
  return 'Read from the staged file names. Edit it before committing.';
}
