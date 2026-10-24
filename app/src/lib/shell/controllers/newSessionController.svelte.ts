/** Owns the draft surface and the side effects of its first send. */
import {
	getConversationSession,
	setConversationAttachments,
	setConversationDraft,
	setConversationSendError,
} from '../conversation/conversationStore.svelte';
import { ensureStructuredConversation, flushConversationSessionDraft, persistConversationSessionDraft, saveConversationClipboardImage, sendStructuredMessage } from '../conversation/conversationService';
import type { ThreadStartRequest } from '../conversation/newSession/threadStartFlow';
import {
	createFreshSession,
	ownedSessionMetaForBackend,
} from '../ownedSessions';
import { projectRegistry } from '../projects/projectRegistry.svelte';
import { shellPanels } from '../shellPanels';
import {
	addOwnedSession,
	rail,
	updateOwnedSession,
} from '../stores/sessionRailStore.svelte';
import { updateAgentConversationSessionMetaFromTauri } from '../../tauriSource';

export class NewSessionController {
	draftOpen = $state(false);
	/** The project the draft opens in; null lets the draft choose its default. */
	projectId = $state<string | null>(null);
	/** The folder of that project the draft starts in, such as one of its worktrees; null is its root. */
	folder = $state<string | null>(null);

	// State so the surface follows a re-open: a new signal is a new draft.
	private draftWork = $state.raw(new AbortController());
	private startingOwnedId: string | null = null;
	pendingFirstMessage = $state<{ ownedId: string; text: string } | null>(null);

	get stopSignal(): AbortSignal {
		return this.draftWork.signal;
	}

	/** An open draft is kept, typing and all, unless a different project or folder is asked for. */
	open(projectId: string | null = null, folder: string | null = null): void {
		if (this.draftOpen && (projectId === null || (projectId === this.projectId && folder === this.folder))) return;
		this.stopDraftWork();
		this.draftWork = new AbortController();
		this.projectId = projectId;
		this.folder = folder;
		this.draftOpen = true;
	}

	close(): void {
		this.draftOpen = false;
		this.stopDraftWork();
	}

	/** One session-selection seam: commenting out this call leaves drafts alone. */
	abandonDraftForSessionSwitch(ownedId: string): void {
		if (this.startingOwnedId === ownedId) return;
		this.draftOpen = false;
		this.stopDraftWork();
	}

