// CLI hook protocol differences belong at this boundary, not in runbook state handling.
export const harnesses = {
  claude: {
    settingsFile: '.claude/settings.json',
    hookOptions: {},
    compactExit: 2,
    compactFailure: (message: string) => ({ decision: 'block', reason: message }),
    usage: (row: any) => {
      const info = row.message?.usage;
      return { window: info?.model_context_window || info?.context_window, tokens: info ? Number(info.input_tokens || 0) + Number(info.cache_read_input_tokens || 0) + Number(info.cache_creation_input_tokens || 0) + Number(info.output_tokens || 0) : undefined };
    },
  },
  codex: {
    settingsFile: '.codex/hooks.json',
    hookOptions: { additionalContextLimit: 4000 },
    compactExit: 0,
    compactFailure: (message: string) => ({ continue: false, stopReason: message }),
    usage: (row: any) => {
      const info = row.payload?.type === 'token_count' ? row.payload.info : undefined;
      return { window: info?.model_context_window, tokens: info?.last_token_usage?.total_tokens };
    },
  },
};
export function harnessFor(provider: string) {
  if (!Object.hasOwn(harnesses, provider)) throw Error('Expected provider codex|claude.');
  return harnesses[provider as keyof typeof harnesses];
}
