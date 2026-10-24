//! This Mac's requests about past provider transcripts on a remote machine.
//! They are found, read and imported there; this Mac only ever reads the rows
//! the history download saves.
use std::{collections::BTreeMap, sync::atomic::Ordering};

use mcb_core::scanners::sessions::AgentSessionRecord;

use super::wire::{RemoteCommand, RemoteResponse};
use super::{RemoteConnectionManager, RemoteHistory};
use crate::agent_conversation::history::ExtendedImport;
use crate::agent_conversation::protocol::AgentConversationProvider;

impl RemoteConnectionManager {
    /// Every past session the machine's scanner finds, or one transcript's details when `log_path` is set.
    pub async fn scan_transcripts(&self, profile: &str, log_path: Option<String>, project_roots: Vec<String>) -> Result<(Vec<AgentSessionRecord>, BTreeMap<String, String>), String> {
        match self.request_for_profile(profile, RemoteCommand::ScanTranscripts { log_path, project_roots }).await? {
            RemoteResponse::Transcripts { sessions, common_dirs } => Ok((sessions, common_dirs)),
            _ => Err("Remote Assembly returned the wrong transcript response".into()),
        }
    }

    /// Names a transcript on `profile` as a session there, filed under this Mac's
    /// project for that folder, and caches the session here. The machine reads the
    /// newest page; the history download brings the rest when the session opens.
    pub async fn begin_import(&self, profile: String, provider: AgentConversationProvider, native_session_id: String,
        transcript_path: String, cwd: String, title: Option<String>) -> Result<(String, bool), String> {
        let project_id = self.store.match_folders_by_path(&serde_json::json!([cwd]).to_string(), &profile)
            .map_err(|error| error.to_string())?.into_iter().next().map(|(_, project_id)| project_id);
        let RemoteResponse::Imported { owned_id, rebuilt } = self.request_for_profile(&profile, RemoteCommand::BeginImport {
            provider, native_session_id, transcript_path, cwd, title, project_id }).await?
        else { return Err("Remote Assembly returned the wrong import response".into()); };
        if rebuilt {
            // Its turns are read again there, so the copy saved here is dropped as a deleted session's is.
            let active = self.downloads.lock().unwrap().remove(&RemoteHistory::key(&profile, &owned_id)).and_then(|entry| entry.request_id);
            if let Some(id) = active { self.cancel_request(id).await; }
            self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).forget(&profile, &owned_id)?;
        }
        self.list_sessions(self.next_request_id.fetch_add(1, Ordering::Relaxed)).await?;
        Ok((owned_id, rebuilt))
    }

    pub async fn extend_import(&self, owned_id: String) -> Result<ExtendedImport, String> {
        let profile = self.profile_for_owned_id(&owned_id)?;
        self.extend_import_from(profile, owned_id).await
    }

    pub async fn extend_child_import(
        &self,
        profile: String,
        source_owned_id: String,
    ) -> Result<ExtendedImport, String> {
        self.extend_import_from(profile, source_owned_id).await
    }

    async fn extend_import_from(
        &self,
        profile: String,
        owned_id: String,
    ) -> Result<ExtendedImport, String> {
        let epoch = self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).epoch(&profile);
        let RemoteResponse::ImportProgress { added, reached_start } = self.request_for_profile(&profile,
            RemoteCommand::ExtendImport { owned_id: owned_id.clone() }).await?
        else { return Err("Remote Assembly returned the wrong import response".into()); };
        if added > 0 {
            let active = self.downloads.lock().unwrap().get_mut(&RemoteHistory::key(&profile, &owned_id))
                .and_then(|entry| entry.request_id.take());
            if let Some(id) = active { self.cancel_request(id).await; }
        }
        {
            let history = self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            history.check(&profile, epoch)?;
            if added > 0 || !reached_start { history.imported_older(&profile, epoch, &owned_id)?; }
        }
        if added > 0 { self.ensure_history_download(&profile, &owned_id).await?; }
        Ok(ExtendedImport { added, reached_start })
    }
}

#[cfg(test)]
mod tests {
    use super::super::connection_tests::{begin_test_download, download_manager, history_event, session};
    use super::super::{encode_history_chunk, ClientRequest};
    use super::*;
    use crate::agent_conversation::protocol::AgentConversationSessionRecord;

