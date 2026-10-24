use super::super::*;

#[tauri::command]
pub(crate) async fn find_source_lsp_definitions(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspDefinitionTarget>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_definitions(preview, request))
        .await
        .map_err(|error| format!("Source LSP definition task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_completions(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspCompletionItem>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_completions(preview, request))
        .await
        .map_err(|error| format!("Source LSP completion task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_implementations(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspDefinitionTarget>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_implementations(preview, request))
        .await
        .map_err(|error| format!("Source LSP implementation task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_type_definitions(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspDefinitionTarget>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_type_definitions(preview, request))
        .await
        .map_err(|error| format!("Source LSP type-definition task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_document_highlights(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspDocumentHighlight>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        registry.find_document_highlights(preview, request)
    })
    .await
    .map_err(|error| format!("Source LSP document-highlight task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_signature_help(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Option<lsp::SourceLspSignatureHelp>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_signature_help(preview, request))
        .await
        .map_err(|error| format!("Source LSP signature-help task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_inlay_hints(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspInlayHint>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_inlay_hints(preview, request))
        .await
        .map_err(|error| format!("Source LSP inlay-hint task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_semantic_tokens(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspSemanticToken>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_semantic_tokens(preview, request))
        .await
        .map_err(|error| format!("Source LSP semantic-token task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_workspace_symbols(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspWorkspaceSymbolRequest,
) -> Result<Vec<lsp::SourceLspWorkspaceSymbol>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_workspace_symbols(preview, request))
        .await
        .map_err(|error| format!("Source LSP workspace-symbol task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn format_source_with_lsp(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspTextEdit>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.format_document(preview, request))
        .await
        .map_err(|error| format!("Source LSP formatting task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn rename_source_with_lsp(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspRenameRequest,
) -> Result<lsp::SourceLspRenameResult, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.rename(preview, request))
        .await
        .map_err(|error| format!("Source LSP rename task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_code_actions(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspCodeActionRequest,
) -> Result<Vec<lsp::SourceLspCodeAction>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_code_actions(preview, request))
        .await
        .map_err(|error| format!("Source LSP code action task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_references(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspReferenceTarget>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_references(preview, request))
        .await
        .map_err(|error| format!("Source LSP reference task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_hover(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Option<lsp::SourceLspHover>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_hover(preview, request))
        .await
        .map_err(|error| format!("Source LSP hover task failed: {error}"))?
}

/// Every symbol in one file, as the editor's margin counts want them: flattened, with
/// both numbers counted from zero.
///
/// This is what switches the margin counts on for Rust and Svelte, where the app's own
/// pattern-based symbol reader has never worked. Unlike the other language-server
/// lookups it is handed a path rather than the editor's copy of the text, and reads the
/// file from disk itself.
#[tauri::command]
pub(crate) async fn find_source_lsp_document_symbols(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
    language: String,
    path: String,
) -> Result<Vec<lsp::SourceLspDocumentSymbol>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        registry.find_document_symbols(root, language, path)
    })
    .await
    .map_err(|error| format!("Source LSP document symbol task failed: {error}"))?
}

/// The last few hundred lines a running language server printed to its own error output.
///
/// When a server refuses to answer, this is usually the only explanation there is. Empty
/// when no server for that language is running.
#[tauri::command]
pub(crate) async fn read_source_lsp_log(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
    language: String,
) -> Result<Vec<String>, String> {
    // The project folder is accepted but not used: one server per language serves every
    // project, so there is only ever one log to hand back.
    let _ = root;
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.read_server_log(&language))
        .await
        .map_err(|error| format!("Source LSP log task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_lsp_symbols(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspSymbol>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.find_symbols(preview, request))
        .await
        .map_err(|error| format!("Source LSP symbol task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_source_lsp_diagnostics(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    preview: lsp::SourceLspPreview,
    request: lsp::SourceLspLookupRequest,
) -> Result<Vec<lsp::SourceLspDiagnostic>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.read_diagnostics(preview, request))
        .await
        .map_err(|error| format!("Source LSP diagnostics task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn list_source_lsp_diagnostics_for_root(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
) -> Result<Vec<lsp::SourceLspDiagnostic>, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        registry.list_diagnostics_for_root(PathBuf::from(root))
    })
    .await
    .map_err(|error| format!("Source LSP project diagnostics task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_source_lsp_status(
    root: String,
    language: String,
) -> Result<lsp::SourceLspStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        lsp::read_source_lsp_status_sync(PathBuf::from(root), language)
    })
    .await
    .map_err(|error| format!("Source LSP status task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn list_source_lsp_statuses(
    root: String,
) -> Result<Vec<lsp::SourceLspStatus>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        lsp::list_source_lsp_statuses_sync(PathBuf::from(root))
    })
    .await
    .map_err(|error| format!("Source LSP readiness task failed: {error}"))
}

/// Proactively re-point any already-running language server(s) at a freshly-selected
/// project root so the cold re-index happens in the background on switch, not on the first
/// file-open under the new project. No-op when no server is running for that root's
/// languages (see `SourceLspRegistry::warm_running_servers_for_root`).
#[tauri::command]
pub(crate) async fn warm_source_lsp_for_root(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
) -> Result<usize, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || registry.warm_running_servers_for_root(&root))
        .await
        .map_err(|error| format!("Source LSP warm task failed: {error}"))?
}

/// Ensure the one native C# language-client endpoint for this canonical root.
/// Both workspace warming and editor startup call this command; the registry
/// coalesces them into the same bounded slot.
#[tauri::command]
pub(crate) async fn ensure_native_csharp_language_client(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
) -> Result<Option<lsp::NativeCsharpEndpoint>, String> {
    if !lsp::workspace_has_csharp_project_marker(&root) {
        return Ok(None);
    }
    // Read mode: no endpoint, and so no Roslyn. "Nothing here" rather than an
    // error, because a workspace the reader has not switched on is the ordinary
    // case, not a failure worth showing them.
    if !lsp::language_intelligence_on(&root) {
        return Ok(None);
    }
    registry.ensure_native_csharp_endpoint(&root).map(Some)
}

#[tauri::command]
pub(crate) async fn mark_native_csharp_language_client_ready(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
) -> Result<(), String> {
    registry.mark_native_csharp_client_ready(&root)
}

/// Turn the C# language server off or on, and stop it now if it is running.
///
/// The reader's setting drives this. Call it with what the setting says when
/// the app starts as well as when the switch is flipped: the flag lives in this
/// process and starts out on, so a reader who turned it off last week would
/// otherwise get the server back on the next launch.
#[tauri::command]
pub(crate) async fn set_csharp_language_server_enabled(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    manager: tauri::State<'_, agent_conversation::manager::AgentRuntimeManager>,
    enabled: bool,
) -> Result<CsharpLanguageServerToggleResult, String> {
    let registry = registry.inner().clone();
    let manager = manager.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let previous = lsp::language_server_settings_snapshot().to_string();
        let changed = lsp::set_csharp_language_server_enabled(enabled);
        persist_language_server_settings(&manager).map_err(|error| {
            let _ = lsp::restore_language_server_settings(&previous);
            error
        })?;
        let stopped_servers = if enabled {
            0
        } else {
            registry.stop_servers_for_language("csharp")?
        };

        Ok(CsharpLanguageServerToggleResult {
            enabled,
            stopped_servers,
            message: describe_csharp_language_server_toggle(enabled, changed, stopped_servers),
        })
    })
    .await
    .map_err(|error| format!("C# language server switch task failed: {error}"))?
}

/// Switch every language server off or on. Off stops each one that is running,
/// every language and every workspace, and nothing starts until it is on
/// again. Like the C# switch, the desktop app forgets this between launches,
/// so the shell pushes the saved setting on start.
#[tauri::command]
pub(crate) async fn set_language_servers_enabled(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    manager: tauri::State<'_, agent_conversation::manager::AgentRuntimeManager>,
    enabled: bool,
) -> Result<LanguageServersToggleResult, String> {
    let registry = registry.inner().clone();
    let manager = manager.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let previous = lsp::language_server_settings_snapshot().to_string();
        let changed = lsp::set_language_servers_enabled(enabled);
        persist_language_server_settings(&manager).map_err(|error| {
            let _ = lsp::restore_language_server_settings(&previous);
            error
        })?;
        let stopped_servers = if enabled {
            0
        } else {
            registry.stop_all_servers()?
        };
        Ok(LanguageServersToggleResult {
            enabled,
            stopped_servers,
            message: describe_language_servers_toggle(enabled, changed, stopped_servers),
        })
    })
    .await
    .map_err(|error| format!("Language servers switch task failed: {error}"))?
}

/// What full mode is doing for one workspace right now.
///
/// Reading costs nothing and starts nothing: the editor asks this when it
/// points at a workspace so the switch shows the right position.
#[tauri::command]
pub(crate) async fn read_workspace_language_intelligence(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
) -> Result<WorkspaceLanguageIntelligence, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let key = lsp::language_intelligence_key(&root);
        let enabled = lsp::language_intelligence_on(&key);
        let server_pids = workspace_language_server_pids(&registry, &key);
        Ok(WorkspaceLanguageIntelligence {
            enabled,
            running_servers: server_pids.len(),
            message: describe_workspace_language_intelligence(enabled, server_pids.len(), 0),
            server_pids,
            stopped_servers: 0,
            root: key,
        })
    })
    .await
    .map_err(|error| format!("Language intelligence read task failed: {error}"))?
}

/// Turn language intelligence on or off for one workspace.
///
/// Give it the language the reader is looking at and turning it on starts that
/// language's server there and then, through the same slot the first hover
/// would have used. Leave the language out and it only records the choice,
/// which is what lets a saved choice be restored on a file open without waking
/// a server for a project nobody is looking at. Off stops the workspace's
/// servers now and gives their memory back.
#[tauri::command]
pub(crate) async fn set_workspace_language_intelligence(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    root: String,
    enabled: bool,
    language: Option<String>,
) -> Result<WorkspaceLanguageIntelligence, String> {
    let registry = registry.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let key = lsp::language_intelligence_key(&root);
        let change = lsp::set_language_intelligence(&key, enabled);
        let enabled = change.may_start_servers();
        let stopped_servers = if enabled {
            registry.set_active_root(&key, true)?
        } else {
            registry.set_active_root(&key, false)?
        };
        // What the switch now says is what the state machine decided, not what
        // the caller asked for — the two only differ if something else changed
        // this workspace in between, and the reader should see the truth.
        // A start that fails is not a failed switch: the choice is recorded
        // either way, so the reason comes back in the message rather than as an
        // error that would make the switch look like it never moved.
        let start = language
            .as_deref()
            .map(str::trim)
            .filter(|language| enabled && !language.is_empty())
            .map(|language| registry.start_server_for_language(&key, language));
        let server_pids = workspace_language_server_pids(&registry, &key);
        Ok(WorkspaceLanguageIntelligence {
            enabled,
            running_servers: server_pids.len(),
            message: match &start {
                Some(start) => describe_language_server_start(start),
                None => describe_workspace_language_intelligence(
                    enabled,
                    server_pids.len(),
                    stopped_servers,
                ),
            },
            server_pids,
            stopped_servers,
            root: key,
        })
    })
    .await
    .map_err(|error| format!("Language intelligence switch task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn set_language_server_enabled(
    registry: tauri::State<'_, lsp::SourceLspRegistry>,
    manager: tauri::State<'_, agent_conversation::manager::AgentRuntimeManager>,
    language: String,
    enabled: bool,
) -> Result<LanguageServerToggleResult, String> {
    let registry = registry.inner().clone();
    let manager = manager.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let language = language.trim().to_ascii_lowercase();
        let previous = lsp::language_server_settings_snapshot().to_string();
        let changed = lsp::set_language_server_enabled(&language, enabled)?;
        persist_language_server_settings(&manager).map_err(|error| {
            let _ = lsp::restore_language_server_settings(&previous);
            error
        })?;
        let stop_languages: &[&str] = match language.as_str() {
            "typescript" | "tsx" | "javascript" | "jsx" => {
                &["typescript", "tsx", "javascript", "jsx"]
            }
            "csharp" | "c#" => &["csharp"],
            "rust" => &["rust"],
            _ => &[],
        };
        let mut stopped_servers = 0;
        if !enabled {
            for language in stop_languages {
                stopped_servers += registry.stop_servers_for_language(language)?;
            }
        }
        Ok(LanguageServerToggleResult {
            language: language.clone(),
            enabled,
            stopped_servers,
            message: if !enabled && stopped_servers > 0 {
                format!("The {language} language server is off and has been stopped.")
            } else if changed {
                format!(
                    "The {language} language server is {}.",
                    if enabled { "on" } else { "off" }
                )
            } else {
                format!(
                    "The {language} language server was already {}.",
                    if enabled { "on" } else { "off" }
                )
            },
        })
    })
    .await
    .map_err(|error| format!("Language server switch task failed: {error}"))?
}