	async start(
		request: ThreadStartRequest,
		images: File[],
		selectSession: (ownedId: string) => Promise<void>,
		showSession: () => void,
		persistAttachmentIds: (ownedId: string, ids: readonly string[]) => Promise<void>,
		discardSession: (ownedId: string) => Promise<void>,
	): Promise<string> {
		const stopSignal = this.stopSignal;
		if (stopSignal.aborted) throw new Error('the new session draft was closed');

		const project = projectRegistry.projects.find((candidate) => candidate.id === request.projectId);
		const owned = {
			...createFreshSession({
				cwd: request.cwd,
				title: request.title,
				executionEnvironment: request.executionEnvironment,
				remoteProfileId: request.remoteProfileId,
			}),
			agent: request.provider,
			projectId: request.projectId,
			projectGroupKey: project?.groupKey ?? 'none',
			projectGroupLabel: project?.title ?? 'No project',
			resumeCommand: null,
			origin: 'app' as const,
		};
		this.startingOwnedId = owned.ownedId;
		this.pendingFirstMessage = { ownedId: owned.ownedId, text: request.prompt };
		addOwnedSession(owned);
		updateOwnedSession(owned.ownedId, {
			state: 'live',
			executionOwner: 'structured',
			runtimeState: 'starting',
			lastError: null,
		});
		shellPanels.allowSessionLoads();
		let promptAccepted = false;

		try {
			await selectSession(owned.ownedId);
			if (stopSignal.aborted) {
				updateOwnedSession(owned.ownedId, {
					state: 'background',
					executionOwner: 'stopped',
					runtimeState: 'closed',
				});
				return owned.ownedId;
			}

			showSession();
			if (images.length) {
				const connection = await ensureStructuredConversation({
					ownedId: owned.ownedId,
					executionEnvironment: owned.executionEnvironment,
					remoteProfileId: owned.remoteProfileId,
					provider: request.provider,
					cwd: owned.cwd,
					reasoningEffort: request.reasoningEffort,
					projectId: request.projectId,
					signal: stopSignal,
				});
				if (!connection) throw new Error('The new conversation could not be started');
			}
			for (const file of images) {
				const attachment = await saveConversationClipboardImage(owned.ownedId, file);
				const current = getConversationSession(owned.ownedId);
				setConversationAttachments(owned.ownedId, [...(current?.attachments ?? []), attachment]);
				await persistAttachmentIds(owned.ownedId, getConversationSession(owned.ownedId)?.attachmentIds ?? []);
			}
			await sendStructuredMessage(owned.ownedId, request.prompt, {
				reasoningEffort: request.reasoningEffort,
				model: request.model,
				approvalPolicy: request.approvalPolicy,
			});
			promptAccepted = true;
			this.draftOpen = false;
			// A switch may stop draft ownership, but it must not cancel the agent turn
			// that sendStructuredMessage has already handed to the background runtime.
			const stillPresented = !stopSignal.aborted;
			await this.persistOwnedMetadata(owned.ownedId);
			updateOwnedSession(owned.ownedId, { runtimeState: 'ready', lastError: null });
			if (stillPresented && !stopSignal.aborted) showSession();
			return owned.ownedId;
		} catch (error) {
			const detail = this.describeError(error);
			// The draft is still open with the prompt and this error on it, so the
			// row made for the send would only be a second, failed copy in the rail.
			// If it cannot be removed, it is kept and marked failed below.
			let discarded = false;
			if (!promptAccepted && !stopSignal.aborted) {
				try {
					await discardSession(owned.ownedId);
					discarded = true;
				} catch {
					// The row stays and is marked failed below.
				}
			}
			if (discarded) throw error;
			if (!promptAccepted) {
				const currentDraft = getConversationSession(owned.ownedId)?.draft ?? '';
				const draft = currentDraft ? `${request.prompt}\n\n${currentDraft}` : request.prompt;
				setConversationDraft(owned.ownedId, draft);
				setConversationSendError(owned.ownedId, detail);
				persistConversationSessionDraft(owned.ownedId, draft);
				try { await flushConversationSessionDraft(owned.ownedId); } catch {
					// Keep the draft in memory and report the original send failure.
				}
			}
			updateOwnedSession(owned.ownedId, {
				state: 'exited',
				executionOwner: 'stopped',
				runtimeState: 'failed',
				lastError: detail,
			});
			rail.error = `could not start ${owned.agent} session: ${detail}`;
			throw error;
		} finally {
			if (this.startingOwnedId === owned.ownedId) this.startingOwnedId = null;
			if (this.pendingFirstMessage?.ownedId === owned.ownedId) this.pendingFirstMessage = null;
		}
	}

	dispose(): void {
		this.draftOpen = false;
		this.startingOwnedId = null;
		this.stopDraftWork();
	}

	private stopDraftWork(): void {
		this.draftWork.abort();
		this.pendingFirstMessage = null;
	}

	private async persistOwnedMetadata(ownedId: string): Promise<void> {
		const session = rail.owned.find((entry) => entry.ownedId === ownedId);
		if (!session) return;
		await updateAgentConversationSessionMetaFromTauri({
			ownedId,
			model: session.model ?? null,
			effort: getConversationSession(ownedId)?.agentConfig.reasoningEffort ?? null,
			meta: ownedSessionMetaForBackend(session),
		});
	}

	private describeError(error: unknown): string {
		return error instanceof Error ? error.message : String(error);
	}
}
