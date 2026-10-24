import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

import {
  deriveAgentSessionMetadata,
  readLocalSourceFile,
  scanLocalAgentSessions,
  scanLocalSourceFiles,
  searchLocalSourceFiles,
  validateLocalProjectRoot,
  writeLocalSourceFile
} from '../src/lib/server/localSourceFs.ts';

const root = await mkdtemp(join(tmpdir(), 'mcb-local-source-'));
const homeRoot = await mkdtemp(join(tmpdir(), 'mcb-local-home-'));

try {
  await mkdir(join(root, 'src', 'Services'), { recursive: true });
  await mkdir(join(root, '.history', 'Docs'), { recursive: true });
  await mkdir(join(root, '.pytest_cache'), { recursive: true });
  await mkdir(join(root, '.vscode'), { recursive: true });
  await mkdir(join(root, 'node_modules'), { recursive: true });
  await mkdir(join(root, 'bin'), { recursive: true });
  await mkdir(join(root, 'plain-folder'), { recursive: true });
  await writeFile(join(root, '.git'), 'gitdir: /tmp/mcb-local-source-fake-git\n', 'utf8');
  await writeFile(
    join(root, 'src', 'Services', 'FormatResolver.cs'),
    'namespace Demo;\npublic sealed class FormatResolver {}',
    'utf8'
  );
  await writeFile(join(root, 'src', 'App.ts'), 'export const appName = "MacCommandBar";\n', 'utf8');
  await writeFile(join(root, '.history', 'Docs', 'Old.md'), '# old\n', 'utf8');
  await writeFile(join(root, '.pytest_cache', 'README.md'), '# cache\n', 'utf8');
  await writeFile(join(root, '.vscode', 'settings.json'), '{}\n', 'utf8');
  await writeFile(join(root, 'node_modules', 'Ignored.ts'), 'export const ignored = true;\n', 'utf8');
  await writeFile(join(root, 'bin', 'Ignored.cs'), 'public sealed class Ignored {}\n', 'utf8');

  assert.deepEqual(await validateLocalProjectRoot(root), {
    path: root,
    exists: true,
    isDirectory: true,
    isGitRepository: true,
    gitRoot: root,
    message: 'Project root ready'
  });
  assert.deepEqual(await validateLocalProjectRoot(join(root, 'plain-folder')), {
    path: join(root, 'plain-folder'),
    exists: true,
    isDirectory: true,
    isGitRepository: false,
    gitRoot: root,
    message:
      `Folder is inside a Git repository. Add ${root} for full project context.`
  });
  assert.deepEqual(await validateLocalProjectRoot(join(root, 'missing')), {
    path: join(root, 'missing'),
    exists: false,
    isDirectory: false,
    isGitRepository: false,
    gitRoot: null,
    message: 'Project path not found'
  });

  const scan = await scanLocalSourceFiles({ root, limit: 20 });
  assert.deepEqual(
    scan.records.map((record) => record.relativePath),
    ['src/Services/FormatResolver.cs', 'src/App.ts']
  );
  assert.equal(scan.truncated, false);
  assert.deepEqual(
    new Set(scan.stats.skippedDirectorySamples.map((sample) => sample.name)),
    new Set(['.history', '.pytest_cache', '.vscode', 'bin', 'node_modules'])
  );
  assert.deepEqual(
    scan.stats.skippedDirectorySamples.find((sample) => sample.name === 'node_modules'),
    {
      path: 'node_modules',
      name: 'node_modules',
      reason: 'dependency directory'
    }
  );
  assert.ok(
    scan.stats.skippedDirectorySamples.every(
      (sample) => sample.path && !sample.path.startsWith(root) && sample.reason
    ),
    'Skipped directory samples should include relative path, name, and reason'
  );

  for (let index = 0; index < 20; index += 1) {
    await mkdir(join(root, `sample-${String(index).padStart(2, '0')}`, 'node_modules'), {
      recursive: true
    });
  }

  const boundedSampleScan = await scanLocalSourceFiles({ root, limit: 20 });
  assert.equal(boundedSampleScan.stats.skippedDirectorySamples.length, 16);
  assert.ok(
    boundedSampleScan.stats.skippedDirectories > boundedSampleScan.stats.skippedDirectorySamples.length,
    'Skipped directory samples should be bounded while the count keeps increasing'
  );

  await mkdir(join(root, 'Docs'), { recursive: true });
  await mkdir(join(root, 'EdiPlatform.Core', 'Services'), { recursive: true });
  await mkdir(join(root, 'EdiPlatform.Core', 'Models'), { recursive: true });
  await writeFile(join(root, 'Docs', 'A.md'), '# docs\n', 'utf8');
  await writeFile(join(root, 'Docs', 'B.md'), '# docs\n', 'utf8');
  await writeFile(
    join(root, 'EdiPlatform.Core', 'Services', 'RuntimeService.cs'),
    'namespace Demo;\npublic sealed class RuntimeService {}',
    'utf8'
  );
  await writeFile(
    join(root, 'EdiPlatform.Core', 'Models', 'RuntimeModel.cs'),
    'namespace Demo;\npublic sealed class RuntimeModel {}',
    'utf8'
  );

  const prioritizedScan = await scanLocalSourceFiles({ root, limit: 3 });
  assert.deepEqual(
    prioritizedScan.records.map((record) => record.relativePath),
    [
      'EdiPlatform.Core/Models/RuntimeModel.cs',
      'EdiPlatform.Core/Services/RuntimeService.cs',
      'src/Services/FormatResolver.cs'
    ]
  );
  assert.equal(prioritizedScan.truncated, true);
  assert.ok(
    prioritizedScan.records.every((record) => !record.relativePath.startsWith('Docs/')),
    'Truncated scans should prioritize app source over docs'
  );

  const projectNamedRoot = join(root, 'Project');
  await mkdir(join(projectNamedRoot, 'Project.Shared'), { recursive: true });
  await mkdir(join(projectNamedRoot, 'src'), { recursive: true });
  await writeFile(
    join(projectNamedRoot, 'Project.Shared', 'Alpha.cs'),
    'namespace Project.Shared;\npublic sealed class Alpha {}\n',
    'utf8'
  );
  await writeFile(
    join(projectNamedRoot, 'Project.Shared', 'Beta.cs'),
    'namespace Project.Shared;\npublic sealed class Beta {}\n',
    'utf8'
  );
  await writeFile(
    join(projectNamedRoot, 'src', 'App.cs'),
    'namespace Project;\npublic sealed class App {}\n',
    'utf8'
  );

  const projectNamedScan = await scanLocalSourceFiles({ root: projectNamedRoot, limit: 2 });
  assert.deepEqual(
    projectNamedScan.records.map((record) => record.relativePath),
    ['src/App.cs', 'Project.Shared/Alpha.cs']
  );
  assert.equal(projectNamedScan.truncated, true);

  const lowLimitRoot = join(root, 'low-limit-project');
  await mkdir(join(lowLimitRoot, 'src'), { recursive: true });
  await mkdir(join(lowLimitRoot, 'node_modules', 'pkg'), { recursive: true });
  await writeFile(join(lowLimitRoot, 'src', 'App.ts'), 'export const app = true;\n', 'utf8');
  await writeFile(join(lowLimitRoot, 'src', 'Worker.ts'), 'export const worker = true;\n', 'utf8');
  await writeFile(join(lowLimitRoot, 'src', 'Widget.ts'), 'export const widget = true;\n', 'utf8');
  await writeFile(
    join(lowLimitRoot, 'node_modules', 'pkg', 'index.ts'),
    'export const dependency = true;\n',
    'utf8'
  );

  const lowLimitScan = await scanLocalSourceFiles({ root: lowLimitRoot, limit: 2 });
  assert.equal(lowLimitScan.records.length, 2);
  assert.equal(lowLimitScan.truncated, true);
  assert.equal(lowLimitScan.stats.requestedLimit, 2);
  assert.equal(lowLimitScan.stats.returnedFiles, 2);
  assert.equal(lowLimitScan.stats.matchedFiles, 3);
  assert.equal(lowLimitScan.stats.collectionLimit, 10_001);
  assert.equal(lowLimitScan.stats.collectionLimitReached, false);
  assert.equal(lowLimitScan.stats.skippedDirectories, 1);
  assert.equal(lowLimitScan.stats.skippedDirectorySamples[0].name, 'node_modules');

  const filteredScan = await scanLocalSourceFiles({ root, query: 'resolver', limit: 20 });
  assert.deepEqual(
    filteredScan.records.map((record) => record.fileName),
    ['FormatResolver.cs']
  );

  await writeFile(join(root, 'src', 'Widget.tsx'), 'export const Widget = () => null;\n', 'utf8');
  await writeFile(join(root, 'src', 'Widget.jsx'), 'export const WidgetJsx = () => null;\n', 'utf8');
  await writeFile(join(root, 'src', 'View.swift'), 'struct View {}\n', 'utf8');

  const languageScan = await scanLocalSourceFiles({ root, query: 'widget', limit: 20 });
  const widgetLanguages = new Map(languageScan.records.map((record) => [record.fileName, record.language]));
  assert.equal(widgetLanguages.get('Widget.jsx'), 'jsx');
  assert.equal(widgetLanguages.get('Widget.tsx'), 'tsx');

  const swiftScan = await scanLocalSourceFiles({ root, query: 'View.swift', limit: 20 });
  assert.deepEqual(
    swiftScan.records.map((record) => [record.fileName, record.language]),
    [['View.swift', 'swift']]
  );

  await writeFile(join(root, 'src', 'Styles.scss'), '.app { color: red; }\n', 'utf8');
  await writeFile(join(root, 'src', 'Article.mdx'), '# Article\n', 'utf8');
  await writeFile(join(root, 'src', 'Page.astro'), '<h1>Page</h1>\n', 'utf8');
  await writeFile(join(root, 'src', 'App.vue'), '<template></template>\n', 'utf8');
  await writeFile(join(root, 'src', 'Project.fsproj'), '<Project />\n', 'utf8');
  await writeFile(join(root, 'src', 'View.xaml'), '<Page />\n', 'utf8');

  const parityLanguageScan = await scanLocalSourceFiles({ root, query: 'src/', limit: 100 });
  const languageByFileName = new Map(
    parityLanguageScan.records.map((record) => [record.fileName, record.language])
  );
  assert.equal(languageByFileName.get('Styles.scss'), 'scss');
  assert.equal(languageByFileName.get('Article.mdx'), 'mdx');
  assert.equal(languageByFileName.get('Page.astro'), 'html');
  assert.equal(languageByFileName.get('App.vue'), 'html');
  assert.equal(languageByFileName.get('Project.fsproj'), 'xml');
  assert.equal(languageByFileName.get('View.xaml'), 'xml');

  const worktreeRoot = join(root, 'worktrees', 'EdiPlatform', 'tsk-127-source-scan');
  await mkdir(join(worktreeRoot, 'EdiPlatform.Core', 'Services'), { recursive: true });
  await mkdir(join(worktreeRoot, 'EdiPlatform.Api', 'Controllers'), { recursive: true });
  await writeFile(join(worktreeRoot, '.git'), 'gitdir: /tmp/repo/.git/worktrees/tsk-127\n', 'utf8');
  await writeFile(
    join(worktreeRoot, 'EdiPlatform.Core', 'Services', 'FormatDetector.cs'),
    'namespace Demo;\npublic sealed class FormatDetector {}',
    'utf8'
  );
  await writeFile(
    join(worktreeRoot, 'EdiPlatform.Api', 'Controllers', 'DashboardController.cs'),
    'namespace Demo;\npublic sealed class DashboardController {}',
    'utf8'
  );

  const worktreeScan = await scanLocalSourceFiles({ root: worktreeRoot, limit: 20 });
  assert.deepEqual(
    worktreeScan.records.map((record) => record.relativePath),
    [
      'EdiPlatform.Core/Services/FormatDetector.cs',
      'EdiPlatform.Api/Controllers/DashboardController.cs'
    ]
  );

  const preview = await readLocalSourceFile(join(root, 'src', 'Services', 'FormatResolver.cs'));
  assert.equal(preview.language, 'csharp');
  assert.equal(preview.lineCount, 2);

  const appPath = join(root, 'src', 'App.ts');
  const openedApp = await readLocalSourceFile(appPath);
  const updatedPreview = await writeLocalSourceFile(
    appPath,
    'export const appName = "Updated";\n',
    openedApp.revision
  );
  assert.match(updatedPreview.content, /Updated/);

  const plainPath = join(root, 'notes.txt');
  await writeFile(plainPath, 'BASE\n', 'utf8');
  const openedPlain = await readLocalSourceFile(plainPath);
  await writeFile(plainPath, 'EXTERNAL\n', 'utf8');
  await assert.rejects(
    writeLocalSourceFile(plainPath, 'LOCAL\n', openedPlain.revision),
    /Source file changed on disk since it was opened/
  );
  assert.equal(await readFile(plainPath, 'utf8'), 'EXTERNAL\n');
  const latestPlain = await readLocalSourceFile(plainPath);
  const savedPlain = await writeLocalSourceFile(plainPath, 'LOCAL\n', latestPlain.revision);
  assert.equal(savedPlain.language, 'plain');
  assert.equal(await readFile(plainPath, 'utf8'), 'LOCAL\n');

  const matches = await searchLocalSourceFiles(scan.records, 'FormatResolver', 10);
  assert.equal(matches.length, 1);
  assert.equal(matches[0].relativePath, 'src/Services/FormatResolver.cs');

  // --- Agent session scan: subagent transcripts, and titles ------------------
  const cwd = '/Users/dev/work/mac-command-bar';
  const projectDir = join(homeRoot, '.claude', 'projects', '-Users-dev-work-mac-command-bar');
  await mkdir(join(projectDir, 'S1', 'subagents'), { recursive: true });

  const jsonl = (...lines) => `${lines.map((line) => JSON.stringify(line)).join('\n')}\n`;
  /** A subagent transcript: every entry is a sidechain, and it carries a sessionId. */
  const subagent = (sessionId) =>
    jsonl(
      {
        parentUuid: null,
        isSidechain: true,
        agentId: 'a0a5',
        type: 'user',
        sessionId,
        cwd,
        timestamp: '2026-07-28T10:00:00Z',
        message: { role: 'user', content: 'You are implementing Task 6 of the Slice 1 plan.' }
      },
      {
        isSidechain: true,
        type: 'assistant',
        sessionId,
        timestamp: '2026-07-28T10:01:00Z',
        message: {
          role: 'assistant',
          content: [{ type: 'text', text: 'Structured output provided successfully' }]
        }
      }
    );

  /**
   * A real session: a user prompt, a tool_result (also `type: "user"`), then an
   * assistant turn that is nothing but a tool_use. The tool_use line is NEWEST,
   * so the merge takes its empty title — which is why real rails filled up with
   * "Claude session".
   */
  const realSession = (sessionId, prompt) =>
    jsonl(
      {
        type: 'user',
        isSidechain: false,
        sessionId,
        cwd,
        timestamp: '2026-07-28T09:00:00Z',
        message: { role: 'user', content: prompt }
      },
      {
        type: 'user',
        isSidechain: false,
        sessionId,
        cwd,
        timestamp: '2026-07-28T09:01:00Z',
        message: { role: 'user', content: [{ type: 'tool_result', content: 'File does not exist.' }] }
      },
      {
        type: 'assistant',
        isSidechain: false,
        sessionId,
        cwd,
        timestamp: '2026-07-28T09:02:00Z',
        message: { role: 'assistant', content: [{ type: 'tool_use', id: 't1', name: 'Read', input: {} }] }
      }
    );

  await writeFile(join(projectDir, 'S1.jsonl'), realSession('S1', 'Fix the resume rail'), 'utf8');
  await writeFile(
    join(projectDir, 'S4.jsonl'),
    realSession('S4', 'Review the terminal service')
      + jsonl({ type: 'ai-title', sessionId: 'S4', aiTitle: 'Review terminal service security' }),
    'utf8'
  );
  // Excluded by path (today's layout) …
  await writeFile(join(projectDir, 'S1', 'subagents', 'agent-a0a5.jsonl'), subagent('S2'), 'utf8');
  // … and by content, for a subagent transcript written flat into the project dir.
  await writeFile(join(projectDir, 'S3.jsonl'), subagent('S3'), 'utf8');

  const sessions = await scanLocalAgentSessions(homeRoot);
  assert.deepEqual(
    sessions.map((session) => session.id).sort(),
    ['S1', 'S4'],
    'Subagent transcripts must never reach the rail'
  );
  assert.equal(
    sessions.find((session) => session.id === 'S1').title,
    'mac-command-bar — Fix the resume rail',
    'A session with no derivable title falls back to project folder + first prompt'
  );
  assert.equal(
    sessions.find((session) => session.id === 'S4').title,
    'Review terminal service security',
    "Claude Code's own ai-title wins outright"
  );

  const longPrompt = await mkdtemp(join(tmpdir(), 'mcb-local-home-'));
  try {
    const longDir = join(longPrompt, '.claude', 'projects', '-Users-dev-work-mac-command-bar');
    await mkdir(longDir, { recursive: true });
    await writeFile(
      join(longDir, 'S5.jsonl'),
      realSession(
        'S5',
        'Fix the resume rail so it stops listing subagent transcripts and generic titles'
      ),
      'utf8'
    );
    // Nothing usable anywhere: the generic label is still the last resort.
    await writeFile(
      join(longDir, 'S6.jsonl'),
      jsonl({ type: 'file-history-snapshot', sessionId: 'S6', timestamp: '2026-07-28T08:00:00Z' }),
      'utf8'
    );

    const fallbacks = await scanLocalAgentSessions(longPrompt);
    const truncated = fallbacks.find((session) => session.id === 'S5').title;
    assert.ok(truncated.startsWith('mac-command-bar — Fix the resume rail'));
    assert.ok(truncated.endsWith('...'));
    assert.equal(truncated.length, 62);
    assert.equal(fallbacks.find((session) => session.id === 'S6').title, 'Claude session');
  } finally {
    await rm(longPrompt, { recursive: true, force: true });
  }

  // --- The branch, task and pull request a session belongs to ----------------
  // These are the same fixture strings the Rust scanner's tests use. Both
  // scanners feed the same rail, so a row must read the same whichever one
  // filled it in, and anything that drifts here shows up as a chip that appears
  // in the app but not in the web preview.
  {
    const derived = deriveAgentSessionMetadata({
      provider: 'codex',
      id: '019e',
      title:
        'TSK-127 branch cdx/tsk-127-agent-session-metadata PR #42 https://github.com/acme/mac-command-bar/pull/42',
      description: null,
      model: null,
      projectPath: '/Users/blackcolours/dev/work/mac-command-bar',
      lastActivity: null,
      resumeCommands: ['codex resume 019e']
    });
    assert.equal(derived.taskId, 'TSK-127');
    assert.equal(derived.branchHint, 'cdx/tsk-127-agent-session-metadata');
    assert.equal(derived.pullRequestHint, 'PR #42');
    assert.equal(derived.sourceLabel, 'Codex · mac-command-bar');

    // A worktree is named for its task, so the folder answers when the title
    // does not — and everything it cannot answer stays empty.
    const fromFolder = deriveAgentSessionMetadata({
      provider: 'codex',
      id: '019e',
      title: 'Claude session',
      description: null,
      model: null,
      projectPath: '/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-128-runtime-audit',
      lastActivity: null,
      resumeCommands: ['codex resume 019e']
    });
    assert.equal(fromFolder.taskId, 'TSK-128');
    assert.equal(fromFolder.branchHint, null);
    assert.equal(fromFolder.pullRequestHint, null);
    assert.equal(fromFolder.sourceLabel, 'Codex · tsk-128-runtime-audit');
  }

  {
    // The edges each helper has, pinned so a rewrite on either side is caught.
    const hints = (title) =>
      deriveAgentSessionMetadata({
        provider: 'cmux-claude',
        id: 'x',
        title,
        description: null,
        model: null,
        projectPath: null,
        lastActivity: null,
        resumeCommands: []
      });

    // The word has to stand on its own, and quotes around the name come off.
    assert.equal(hints('branch: `tsk-9-fix`').branchHint, 'tsk-9-fix');
    assert.equal(hints('branch=release/2.1').branchHint, 'release/2.1');
    assert.equal(hints('rebranch main').branchHint, null);
    // Punctuation is not a branch name.
    assert.equal(hints('branch: ---').branchHint, null);
    // A task id is the word plus digits, in any of the shapes people write it.
    assert.equal(hints('worktrees/tsk-788-session-workspaces').taskId, 'TSK-788');
    assert.equal(hints('TSK#42 done').taskId, 'TSK-42');
    assert.equal(hints('tsk-abc').taskId, null);
    assert.equal(hints('worktsk-9').taskId, null);
    // A pull request link counts even when nobody wrote the words.
    assert.equal(
      hints('see https://github.com/acme/repo/pull/7).').pullRequestHint,
      'PR #7'
    );
    assert.equal(hints('approved, nothing to merge').pullRequestHint, null);
    assert.equal(hints('pull request 108 is green').pullRequestHint, 'PR #108');
    // Nothing at all to say: no chips, and the label still says who and where.
    const bare = hints('Untitled Codex session');
    assert.equal(bare.branchHint, null);
    assert.equal(bare.taskId, null);
    assert.equal(bare.pullRequestHint, null);
    assert.equal(bare.sourceLabel, 'CMUX Claude · Untitled Codex session');
  }

  // --- How much was said, and the last thing said ----------------------------
  // The fixtures below are written out line for line in the Rust scanner's tests
  // (`core/src/scanners/sessions.rs`). Both scanners fill the same rail, so a row
  // must read the same whichever one produced it, and anything that drifts here
  // shows up as a message count in the app that the web preview disagrees with.
  //
  // One honest difference: this file cuts long text with three dots where the
  // Rust scanner uses a single ellipsis character. That has been true of session
  // titles since they were written, and the assertions below pin it rather than
  // pretend otherwise.
  const conversationHome = await mkdtemp(join(tmpdir(), 'mcb-local-home-'));
  try {
    const conversationDir = join(
      conversationHome, '.claude', 'projects', '-Users-dev-work-mac-command-bar'
    );
    await mkdir(conversationDir, { recursive: true });

    /**
     * A session with an actual back-and-forth in it, and the tool traffic that
     * ran in between: two things the user typed (one of them a slash command,
     * which is not conversation), two answers in words, one tool call and one
     * tool result.
     */
    const conversation = (lastAnswer) =>
      jsonl(
        {
          type: 'user', isSidechain: false, sessionId: 'S9', cwd,
          timestamp: '2026-07-29T09:00:00Z',
          message: { role: 'user', content: 'Fix the resume rail' }
        },
        {
          type: 'assistant', isSidechain: false, sessionId: 'S9',
          timestamp: '2026-07-29T09:01:00Z',
          message: { role: 'assistant', content: [{ type: 'text', text: 'Reading the scanner now.' }] }
        },
        {
          type: 'assistant', isSidechain: false, sessionId: 'S9',
          timestamp: '2026-07-29T09:02:00Z',
          message: {
            role: 'assistant',
            content: [{ type: 'tool_use', id: 't1', name: 'Read', input: {} }]
          }
        },
        {
          type: 'user', isSidechain: false, sessionId: 'S9',
          timestamp: '2026-07-29T09:03:00Z',
          message: {
            role: 'user',
            content: [{ type: 'tool_result', content: 'File does not exist.' }]
          }
        },
        {
          type: 'user', isSidechain: false, sessionId: 'S9',
          timestamp: '2026-07-29T09:04:00Z',
          message: { role: 'user', content: '<command-name>compact</command-name>' }
        },
        {
          type: 'assistant', isSidechain: false, sessionId: 'S9',
          timestamp: '2026-07-29T09:05:00Z',
          message: { role: 'assistant', content: [{ type: 'text', text: lastAnswer }] }
        }
      );

    await writeFile(
      join(conversationDir, 'S9.jsonl'),
      conversation('The  rail was\n  reading the wrong file.'),
      'utf8'
    );
    // A transcript that held no conversation says nothing rather than zero.
    await writeFile(
      join(conversationDir, 'S10.jsonl'),
      jsonl({ type: 'file-history-snapshot', sessionId: 'S10', timestamp: '2026-07-29T08:00:00Z' }),
      'utf8'
    );

    const talked = await scanLocalAgentSessions(conversationHome);
    const said = talked.find((session) => session.id === 'S9');
    // Only words count: the tool call, its result and the slash command are how
    // the work got done, not what was said.
    assert.equal(said.messageCount, 3);
    assert.equal(said.latestTurnPreview, 'Agent: The rail was reading the wrong file.');
    const quiet = talked.find((session) => session.id === 'S10');
    assert.equal(quiet.messageCount, null);
    assert.equal(quiet.latestTurnPreview, null);

    // One line means one line: a longer answer is cut and marked.
    const longAnswer =
      'The scanner was reading the wrong file the whole time, which is why every '
      + 'row said Claude session and none of them said anything else at all';
    await writeFile(join(conversationDir, 'S9.jsonl'), conversation(longAnswer), 'utf8');
    const cut = (await scanLocalAgentSessions(conversationHome))
      .find((session) => session.id === 'S9').latestTurnPreview;
    assert.ok(cut.startsWith('Agent: The scanner was reading the wrong file'));
    // 119 characters of text plus the three dots this file cuts with.
    assert.equal(cut.length, 122);
    assert.ok(cut.endsWith('...'));

    // The user's own turn is shown as the user's when it came last. Mirrors the
    // Rust case of the same name.
    await writeFile(
      join(conversationDir, 'S9.jsonl'),
      `${conversation('The  rail was\n  reading the wrong file.')}\n${jsonl({
        type: 'user', isSidechain: false, sessionId: 'S9',
        timestamp: '2026-07-29T09:06:00Z',
        message: { role: 'user', content: 'Try it again' }
      })}`,
      'utf8'
    );
    const answered = (await scanLocalAgentSessions(conversationHome))
      .find((session) => session.id === 'S9');
    assert.equal(answered.messageCount, 4);
    assert.equal(answered.latestTurnPreview, 'You: Try it again');

    // Pressing Escape in the middle of an answer is how a session usually ends,
    // and Claude writes that as a user message. It is not something the user
    // said, so it is not a turn — and letting it through put "You: [Request
    // interrupted by user]" on the most prominent line of the card, where it
    // says nothing at all about the session. The row falls back to the last
    // thing that really was said.
    await writeFile(
      join(conversationDir, 'S9.jsonl'),
      `${conversation('The  rail was\n  reading the wrong file.')}\n${jsonl({
        type: 'user', isSidechain: false, sessionId: 'S9',
        timestamp: '2026-07-29T09:06:00Z',
        message: { role: 'user', content: '[Request interrupted by user]' }
      })}`,
      'utf8'
    );
    const interrupted = (await scanLocalAgentSessions(conversationHome))
      .find((session) => session.id === 'S9');
    assert.equal(interrupted.messageCount, 3);
    assert.equal(interrupted.latestTurnPreview, 'Agent: The rail was reading the wrong file.');
  } finally {
    await rm(conversationHome, { recursive: true, force: true });
  }

  const codexTalkHome = await mkdtemp(join(tmpdir(), 'mcb-local-home-'));
  try {
    await mkdir(join(codexTalkHome, '.codex', 'sessions'), { recursive: true });
    await writeFile(
      join(codexTalkHome, '.codex', 'sessions', 'rollout-019fa964.jsonl'),
      jsonl(
        {
          timestamp: '2026-07-28T16:42:17.000Z',
          type: 'session_meta',
          payload: {
            id: '019fa964',
            cwd: '/Users/dev/work/rental-management',
            originator: 'codex-tui',
            thread_source: 'user',
            source: 'cli'
          }
        },
        {
          timestamp: '2026-07-28T16:42:18.000Z',
          type: 'response_item',
          payload: {
            type: 'message',
            role: 'user',
            content: [
              {
                type: 'input_text',
                text: '# AGENTS.md instructions for /Users/dev/work/rental-management'
              }
            ]
          }
        },
        {
          timestamp: '2026-07-28T16:42:30.000Z',
          type: 'response_item',
          payload: {
            type: 'message',
            role: 'user',
            content: [
              {
                type: 'input_text',
                text: 'Please plan out an entire year of scans and entries for the 2027 simulation.'
              }
            ]
          }
        },
        {
          timestamp: '2026-07-28T16:43:00.000Z',
          type: 'response_item',
          payload: {
            type: 'message',
            role: 'assistant',
            content: [{ type: 'output_text', text: 'Here  is the plan\n for 2027.' }]
          }
        }
      ),
      'utf8'
    );

    const [talked] = await scanLocalAgentSessions(codexTalkHome);
    // The repository instructions Codex sends as the user are not a turn.
    assert.equal(talked.messageCount, 2);
    assert.equal(talked.latestTurnPreview, 'Agent: Here is the plan for 2027.');

    // A rollout file with nothing but its opening record says nothing rather
    // than zero. Mirrors the Rust case of the same name.
    await writeFile(
      join(codexTalkHome, '.codex', 'sessions', 'rollout-019fa964.jsonl'),
      jsonl({
        timestamp: '2026-07-28T16:42:17.000Z',
        type: 'session_meta',
        payload: { id: '019fa964', cwd: '/Users/dev', source: 'cli' }
      }),
      'utf8'
    );
    const [quiet] = await scanLocalAgentSessions(codexTalkHome);
    assert.equal(quiet.messageCount, null);
    assert.equal(quiet.latestTurnPreview, null);
  } finally {
    await rm(codexTalkHome, { recursive: true, force: true });
  }

  // Codex writes a separate record for every paragraph it narrates between tool
  // calls, so counting records put roughly twenty times as many "messages" on a
  // Codex row as on a Claude row for the same amount of conversation — and the
  // two sit on the same list. A run of them is one thing the agent said back.
  //
  // The index file that names the session knows neither the count nor the last
  // turn, and merging it must not wipe out what the rollout file found.
  const codexNarrationHome = await mkdtemp(join(tmpdir(), 'mcb-local-home-'));
  try {
    await mkdir(join(codexNarrationHome, '.codex', 'sessions'), { recursive: true });
    const agentSays = (timestamp, text) => ({
      timestamp,
      type: 'response_item',
      payload: { type: 'message', role: 'assistant', content: [{ type: 'output_text', text }] }
    });
    await writeFile(
      join(codexNarrationHome, '.codex', 'sessions', 'rollout-019fa964.jsonl'),
      jsonl(
        {
          timestamp: '2026-07-28T16:42:17.000Z',
          type: 'session_meta',
          payload: {
            id: '019fa964',
            cwd: '/Users/dev/work/rental-management',
            originator: 'codex-tui',
            thread_source: 'user',
            source: 'cli'
          }
        },
        {
          timestamp: '2026-07-28T16:42:30.000Z',
          type: 'response_item',
          payload: {
            type: 'message',
            role: 'user',
            content: [{ type: 'input_text', text: 'Plan the 2027 simulation.' }]
          }
        },
        agentSays('2026-07-28T16:43:00.000Z', 'Reading the scanner now.'),
        {
          timestamp: '2026-07-28T16:43:10.000Z',
          type: 'response_item',
          payload: { type: 'function_call', name: 'shell', arguments: '{}' }
        },
        agentSays('2026-07-28T16:43:20.000Z', 'Now changing the reader.'),
        agentSays('2026-07-28T16:43:30.000Z', 'Here is the plan for 2027.')
      ),
      'utf8'
    );
    await writeFile(
      join(codexNarrationHome, '.codex', 'session_index.jsonl'),
      jsonl({
        id: '019fa964',
        thread_name: 'Year simulation planning',
        updated_at: '2026-07-29T16:00:00.000000Z'
      }),
      'utf8'
    );

    const merged = await scanLocalAgentSessions(codexNarrationHome);
    assert.equal(merged.length, 1);
    // One thing the user typed, one thing the agent said back.
    assert.equal(merged[0].messageCount, 2);
    assert.equal(merged[0].latestTurnPreview, 'Agent: Here is the plan for 2027.');
    assert.equal(merged[0].title, 'Year simulation planning');
  } finally {
    await rm(codexNarrationHome, { recursive: true, force: true });
  }

  // A rollout file is read as a window from the start and a window from the end.
  // When the file is bigger than one window but smaller than two, those two
  // windows OVERLAP. This bridge decides on the file's real length and reads it
  // whole instead; the Rust scanner used to compare strings and got it wrong,
  // so a session in this size band reported two different counts depending on
  // which side served the row. Mirrors the Rust case of the same name.
  const codexBigHome = await mkdtemp(join(tmpdir(), 'mcb-local-home-'));
  try {
    await mkdir(join(codexBigHome, '.codex', 'sessions'), { recursive: true });
    const padding = 'x'.repeat(700);
    const exchanges = 200;
    const lines = [
      {
        timestamp: '2026-07-28T16:42:17.000Z',
        type: 'session_meta',
        payload: {
          id: '019fa964',
          cwd: '/Users/dev/work/rental-management',
          originator: 'codex-tui',
          thread_source: 'user',
          source: 'cli'
        }
      }
    ];
    for (let index = 0; index < exchanges; index += 1) {
      lines.push({
        timestamp: '2026-07-28T16:42:30.000Z',
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'user',
          content: [{ type: 'input_text', text: `Question ${index} ${padding}` }]
        }
      });
      lines.push({
        timestamp: '2026-07-28T16:43:00.000Z',
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'assistant',
          content: [{ type: 'output_text', text: `Answer ${index} ${padding}` }]
        }
      });
    }
    const body = jsonl(...lines);
    // 256 KB from the start, 256 KB from the end: the fixture has to land in the
    // band between one window and two, or it proves nothing.
    assert.ok(body.length > 256 * 1024 && body.length < 512 * 1024);
    await writeFile(
      join(codexBigHome, '.codex', 'sessions', 'rollout-019fa964.jsonl'),
      body,
      'utf8'
    );

    const [big] = await scanLocalAgentSessions(codexBigHome);
    assert.equal(big.messageCount, exchanges * 2);
  } finally {
    await rm(codexBigHome, { recursive: true, force: true });
  }

  const codexHome = await mkdtemp(join(tmpdir(), 'mcb-local-home-'));
  try {
    await mkdir(join(codexHome, '.codex', 'sessions'), { recursive: true });
    await writeFile(
      join(codexHome, '.codex', 'sessions', 'rollout-019fa964.jsonl'),
      jsonl(
        {
          timestamp: '2026-07-28T16:42:17.000Z',
          type: 'session_meta',
          payload: {
            id: '019fa964',
            cwd: '/Users/dev/work/mac-command-bar',
            originator: 'codex-tui',
            thread_source: 'user',
            source: 'cli'
          }
        },
        {
          timestamp: '2026-07-28T16:42:30.000Z',
          type: 'response_item',
          payload: {
            type: 'message',
            role: 'user',
            content: [
              {
                type: 'input_text',
                text: 'tsk-788 on branch tsk-788-session-workspaces, PR #12'
              }
            ]
          }
        }
      ),
      'utf8'
    );

    const [scanned] = await scanLocalAgentSessions(codexHome);
    assert.equal(scanned.id, '019fa964');
    assert.equal(scanned.taskId, 'TSK-788');
    assert.equal(scanned.branchHint, 'tsk-788-session-workspaces');
    assert.equal(scanned.pullRequestHint, 'PR #12');
    assert.equal(scanned.sourceLabel, 'Codex · mac-command-bar');
  } finally {
    await rm(codexHome, { recursive: true, force: true });
  }
} finally {
  await rm(root, { recursive: true, force: true });
  await rm(homeRoot, { recursive: true, force: true });
}
