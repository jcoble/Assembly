<script lang="ts">
  /**
   * The Pull requests tab: a searchable list, and one pull request at a time
   * as a Summary page (description, activity, comment box, merge card, and a
   * Threads / Comments / Reviews / Checks column) or a Changes page (every
   * changed file stacked in the multi-file diff).
   */
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  import { untrack } from 'svelte';
  import { hydrateProjects, projectRegistry } from '$lib/shell/projects/projectRegistry.svelte';
  import { rail } from '$lib/shell/stores/sessionRailStore.svelte';
  import { parseRemoteWorkspacePath, remoteWorkspacePath, sessionWorkspaceRoot } from '$lib/workspacePaths';
  import { requestOpenFile } from '$lib/shell/openFileBus';
  import {
    commentGithubPullRequestFromTauri,
    listGithubPullRequestsFromTauri,
    mergeGithubPullRequestFromTauri,
    readGithubPullRequestFromTauri,
    readGithubPullRequestFileFromTauri,
    replyGithubPullRequestCommentFromTauri,
    runHelperJobFromTauri,
    submitGithubPullRequestReviewFromTauri,
    type GithubReviewSubmission,
    type GithubMergeRequest,
    type GithubPullRequestDetail,
    type GithubPullRequestSummary,
    type SourceGitDiff
  } from '$lib/tauriSource';
  import type { PullRequestLink } from '$lib/shell/workbenchNavigation';
  import type { DiffMode } from '$lib/shell/sessionWorkspaces';
  import { sessionRowJump } from '$lib/shell/components/sessionRowJump';
  import { formatAge, exactLocalTime } from '$lib/shell/relativeTime';
  import { pullRequestSelection } from './pullRequestSelection.svelte';
  import { pullRequestSearchQuery, sessionProjectFilter, type PullRequestScope, type PullRequestState } from './pullRequestSearch';
  import ConversationMessage from '$lib/shell/conversation/components/ConversationMessage.svelte';
  import MultiFileDiff from '$lib/shell/git/components/MultiFileDiff.svelte';
  import { Button, buttonVariants } from '$lib/components/ui/button/index.js';
  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import { Input } from '$lib/components/ui/input/index.js';
  import { SegmentedControl } from '$lib/components/ui/segmented-control/index.js';
  import * as Select from '$lib/components/ui/select/index.js';
  import ArrowRight from '@lucide/svelte/icons/arrow-right';
  import ChevronLeft from '@lucide/svelte/icons/chevron-left';
  import ExternalLink from '@lucide/svelte/icons/external-link';
  import GitCommitHorizontal from '@lucide/svelte/icons/git-commit-horizontal';
  import GitMerge from '@lucide/svelte/icons/git-merge';
  import GitPullRequest from '@lucide/svelte/icons/git-pull-request';
  import GitPullRequestClosed from '@lucide/svelte/icons/git-pull-request-closed';
  import GitPullRequestDraft from '@lucide/svelte/icons/git-pull-request-draft';
  import Link from '@lucide/svelte/icons/link';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';

  let { showing }: { showing: boolean } = $props();

  const STATE_ITEMS = [
    { value: 'open', label: 'Open' },
    { value: 'merged', label: 'Merged' },
    { value: 'closed', label: 'Closed' },
    { value: 'all', label: 'All' }
  ];
  const DIFF_MODES = [
    { value: 'unified', label: 'Unified' },
    { value: 'side-by-side', label: 'Side by side' }
  ];
  const SCOPE_LABELS: Record<PullRequestScope, string> = { everyone: 'Everyone', mine: 'Mine', 'needs-review': 'Needs my review' };

  // ── The list ──────────────────────────────────────────────────────────────
  let stateFilter = $state<PullRequestState>('all');
  let scope = $state<PullRequestScope>('everyone');
  let projectFilter = $state('');
  /** The session the project filter last followed; a new active session picks its project again. */
  let followedSession: string | null | undefined;
  let searchInput = $state('');
  /** The text the current list was searched with, so typing the same text again does not reload. */
  let searchedText = '';
  let searchTimer: ReturnType<typeof setTimeout> | undefined;
  let items = $state<GithubPullRequestSummary[]>([]);
  let cursor = $state<string | null>(null);
  let totalCount = $state(0);
  let loading = $state(false);
  let loaded = $state(false);
  let error = $state<string | null>(null);
  let generation = 0;
  let linkMissing = $state<PullRequestLink | null>(null);

  // ── One pull request ──────────────────────────────────────────────────────
  let detail = $state<GithubPullRequestDetail | null>(null);
  let detailLoading = $state(false);
  let detailError = $state<string | null>(null);
  let detailGeneration = 0;
  let page = $state<'summary' | 'changes'>('summary');
  let diffMode = $state<DiffMode>('unified');
  /** Head versions of files whose collapsed lines were opened on the Changes page. */
  let fullTexts = $state<Record<string, string>>({});
  let diffView = $state<MultiFileDiff>();
  /** Files marked viewed on the Changes page, for this pull request only. */
  let viewedFiles = $state<Record<string, true>>({});
  /** Line comments waiting to go out with the next review, and the one being written. */
  let draftLines = $state<GithubReviewSubmission['comments']>([]);
  let lineTarget = $state<{ path: string; side: 'LEFT' | 'RIGHT'; line: number } | null>(null);
  let lineBody = $state('');
  let pendingComment = $state<{ body: string } | null>(null);
  let linkCopied = $state(false);
  let draftSummary = $state('');
  let draftHeadSha = $state('');
  let pendingReview = $state<GithubReviewSubmission | null>(null);
  let posting = $state(false);
  let postUncertain = $state(false);
  let postedUrl = $state('');
  let helperDrafting = $state(false);
  let replyTarget = $state<GithubPullRequestDetail['comments'][number] | null>(null);
  let replyBody = $state('');
  let pendingReply = $state<{ commentId: number; body: string; headSha: string } | null>(null);
  let mergeMethod = $state<GithubMergeRequest['method']>('merge');
  let pendingMerge = $state<(GithubMergeRequest & { repository: string; title: string; baseBranch: string; headBranch: string }) | null>(null);
  let merging = $state(false);
  let mergeUncertain = $state(false);
  let mergeError = $state('');
  let mergedUrl = $state('');

  // Only projects whose remote is on github.com can list pull requests.
  const roots = $derived(projectRegistry.projects.filter((project) => project.machine === 'local' && project.repoKey.toLowerCase().startsWith('github.com/')));
  // Remote projects come from the rail's sessions; each one is searched on its own machine.
  const remoteRoots = $derived([...new Set(rail.owned.filter((session) => session.executionEnvironment === 'remote').map((session) => sessionWorkspaceRoot(session, projectRegistry.projects)).filter(Boolean))]);
  const projectLabel = $derived(roots.find((root) => root.rootPath === projectFilter)?.title ?? remoteRoots.find((root) => root === projectFilter)?.split('/').filter(Boolean).at(-1) ?? 'All local projects');
  const headerState = $derived(prState(detail ?? pullRequestSelection.selected));
  // Detail loads re-run only when the chosen pull request changes, not when the
  // selection object is filled in from the detail it loaded.
  const selectedKey = $derived(pullRequestSelection.selected ? `${pullRequestSelection.selected.localRoot}\n${pullRequestSelection.selected.number}` : '');
  const changeTotals = $derived(detail ? detail.files.reduce((sum, file) => ({ additions: sum.additions + file.additions, deletions: sum.deletions + file.deletions }), { additions: 0, deletions: 0 }) : null);
  const changedFiles = $derived<SourceGitDiff[]>(detail ? detail.files.map((file) => ({
    relativePath: file.path,
    status: file.status,
    diff: file.patch ?? '',
    isBinary: false,
    originalContent: null,
    modifiedContent: fullTexts[file.path] ?? null
  })) : []);
  // Assembly sessions whose conversation names this pull request.
  const threads = $derived.by(() => {
    if (!detail) return [];
    const { number, localRoot } = detail;
    return rail.owned.filter((session) => session.pullRequest === `PR #${number}` && sessionWorkspaceRoot(session, projectRegistry.projects) === localRoot);
  });
  const activity = $derived(detail ? activityRows(detail) : []);
  const canMerge = $derived(detail?.state === 'OPEN' && !detail.isDraft && detail.mergeable === 'MERGEABLE' && !detailLoading && !posting && !merging && !mergeUncertain);
  const checkSummary = $derived.by(() => {
    if (!detail || detail.checks.length === 0) return 'No checks';
    const passing = detail.checks.filter((check) => checkTone(check) === 'good').length;
    const failing = detail.checks.filter((check) => checkTone(check) === 'bad').length;
    return failing ? `${failing} failing` : passing === detail.checks.length ? 'All passing' : `${passing} of ${detail.checks.length} passing`;
  });

  type ActivityRow =
    | { kind: 'opened' | 'merged' | 'closed'; key: string; at: string; author: string }
    | { kind: 'commit'; key: string; at: string; author: string; oid: string; headline: string }
    | { kind: 'comment'; key: string; at: string; comment: GithubPullRequestDetail['comments'][number] }
    | { kind: 'review'; key: string; at: string; review: GithubPullRequestDetail['reviews'][number] };

  /** Everything that happened on the pull request, oldest first. */
  function activityRows(pr: GithubPullRequestDetail): ActivityRow[] {
    const rows: ActivityRow[] = [{ kind: 'opened', key: 'opened', at: pr.createdAt, author: pr.author }];
    for (const commit of pr.commits) rows.push({ kind: 'commit', key: `commit-${commit.oid}`, at: commit.committedAt, author: commit.author, oid: commit.oid, headline: commit.headline });
    for (const comment of pr.comments) rows.push({ kind: 'comment', key: `comment-${comment.id}`, at: comment.createdAt, comment });
    pr.reviews.forEach((review, index) => { if (review.body.trim() || review.state !== 'COMMENTED') rows.push({ kind: 'review', key: `review-${index}`, at: review.submittedAt, review }); });
    if (pr.mergedAt) rows.push({ kind: 'merged', key: 'merged', at: pr.mergedAt, author: pr.mergedBy });
    else if (pr.closedAt) rows.push({ kind: 'closed', key: 'closed', at: pr.closedAt, author: '' });
    return rows.sort((left, right) => left.at.localeCompare(right.at));
  }

  function age(value: string): string {
    return formatAge(value, new Date());
  }

  function avatarUrl(login: string): string {
    return `https://github.com/${encodeURIComponent(login)}.png?size=40`;
  }

  function repoName(repository: string): string {
    return repository.split('/').at(-1) ?? repository;
  }

  function mergeMethodLabel(method: GithubMergeRequest['method']): string {
    return method === 'squash' ? 'Squash and merge' : method === 'rebase' ? 'Rebase and merge' : 'Create merge commit';
  }

  function prState(pr: { isDraft: boolean; state: string } | null): { label: string; tone: string } {
    // A pull request opened from a link has no state until its detail loads.
    if (!pr?.state) return { label: '', tone: 'idle' };
    const state = pr.state.toUpperCase();
    if (state === 'MERGED') return { label: 'Merged', tone: 'merged' };
    if (state === 'CLOSED') return { label: 'Closed', tone: 'bad' };
    if (pr.isDraft) return { label: 'Draft', tone: 'idle' };
    return { label: 'Open', tone: 'good' };
  }

  function checkTone(check: GithubPullRequestDetail['checks'][number]): string {
    const value = (check.conclusion || check.status).toLowerCase();
    if (value === 'success' || value === 'neutral' || value === 'skipped') return 'good';
    if (/fail|error|cancel|timed_out|action_required/.test(value)) return 'bad';
    return 'attention';
  }

  async function loadDetail(root: string, number: number): Promise<void> {
    const current = ++detailGeneration;
    const previousHeadSha = detail?.headSha;
    detail = null;
    detailError = null;
    detailLoading = true;
    postUncertain = false;
    mergeUncertain = false;
    mergeError = '';
    fullTexts = {};
    diffView?.reset();
    try {
      const result = await readGithubPullRequestFromTauri(root, number);
      if (current !== detailGeneration) return;
      if (!result) throw new Error('Pull request details are available in the desktop app.');
      detail = result;
      // A pull request opened from a link starts with only its number; fill in the rest.
      const selected = pullRequestSelection.selected;
      if (selected && selected.number === result.number && !selected.title) {
        Object.assign(selected, { repository: result.repository, title: result.title, url: result.url, author: result.author, state: result.state, isDraft: result.isDraft, headBranch: result.headBranch, baseBranch: result.baseBranch });
      }
      if (draftHeadSha && draftHeadSha !== result.headSha) {
        cancelDraft();
        detailError = 'The PR changed. Your unposted comment was cleared; look at the new changes before commenting.';
      }
      if (replyTarget && previousHeadSha !== result.headSha) { replyTarget = null; replyBody = ''; pendingReply = null; }
      // Line comments go out in a review, which GitHub takes only while the PR is open.
      if (result.state !== 'OPEN') { draftLines = []; lineTarget = null; lineBody = ''; }
    } catch (reason) {
      if (current === detailGeneration) detailError = reason instanceof Error ? reason.message : String(reason);
    } finally {
      if (current === detailGeneration) detailLoading = false;
    }
  }

  /** Opening a collapsed run of lines needs the file's text at the PR head. */
  async function loadFullText(path: string): Promise<void> {
    const pr = detail;
    const file = pr?.files.find((candidate) => candidate.path === path);
    if (!pr || !file) return;
    try {
      const versions = await readGithubPullRequestFileFromTauri({ root: pr.localRoot, path: file.path, previousPath: file.previousPath, status: file.status, baseSha: pr.baseSha, headSha: pr.headSha });
      if (detail !== pr || !versions) return;
      fullTexts[path] = versions.modifiedContent;
    } catch (reason) {
      if (detail === pr) detailError = reason instanceof Error ? reason.message : String(reason);
    }
  }

  async function load(reset: boolean): Promise<void> {
    const current = ++generation;
    loading = true;
    error = null;
    try {
      await hydrateProjects();
      if (current !== generation) return;
      if (followedSession !== rail.activeOwnedId) {
        followedSession = rail.activeOwnedId;
        projectFilter = sessionProjectFilter(rail.owned.find((session) => session.ownedId === followedSession), projectRegistry.projects);
      }
      if (reset) searchedText = searchInput.trim();
      const result = await listGithubPullRequestsFromTauri({
        roots: parseRemoteWorkspacePath(projectFilter) ? [projectFilter] : roots.map((root) => root.rootPath),
        projectFilter: projectFilter || null,
        search: pullRequestSearchQuery(stateFilter, scope, searchedText),
        cursor: reset ? null : cursor,
        pageSize: 30
      });
      if (current !== generation) return;
      if (!result) throw new Error('Pull requests are available in the desktop app.');
      items = reset ? result.items : [...items, ...result.items];
      cursor = result.nextCursor;
      totalCount = result.totalCount;
      loaded = true;
    } catch (reason) {
      if (current !== generation) return;
      error = reason instanceof Error ? reason.message : String(reason);
      // Rows from an earlier search would read as this one's results.
      if (reset) { items = []; cursor = null; }
    } finally {
      if (current === generation) loading = false;
    }
  }

  function onSearchKey(event: KeyboardEvent): void {
    if (event.key !== 'Enter') return;
    clearTimeout(searchTimer);
    void load(true);
  }

  /** The list follows the box once typing pauses; Enter searches at once. */
  function onSearchInput(): void {
    clearTimeout(searchTimer);
    if (searchInput.trim() !== searchedText) searchTimer = setTimeout(() => void load(true), 350);
  }

  /** A link names a repository; find the project that has it as its remote.
   * The link stays stored while it resolves, so a newer link replaces it and
   * this one gives up once its await returns. */
  async function openLink(link: PullRequestLink): Promise<void> {
    linkMissing = null;
    const sameRepository = (repository: string) => repository.toLowerCase() === link.repository.toLowerCase();
    const listed = items.find((item) => sameRepository(item.repository) && item.number === link.number);
    if (!listed) await hydrateProjects();
    if (pullRequestSelection.link !== link) return; // a newer link arrived meanwhile
    pullRequestSelection.link = null;
    if (listed) {
      selectPullRequest(listed);
      return;
    }
    const project = projectRegistry.projects.find((candidate) => candidate.repoKey.toLowerCase() === `github.com/${link.repository}`.toLowerCase());
    if (!project) {
      pullRequestSelection.selected = null;
      linkMissing = link;
      return;
    }
    selectPullRequest({
      repository: link.repository,
      localRoot: project.machine === 'local' ? project.rootPath : remoteWorkspacePath(project.machine, project.rootPath),
      number: link.number,
      title: '',
      url: `https://github.com/${link.repository}/pull/${link.number}`,
      isDraft: false,
      updatedAt: '',
      state: '',
      author: '',
      headBranch: '',
      baseBranch: ''
    });
  }

  async function copyLink(url: string): Promise<void> {
    await navigator.clipboard.writeText(url);
    linkCopied = true;
  }

  function resetPullRequestState(): void {
    cancelDraft();
    viewedFiles = {};
    pendingComment = null;
    replyTarget = null;
    replyBody = '';
    pendingReply = null;
    pendingMerge = null;
    mergedUrl = '';
    mergeError = '';
    mergeMethod = 'merge';
    postedUrl = '';
    page = 'summary';
    linkMissing = null;
    linkCopied = false;
  }

  function selectPullRequest(item: GithubPullRequestSummary): void {
    if (pullRequestSelection.selected?.repository === item.repository && pullRequestSelection.selected.number === item.number) return;
    resetPullRequestState();
    pullRequestSelection.selected = item;
  }

  /** Back to the list. */
  function closePullRequest(): void {
    resetPullRequestState();
    detailGeneration += 1;
    detail = null;
    detailError = null;
    detailLoading = false;
    pullRequestSelection.selected = null;
  }

  function cancelDraft(): void {
    draftSummary = '';
    draftHeadSha = '';
    pendingReview = null;
    draftLines = [];
    lineTarget = null;
    lineBody = '';
  }

  function startLineDraft(path: string, side: 'LEFT' | 'RIGHT', line: number): void {
    lineTarget = { path, side, line };
    lineBody = '';
  }

  function saveLineDraft(): void {
    if (!detail || !lineTarget || !lineBody.trim()) return;
    draftHeadSha = detail.headSha;
    draftLines = [...draftLines, { ...lineTarget, body: lineBody.trim() }];
    lineTarget = null;
    lineBody = '';
  }

  /** The file as it is in the project's own checkout, which may be on another branch. */
  function openFile(path: string, line: number | null): void {
    const root = detail?.localRoot;
    if (!root) return;
    requestOpenFile({ path: `${root.replace(/\/+$/, '')}/${path}`, projectRoot: root, line: line && line > 0 ? line : undefined });
  }

  async function draftWithHelper(target: 'overall' | 'line'): Promise<void> {
    if (!detail || helperDrafting || (target === 'line' && !lineTarget)) return;
    const pr = detail;
    const line = lineTarget;
    const patchBudget = Math.floor(24_000 / Math.max(pr.files.length, 1));
    const context = line && target === 'line'
      ? `Target: ${line.path}:${line.line} (${line.side})\n${pr.files.find((file) => file.path === line.path)?.patch ?? 'No text patch available.'}`
      : `Target: overall review\n${pr.files.map((file) => `File: ${file.path}\n${file.patch?.slice(0, patchBudget) ?? 'No text patch available.'}`).join('\n\n')}`;
    const input = `Repository: ${pr.repository}\nPR #${pr.number}: ${pr.title}\nDescription: ${pr.body.slice(0, 4000)}\n\n${context.slice(0, 28_000)}`;
    helperDrafting = true;
    detailError = null;
    try {
      const draft = await runHelperJobFromTauri('review', input);
      if (detail?.repository !== pr.repository || detail?.number !== pr.number || detail?.headSha !== pr.headSha) return;
      if (target === 'line' && (lineTarget?.path !== line?.path || lineTarget?.line !== line?.line)) return;
      draftHeadSha = pr.headSha;
      if (target === 'line') lineBody = draft.trim();
      else draftSummary = draft.trim();
    } catch (reason) {
      detailError = reason instanceof Error ? reason.message : String(reason);
    } finally {
      helperDrafting = false;
    }
  }

  function previewReply(): void {
    if (!detail || !replyTarget || !replyBody.trim() || posting || postUncertain) return;
    postedUrl = '';
    pendingReply = { commentId: Number(replyTarget.id), body: replyBody.trim(), headSha: detail.headSha };
  }

  async function confirmReply(): Promise<void> {
    if (!detail || !pendingReply || posting) return;
    const pr = detail;
    const reply = pendingReply;
    pendingReply = null;
    posting = true;
    detailError = null;
    // Another PR opened (or Back) while posting moves the generation on; then leave it alone.
    const generation = detailGeneration;
    try {
      const url = await replyGithubPullRequestCommentFromTauri({ root: pr.localRoot, number: pr.number, expectedHeadSha: reply.headSha, commentId: reply.commentId, body: reply.body });
      if (generation !== detailGeneration) return;
      postedUrl = url;
      replyTarget = null;
      replyBody = '';
      await loadDetail(pr.localRoot, pr.number);
    } catch (reason) {
      if (generation !== detailGeneration) return;
      detailError = reason instanceof Error ? reason.message : String(reason);
      postUncertain = /uncertain|not confirmed/i.test(detailError);
    } finally {
      posting = false;
    }
  }

  /** Line comments go out as one review; without them the text is a plain
   * conversation comment, which GitHub takes in any state. */
  function previewPost(): void {
    if (!detail || !draftSummary.trim() || posting || postUncertain) return;
    postedUrl = '';
    if (draftLines.length === 0) {
      pendingComment = { body: draftSummary.trim() };
      return;
    }
    pendingReview = {
      root: detail.localRoot,
      number: detail.number,
      expectedHeadSha: draftHeadSha || detail.headSha,
      body: draftSummary.trim(),
      comments: draftLines.map((line) => ({ ...line, body: line.body.trim() })).filter((line) => line.body)
    };
  }

  async function confirmComment(): Promise<void> {
    if (!detail || !pendingComment || posting) return;
    const pr = detail;
    const body = pendingComment.body;
    pendingComment = null;
    posting = true;
    detailError = null;
    const generation = detailGeneration;
    try {
      const url = await commentGithubPullRequestFromTauri({ root: pr.localRoot, number: pr.number, body });
      if (generation !== detailGeneration) return;
      postedUrl = url;
      cancelDraft();
      await loadDetail(pr.localRoot, pr.number);
    } catch (reason) {
      if (generation !== detailGeneration) return;
      detailError = reason instanceof Error ? reason.message : String(reason);
      postUncertain = /uncertain|not confirmed/i.test(detailError);
    } finally {
      posting = false;
    }
  }

  async function confirmReview(): Promise<void> {
    if (!pendingReview || posting) return;
    const submission = pendingReview;
    posting = true;
    detailError = null;
    pendingReview = null;
    const generation = detailGeneration;
    try {
      const url = await submitGithubPullRequestReviewFromTauri(submission);
      if (generation !== detailGeneration) return;
      postedUrl = url;
      cancelDraft();
      await loadDetail(submission.root, submission.number);
    } catch (reason) {
      if (generation !== detailGeneration) return;
      detailError = reason instanceof Error ? reason.message : String(reason);
      postUncertain = /uncertain|not confirmed/i.test(detailError);
    } finally {
      posting = false;
    }
  }

  function previewMerge(): void {
    if (!detail || !canMerge) return;
    pendingMerge = {
      root: detail.localRoot,
      repository: detail.repository,
      number: detail.number,
      title: detail.title,
      headBranch: detail.headBranch,
      baseBranch: detail.baseBranch,
      expectedHeadSha: detail.headSha,
      expectedBaseSha: detail.baseSha,
      method: mergeMethod
    };
  }

  async function confirmMerge(): Promise<void> {
    if (!pendingMerge || merging || posting) return;
    const request = pendingMerge;
    pendingMerge = null;
    merging = true;
    mergeError = '';
    mergedUrl = '';
    try {
      mergedUrl = await mergeGithubPullRequestFromTauri(request);
      cancelDraft();
      items = items.filter((item) => item.repository !== request.repository || item.number !== request.number);
      if (pullRequestSelection.selected?.repository === request.repository && pullRequestSelection.selected.number === request.number) await loadDetail(request.root, request.number);
    } catch (reason) {
      mergeError = reason instanceof Error ? reason.message : String(reason);
      mergeUncertain = /uncertain/i.test(mergeError);
    } finally {
      merging = false;
    }
  }

  $effect(() => {
    // Showing the tab, or a different active session, picks the list (and its project) again.
    const active = rail.activeOwnedId;
    if (showing && untrack(() => (!loaded && !loading && !error) || active !== followedSession)) untrack(() => void load(true));
  });
  $effect(() => () => clearTimeout(searchTimer));
  $effect(() => {
    const key = selectedKey;
    const target = untrack(() => pullRequestSelection.selected);
    if (showing && key && target) untrack(() => void loadDetail(target.localRoot, target.number));
  });
  $effect(() => {
    const link = pullRequestSelection.link;
    if (link) untrack(() => void openLink(link));
  });
