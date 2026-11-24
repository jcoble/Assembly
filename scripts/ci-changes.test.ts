import assert from 'node:assert/strict';
import { test } from 'node:test';
import { jobs } from './ci-changes.ts';

const pr = (files: string[], draft = false) => jobs({ event: 'pull_request', draft, files });

test('a draft pull request runs none of the suite, whatever it changes', () => {
  assert.deepEqual(pr(['dotnet/Assembly.Engine/EngineCommands.cs'], true), {
    full: false,
    web: false,
    desktop: false,
    backend: false,
    scripts: false,
  });
});

test('a web-only change skips desktop, dotnet, and scripts jobs', () => {
  assert.deepEqual(pr(['web/src/lib/features/rail/SessionsRail.svelte']), {
    full: true,
    web: true,
    desktop: false,
    backend: false,
    scripts: false,
  });
});

test('a desktop-only change does not run the .NET, web, or scripts jobs', () => {
  assert.deepEqual(pr(['desktop/src/main.rs']), {
    full: true,
    web: false,
    desktop: true,
    backend: false,
    scripts: false,
  });
});

test('desktop plus script change runs desktop and scripts without .NET or web', () => {
  assert.deepEqual(pr(['desktop/src/main.rs', 'scripts/app-dev.ts']), {
    full: true,
    web: false,
    desktop: true,
    backend: false,
    scripts: true,
  });
});

for (const file of ['dotnet/Assembly.Tests/Core/BackgroundWorkTests.cs', 'scripts/package-adapters.ts', 'dotnet/scripts/agents-file.sh', 'scripts/dev-toolchain.ts', 'desktop/Cargo.toml'])
  test(`${file} runs the backend job`, () => {
    const result = pr([file]);
    assert.equal(result.full, true);
    assert.equal(result.backend, true);
  });

test('.github/workflows/ci.yml runs every subsystem job', () => {
  assert.deepEqual(pr(['.github/workflows/ci.yml']), {
    full: true,
    web: true,
    desktop: true,
    backend: true,
    scripts: true,
  });
});

for (const event of ['push', 'workflow_dispatch'])
  test(`${event} runs everything`, () =>
    assert.deepEqual(jobs({ event, draft: false, files: [] }), {
      full: true,
      web: true,
      desktop: true,
      backend: true,
      scripts: true,
    }));
