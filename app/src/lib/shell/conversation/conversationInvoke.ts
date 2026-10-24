import { invoke } from '@tauri-apps/api/core';
import type { ConversationCommandError } from './conversationTypes.ts';

export type ConversationCommandInvoker = (
  command: string,
  args?: Record<string, unknown>
) => Promise<unknown>;

export type ConversationCommandLogger = (
  command: string,
  error: ConversationCommandError
) => void;

/** Gives non-Error Tauri rejections the standard Error behavior callers expect. */
export class ConversationInvokeError extends Error implements ConversationCommandError {
  readonly code: string;
  readonly recoverable: boolean;

  constructor(error: ConversationCommandError) {
    super(error.message);
    this.name = 'ConversationInvokeError';
    this.code = error.code;
    this.recoverable = error.recoverable;
  }
}

/** Converts any native rejection into the conversation command error contract. */
function normalizeConversationCommandError(error: unknown): ConversationInvokeError {
  if (error instanceof ConversationInvokeError) return error;
  if (error && typeof error === 'object') {
    const value = error as Partial<ConversationCommandError>;
    if (typeof value.message === 'string') {
      return new ConversationInvokeError({
        code: typeof value.code === 'string' ? value.code : 'agent-conversation-command-failed',
        message: value.message,
        recoverable: typeof value.recoverable === 'boolean' ? value.recoverable : false
      });
    }
  }
  return new ConversationInvokeError({
    code: 'agent-conversation-command-failed',
    message: error instanceof Error ? error.message : String(error),
    recoverable: false
  });
}

/** Writes only the command name and typed error fields to the existing frontend log. */
function logConversationCommandError(command: string, error: ConversationCommandError): void {
  console.warn('Conversation command failed.', {
    command,
    code: error.code,
    message: error.message
  });
}

/** Invokes one conversation command, logs a sanitized failure, and rethrows it as a typed Error. */
export async function invokeConversationCommand<T>(
  command: string,
  args?: Record<string, unknown>,
  invokeNative: ConversationCommandInvoker = invoke,
  logger: ConversationCommandLogger = logConversationCommandError
): Promise<T> {
  try {
    return await invokeNative(command, args) as T;
  } catch (error) {
    const typedError = normalizeConversationCommandError(error);
    logger(command, typedError);
    throw typedError;
  }
}
