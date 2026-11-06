namespace Assembly.DevSeed;

public static partial class SeedScenario
{
    private const string Retry = """
        export function backoffMs(attempt: number): number {
          return Math.min(1000 * 2 ** attempt, 60_000);
        }
        """;
    private const string Limiter = """
        export class TenantLimiter {
          private readonly counts = new Map<string, number>();
          allow(tenant: string, limit: number): boolean {
            const used = this.counts.get(tenant) ?? 0;
            if (used >= limit) return false;
            this.counts.set(tenant, used + 1);
            return true;
          }
        }
        """;
    private const string ShipYaml = """
        name: Ship a feature
        limits: {max_sessions: 2, step_timeout_minutes: 30, max_rounds: 2}
        steps:
          build:
            agent: {provider: codex}
            prompt: prompts/build.md
            result: build
            display: {present: Building, past: Built}
          checks:
            needs: [build]
            run: ["npm test"]
            display: {present: Running checks, past: Checks ran}
          review:
            needs: [build, checks]
            review: {of: build, agent: {provider: claude}, prompt: prompts/review.md}
            result: review
            display: {present: Reviewing, past: Reviewed}
          approve:
            needs: [review]
            gate: {prompt: "Merge this once you have looked at the diff?"}
            display: {present: Waiting for approval, past: Approved}
        """;

    private static SeedRepo HarborApi => new("harbor-api", "Harbor API",
    [
        Commit("Start the webhook service", 21,
            ("README.md", "# Harbor API\n\nReceives partner webhooks and queues deliveries.\n"),
            ("package.json", "{\n  \"name\": \"harbor-api\",\n  \"private\": true,\n  \"scripts\": { \"test\": \"vitest run\" }\n}\n"),
            ("src/server.ts", "import { handle } from './webhooks';\n\nexport const routes = { '/hooks': handle };\n"),
            ("src/webhooks.ts", "export function handle(body: unknown) {\n  return { queued: true, body };\n}\n")),
        Commit("Add delivery retries", 12,
            ("src/retry.ts", "export function backoffMs(attempt: number): number {\n  return 1000 * attempt;\n}\n"),
            ("test/retry.test.ts", "import { backoffMs } from '../src/retry';\n\ntest('grows', () => expect(backoffMs(2)).toBe(2000));\n")),
        Commit("Add the ship-feature workflow", 6,
            (".assembly/workflows/ship-feature.yml", ShipYaml + "\n"),
            (".assembly/workflows/prompts/build.md", "Build what was asked for: ${{ inputs.request }}\nCommit it and report what changed.\n"),
            (".assembly/workflows/prompts/review.md", "Review the build for bugs and missing tests. Report findings.\n")),
        Commit("Log rejected signatures", 2,
            ("src/webhooks.ts", "export function handle(body: unknown) {\n  console.info('hook received');\n  return { queued: true, body };\n}\n")),
    ],
    [
        new("feature/retry-backoff", true, [Commit("Back off exponentially, capped at a minute", 1, ("src/retry.ts", Retry + "\n"))]),
        new("fix/webhook-signature", true, [Commit("Check signatures in constant time", 0.5, ("src/signature.ts", "import { timingSafeEqual } from 'node:crypto';\n\nexport const same = (a: Buffer, b: Buffer) => a.length === b.length && timingSafeEqual(a, b);\n"))],
            Files(("src/signature.ts", "import { timingSafeEqual } from 'node:crypto';\n\n// TODO: accept the previous key during rotation\nexport const same = (a: Buffer, b: Buffer) => a.length === b.length && timingSafeEqual(a, b);\n"))),
        new("feature/rate-limit", true, [Commit("Limit requests per tenant", 0.2, ("src/limiter.ts", Limiter + "\n"))],
            Files(("test/limiter.test.ts", "import { TenantLimiter } from '../src/limiter';\n\ntest('stops at the limit', () => {\n  const limiter = new TenantLimiter();\n  expect(limiter.allow('a', 0)).toBe(false);\n});\n"))),
        new("spike/otel", true, [Commit("Try OpenTelemetry on the hot path", 9, ("src/tracing.ts", "export const tracer = { start: (name: string) => name };\n"))]),
    ],
        Staged: Files(("docs/retries.md", "# Retries\n\nDeliveries back off up to one minute, then go to the dead-letter queue.\n")),
        Modified: Files(("README.md", "# Harbor API\n\nReceives partner webhooks and queues deliveries.\n\n## Running\n\n    npm test\n"), ("notes.txt", "ask about tenant limit defaults\n")),
        Origin: true);

