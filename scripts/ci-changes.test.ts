import assert from 'node:assert/strict';
import { test } from 'node:test';
import { jobs } from './ci-changes.ts';

const pr = (files: string[], draft = false) => jobs({ event: 'pull_request', draft, files });

test('a draft pull request runs none of the suite, whatever it changes', () => {
  assert.deepEqual(pr(['dotnet/Assembly.Engine/EngineCommands.cs'], true), { full: false, backend: false });
});
test('a desktop-only change does not run the .NET job', () => {
  assert.deepEqual(pr(['desktop/src/main.rs', 'scripts/app-dev.ts']), { full: true, backend: false });
});
for (const file of ['dotnet/Assembly.Tests/Core/BackgroundWorkTests.cs', 'scripts/adapters/claude-launcher.ts', 'scripts/package-adapters.ts', 'dotnet/scripts/agents-file.sh', 'scripts/dev-toolchain.ts', 'desktop/Cargo.toml', '.github/workflows/ci.yml'])
  test(`${file} runs everything for the backend`, () => assert.deepEqual(pr([file]), { full: true, backend: true }));
for (const event of ['push', 'workflow_dispatch'])
  test(`${event} runs everything`, () => assert.deepEqual(jobs({ event, draft: false, files: [] }), { full: true, backend: true }));
