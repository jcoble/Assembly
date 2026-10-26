// The context key for the conversation's tool detail reader: (itemId) => ToolDetail.
export const TOOL_DETAIL = Symbol('tool-detail');
// Answers an ask card: (askId, answer) => the POST.
export const ASK_ANSWER = Symbol('ask-answer');
export const TASK_STOP = Symbol('task-stop');
// Opens another session by id (a subagent's, or its parent).
export const SESSION_OPEN = Symbol('session-open');

// Bash's command, from the call's arguments (the detail's `input` JSON).
export function commandOf(name: string, input: string | null): string | null {
  if (name !== 'Bash' || !input) return null;
  try {
    const command = (JSON.parse(input) as { command?: unknown } | null)?.command;
    return typeof command === 'string' ? command : null;
  } catch {
    return null;
  }
}

// The last `shown` lines of a tool's output, and how many lines are folded
// above them, so a huge output never mounts more than `shown` lines.
export function foldOutput(output: string, shown: number): { hidden: number; text: string } {
  const lines = output.replace(/\n$/, '').split('\n');
  const hidden = Math.max(0, lines.length - shown);
  return { hidden, text: hidden ? lines.slice(hidden).join('\n') : lines.join('\n') };
}