    private static IEnumerable<SeedChat> HarborChats()
    {
        yield return new SeedChat("harbor-retry", "claude", "harbor-api", "Exponential backoff for webhook retries").On("feature/retry-backoff").Ago(380).Is(SeedStatus.Settled).Costing("0.84")
            .Turn("Deliveries retry linearly and hammer partners when they're down. Make it exponential with a cap.")
            .Think("The retry delay lives in src/retry.ts. A cap matters more than the curve: without one a long outage leaves deliveries waiting for hours.")
            .Plan(("Read the current backoff", "completed"), ("Make it exponential with a cap", "completed"), ("Update the test", "completed"))
            .Read("src/retry.ts", "export function backoffMs(attempt: number): number {\n  return 1000 * attempt;\n}\n")
            .Edit("src/retry.ts", """
                --- a/src/retry.ts
                +++ b/src/retry.ts
                @@ -1,3 +1,3 @@
                 export function backoffMs(attempt: number): number {
                -  return 1000 * attempt;
                +  return Math.min(1000 * 2 ** attempt, 60_000);
                 }
                """)
            .Edit("test/retry.test.ts", """
                --- a/test/retry.test.ts
                +++ b/test/retry.test.ts
                @@ -1,3 +1,4 @@
                 import { backoffMs } from '../src/retry';
                 
                -test('grows', () => expect(backoffMs(2)).toBe(2000));
                +test('doubles', () => expect(backoffMs(2)).toBe(4000));
                +test('stops at a minute', () => expect(backoffMs(30)).toBe(60_000));
                """)
            .Run("npm test", " ✓ test/retry.test.ts (2)\n\n Test Files  1 passed (1)\n      Tests  2 passed (2)\n")
            .Say("Done. `backoffMs` now doubles each attempt and stops at **one minute**.\n\n- `src/retry.ts`: the new curve\n- `test/retry.test.ts`: covers doubling and the cap\n\nBoth tests pass.")
            .Context(41_200, 200_000)
            .End(inputTokens: 41_200, outputTokens: 3_900, costUsd: 0.84m);

        yield return new SeedChat("harbor-rate-limit", "codex", "harbor-api", "Per-tenant rate limits on /hooks").On("feature/rate-limit").Ago(32).Is(SeedStatus.Working).MarkUnread().Costing("1.12")
            .Turn("One noisy tenant is starving everyone else. Add a per-tenant limit in front of the webhook handler.")
            .Find("handle(", "src/server.ts:3:export const routes = { '/hooks': handle };\nsrc/webhooks.ts:1:export function handle(body: unknown) {\n")
            .Edit("src/limiter.ts", """
                --- /dev/null
                +++ b/src/limiter.ts
                @@ -0,0 +1,9 @@
                +export class TenantLimiter {
                +  private readonly counts = new Map<string, number>();
                +  allow(tenant: string, limit: number): boolean {
                +    const used = this.counts.get(tenant) ?? 0;
                +    if (used >= limit) return false;
                +    this.counts.set(tenant, used + 1);
                +    return true;
                +  }
                +}
                """)
            .Run("npm test -- limiter", " ✓ test/limiter.test.ts (1)\n      Tests  1 passed (1)\n")
            .Say("The limiter is in and tested, but nothing resets the counts yet. That needs a window, and picking one is a product call: **per minute** or **per hour**?\n\nThe workflow is waiting on your approval on the Board.")
            .End(inputTokens: 52_800, outputTokens: 4_400, costUsd: 1.12m);

        yield return new SeedChat("harbor-signature", "claude", "harbor-api", "Signature check during key rotation").On("fix/webhook-signature").Ago(175).Is(SeedStatus.Done).MarkUnread().Costing("0.97")
            .Turn("Partners rotating their signing key get rejected for the overlap window. Accept the previous key too.")
            .Think("Comparison has to stay constant time, so checking two keys should always do both comparisons rather than returning at the first match.")
            .Read("src/signature.ts", "import { timingSafeEqual } from 'node:crypto';\n\nexport const same = (a: Buffer, b: Buffer) => a.length === b.length && timingSafeEqual(a, b);\n")
            .Edit("src/signature.ts", """
                --- a/src/signature.ts
                +++ b/src/signature.ts
                @@ -1,3 +1,5 @@
                 import { timingSafeEqual } from 'node:crypto';
                 
                 export const same = (a: Buffer, b: Buffer) => a.length === b.length && timingSafeEqual(a, b);
                +export const matchesEither = (given: Buffer, current: Buffer, previous: Buffer) =>
                +  [same(given, current), same(given, previous)].some(Boolean);
                """)
            .Run("npm test", " ❯ test/webhook-signature.test.ts (4)\n   × rejects an expired previous key\n   × logs which key matched\n\n Test Files  1 failed (1)\n      Tests  2 failed | 2 passed (4)\n", failed: true)
            .Say("Two tests fail, and they're real: nothing expires the previous key, so a leaked old key would work forever.\n\nI've stopped here rather than guess at the overlap window. How long should the previous key stay valid?")
            .End(status: "failed", inputTokens: 38_100, outputTokens: 2_900, costUsd: 0.97m);

        yield return new SeedChat("harbor-otel", "codex", "harbor-api", "Spike: OpenTelemetry on the hot path").On("spike/otel").Ago(880).Is(SeedStatus.Settled)
            .Turn("Quick spike: how much does tracing every delivery cost us?")
            .Run("npm run bench", "baseline   41,200 req/s\ntraced     38,750 req/s  (-5.9%)\n")
            .Say("About **6%** at full sampling, under 1% at a 10% sample. It isn't worth merging as is; I'd sample and keep it behind a flag.")
            .End();

        yield return new SeedChat("harbor-dlq", "codex", "harbor-api", "Dead-letter queue for failed deliveries").Ago(95).Is(SeedStatus.Working).Pin()
            .Turn("Where should deliveries go after the last retry? Right now they vanish.")
            .Find("deadLetter", "(no matches)\n")
            .Say("Nothing handles this yet, so they do vanish. I can add a `dead_letters` table and move a delivery there on its final failure. Migration next, if you're happy with that.")
            .Waiting("Run the database migration?", "npm run db:migrate -- --name add_dead_letters")
            .End();

        yield return new SeedChat("harbor-readme", "claude", "harbor-api", "README: how to run it").Ago(1900).Is(SeedStatus.Settled)
            .Turn("The README doesn't say how to run anything.")
            .Edit("README.md", """
                --- a/README.md
                +++ b/README.md
                @@ -1,3 +1,7 @@
                 # Harbor API
                 
                 Receives partner webhooks and queues deliveries.
                +
                +## Running
                +
                +    npm test
                """)
            .Say("Added a short Running section.")
            .End(inputTokens: 6_200, outputTokens: 310, costUsd: 0.04m);

        yield return new SeedChat("harbor-flaky", "claude", "harbor-api", "Why is the retry test flaky?").Ago(640).Is(SeedStatus.Done)
            .Turn("test/retry.test.ts failed on CI twice this week and passes locally.")
            .Run("npm test -- --repeat 50", " Test Files  1 passed (1)\n      Tests  100 passed (100)\n")
            .Find("Date.now", "test/retry.test.ts:12:  const before = Date.now();\n")
            .Say("I couldn't reproduce it in 50 runs, but line 12 reads the real clock, which would explain CI only: a slow machine stretches the gap. Fake timers would remove the guess.")
            .End()
            .Turn("Do it.")
            .Edit("test/retry.test.ts", """
                --- a/test/retry.test.ts
                +++ b/test/retry.test.ts
                @@ -9,5 +9,5 @@
                -  const before = Date.now();
                +  vi.useFakeTimers();
                +  const before = Date.now();
                """)
            .Run("npm test", " Test Files  1 passed (1)\n      Tests  2 passed (2)\n")
            .Say("Done. The test no longer depends on how fast the machine is.")
            .End();

        yield return new SeedChat("harbor-logs", "codex", "harbor-api", "Stop logging request bodies").Ago(2300).Is(SeedStatus.Settled)
            .Turn("Are we logging webhook bodies anywhere? They can hold customer data.")
            .Approved("Search the logs folder?", "grep -rl 'body' logs/", "logs/app.2026-10-30.log\n")
            .Say("Yes, one debug line did. It's gone now, and `hook received` is all that gets logged.")
            .End(inputTokens: 9_800, outputTokens: 640, costUsd: 0.07m);

        yield return new SeedChat("harbor-queue-picture", "claude", "harbor-api", "Delivery dashboard looks wrong").Ago(55).Is(SeedStatus.Done).MarkUnread()
            .Turn("The delivery dashboard shows bars longer than the total. Screenshot attached.", attachments: 1)
            .Think("Bars longer than the total usually means the denominator is the current page rather than everything.")
            .Find("percent", "src/dashboard.ts:18:  const percent = row.count / page.total;\n")
            .Say("Found it: `src/dashboard.ts:18` divides by the page's total rather than the overall one, so a short page overflows. It's a one-line fix; shall I make it?")
            .End();

        yield return new SeedChat("harbor-staging-deploy", "codex", "harbor-api", "Deploy check on the test box").OnMachine(SeedDatabase.OfflineMachine).Ago(6).Is(SeedStatus.Working)
            .Turn("Run the smoke tests on the test box and tell me if the retry change behaves.")
            .Think("The box has its own checkout, so I'll pull first and then run only the smoke suite.")
            .Run("git pull --ff-only && npm run smoke", "Updating 4f1c2ab..9de07c3\n ✓ smoke/delivery.test.ts (6)\n")
            .Say("Smoke tests pass so far. Waiting on the slow retry case, which sleeps through a full backoff cycle.");

        yield return new SeedChat("harbor-tenant-audit", "claude", "harbor-api", "Which tenants would the limit hit?").Ago(260).Is(SeedStatus.Done)
            .Turn("Before the limit ships, who would it have stopped last week?")
            .Helper(new SeedChat("harbor-tenant-audit-helper", "claude", "harbor-api", "Count requests per tenant")
                .Turn("Count last week's requests per tenant from the access logs.")
                .Run("awk '{print $4}' logs/access.log | sort | uniq -c | sort -rn | head -3", " 918211 tenant-northwind\n  40122 tenant-aurora\n  11873 tenant-cobalt\n")
                .Say("Northwind sent 918k requests; the next tenant sent 40k.")
                .End(), "Count last week's requests per tenant")
            .Background("watch access log")
            .Say("Only **Northwind** would hit a 100k limit, by a wide margin. Everyone else sits under half of it.")
            .End();
    }
}
