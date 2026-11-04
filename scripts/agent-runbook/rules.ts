// Condensed operational instructions. Keep these aligned with the canonical AGENTS.md files.
export function rules(project = '') {
  const common = [
    '# Recovery rules',
    '- Follow Jesse’s current direction and the approved spec/plan. Never invent his words or approval; raise conflicts.',
    '- If you have a spawning parent, check with them before resuming; report progress, questions, blockers, deviations and proof. Only the parent writes this runbook.',
    '- Notion is canonical for tasks, decisions and documents. This runbook holds live state; checkpoint it to the task at milestones/handoff. No writable mirrors.',
    '- Never edit AGENTS.md/CLAUDE.md without Jesse’s explicit ask or merge to main without his explicit permission.',
    '- Use isolated worktrees; protect others’ work, live data and credentials. No git reset --hard or killing by name.',
    '- Keep one implementation, small responsibilities and fast/light behavior. Atomic saves; related writes share a transaction. Push state, avoid polling.',
    '- Independent verification needs real, inspectable proof on Drive, linked from the task and PR. Missing proof means REDO.',
  ];
  if (project === 'Assembly') common.push('- Assembly: C# backend, Rust GPUI, separate remote relay. Follow the approved port plan/intended behavior; verify affected providers, local/remote and mobile.');
  if (project === 'rental-management') common.push('- Rental Command: preserve portfolio authorization and live data; related writes commit together and external effects use the outbox. Use the project’s local preview and verify affected web/mobile flows.');
  return common.join('\n');
}