</script>

{#snippet stateIcon(pr: { isDraft: boolean; state: string }, size: number)}
  {@const tone = prState(pr).tone}
  {#if tone === 'merged'}<GitMerge {size} aria-hidden="true" />
  {:else if tone === 'bad'}<GitPullRequestClosed {size} aria-hidden="true" />
  {:else if pr.isDraft}<GitPullRequestDraft {size} aria-hidden="true" />
  {:else}<GitPullRequest {size} aria-hidden="true" />{/if}
{/snippet}

{#snippet lineDrafts(path: string)}
  {#each draftLines as line, index (index)}
    {#if line.path === path}<div class="line-draft in-diff"><strong>Line {line.line}</strong><span class="truncate">{line.body}</span><Button variant="ghost" size="sm" onclick={() => (draftLines = draftLines.filter((_, row) => row !== index))}>Remove</Button></div>{/if}
  {/each}
  {#if lineTarget?.path === path}
    <div class="composer in-diff" role="group" aria-label="Line comment">
      <strong>Comment on line {lineTarget.line} ({lineTarget.side === 'LEFT' ? 'old' : 'new'} side)</strong>
      <textarea aria-label="Line review comment" rows="3" placeholder="Write a comment for this line" bind:value={lineBody}></textarea>
      <div class="composer-actions">
        <Button variant="secondary" size="sm" disabled={!lineBody.trim()} onclick={saveLineDraft}>Add to review</Button>
        <Button variant="ghost" size="sm" disabled={helperDrafting} onclick={() => void draftWithHelper('line')}>{helperDrafting ? 'Drafting…' : 'Draft with utility model'}</Button>
        <Button variant="ghost" size="sm" onclick={() => { lineTarget = null; lineBody = ''; }}>Cancel</Button>
      </div>
    </div>
  {/if}
{/snippet}

{#snippet avatar(login: string)}
  {#if login}<img class="avatar" src={avatarUrl(login)} alt="" width="20" height="20" loading="lazy" decoding="async" referrerpolicy="no-referrer" />{/if}
{/snippet}

<div class="workspace" data-selectable="true">
  {#if pullRequestSelection.selected}
    {@const pr = pullRequestSelection.selected}
    <div class="page-bar">
      <IconButton label="All pull requests" onclick={closePullRequest}><ChevronLeft /></IconButton>
      <div class="switch" role="group" aria-label="Pull request view">
        <button type="button" class:active={page === 'summary'} aria-pressed={page === 'summary'} onclick={() => (page = 'summary')}>Summary</button>
        <button type="button" class:active={page === 'changes'} aria-pressed={page === 'changes'} onclick={() => (page = 'changes')}>Changes{#if changeTotals}<span class="add">+{changeTotals.additions}</span><span class="del">−{changeTotals.deletions}</span>{/if}</button>
      </div>
      <span class="spacer"></span>
      {#if page === 'changes'}
        <SegmentedControl size="sm" items={DIFF_MODES} value={diffMode} aria-label="Diff layout" onValueChange={(value) => { diffMode = value === 'side-by-side' ? 'side-by-side' : 'unified'; }} />
      {/if}
      <div class="icon-group">
        <IconButton label={linkCopied ? 'Link copied' : 'Copy link'} onclick={() => void copyLink(pr.url)}><Link /></IconButton>
        <IconButton label="Refresh this pull request" disabled={detailLoading || posting} onclick={() => void loadDetail(pr.localRoot, pr.number)}><RefreshCw /></IconButton>
        <a class={buttonVariants({ variant: 'ghost', size: 'icon-sm' })} href={pr.url} target="_blank" rel="noreferrer" aria-label="Open on GitHub" title="Open on GitHub"><ExternalLink /></a>
      </div>
    </div>
    {#if detailError}<p class="notice error page-notice" role="alert">{detailError}</p>{/if}
    {#if page === 'summary'}
      <div class="summary-scroll">
        <div class="summary">
          <div class="summary-main">
            <div class="pr-kicker">{#if headerState.label}<span class="state-pill {headerState.tone}">{@render stateIcon(detail ?? pr, 13)}{headerState.label}</span>{/if}<span>{repoName(pr.repository)} #{pr.number}</span></div>
            <h1 class="pr-title">{detail?.title || pr.title || `#${pr.number}`}</h1>
            <div class="pr-meta">
              {#if detail?.author || pr.author}{@render avatar(detail?.author || pr.author)}<strong>{detail?.author || pr.author}</strong>{/if}
              {#if detail?.createdAt}<span title={exactLocalTime(detail.createdAt)}>{age(detail.createdAt)} ago</span>{/if}
              {#if detail?.headBranch || pr.headBranch}<span aria-hidden="true">·</span><span class="branches">{detail?.headBranch || pr.headBranch}<ArrowRight size={13} aria-hidden="true" />{detail?.baseBranch || pr.baseBranch}</span>{/if}
            </div>
            {#if detailLoading}<p class="notice"><WorkingSpinner size={12} /> Loading pull request…</p>{/if}
            {#if postedUrl}<p class="notice" role="status">Comment posted. <a href={postedUrl} target="_blank" rel="noreferrer">View it on GitHub</a></p>{/if}
            {#if mergedUrl}<p class="notice" role="status">Pull request merged. <a href={mergedUrl} target="_blank" rel="noreferrer">View it on GitHub</a></p>{/if}
            {#if detail}
              <div class="pr-body"><ConversationMessage text={detail.body || 'No description.'} role="assistant" showImages /></div>
              <section class="activity" aria-label="Activity">
                <h2>Activity</h2>
                {#each activity as row (row.key)}
                  {#if row.kind === 'comment'}
                    <div class="entry">
                      <div class="entry-head">{@render avatar(row.comment.author)}<strong>{row.comment.author}</strong><span>{row.comment.path ? 'commented on' : 'commented'}</span>{#if row.comment.path}<code>{row.comment.path}{row.comment.line ? `:${row.comment.line}` : ''}</code>{/if}<span class="entry-age" title={exactLocalTime(row.at)}>{age(row.at)}</span></div>
                      <div class="entry-body"><ConversationMessage text={row.comment.body} role="assistant" showImages /></div>
                      {#if row.comment.path && row.comment.replyToId === null && detail.state === 'OPEN'}<Button variant="ghost" size="sm" class="mb-2 ml-9" onclick={() => { replyTarget = row.comment; replyBody = ''; }}>Reply</Button>{/if}
                    </div>
                  {:else if row.kind === 'review'}
                    <div class="entry">
                      <div class="entry-head">{@render avatar(row.review.author)}<strong>{row.review.author}</strong><span class="review-state">{row.review.state.replaceAll('_', ' ').toLowerCase()}</span><span class="entry-age" title={exactLocalTime(row.at)}>{age(row.at)}</span></div>
                      {#if row.review.body.trim()}<div class="entry-body"><ConversationMessage text={row.review.body} role="assistant" showImages /></div>{/if}
                    </div>
                  {:else}
                    <div class="event">
                      <span class="event-icon {row.kind}">
                        {#if row.kind === 'commit'}<GitCommitHorizontal size={15} aria-hidden="true" />
                        {:else if row.kind === 'merged'}<GitMerge size={15} aria-hidden="true" />
                        {:else if row.kind === 'closed'}<GitPullRequestClosed size={15} aria-hidden="true" />
                        {:else}<GitPullRequest size={15} aria-hidden="true" />{/if}
                      </span>
                      <span class="event-text">{#if row.kind === 'commit'}{row.headline}{:else if row.kind === 'opened'}{row.author} opened this pull request{:else if row.kind === 'merged'}{row.author || 'Someone'} merged this pull request{:else}This pull request was closed{/if}</span>
                      {#if row.kind === 'commit'}<code class="oid">{row.oid.slice(0, 7)}</code>{@render avatar(row.author)}{/if}
                      <span class="entry-age" title={exactLocalTime(row.at)}>{age(row.at)}</span>
                    </div>
                  {/if}
                {/each}
                {#if replyTarget}
                  <div class="composer">
                    <strong>Reply to {replyTarget.author} on {replyTarget.path}:{replyTarget.line}</strong>
                    <textarea aria-label="Review reply" rows="3" bind:value={replyBody}></textarea>
                    <div class="composer-actions"><Button variant="ghost" size="sm" onclick={() => { replyTarget = null; replyBody = ''; }}>Cancel</Button><Button variant="secondary" size="sm" disabled={!replyBody.trim() || posting || postUncertain} onclick={previewReply}>Reply</Button></div>
                  </div>
                {/if}
              </section>
              <section class="composer" aria-label="Comment">
                <textarea aria-label="Comment" rows="2" placeholder="Leave a comment" bind:value={draftSummary} oninput={() => { if (detail && draftSummary.trim()) draftHeadSha = detail.headSha; }}></textarea>
                {#each draftLines as line, index (index)}
                  <div class="line-draft"><strong>{line.path}:{line.line}</strong><span class="truncate">{line.body}</span><Button variant="ghost" size="sm" onclick={() => (draftLines = draftLines.filter((_, row) => row !== index))}>Remove</Button></div>
                {/each}
                <div class="composer-actions">
                  <Button variant="secondary" size="sm" disabled={!draftSummary.trim() || posting || postUncertain} onclick={previewPost}>{draftLines.length ? `Post review · ${draftLines.length} line ${draftLines.length === 1 ? 'comment' : 'comments'}` : 'Comment'}</Button>
                  <Button variant="ghost" size="sm" disabled={helperDrafting} onclick={() => void draftWithHelper('overall')}>{helperDrafting ? 'Drafting…' : 'Draft with utility model'}</Button>
                  {#if draftSummary.trim() || draftLines.length}<Button variant="ghost" size="sm" onclick={cancelDraft}>Discard</Button>{/if}
                </div>
              </section>
              {#if detail.state === 'OPEN'}
                <section class="merge-card" aria-label="Merge pull request">
                  <div class="merge-fact">
                    <span class:merge-good={detail.checks.length > 0 && detail.checks.every((check) => checkTone(check) === 'good')} aria-hidden="true">{detail.checks.length > 0 && detail.checks.every((check) => checkTone(check) === 'good') ? '✓' : '•'}</span>
                    {detail.checks.length === 0 ? 'No checks reported' : `${detail.checks.filter((check) => checkTone(check) === 'good').length} of ${detail.checks.length} checks successful`}
                  </div>
                  <div class="merge-fact">
                    <span class:merge-good={detail.mergeable === 'MERGEABLE'} aria-hidden="true">{detail.mergeable === 'MERGEABLE' ? '✓' : '•'}</span>
                    {detail.mergeable === 'MERGEABLE' ? 'This branch has no conflicts with the base branch.' : detail.mergeable === 'CONFLICTING' ? 'This branch has conflicts with the base branch.' : 'GitHub is still checking whether this branch can merge.'}
                  </div>
                  <div class="merge-controls">
                    <Button size="sm" disabled={!canMerge} onclick={previewMerge}>{#if merging}<WorkingSpinner size={12} /> Merging…{:else}Merge pull request{/if}</Button>
                    <span>using</span>
                    <Select.Root type="single" value={mergeMethod} disabled={merging} onValueChange={(value) => { mergeMethod = value as GithubMergeRequest['method']; }}>
                      <Select.Trigger size="sm" aria-label="Merge method">{mergeMethodLabel(mergeMethod)}</Select.Trigger>
                      <Select.Content>
                        <Select.Item value="merge" label="Create merge commit" />
                        <Select.Item value="squash" label="Squash and merge" />
                        <Select.Item value="rebase" label="Rebase and merge" />
                      </Select.Content>
                    </Select.Root>
                  </div>
                  {#if detail.isDraft}<p class="merge-note">Mark this draft ready for review on GitHub before merging.</p>{/if}
                  {#if mergeUncertain}<p class="merge-note">Refresh this PR to check whether the merge completed before trying again.</p>{/if}
                  {#if mergeError}<p class="merge-note error" role="alert">{mergeError}</p>{/if}
                </section>
              {/if}
            {/if}
          </div>
          {#if detail}
            <aside class="summary-side" aria-label="Pull request facts">
              <section>
                <h3>Threads</h3>
                {#each threads as thread (thread.ownedId)}
                  <button type="button" class="side-row" onclick={() => void sessionRowJump(thread.ownedId, 'session')}><MessageSquare size={15} aria-hidden="true" /><span class="truncate">{thread.title || 'Untitled session'}</span></button>
                {:else}<p class="side-empty">No threads</p>{/each}
              </section>
              <section>
                <h3>Comments</h3>
                {#each detail.comments as comment (comment.id)}
                  <p class="side-row">{@render avatar(comment.author)}<span class="truncate"><strong>{comment.author}</strong> {comment.body}</span></p>
                {:else}<p class="side-empty">No comments</p>{/each}
              </section>
              <section>
                <h3>Reviews</h3>
                {#each detail.reviews as review, index (index)}
                  <p class="side-row">{@render avatar(review.author)}<strong>{review.author}</strong><span class="side-meta">{review.state.replaceAll('_', ' ').toLowerCase()}</span></p>
                {:else}<p class="side-empty">No reviews</p>{/each}
                {#if detail.reviewers.length}<p class="side-empty">Requested: {detail.reviewers.join(', ')}</p>{/if}
              </section>
              <section>
                <div class="side-head"><h3>Checks</h3><span class="side-meta">{checkSummary}</span></div>
                {#each detail.checks as check, index (index)}
                  <p class="side-row"><i class="check-dot {checkTone(check)}" aria-hidden="true"></i><span class="truncate">{check.name || 'Check'}</span>{#if check.url}<a class="side-meta" href={check.url} target="_blank" rel="noreferrer">{(check.conclusion || check.status || 'unknown').replaceAll('_', ' ').toLowerCase()}</a>{:else}<span class="side-meta">{(check.conclusion || check.status || 'unknown').replaceAll('_', ' ').toLowerCase()}</span>{/if}</p>
                {/each}
              </section>
            </aside>
          {/if}
        </div>
      </div>
    {:else if detail}
      {#if detail.moreFiles}<p class="notice page-notice">Showing the first 100 files. Open GitHub to see the rest.</p>{/if}
      {#if draftLines.length}<p class="notice page-notice">{draftLines.length} line {draftLines.length === 1 ? 'comment' : 'comments'} in your review. Post {draftLines.length === 1 ? 'it' : 'them'} from <button type="button" class="link-button" onclick={() => (page = 'summary')}>Summary</button>.</p>{/if}
      <MultiFileDiff
        bind:this={diffView}
        bind:viewed={viewedFiles}
        files={changedFiles}
        mode={diffMode}
        onOpenLine={openFile}
        onLoadFullText={(path) => void loadFullText(path)}
        onLineComment={detail.state === 'OPEN' ? startLineDraft : undefined}
        fileFooter={lineDrafts}
      />
    {:else if detailLoading}
      <p class="notice page-notice"><WorkingSpinner size={12} /> Loading pull request…</p>
    {/if}
  {:else}
    <div class="list-view">
      <div class="list-head">
        <h2>Pull requests</h2>
        <IconButton label="Refresh pull requests" disabled={loading} onclick={() => void load(true)}><RefreshCw /></IconButton>
      </div>
      <div class="list-filters">
        <Input type="search" class="search" aria-label="Search pull requests" placeholder="Search title, branch, author or #number" maxlength={150} bind:value={searchInput} onkeydown={onSearchKey} oninput={onSearchInput} />
        <SegmentedControl size="sm" items={STATE_ITEMS} value={stateFilter} aria-label="Pull request state" onValueChange={(value) => { stateFilter = value as PullRequestState; void load(true); }} />
        <Select.Root type="single" value={scope} onValueChange={(value) => { scope = value as PullRequestScope; void load(true); }}>
          <Select.Trigger size="sm" aria-label="Whose pull requests">{SCOPE_LABELS[scope]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(SCOPE_LABELS) as [value, label] (value)}<Select.Item {value} {label} />{/each}
          </Select.Content>
        </Select.Root>
        <Select.Root type="single" value={projectFilter || '*'} onValueChange={(value) => { projectFilter = value === '*' ? '' : value; void load(true); }}>
          <Select.Trigger size="sm" aria-label="Filter by project"><span class="truncate">{projectLabel}</span></Select.Trigger>
          <Select.Content>
            <Select.Item value="*" label="All local projects" />
            {#each roots as root (root.id)}<Select.Item value={root.rootPath} label={root.title} />{/each}
            {#each remoteRoots as root (root)}<Select.Item value={root} label={`${root.split('/').filter(Boolean).at(-1)} (remote)`} />{/each}
          </Select.Content>
        </Select.Root>
      </div>
      {#if linkMissing}
        <p class="notice" role="status">github.com/{linkMissing.repository} is not a project in Assembly, so #{linkMissing.number} cannot open here. Add the repository as a project, or <a href={`https://github.com/${linkMissing.repository}/pull/${linkMissing.number}`} target="_blank" rel="noreferrer">open it on GitHub</a>.</p>
      {/if}
      {#if error}<p class="notice error" role="alert">{error}</p>{/if}
      {#if loading && items.length === 0}<p class="notice"><WorkingSpinner size={12} /> Loading pull requests…</p>{/if}
      {#if loaded && items.length === 0 && !loading && !error}<p class="notice">No pull requests match these filters.</p>{/if}
      <div class="rows">
        {#each items as item (`${item.repository}#${item.number}`)}
          <button type="button" class="row" onclick={() => selectPullRequest(item)}>
            <span class="row-icon {prState(item).tone}">{@render stateIcon(item, 15)}</span>
            <span class="row-text">
              <span class="row-title">{item.title}</span>
              <span class="row-meta">{repoName(item.repository)} #{item.number} · {item.headBranch} <ArrowRight size={11} aria-hidden="true" /> {item.baseBranch}</span>
            </span>
            <span class="row-side">{@render avatar(item.author)}{#if item.updatedAt}<span title={exactLocalTime(item.updatedAt)}>{age(item.updatedAt)} ago</span>{/if}</span>
          </button>
        {/each}
        {#if cursor}<Button variant="ghost" size="sm" class="mt-2" disabled={loading} onclick={() => void load(false)}>{#if loading}<WorkingSpinner size={12} /> Loading…{:else}Load more · {items.length} of {totalCount}{/if}</Button>{/if}
      </div>
    </div>
  {/if}
  {#if pendingReview}
    <div class="confirm-backdrop" role="presentation">
      <div class="confirm-review" role="dialog" aria-modal="true" aria-label="Confirm review">
        <h3>Post this review?</h3>
        <p><strong>{pullRequestSelection.selected?.repository} #{pendingReview.number}</strong> · head {pendingReview.expectedHeadSha.slice(0, 10)}</p>
        <pre>{pendingReview.body}</pre>
        {#each pendingReview.comments as line, index (index)}<p><strong>{line.path}:{line.line}</strong> {line.body}</p>{/each}
        <p>GitHub will notify participants. Assembly will check the PR head again before posting.</p>
        <div class="confirm-actions"><Button variant="ghost" size="sm" onclick={() => (pendingReview = null)}>Cancel</Button><Button size="sm" onclick={() => void confirmReview()}>Post to GitHub</Button></div>
      </div>
    </div>
  {/if}
  {#if pendingComment}
    <div class="confirm-backdrop" role="presentation">
      <div class="confirm-review" role="dialog" aria-modal="true" aria-label="Confirm comment">
        <h3>Post this comment?</h3>
        <p><strong>{pullRequestSelection.selected?.repository} #{pullRequestSelection.selected?.number}</strong></p>
        <pre>{pendingComment.body}</pre>
        <p>GitHub will notify participants.</p>
        <div class="confirm-actions"><Button variant="ghost" size="sm" onclick={() => (pendingComment = null)}>Cancel</Button><Button size="sm" onclick={() => void confirmComment()}>Post to GitHub</Button></div>
      </div>
    </div>
  {/if}
  {#if pendingReply}
    <div class="confirm-backdrop" role="presentation">
      <div class="confirm-review" role="dialog" aria-modal="true" aria-label="Confirm review reply">
        <h3>Post this reply?</h3>
        <p><strong>{pullRequestSelection.selected?.repository} #{pullRequestSelection.selected?.number}</strong> · comment {pendingReply.commentId}</p>
        <pre>{pendingReply.body}</pre>
        <p>GitHub will notify participants. Assembly will check the PR head again before posting.</p>
        <div class="confirm-actions"><Button variant="ghost" size="sm" onclick={() => (pendingReply = null)}>Cancel</Button><Button size="sm" onclick={() => void confirmReply()}>Post reply to GitHub</Button></div>
      </div>
    </div>
  {/if}
  {#if pendingMerge}
    <div class="confirm-backdrop" role="presentation">
      <div class="confirm-review" role="dialog" aria-modal="true" aria-label="Confirm pull request merge">
        <h3>Merge this pull request?</h3>
        <p><strong>{pendingMerge.repository} #{pendingMerge.number}</strong> · {pendingMerge.title}</p>
        <p><code>{pendingMerge.headBranch}</code> into <code>{pendingMerge.baseBranch}</code> using {mergeMethodLabel(pendingMerge.method)}.</p>
        <p>Head {pendingMerge.expectedHeadSha.slice(0, 10)} · base {pendingMerge.expectedBaseSha.slice(0, 10)}</p>
        {#if draftSummary.trim() || draftLines.length}<p>Your unposted comment will be cleared if the merge succeeds.</p>{/if}
        <p>Assembly checks the PR again before asking GitHub to merge it. GitHub enforces the repository's merge rules.</p>
        <div class="confirm-actions"><Button variant="ghost" size="sm" onclick={() => (pendingMerge = null)}>Cancel</Button><Button size="sm" onclick={() => void confirmMerge()}>Merge on GitHub</Button></div>
      </div>
    </div>
  {/if}
</div>

<style>
  .workspace{container-type:inline-size;display:flex;flex-direction:column;height:100%;min-height:0;min-width:0;background:var(--color-bg);color:var(--color-text)}
  .notice{font-size:var(--text-quiet);color:var(--color-text-3);margin:var(--space-3) 0}.notice.error{color:var(--color-bad)}.notice a{color:var(--color-accent)}
  .page-notice{margin:var(--space-2) var(--space-4)}

  /* The list */
  .list-view{display:flex;flex-direction:column;gap:var(--space-3);min-height:0;flex:1;padding:var(--space-5) var(--space-6) 0}
  .list-head{display:flex;align-items:center;justify-content:space-between;gap:var(--space-2)}.list-head h2{font-size:var(--text-heading);font-weight:var(--text-heading-weight);margin:0}
  .list-filters{display:flex;flex-wrap:wrap;align-items:center;gap:var(--space-2)}.list-filters :global(.search){flex:1 1 260px;max-width:420px}
  .rows{flex:1;min-height:0;overflow:auto;padding-bottom:var(--space-5)}
  .row{display:flex;align-items:center;gap:var(--space-3);width:100%;padding:var(--space-2) var(--space-3);border:0;border-bottom:1px solid var(--color-border);background:transparent;color:inherit;font:inherit;text-align:left;cursor:pointer}.row:hover{background:var(--color-hover)}.row:focus-visible{outline:2px solid var(--color-focus);outline-offset:-2px}
  .row-icon{display:flex;flex:none;color:var(--color-good)}.row-icon.merged{color:var(--color-merged)}.row-icon.bad{color:var(--color-bad)}.row-icon.idle{color:var(--color-text-3)}
  .row-text{display:flex;flex-direction:column;gap:2px;min-width:0;flex:1}.row-title,.row-meta{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.row-title{font-size:var(--text-body)}.row-meta{display:flex;align-items:center;gap:4px;font-size:12px;color:var(--color-text-3)}
  .row-side{display:flex;align-items:center;gap:var(--space-2);flex:none;font-size:12px;color:var(--color-text-3)}

  /* One pull request */
  .page-bar{display:flex;align-items:center;gap:var(--space-2);flex:none;padding:var(--space-2) var(--space-3);border-bottom:1px solid var(--color-border)}
  .switch{display:inline-flex;align-items:center;gap:2px;padding:3px;border-radius:999px;background:var(--color-muted)}
  .switch button{display:inline-flex;align-items:center;gap:6px;height:24px;padding:0 12px;border:0;border-radius:999px;background:transparent;color:var(--color-text-3);font:inherit;font-size:var(--text-quiet);cursor:pointer}.switch button:hover{color:var(--color-text)}.switch button.active{background:var(--color-secondary);color:var(--color-text)}.switch button:focus-visible{outline:2px solid var(--color-focus)}
  .add{color:var(--color-good)}.del{color:var(--color-bad)}.switch .add,.switch .del{font-size:12px}
  .spacer{flex:1}
  .icon-group{display:flex;align-items:center;gap:2px;padding:2px;border-radius:999px;background:var(--color-muted)}
  .summary-scroll{flex:1;min-height:0;overflow:auto}
  .summary{display:grid;grid-template-columns:minmax(0,760px) minmax(220px,300px);gap:var(--space-5);align-items:start;padding:var(--space-3) var(--space-5) var(--space-5) 88px}
  .summary-main{min-width:0}
  .pr-kicker{display:flex;align-items:center;gap:var(--space-2);font-size:var(--text-quiet);color:var(--color-text-3)}
  .state-pill{display:inline-flex;align-items:center;gap:5px;border-radius:8px;padding:3px 9px;font-size:var(--text-quiet);background:var(--color-elevated);color:var(--color-text-2)}.state-pill.good{background:var(--color-good-bg);color:var(--color-good)}.state-pill.bad{background:var(--color-bad-bg);color:var(--color-bad)}.state-pill.merged{background:color-mix(in srgb,var(--color-merged) 16%,transparent);color:var(--color-merged)}
  .pr-title{font-size:18px;font-weight:600;line-height:1.3;margin:var(--space-2) 0 var(--space-1);overflow-wrap:anywhere}
  .pr-meta{display:flex;flex-wrap:wrap;align-items:center;gap:var(--space-2);font-size:var(--text-quiet);color:var(--color-text-3)}.pr-meta strong{color:var(--color-text);font-weight:500}
  .branches{display:inline-flex;align-items:center;gap:var(--space-2)}
  code{font:12px ui-monospace,monospace;color:var(--color-text-2)}
  .avatar{width:20px;height:20px;flex:none;border-radius:50%;background:var(--color-elevated)}
  .pr-body{margin-top:var(--space-3);font-size:var(--text-body);overflow-wrap:anywhere}
  .activity{margin-top:var(--space-4);padding-top:var(--space-4);border-top:1px solid var(--color-border)}.activity h2{font-size:15px;font-weight:600;margin:0 0 var(--space-2)}
  .event,.entry{border-radius:12px;background:var(--color-surface);margin-bottom:var(--space-2);font-size:var(--text-body)}
  .event{display:flex;align-items:center;gap:var(--space-3);min-height:38px;padding:var(--space-2) var(--space-3)}
  .event-icon{display:flex;flex:none;color:var(--color-text-3)}.event-icon.opened{color:var(--color-good)}.event-icon.merged{color:var(--color-merged)}.event-icon.closed{color:var(--color-bad)}
  .event-text{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .oid{color:var(--color-text-3)}.entry-age{flex:none;margin-left:auto;font-size:12px;color:var(--color-text-3)}.event .entry-age{margin-left:0}
  .entry{overflow:hidden}.entry-head{display:flex;flex-wrap:wrap;align-items:center;gap:var(--space-2);padding:var(--space-2) var(--space-3);font-size:var(--text-quiet);color:var(--color-text-3)}.entry-head strong{color:var(--color-text);font-weight:500}
  .entry-body{padding:0 var(--space-3) var(--space-3) 40px;overflow-wrap:anywhere}
  .review-state{text-transform:capitalize}
  .composer{display:flex;flex-direction:column;gap:var(--space-2);margin-top:var(--space-4);border-radius:12px;background:var(--color-surface);padding:var(--space-3)}.composer strong{font-size:12px}
  .composer textarea{display:block;width:100%;resize:none;min-height:44px;background:transparent;border:0;color:var(--color-text);padding:var(--space-1);font:var(--text-body)/1.5 inherit}.composer textarea:focus-visible{outline:none}.composer:focus-within{box-shadow:0 0 0 1px var(--color-focus)}
  .composer-actions{display:flex;align-items:center;gap:var(--space-2)}
  .line-draft{display:flex;align-items:center;gap:var(--space-2);min-width:0;font-size:var(--text-quiet);color:var(--color-text-2)}.line-draft strong{flex:none;font:12px ui-monospace,monospace;color:var(--color-text-3)}
  .in-diff{margin:var(--space-2) var(--space-3)}
  .link-button{border:0;padding:0;background:none;color:var(--color-live);font:inherit;cursor:pointer;text-decoration:underline}
  .merge-card{margin-top:var(--space-4);border-radius:12px;background:var(--color-surface);overflow:hidden;font-size:var(--text-quiet)}
  .merge-fact{display:flex;align-items:center;gap:var(--space-3);margin:0;padding:var(--space-3) var(--space-4);border-bottom:1px solid var(--color-border)}
  .merge-fact span{color:var(--color-text-3);font-size:15px}.merge-fact span.merge-good{color:var(--color-good)}
  .merge-controls{display:flex;flex-wrap:wrap;align-items:center;gap:var(--space-2);padding:var(--space-3) var(--space-4)}.merge-controls span{color:var(--color-text-2)}
  .merge-note{margin:0;padding:0 var(--space-4) var(--space-3);color:var(--color-text-3)}.merge-note.error{color:var(--color-bad)}
  .summary-side{position:sticky;top:var(--space-4);display:flex;flex-direction:column;font-size:var(--text-quiet)}
  .summary-side section{padding:var(--space-3) 0;border-bottom:1px solid var(--color-border)}.summary-side section:first-child{padding-top:0}.summary-side section:last-child{border-bottom:0}
  .summary-side h3{font-size:var(--text-quiet);font-weight:400;color:var(--color-text-3);margin:0 0 var(--space-2)}
  .side-head{display:flex;align-items:baseline;justify-content:space-between}.side-head h3{margin:0}.side-head .side-meta{text-transform:none}
  .side-row{display:flex;align-items:center;gap:var(--space-2);width:100%;min-width:0;margin:0;padding:var(--space-1) 0;border:0;background:transparent;color:var(--color-text);font:inherit;text-align:left}
  button.side-row{cursor:pointer;border-radius:8px}button.side-row:hover{background:var(--color-hover)}button.side-row:focus-visible{outline:2px solid var(--color-focus)}
  .side-row strong{font-weight:500}.side-meta{margin-left:auto;flex:none;color:var(--color-text-3);text-transform:capitalize}.side-empty{margin:0;color:var(--color-text-3)}
  .check-dot{width:8px;height:8px;flex:none;border-radius:50%;background:var(--color-attention)}.check-dot.good{background:var(--color-good)}.check-dot.bad{background:var(--color-bad)}

  .confirm-backdrop{position:fixed;inset:0;z-index:1000;display:grid;place-items:center;background:var(--color-scrim)}.confirm-review{width:min(680px,calc(100vw - 40px));max-height:calc(100vh - 40px);overflow:auto;border-radius:16px;background:var(--color-popover);box-shadow:0 20px 70px rgba(0,0,0,.45);padding:var(--space-5)}.confirm-review h3{font-size:var(--text-heading);margin:0 0 var(--space-3)}.confirm-review p{font-size:var(--text-quiet);color:var(--color-text-3)}.confirm-review pre{white-space:pre-wrap;overflow-wrap:anywhere;font:12px/1.5 ui-monospace,monospace;border:1px solid var(--color-border);border-radius:8px;padding:var(--space-3);max-height:250px;overflow:auto}
  .confirm-actions{display:flex;justify-content:flex-end;gap:var(--space-2);margin-top:var(--space-3)}
  @container (max-width: 1180px){.summary{grid-template-columns:minmax(0,1fr);padding-left:var(--space-5)}.summary-side{position:static}}
</style>
