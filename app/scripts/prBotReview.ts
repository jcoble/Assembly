// Waits for the Codex review bot to finish reviewing this branch's pull request at its current head,
// then prints every unresolved bot thread. Exit 0: the head is reviewed and nothing is open.
// Exit 1: open findings, printed with the commands to reply to and resolve each one.
// Exit 2: nothing is open, but the bot did not review the head, even after one fresh request.
// Run from the branch's worktree: node --experimental-strip-types tauri-svelte-preview/scripts/prBotReview.ts
import { execFileSync } from 'node:child_process';

const BOT = 'chatgpt-codex-connector[bot]';
// Two waits of 15 minutes keep the whole run near 30 minutes, well inside the workflow step timeouts.
const WAIT_MINUTES = 15;
const POLL_SECONDS = 30;
const POLLS = (WAIT_MINUTES * 60) / POLL_SECONDS;

type ReviewState = { commit_sha: string | null; status: string | null };
type Thread = { id: string; isResolved: boolean; comments: { nodes: Array<{ databaseId: number; author: { __typename: string; login: string } | null; path: string; line: number | null; originalLine: number | null; body: string }> } };

function gh(args: string[]): string {
  return execFileSync('gh', args, { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
}

// The bot keeps one summary comment whose hidden marker carries the commit it last reviewed.
export function reviewState(commentBodies: string[]): ReviewState | null {
  for (const body of commentBodies.toReversed()) {
    const marker = body.match(/codex-review-state:v1 (\{.*?\}) -->/s);
    if (marker) return JSON.parse(marker[1]).code_review as ReviewState;
  }
  return null;
}

// GraphQL reports a bot's login without the "[bot]" suffix REST adds, so check the account type as well.
export function openBotThreads(threads: Thread[]): Thread[] {
  return threads.filter((thread) => {
    const author = thread.comments.nodes[0]?.author;
    return !thread.isResolved && author?.__typename === 'Bot' && `${author.login}[bot]` === BOT;
  });
}

function main(): void {
  const repo = gh(['repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner']).trim();
  const [owner, name] = repo.split('/');
  const pr = JSON.parse(gh(['pr', 'view', '--json', 'number,headRefOid'])) as { number: number; headRefOid: string };
  const head = pr.headRefOid;
  const botComments = () => JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${pr.number}/comments`]))
    .flat()
    .filter((comment: { user: { login: string } }) => comment.user.login === BOT)
    .map((comment: { body: string }) => comment.body) as string[];

  const askForReview = () => {
    gh(['pr', 'comment', String(pr.number), '--body', '@codex review']);
    console.log(`Asked the review bot to review ${head.slice(0, 9)}.`);
  };
  const waitForReview = () => {
    for (let poll = 0; poll < POLLS; poll++) {
      const state = reviewState(botComments());
      if (state?.commit_sha === head && state.status === 'completed') return true;
      execFileSync('sleep', [String(POLL_SECONDS)]);
    }
    return false;
  };

  // A new PR is reviewed on its own; a later push needs an explicit request.
  const before = reviewState(botComments());
  if (before && before.commit_sha !== head) askForReview();
  let reviewed = waitForReview();
  // The bot sometimes stays "running" on a head and never posts; one fresh request usually gets it going.
  if (!reviewed) {
    console.log(`The review bot did not finish ${head.slice(0, 9)} within ${WAIT_MINUTES} minutes.`);
    askForReview();
    reviewed = waitForReview();
  }

  const query = `query($owner:String!,$name:String!,$number:Int!,$endCursor:String){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100,after:$endCursor){pageInfo{hasNextPage endCursor} nodes{id isResolved comments(first:1){nodes{databaseId author{__typename login} path line originalLine body}}}}}}}`;
  const pages = JSON.parse(gh(['api', 'graphql', '--paginate', '--slurp', '-f', `query=${query}`, '-F', `owner=${owner}`, '-F', `name=${name}`, '-F', `number=${pr.number}`])) as Array<{ data: { repository: { pullRequest: { reviewThreads: { nodes: Thread[] } } } } }>;
  const open = openBotThreads(pages.flatMap((page) => page.data.repository.pullRequest.reviewThreads.nodes));
  if (open.length === 0) {
    if (reviewed) {
      console.log(`The review bot reviewed ${head.slice(0, 9)} and has no open findings.`);
    } else {
      console.log(`The review bot did not review ${head.slice(0, 9)} after a second request and ${WAIT_MINUTES} more minutes. Nothing is open, but the head is unreviewed.`);
      process.exitCode = 2;
    }
    return;
  }
  console.log(`The review bot has ${open.length} open finding(s) on PR #${pr.number}:\n`);
  for (const thread of open) {
    const comment = thread.comments.nodes[0];
    console.log(`--- ${comment.path}:${comment.line ?? comment.originalLine}`);
    console.log(comment.body.trim());
    console.log(`Reply:   gh api -X POST repos/${repo}/pulls/${pr.number}/comments/${comment.databaseId}/replies -f body='<what you did>'`);
    console.log(`Resolve: gh api graphql -f query='mutation{resolveReviewThread(input:{threadId:"${thread.id}"}){thread{isResolved}}}'\n`);
  }
  process.exitCode = 1;
}

if (process.argv[1]?.endsWith('prBotReview.ts')) main();
