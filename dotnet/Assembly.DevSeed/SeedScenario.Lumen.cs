namespace Assembly.DevSeed;

public static partial class SeedScenario
{
    private const string AuditYaml = """
        name: Audit
        limits: {max_sessions: 1, step_timeout_minutes: 20, max_rounds: 1}
        steps:
          scan:
            run: ["git grep -n TODO"]
            display: {present: Scanning, past: Scanned}
          report:
            needs: [scan]
            agent: {provider: claude}
            prompt: prompts/report.md
            result: recon
            display: {present: Writing the report, past: Report written}
        """;

    private static SeedRepo LumenUi => new("lumen-ui", "Lumen UI",
    [
        Commit("Start the component library", 30,
            ("README.md", "# Lumen UI\n\nThe shared components for the Lumen dashboard.\n"),
            ("src/button.tsx", "export function Button({ label }: { label: string }) {\n  return <button style={{ background: '#2563eb' }}>{label}</button>;\n}\n"),
            ("src/card.tsx", "export function Card({ children }: { children: React.ReactNode }) {\n  return <div style={{ padding: 16, border: '1px solid #e5e7eb' }}>{children}</div>;\n}\n")),
        Commit("Add the audit workflow", 8,
            (".assembly/workflows/audit.yml", AuditYaml + "\n"),
            (".assembly/workflows/prompts/report.md", "Write up what the scan found: ${{ inputs.request }}\n")),
        Commit("Add a tooltip", 3,
            ("src/tooltip.tsx", "export function Tooltip({ text }: { text: string }) {\n  return <span role=\"tooltip\" style={{ color: '#6b7280' }}>{text}</span>;\n}\n")),
    ],
    [
        new("theme-tokens", true, [Commit("Name the colors", 0.4, ("src/tokens.ts", "export const color = {\n  accent: '#2563eb',\n  border: '#e5e7eb',\n  muted: '#6b7280',\n};\n"))],
            Files(("src/button.tsx", "import { color } from './tokens';\n\nexport function Button({ label }: { label: string }) {\n  return <button style={{ background: color.accent }}>{label}</button>;\n}\n"))),
        new("fix/focus-ring", false, [Commit("Show a focus ring on buttons", 4, ("src/button.css", "button:focus-visible {\n  outline: 2px solid currentColor;\n}\n"))]),
    ],
        Staged: new Dictionary<string, string>(),
        Modified: Files(("src/tooltip.tsx", "export function Tooltip({ text }: { text: string }) {\n  return <span role=\"tooltip\" style={{ color: '#4b5563' }}>{text}</span>;\n}\n")));

