//! The Home panel against a scripted backend: a press on Home opens it, it subscribes, and it shows the state the
//! backend pushes. Wording and layout are checked in the real window, not here.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::{Modifiers, TestAppContext, VisualTestContext};
use serde_json::{Value, json};

use super::home_button::HomeButton;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;

/// One pushed answer: Codex needs a sign-in on the remote, so the backend says so in the summary.
fn pushed() -> Value {
    json!({
        "summary": {"kind": "needs_sign_in", "message": "Codex needs sign-in on Workbox."},
        "providers": [{
            "id": "codex", "name": "Codex", "glyph": "icons/square-terminal.svg", "vendorFamily": "codex",
            "enabled": true, "state": "needs_sign_in", "models": null,
            "machines": [
                {"machineKey": "local", "engineMachine": "engine-1", "state": "ready", "installed": true, "cliVersion": "0.42.0",
                 "providerTargetId": "codex/", "version": 7,
                 "models": [{"id": "gpt-5.4", "name": "GPT-5.4"}], "lastFailure": null},
                {"machineKey": "Workbox", "state": "needs_sign_in", "installed": true, "models": null,
                 "lastFailure": {"message": "Codex isn't signed in on Workbox.", "detail": "auth_required"}}
            ]
        }, {
            "id": "claude", "name": "Claude Code", "glyph": "icons/square-terminal.svg", "vendorFamily": "claude",
            "enabled": true, "state": "ready", "models": null,
            "machines": [
                {"machineKey": "local", "engineMachine": "engine-1", "state": "ready", "installed": true,
                 "providerTargetId": "claude/", "version": 3, "models": null, "lastFailure": null}
            ]
        }],
        "machines": [
            {"key": "local", "displayName": "This Mac", "connectionState": "connected", "updateState": "up_to_date",
             "waitingOnSessions": false, "machine": {"key": "local", "name": null, "reachable": true}},
            {"key": "Workbox", "displayName": "Workbox", "connectionState": "connected", "updateState": "up_to_date",
             "waitingOnSessions": false, "machine": {"key": "Workbox", "name": "Workbox", "reachable": true}}
        ]
    })
}

