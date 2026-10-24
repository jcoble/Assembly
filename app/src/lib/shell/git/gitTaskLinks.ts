export type GitTaskID = string;

export type GitTaskLink = {
  id: GitTaskID;
  label: string;
  href?: string;
};

export type GitTaskSearchTarget = {
  kind: 'notion-search';
  id: GitTaskID;
  label: string;
  query: string;
};

export type GitTaskLinkOptions = {
  baseURL?: string | URL | null;
};

const taskIDPattern = /(?<![A-Za-z0-9_])TSK-(\d+(?:-\d+)*)(?![A-Za-z0-9_])/gi;
const humanTaskPattern = /(?<![A-Za-z0-9_])task\s+#?(\d+)(?![A-Za-z0-9_])/gi;
const exactTaskIDPattern = /^\[?\s*(TSK-\d+)\s*\]?$/i;

type GitTaskMatch = {
  index: number;
  taskIDs: GitTaskID[];
};

export function normalizeGitTaskID(input: string | null | undefined): GitTaskID | null {
  const match = exactTaskIDPattern.exec(String(input ?? '').trim());
  return match ? match[1].toUpperCase() : null;
}

export function extractGitTaskIDs(input: string | null | undefined): GitTaskID[] {
  const text = String(input ?? '');
  const seen = new Set<GitTaskID>();
  const taskIDs: GitTaskID[] = [];
  const matches = [...matchExplicitGitTaskIDs(text), ...matchHumanGitTaskIDs(text)].sort(
    (left, right) => left.index - right.index
  );

  for (const match of matches) {
    for (const taskID of match.taskIDs) {
      if (seen.has(taskID)) continue;

      seen.add(taskID);
      taskIDs.push(taskID);
    }
  }

  return taskIDs;
}

export function buildGitTaskLink(
  input: string | null | undefined,
  options: GitTaskLinkOptions = {}
): GitTaskLink | null {
  const taskID = normalizeGitTaskID(input) ?? extractGitTaskIDs(input)[0] ?? null;
  if (!taskID) return null;

  const href = buildGitTaskHref(taskID, options.baseURL);
  return href ? { id: taskID, label: taskID, href } : { id: taskID, label: taskID };
}

export function buildGitTaskLinksFromText(
  input: string | null | undefined | readonly (string | null | undefined)[],
  options: GitTaskLinkOptions = {}
): GitTaskLink[] {
  const inputs = Array.isArray(input) ? input : [input];
  const taskIDs = uniqueGitTaskIDs(inputs.flatMap((item) => extractGitTaskIDs(item)));
  return taskIDs.map((taskID) => buildGitTaskLink(taskID, options)).filter(isGitTaskLink);
}

export function buildGitTaskSearchTarget(input: string | null | undefined): GitTaskSearchTarget | null {
  const taskID = normalizeGitTaskID(input) ?? extractGitTaskIDs(input)[0] ?? null;
  return taskID ? gitTaskSearchTarget(taskID) : null;
}

export function buildGitTaskSearchTargetsFromText(
  input: string | null | undefined | readonly (string | null | undefined)[]
): GitTaskSearchTarget[] {
  const inputs = Array.isArray(input) ? input : [input];
  return uniqueGitTaskIDs(inputs.flatMap((item) => extractGitTaskIDs(item))).map(gitTaskSearchTarget);
}

function matchExplicitGitTaskIDs(text: string): GitTaskMatch[] {
  return [...text.matchAll(taskIDPattern)].flatMap((match) => {
    const taskIDs = String(match[1] ?? '')
      .split('-')
      .filter(Boolean)
      .map((taskID) => `TSK-${Number.parseInt(taskID, 10)}`);

    return taskIDs.length > 0 ? [{ index: match.index ?? 0, taskIDs }] : [];
  });
}

function matchHumanGitTaskIDs(text: string): GitTaskMatch[] {
  return [...text.matchAll(humanTaskPattern)].map((match) => ({
    index: match.index ?? 0,
    taskIDs: [`TSK-${Number.parseInt(match[1] ?? '', 10)}`]
  }));
}

function gitTaskSearchTarget(taskID: GitTaskID): GitTaskSearchTarget {
  return {
    kind: 'notion-search',
    id: taskID,
    label: taskID,
    query: taskID
  };
}

function uniqueGitTaskIDs(taskIDs: GitTaskID[]): GitTaskID[] {
  const seen = new Set<GitTaskID>();
  const uniqueTaskIDs: GitTaskID[] = [];

  for (const taskID of taskIDs) {
    if (seen.has(taskID)) continue;

    seen.add(taskID);
    uniqueTaskIDs.push(taskID);
  }

  return uniqueTaskIDs;
}

function buildGitTaskHref(taskID: GitTaskID, baseURL: string | URL | null | undefined): string | null {
  const rawBaseURL = String(baseURL ?? '').trim();
  if (!rawBaseURL) return null;

  try {
    const url = new URL(rawBaseURL);
    const basePath = url.pathname.endsWith('/') ? url.pathname : `${url.pathname}/`;
    url.pathname = `${basePath}${encodeURIComponent(taskID)}`;
    return url.toString();
  } catch {
    return null;
  }
}

function isGitTaskLink(link: GitTaskLink | null): link is GitTaskLink {
  return link !== null;
}