    private static IEnumerable<SeedChat> LumenChats()
    {
        yield return new SeedChat("lumen-tokens", "claude", "lumen-ui", "Move hard-coded colors to tokens").On("theme-tokens").Ago(10).Is(SeedStatus.Working).Costing("0.61")
            .Turn("Every component hard-codes its colors. Pull them into one tokens file so a dark theme is possible.")
            .Plan(("Find every hard-coded color", "completed"), ("Name them in src/tokens.ts", "completed"), ("Swap the button", "completed"), ("Swap the card and tooltip", "in_progress"), ("Check the contrast", "pending"))
            .Find("#[0-9a-f]{6}", "src/button.tsx:2:#2563eb\nsrc/card.tsx:2:#e5e7eb\nsrc/tooltip.tsx:2:#6b7280\n")
            .Edit("src/button.tsx", """
                --- a/src/button.tsx
                +++ b/src/button.tsx
                @@ -1,3 +1,5 @@
                +import { color } from './tokens';
                +
                 export function Button({ label }: { label: string }) {
                -  return <button style={{ background: '#2563eb' }}>{label}</button>;
                +  return <button style={{ background: color.accent }}>{label}</button>;
                 }
                """)
            .Say("The button is done. The card and tooltip are next; the workflow is auditing the rest of the repo meanwhile.")
            .Context(88_000, 200_000)
            .End(inputTokens: 88_000, outputTokens: 5_300, costUsd: 0.61m);

        yield return new SeedChat("lumen-contrast", "codex", "lumen-ui", "Tooltip contrast fails AA").Ago(48).Is(SeedStatus.Working).MarkUnread()
            .Turn("Accessibility review flagged the tooltip text. Screenshot attached, the grey is hard to read.", attachments: 1)
            .Read("src/tooltip.tsx", "export function Tooltip({ text }: { text: string }) {\n  return <span role=\"tooltip\" style={{ color: '#6b7280' }}>{text}</span>;\n}\n")
            .Say("`#6b7280` on white is 4.4:1, just under the 4.5:1 AA asks for. `#4b5563` gets 7.5:1 and still reads as secondary text.")
            .Edit("src/tooltip.tsx", """
                --- a/src/tooltip.tsx
                +++ b/src/tooltip.tsx
                @@ -1,3 +1,3 @@
                 export function Tooltip({ text }: { text: string }) {
                -  return <span role="tooltip" style={{ color: '#6b7280' }}>{text}</span>;
                +  return <span role="tooltip" style={{ color: '#4b5563' }}>{text}</span>;
                 }
                """)
            .Say("Changed but not committed, so you can look at it first.")
            .End();

        yield return new SeedChat("lumen-focus", "codex", "lumen-ui", "Keyboard focus ring on buttons").Ago(5800).Is(SeedStatus.Settled)
            .Turn("Tabbing through the dashboard shows no focus at all.")
            .Edit("src/button.css", """
                --- /dev/null
                +++ b/src/button.css
                @@ -0,0 +1,3 @@
                +button:focus-visible {
                +  outline: 2px solid currentColor;
                +}
                """)
            .Say("Added `:focus-visible` so mouse clicks don't draw a ring but the keyboard does.")
            .End(inputTokens: 7_400, outputTokens: 420, costUsd: 0.05m);

        yield return new SeedChat("lumen-card-api", "claude", "lumen-ui", "Should Card take a title prop?").Ago(1500).Is(SeedStatus.Done)
            .Turn("Half the cards have a heading and each one styles it differently. Should Card take a `title`?")
            .Find("<Card>", "src/pages/usage.tsx:12:<Card><h3>Usage</h3>\nsrc/pages/billing.tsx:30:<Card><h2>Billing</h2>\nsrc/pages/team.tsx:8:<Card><b>Team</b>\n")
            .Say("Yes. Three pages, three different headings, and nothing about them needs to differ.\n\n| Page | Today | After |\n|---|---|---|\n| usage | `h3` | `title` |\n| billing | `h2` | `title` |\n| team | `b` | `title` |\n\nI'd make it optional and render an `h3`.")
            .End();

        yield return new SeedChat("lumen-storybook", "codex", "lumen-ui", "Storybook build broke").Ago(3100).Is(SeedStatus.Ended)
            .Turn("`npm run storybook` dies on startup after the last dependency bump.")
            .Run("npm run storybook", "ERR! Cannot find module '@storybook/react/preset'\n", failed: true)
            .Say("The bump moved the preset to `@storybook/react-vite`. I've pointed `.storybook/main.ts` at it.")
            .Approved("Install the new preset?", "npm install -D @storybook/react-vite", "added 14 packages in 3s\n")
            .Run("npm run storybook -- --smoke-test", "Storybook started on port 6006\n")
            .Say("It starts again.")
            .End();

        yield return new SeedChat("lumen-bundle", "claude", "lumen-ui", "Bundle size jumped 40 KB").Ago(210).Is(SeedStatus.Done)
            .Turn("Our bundle grew 40 KB last week. Find out why.")
            .Run("npx vite-bundle-visualizer --json | head -5", "date-fns           38.2 KB\nreact-dom          31.1 KB\nsrc/                9.4 KB\n")
            .Find("date-fns", "src/pages/usage.tsx:2:import { format } from 'date-fns';\n")
            .Say("A single `format` call pulled in `date-fns`. `Intl.DateTimeFormat` does what that page needs for nothing. Want me to swap it?")
            .End()
            .Turn("Yes please.")
            .Edit("src/pages/usage.tsx", """
                --- a/src/pages/usage.tsx
                +++ b/src/pages/usage.tsx
                @@ -1,4 +1,4 @@
                -import { format } from 'date-fns';
                +const format = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });
                """)
            .Run("npm run build", "dist/index.js  212.40 kB\n✓ built in 2.1s\n")
            .Say("Back down to 212 KB.")
            .End(inputTokens: 26_400, outputTokens: 1_850, costUsd: 0.33m);

        yield return new SeedChat("lumen-modal", "claude", "lumen-ui", "Modal traps focus wrongly").Ago(22).Is(SeedStatus.Working)
            .Turn("Pressing Escape in the settings modal leaves focus on nothing. Here are the before and after shots.", attachments: 2)
            .Think("Escape closes the modal but doesn't return focus to whatever opened it. The opener has to be remembered when the modal opens.")
            .Say("It never remembers who opened it. I'll store `document.activeElement` on open and restore it on close.")
            .Waiting("Write src/modal.tsx?", "Edit src/modal.tsx: remember the opener and restore focus on close")
            .End();

        yield return new SeedChat("lumen-i18n", "codex", "lumen-ui", "Which strings aren't translated?").Ago(4300).Is(SeedStatus.Settled)
            .Turn("Roughly how much of the UI is still hard-coded English?")
            .Find(">[A-Z][a-z]+", "src/pages/usage.tsx:12\nsrc/pages/billing.tsx:30\nsrc/pages/team.tsx:8\n(37 more)\n")
            .Say("About 40 strings across 11 files. It's a day of work if the keys don't need review, more if they do.")
            .End(inputTokens: 8_800, outputTokens: 500, costUsd: 0.06m);

        yield return new SeedChat("lumen-outage", "codex", "lumen-ui", "Rename the token file").Ago(740).Is(SeedStatus.Done)
            .Turn("Rename src/tokens.ts to src/theme.ts and fix the imports.")
            .Find("from './tokens'", "src/button.tsx:1\n")
            .Error("The provider stopped responding before this turn finished.", "provider_exited")
            .End(status: "failed", inputTokens: 3_100, outputTokens: 90, costUsd: 0.01m);
    }
}