#[gpui::test]
async fn a_press_on_home_opens_the_panel_showing_the_pushed_state(cx: &mut TestAppContext) {
    // The backend answers the panel's subscription with one pushed item, and notes each frame it gets.
    let frames = Arc::new(Mutex::new(Vec::<Value>::new()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = {
        let frames = frames.clone();
        FakeBackend::start(move |frame| {
            if let Some(replies) = hello(frame) {
                return replies;
            }
            frames.lock().unwrap().push(frame.clone());
            _ = tell.unbounded_send(());
            match frame["t"].as_str() {
                Some("sub") if frame["topic"] == "settings_state" => vec![Reply::Frame(
                    json!({"t":"state","sub":frame["id"],"items":[{"key":"current","item":pushed()}]}),
                )],
                _ => Vec::new(),
            }
        })
    };
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let (home, cx) = cx.add_window_view(|_, _| HomeButton::default());
    cx.run_until_parked();
    let subscribed = |frames: &Arc<Mutex<Vec<Value>>>| {
        frames
            .lock()
            .unwrap()
            .iter()
            .any(|frame| frame["t"] == "sub")
    };
    assert!(!subscribed(&frames), "a closed panel subscribes to nothing");

    // A press on Home opens the panel, which subscribes and shows what came.
    let button = cx.debug_bounds("home-button").expect("Home is drawn");
    cx.simulate_click(button.center(), Modifiers::none());
    until("the pushed state shown", &mut changes, &changed, || {
        cx.run_until_parked();
        let panel = cx.read(|cx| home.read(cx).panel());
        panel.is_some_and(|panel| cx.read(|cx| panel.read(cx).state.is_some()))
    })
    .await;
    let panel = cx
        .read(|cx| home.read(cx).panel())
        .expect("the panel is open");
    let state = cx
        .read(|cx| panel.read(cx).state.clone())
        .expect("a pushed state");
    assert_eq!(state.summary.message, "Codex needs sign-in on Workbox.");
    assert_eq!(state.providers[0].state.as_deref(), Some("needs_sign_in"));
    assert_eq!(
        state.providers[0].machines[1].state.as_deref(),
        Some("needs_sign_in")
    );
    assert!(
        cx.debug_bounds("home-panel").is_some(),
        "the panel is drawn"
    );
    assert!(
        cx.debug_bounds("home-provider-codex").is_some(),
        "with the provider's row"
    );
    let sub = frames
        .lock()
        .unwrap()
        .iter()
        .find(|frame| frame["t"] == "sub")
        .cloned()
        .unwrap();
    assert_eq!(
        (sub["topic"].as_str(), sub["key"].as_str()),
        (Some("settings_state"), Some("current"))
    );
}

/// The scripted backend's provider-settings store: one revision for the whole agents file, as the real
/// backend has, bumped by every save.
#[derive(Default)]
struct SettingsServer {
    revision: u64,
    settings: std::collections::HashMap<String, Value>,
    reads: Vec<String>,
    saves: Vec<Value>,
    /// Set by a save: the next read a different provider makes is followed at once by another
    /// editor's save, so that reader's revision is stale by the time it saves.
    bump_after_read: bool,
    /// The provider that saved last.
    last_saver: Option<String>,
}

fn settings_reply(server: &SettingsServer, provider: &str) -> Value {
    json!({
        "machine": "engine-1",
        "provider": provider,
        "revision": format!("rev-{}", server.revision),
        "configurationTargetId": "cfg",
        "accountContext": "",
        "settings": server.settings.get(provider).cloned().unwrap_or_else(|| json!({
            "binaryPath": "/bin/cli", "configHome": null, "shadowHome": null, "launchArguments": []
        })),
    })
}

/// Two Advanced editors open on the same agents file: the second saves, the first's save is refused for
/// its stale revision, and the panel must refresh and let the same typed edit save on the next try.
#[gpui::test]
async fn a_stale_advanced_save_refreshes_and_the_retry_saves(cx: &mut TestAppContext) {
    let server = Arc::new(Mutex::new(SettingsServer {
        revision: 1,
        ..Default::default()
    }));
    let (changed, mut changes) = unbounded();
    let backend = {
        let (server, tell) = (server.clone(), changed.clone());
        FakeBackend::start(move |frame| {
            if let Some(replies) = hello(frame) {
                return replies;
            }
            _ = tell.unbounded_send(());
            let id = frame["id"].clone();
            match (
                frame["t"].as_str(),
                frame["feature"].as_str(),
                frame["method"].as_str(),
            ) {
                (Some("sub"), _, _) if frame["topic"] == "settings_state" => vec![Reply::Frame(
                    json!({"t":"state","sub":frame["id"],"items":[{"key":"current","item":pushed()}]}),
                )],
                (Some("call"), Some("providers"), Some("runtimeRead")) => {
                    let mut server = server.lock().unwrap();
                    let provider = frame["args"]["provider"].as_str().unwrap().to_string();
                    server.reads.push(provider.clone());
                    let reply = settings_reply(&server, &provider);
                    if server.bump_after_read
                        && server.last_saver.as_deref() != Some(provider.as_str())
                    {
                        server.bump_after_read = false;
                        server.revision += 1;
                        // The other editor's save changed this provider's other field (the
                        // config folder), which this editor never touched.
                        server.settings.insert(
                            provider.clone(),
                            json!({
                                "binaryPath": "/bin/cli",
                                "configHome": "/other-editor-config",
                                "shadowHome": null,
                                "launchArguments": []
                            }),
                        );
                    }
                    vec![Reply::Frame(json!({"t":"result","id":id,"result":reply}))]
                }
                (Some("call"), Some("providers"), Some("runtimeSave")) => {
                    let mut server = server.lock().unwrap();
                    if frame["args"]["expectedRevision"] != format!("rev-{}", server.revision) {
                        return vec![Reply::Frame(json!({
                            "t":"error","id":id,"code":"runtime_settings_conflict",
                            "message":"Provider runtime settings are unavailable."
                        }))];
                    }
                    server.revision += 1;
                    let provider = frame["args"]["provider"].as_str().unwrap().to_string();
                    server.bump_after_read = true;
                    server.last_saver = Some(provider.clone());
                    server
                        .settings
                        .insert(provider.clone(), frame["args"]["settings"].clone());
                    server.saves.push(frame["args"].clone());
                    vec![Reply::Frame(
                        json!({"t":"result","id":id,"result":settings_reply(&server, &provider)}),
                    )]
                }
                _ => Vec::new(),
            }
        })
    };
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let (home, cx) = cx.add_window_view(|_, _| HomeButton::default());
    // A draw runs the growers' springs; settling runs them to rest, as the row_drop_down tests do.
    fn draw(cx: &mut VisualTestContext) {
        cx.run_until_parked();
        cx.update(|window, cx| window.draw(cx).clear(cx));
        cx.run_until_parked();
    }
    fn settle(cx: &mut VisualTestContext) {
        for _ in 0..40 {
            cx.executor()
                .advance_clock(std::time::Duration::from_millis(16));
            draw(cx);
        }
    }
    draw(cx);
    let click = |cx: &mut VisualTestContext, selector: &'static str| {
        draw(cx);
        let bounds = cx
            .debug_bounds(selector)
            .unwrap_or_else(|| panic!("{selector} is drawn"));
        cx.simulate_click(bounds.center(), Modifiers::none());
        settle(cx);
    };
    let panel = |cx: &VisualTestContext| {
        cx.read(|cx| home.read(cx).panel())
            .expect("the panel is open")
    };
    let fields_made = |cx: &VisualTestContext, key: &str| {
        let panel = panel(cx);
        cx.read(|cx| {
            panel
                .read(cx)
                .edits
                .get(key)
                .is_some_and(|edit| edit.fields.is_some())
        })
    };
    let set_cli = |cx: &mut VisualTestContext, key: &str, value: &str| {
        let panel = panel(cx);
        let field = cx.read(|cx| {
            panel.read(cx).edits[key]
                .fields
                .as_ref()
                .expect("fields are made")[0]
                .clone()
        });
        cx.update(|window, cx| field.update(cx, |field, cx| field.set_value(value, window, cx)));
        cx.run_until_parked();
    };
    let cli_value = |cx: &VisualTestContext, key: &str| {
        let panel = panel(cx);
        cx.read(|cx| {
            panel.read(cx).edits[key]
                .fields
                .as_ref()
                .expect("fields are made")[0]
                .read(cx)
                .value()
                .to_string()
        })
    };

    click(cx, "home-button");
    until("the pushed state shown", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.read(|cx| {
            home.read(cx)
                .panel()
                .is_some_and(|panel| panel.read(cx).state.is_some())
        })
    })
    .await;

    // Open Claude's Advanced and type a new CLI path.
    click(cx, "home-provider-claude");
    click(cx, "home-advanced-claude-local");
    until("Claude's fields", &mut changes, &changed, || {
        draw(cx);
        fields_made(cx, "claude/local")
    })
    .await;
    set_cli(cx, "claude/local", "/changed-claude");

    // Open Codex's Advanced, change its CLI path and save: the file's revision moves to rev-2.
    click(cx, "home-provider-codex");
    click(cx, "home-advanced-codex-local");
    until("Codex's fields", &mut changes, &changed, || {
        draw(cx);
        fields_made(cx, "codex/local")
    })
    .await;
    set_cli(cx, "codex/local", "/changed-codex");
    click(cx, "home-advanced-save-codex-local");
    until("Codex's save lands", &mut changes, &changed, || {
        cx.run_until_parked();
        server.lock().unwrap().saves.len() == 1
    })
    .await;
    assert_eq!(
        server.lock().unwrap().saves[0]["expectedRevision"],
        json!("rev-1")
    );

    // Back to Claude: reopening its Advanced re-reads, and what was typed is still there.
    click(cx, "home-provider-claude");
    click(cx, "home-advanced-claude-local");
    until(
        "Claude is re-read on reopen",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            let reads = server.lock().unwrap().reads.clone();
            reads
                .iter()
                .filter(|provider| Some(*provider) == reads.first())
                .count()
                >= 2
        },
    )
    .await;
    assert_eq!(cli_value(cx, "claude/local"), "/changed-claude");

    // A save landed just after that re-read, so the first save is refused for the stale
    // revision; the panel refreshes and says so.
    click(cx, "home-advanced-save-claude-local");
    until(
        "the refusal is answered with a refresh",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            let server = server.lock().unwrap();
            server
                .reads
                .iter()
                .filter(|provider| Some(*provider) == server.reads.first())
                .count()
                >= 3
                && {
                    let panel = panel(cx);
                    cx.read(|cx| {
                        panel.read(cx).edits["claude/local"]
                            .notice
                            .as_deref()
                            .is_some_and(|notice| notice.contains("wasn't saved"))
                    })
                }
        },
    )
    .await;
    assert_eq!(cli_value(cx, "claude/local"), "/changed-claude");

    // One more Save now works, at the refreshed revision, with the typed value.
    click(cx, "home-advanced-save-claude-local");
    until("the retry saves", &mut changes, &changed, || {
        cx.run_until_parked();
        server.lock().unwrap().saves.len() == 2
    })
    .await;
    let saves = server.lock().unwrap().saves.clone();
    assert_eq!(saves[1]["provider"], json!("claude"));
    assert_eq!(saves[1]["expectedRevision"], json!("rev-3"));
    assert_eq!(saves[1]["settings"]["binaryPath"], json!("/changed-claude"));
    // The field this editor never changed keeps the other editor's value, not the stale text.
    assert_eq!(
        saves[1]["settings"]["configHome"],
        json!("/other-editor-config")
    );
    until("the saved notice", &mut changes, &changed, || {
        cx.run_until_parked();
        let panel = panel(cx);
        cx.read(|cx| panel.read(cx).edits["claude/local"].notice.as_deref() == Some("Saved."))
    })
    .await;

    // The saved values are what the inputs now show: the config folder the other editor set is
    // in its field, not the empty text this editor started from.
    let config_value = {
        let panel = panel(cx);
        cx.read(|cx| {
            panel.read(cx).edits["claude/local"]
                .fields
                .as_ref()
                .expect("fields are made")[1]
                .read(cx)
                .value()
                .to_string()
        })
    };
    assert_eq!(config_value, "/other-editor-config");

    // A second save in the same panel, editing only the CLI path, keeps that config folder.
    set_cli(cx, "claude/local", "/changed-again");
    click(cx, "home-advanced-save-claude-local");
    until("the second save lands", &mut changes, &changed, || {
        cx.run_until_parked();
        server.lock().unwrap().saves.len() == 3
    })
    .await;
    let saves = server.lock().unwrap().saves.clone();
    assert_eq!(saves[2]["settings"]["binaryPath"], json!("/changed-again"));
    assert_eq!(
        saves[2]["settings"]["configHome"],
        json!("/other-editor-config")
    );
}
