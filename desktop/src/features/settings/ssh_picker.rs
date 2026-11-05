//! The Add a machine form's one search box: the hosts this account already knows, from the backend, which reads them when
//! the form opens and watches nothing. Picking one asks SSH what it means and fills the form; every field stays editable,
//! and a host that isn't listed can just be typed into SSH destination.

use gpui_kit::component::IndexPath;
use gpui_kit::component::searchable_list::SearchableListItem;
use gpui_kit::component::select::{SearchableVec, Select, SelectEvent, SelectState};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

use super::settings_page::SettingsPage;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub(super) struct HostChoice {
    pub host: String,
    pub host_name: Option<String>,
}

impl HostChoice {
    pub(super) fn words(&self) -> String {
        match &self.host_name {
            Some(name) if name != &self.host => format!("{} · {name}", self.host),
            _ => self.host.clone(),
        }
    }
}

impl SearchableListItem for HostChoice {
    type Value = String;
    fn title(&self) -> SharedString {
        self.words().into()
    }
    fn value(&self) -> &String {
        &self.host
    }
}

#[derive(Debug, Deserialize)]
struct HostList {
    hosts: Vec<HostChoice>,
}

/// What SSH says it will use.
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub(super) struct SshTarget {
    pub host: String,
    pub user: String,
    pub host_name: String,
    pub port: u16,
}

impl SshTarget {
    /// The line under the form, so what a pick means can be read before saving.
    pub(super) fn words(&self) -> String {
        format!(
            "SSH connects as {}@{}, SSH port {}",
            self.user, self.host_name, self.port
        )
    }
}

/// What a pick does to the form: the destination, and a name only when none was typed. The alias stays the destination,
/// so everything SSH's config says about it keeps applying.
pub(super) fn filled_fields(target: &SshTarget, name_typed: &str) -> (String, String) {
    let name = if name_typed.trim().is_empty() {
        target.host.clone()
    } else {
        name_typed.to_string()
    };
    (name, target.host.clone())
}

type HostSelect = SelectState<SearchableVec<HostChoice>>;

pub(super) struct SshPicker {
    pub(super) state: Entity<HostSelect>,
    pub(super) target: Option<SshTarget>,
    pub(super) failure: Option<String>,
    loaded: bool,
    _loading: Option<Task<()>>,
    _picked: Subscription,
}

impl SshPicker {
    pub(super) fn new(window: &mut Window, cx: &mut Context<SettingsPage>) -> Self {
        let state = cx.new(|cx| {
            SelectState::new(
                SearchableVec::new(Vec::<HostChoice>::new()),
                None::<IndexPath>,
                window,
                cx,
            )
            .searchable(true)
        });
        let picked = cx.subscribe_in(
            &state,
            window,
            |page: &mut SettingsPage,
             _,
             event: &SelectEvent<SearchableVec<HostChoice>>,
             window,
             cx| {
                if let SelectEvent::Confirm(Some(host)) = event {
                    page.pick_host(host.clone(), window, cx);
                }
            },
        );
        Self {
            state,
            target: None,
            failure: None,
            loaded: false,
            _loading: None,
            _picked: picked,
        }
    }

    /// What's under the destination: a failed pick, or what SSH said while the destination is still the picked host.
    pub(super) fn words(&self, destination: &str) -> Option<String> {
        self.failure.clone().or_else(|| {
            self.target
                .as_ref()
                .filter(|target| target.host == destination)
                .map(SshTarget::words)
        })
    }

    pub(super) fn select(&self) -> Select<SearchableVec<HostChoice>> {
        Select::new(&self.state)
            .w_full()
            .placeholder("Search your SSH hosts")
            .search_placeholder("Search your SSH hosts")
    }
}

impl SettingsPage {
    /// Reads the hosts each time the form comes into view, so a host added to SSH since then is there next time.
    pub(super) fn follow_ssh_hosts(
        &mut self,
        shown: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !shown {
            self.ssh_picker.loaded = false;
            return;
        }
        if self.ssh_picker.loaded {
            return;
        }
        self.ssh_picker.loaded = true;
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call = socket
            .read(cx)
            .call::<HostList>("sshHosts", "list", json!({}), None);
        self.ssh_picker._loading = Some(cx.spawn_in(window, async move |page, cx| {
            let Outcome::Ok(list) = call.await else {
                return;
            };
            _ = page.update_in(cx, |page, window, cx| {
                page.ssh_picker.state.update(cx, |state, cx| {
                    state.set_items(SearchableVec::new(list.hosts), window, cx)
                });
            });
        }));
    }

    fn pick_host(&mut self, host: String, window: &mut Window, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call =
            socket
                .read(cx)
                .call::<SshTarget>("sshHosts", "target", json!({ "host": host }), None);
        self.ssh_picker._loading = Some(cx.spawn_in(window, async move |page, cx| {
            let outcome = call.await;
            _ = page.update_in(cx, |page, window, cx| {
                match outcome {
                    Outcome::Ok(target) => page.fill_from_host(target, window, cx),
                    Outcome::Failed(failure) => {
                        page.ssh_picker.target = None;
                        page.ssh_picker.failure = Some(failure.message);
                    }
                    Outcome::Aborted => {}
                }
                cx.notify();
            });
        }));
    }

    pub(super) fn fill_from_host(
        &mut self,
        target: SshTarget,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let [name, ssh, _, _] = &self.machine_fields;
        let typed = name.read(cx).value().to_string();
        let (new_name, destination) = filled_fields(&target, &typed);
        name.update(cx, |field, cx| field.set_value(new_name, window, cx));
        ssh.update(cx, |field, cx| field.set_value(destination, window, cx));
        self.ssh_picker.failure = None;
        self.ssh_picker.target = Some(target);
    }
}

#[cfg(test)]
#[path = "ssh_picker_tests.rs"]
mod tests;