    #[tokio::test]
    async fn a_rebuilt_import_drops_the_copy_saved_here() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 100, Some(1), Some(1)).await;
        manager.receive_history_chunk("cache-test", id, "large-history", 100, encode_history_chunk(&[history_event(1)]).unwrap()).unwrap();
        manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
        let importer = manager.clone();
        let import = tokio::spawn(async move { importer.begin_import("cache-test".into(), AgentConversationProvider::Claude,
            "native".into(), "/home/me/t.jsonl".into(), "/home/me".into(), None).await });
        let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected import"); };
        assert!(matches!(command, RemoteCommand::BeginImport { .. }));
        reply.send(Ok(RemoteResponse::Imported { owned_id: "large-history".into(), rebuilt: true })).unwrap();
        let ClientRequest::Execute { reply, .. } = requests.recv().await.unwrap() else { panic!("expected listing"); };
        reply.send(Ok(RemoteResponse::Sessions(Vec::new()))).unwrap();
        assert_eq!(import.await.unwrap().unwrap(), ("large-history".to_string(), true));
        assert!(manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().is_none());
    }

    #[tokio::test]
    async fn a_session_read_again_on_its_machine_drops_the_copy_another_mac_saved() {
        let manager = download_manager();
        let (id, _requests) = begin_test_download(&manager, 100, Some(1), Some(1)).await;
        manager.receive_history_chunk("cache-test", id, "large-history", 100, encode_history_chunk(&[history_event(1)]).unwrap()).unwrap();
        manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
        let saved = || manager.store.remote_saved_bounds(&RemoteHistory::key("cache-test", "large-history")).unwrap().0;
        let listed = |numbered_after| [AgentConversationSessionRecord { numbered_after, ..session("large-history", "cache-test", 1) }];
        // A machine too old to say where its journal restarted keeps the copy.
        manager.replace_cached_profile_sessions("cache-test", &listed(None)).unwrap();
        assert_eq!(saved(), Some(1));
        // Another Mac read it again from its transcript there: the turns saved here would be shown twice.
        manager.replace_cached_profile_sessions("cache-test", &listed(Some(3))).unwrap();
        assert_eq!(saved(), None);
    }

    #[tokio::test]
    async fn a_session_read_again_on_its_machine_keeps_the_project_it_was_moved_to_here() {
        let manager = download_manager();
        for id in ["a", "b"] {
            manager.store.insert_or_get_project(&mcb_core::session_store::ProjectRow {
                id: id.into(), machine: "cache-test".into(), root_path: format!("/work/{id}"), title: id.into(),
                repo_key: String::new(), created_at_ms: 0, group_key: String::new(), pinned_at_ms: None, last_used_ms: None,
            }).unwrap();
        }
        let key = RemoteHistory::key("cache-test", "large-history");
        let listed = |numbered_after| [AgentConversationSessionRecord { numbered_after, project_id: Some("a".into()), ..session("large-history", "cache-test", 1) }];
        manager.replace_cached_profile_sessions("cache-test", &listed(None)).unwrap();
        assert!(manager.store.move_session_project(&key, "b").unwrap());
        manager.replace_cached_profile_sessions("cache-test", &listed(Some(3))).unwrap();
        assert_eq!(manager.store.session_projects(&serde_json::json!([key]).to_string()).unwrap(), vec![Some("b".to_string())]);
    }

    #[tokio::test]
    async fn a_download_running_when_another_mac_reads_the_session_again_saves_nothing_more() {
        let manager = download_manager();
        let (id, _requests) = begin_test_download(&manager, 100, Some(1), Some(2)).await;
        let listed = |numbered_after| [AgentConversationSessionRecord { numbered_after, ..session("large-history", "cache-test", 1) }];
        manager.replace_cached_profile_sessions("cache-test", &listed(None)).unwrap();
        manager.replace_cached_profile_sessions("cache-test", &listed(Some(3))).unwrap();
        // A chunk the old stream had already sent: its rows belong to the journal that was replaced.
        manager.receive_history_chunk("cache-test", id, "large-history", 50, encode_history_chunk(&[history_event(2)]).unwrap()).unwrap();
        assert_eq!(manager.store.remote_saved_bounds(&RemoteHistory::key("cache-test", "large-history")).unwrap().0, None);
    }
}
