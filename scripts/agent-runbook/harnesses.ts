// CLI hook protocol differences belong at this boundary, not in runbook state handling.
export const harnesses = {
  claude: {
    settingsFile: '.claude/settings.json',
    hookOptions: {},
    compactExit: 2,
    compactFailure: (message: string) => ({ decision: 'block', reason: message }),
  },
  codex: {
    settingsFile: '.codex/hooks.json',
    hookOptions: { additionalContextLimit: 4000 },
    compactExit: 0,
    compactFailure: (message: string) => ({ continue: false, stopReason: message }),
  },
};
export function harnessFor(provider: string) {
  if (!Object.hasOwn(harnesses, provider)) throw Error('Expected provider codex|claude.');
  return harnesses[provider as keyof typeof harnesses];
}
