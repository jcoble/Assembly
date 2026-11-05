//! `OpenAnotherProject` is the Git tab's Open another project: what its bar shows, and picking a project or worktree on
//! any connected machine. Picking switches Files and Git together, and only ever changes `ViewedProject`, never the session.

use std::collections::{HashMap, HashSet};
use std::rc::Rc;

use gpui_kit::component::input::{InputEvent, InputState};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

use crate::core::intents::open_project::OpenProject;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::{GlobalSessions, Sessions};
use crate::core::state::viewed_project::{LOCAL_MACHINE, Viewed, ViewedProject};
use crate::features::projects::projects::{ProjectRecord, Projects};
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
use crate::open_project_component::{
    FILTER_PLACEHOLDER, OpenProjectBar, ProjectGroup, ProjectOption, filtered,
};

/// What this machine is called in the list and the bar.
fn this_machine() -> &'static str {
    if cfg!(target_os = "macos") {
        "This Mac"
    } else {
        "This computer"
    }
}

fn machine_label(machine: &str) -> String {
    if machine == LOCAL_MACHINE {
        this_machine().to_string()
    } else {
        machine.to_string()
    }
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WorktreeRow {
    pub path: String,
    pub branch: Option<String>,
}

#[derive(Deserialize)]
struct WorktreeList {
    worktrees: Vec<WorktreeRow>,
}

/// The old app's otherProjectGroups: this machine, then each connected one, each project with its worktrees under it.
pub fn project_groups(
    projects: &[ProjectRecord],
    machines: &[String],
    worktrees: &HashMap<String, Vec<WorktreeRow>>,
) -> Vec<ProjectGroup> {
    std::iter::once(LOCAL_MACHINE.to_string())
        .chain(machines.iter().cloned())
        .filter_map(|machine| {
            let mut options = Vec::new();
            for project in projects
                .iter()
                .filter(|project| project.machine == machine && project.removed_at.is_none())
            {
                options.push(ProjectOption {
                    project_id: project.id.clone(),
                    machine: machine.clone(),
                    label: project.title.clone(),
                    folder: project.root_path.clone(),
                    worktree: false,
                });
                for row in worktrees.get(&project.id).into_iter().flatten() {
                    if row.path == project.root_path {
                        continue;
                    }
                    let folder = row.path.rsplit('/').next().unwrap_or(&row.path);
                    let label = match row.branch.as_deref() {
                        Some(branch) if !branch.is_empty() && branch != folder => {
                            format!("{folder} — {branch}")
                        }
                        _ => folder.to_string(),
                    };
                    options.push(ProjectOption {
                        project_id: project.id.clone(),
                        machine: machine.clone(),
                        label,
                        folder: row.path.clone(),
                        worktree: true,
                    });
                }
            }
            (!options.is_empty()).then(|| ProjectGroup {
                label: machine_label(&machine),
                local: machine == LOCAL_MACHINE,
                machine,
                options,
            })
        })
        .collect()
}

pub struct OpenAnotherProject {
    open: bool,
    filter: Entity<InputState>,
    highlighted: usize,
    list_scroll: ScrollHandle,
    worktrees: HashMap<String, Vec<WorktreeRow>>,
    /// The worktree listings by project; closing the popover drops them, which cancels them on the backend.
    reads: HashMap<String, Task<()>>,
    /// What the backend found for the filter, which can reach past the registry's loaded pages.
    searched: Vec<ProjectRecord>,
    search: Option<Task<()>>,
    registry_revision: Option<i64>,
    heard_lists: bool,
    _heard: Vec<Subscription>,
}

impl OpenAnotherProject {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let filter = cx.new(|cx| InputState::new(window, cx).placeholder(FILTER_PLACEHOLDER));
        let viewed = ViewedProject::global(cx);
        let mut heard =
            vec![
                cx.observe(&viewed, |_, _, cx| cx.notify()),
                cx.observe(&SelectedSession::global(cx), |_, _, cx| cx.notify()),
                cx.subscribe_in(&filter, window, |bar, _, event: &InputEvent, window, cx| {
                    match event {
                        InputEvent::Change => {
                            bar.filter_changed(cx);
                        }
                        InputEvent::PressEnter { .. } => bar.pick_highlighted(window, cx),
                        _ => {}
                    }
                }),
            ];
        // The window opens before the backend's socket has started; these lists begin with it.
        heard.push(cx.observe_global::<GlobalSessions>(Self::hear_backend_lists));
        let mut bar = Self {
            open: false,
            filter,
            highlighted: 0,
            list_scroll: ScrollHandle::new(),
            worktrees: HashMap::new(),
            reads: HashMap::new(),
            searched: Vec::new(),
            search: None,
            registry_revision: None,
            heard_lists: false,
            _heard: heard,
        };
        bar.hear_backend_lists(cx);
        bar
    }

    fn hear_backend_lists(&mut self, cx: &mut Context<Self>) {
        if self.heard_lists {
            return;
        }
        let (Some(sessions), Some(machines), Some(projects)) = (
            Sessions::try_global(cx),
            Machines::try_global(cx),
            Projects::get(cx),
        ) else {
            return;
        };
        self.heard_lists = true;
        self._heard
            .push(cx.observe(&sessions, |_, _, cx| cx.notify()));
        self._heard
            .push(cx.observe(&machines, |_, _, cx| cx.notify()));
        self._heard.push(cx.observe(&projects, |bar, projects, cx| {
            let revision = projects.read(cx).revision();
            let changed = revision != bar.registry_revision;
            bar.registry_revision = revision;
            if bar.open {
                bar.read_worktrees(cx);
                if changed {
                    bar.run_search(cx);
                }
            }
            cx.notify();
        }));
        if self.open {
            self.read_worktrees(cx);
        }
        cx.notify();
    }

    /// The connected remote machines, in the list's order.
    fn machines(cx: &App) -> Vec<String> {
        let Some(machines) = Machines::try_global(cx) else {
            return Vec::new();
        };
        machines
            .read(cx)
            .machines()
            .filter(|machine| machine.reachable)
            .filter_map(|machine| machine.name.clone())
            .collect()
    }

    fn rows(cx: &mut App) -> Vec<ProjectRecord> {
        Projects::get(cx).map_or_else(Vec::new, |projects| projects.read(cx).rows().to_vec())
    }

    /// The registry's loaded pages, then whatever else the filter's search found.
    fn listed(&self, cx: &mut App) -> Vec<ProjectRecord> {
        let mut rows = Self::rows(cx);
        let loaded: HashSet<String> = rows.iter().map(|project| project.id.clone()).collect();
        rows.extend(
            self.searched
                .iter()
                .filter(|project| !loaded.contains(&project.id))
                .cloned(),
        );
        rows
    }

    fn all_groups(&self, cx: &mut App) -> Vec<ProjectGroup> {
        project_groups(&self.listed(cx), &Self::machines(cx), &self.worktrees)
    }

    fn shown_groups(&self, cx: &mut App) -> Vec<ProjectGroup> {
        let filter = self.filter.read(cx).value().to_string();
        filtered(&self.all_groups(cx), &filter)
    }

    fn filter_changed(&mut self, cx: &mut Context<Self>) {
        self.highlighted = 0;
        self.run_search(cx);
        cx.notify();
    }

    /// SQL filters the whole registry; a newer filter drops the search before it.
    fn run_search(&mut self, cx: &mut Context<Self>) {
        let filter = self.filter.read(cx).value().to_string();
        self.search = None;
        if filter.trim().is_empty() {
            self.searched.clear();
            return;
        }
        let Some(projects) = Projects::get(cx) else {
            return;
        };
        let call = projects.update(cx, |projects, cx| projects.search(&filter, cx));
        self.search = Some(cx.spawn(async move |bar, cx| {
            let answer = call.await;
            _ = bar.update(cx, |bar, cx| {
                // A failed search leaves the loaded pages to filter.
                bar.searched = answer.unwrap_or_default();
                bar.read_worktrees(cx);
                cx.notify();
            });
        }));
    }

    /// Whether the registry has a page not loaded yet, offered while nothing is filtered.
    fn has_more(&self, cx: &mut App) -> bool {
        self.filter.read(cx).value().trim().is_empty()
            && Projects::get(cx).is_some_and(|projects| projects.read(cx).next.is_some())
    }

    fn load_more(&mut self, cx: &mut Context<Self>) {
        if let Some(projects) = Projects::get(cx) {
            projects.update(cx, |projects, cx| projects.load_more(cx));
        }
    }

    /// Asks each listed project's machine for its worktrees.
    fn read_worktrees(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let machines = Self::machines(cx);
        let wanted: Vec<_> = self
            .listed(cx)
            .into_iter()
            .filter(|project| {
                project.removed_at.is_none()
                    && !self.worktrees.contains_key(&project.id)
                    && !self.reads.contains_key(&project.id)
                    && (project.machine == LOCAL_MACHINE || machines.contains(&project.machine))
            })
            .collect();
        for project in wanted {
            let call = socket.read(cx).call::<WorktreeList>(
                "projects",
                "worktrees",
                json!({ "projectId": project.id }),
                Some(&format!("open-project/worktrees/{}", project.id)),
            );
            let id = project.id.clone();
            let read = cx.spawn(async move |bar, cx| {
                // A machine that can't list them still shows the project itself.
                if let Outcome::Ok(list) = call.await {
                    _ = bar.update(cx, |bar, cx| {
                        bar.worktrees.insert(project.id.clone(), list.worktrees);
                        cx.notify();
                    });
                }
            });
            self.reads.insert(id, read);
        }
    }

    fn set_open(&mut self, open: bool, window: &mut Window, cx: &mut Context<Self>) {
        if self.open == open {
            return;
        }
        self.open = open;
        self.highlighted = 0;
        if open {
            self.worktrees.clear();
            self.read_worktrees(cx);
            self.filter
                .update(cx, |filter, cx| filter.focus(window, cx));
        } else {
            self.reads.clear();
            self.search = None;
            self.searched.clear();
            self.filter
                .update(cx, |filter, cx| filter.set_value("", window, cx));
        }
        cx.notify();
    }

    fn pick_highlighted(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let at = self.highlighted;
        let picked = self
            .shown_groups(cx)
            .into_iter()
            .flat_map(|group| group.options)
            .nth(at);
        if let Some(option) = picked {
            self.pick(&option, window, cx);
        }
    }

    fn key_down(&mut self, event: &KeyDownEvent, cx: &mut Context<Self>) {
        let groups = self.shown_groups(cx);
        let count: usize = groups.iter().map(|group| group.options.len()).sum();
        if count == 0 {
            return;
        }
        self.highlighted = match event.keystroke.key.as_str() {
            "down" => (self.highlighted + 1) % count,
            "up" => (self.highlighted + count - 1) % count,
            _ => return,
        };
        // The list's children are each group's heading, then its options.
        let mut child = 0;
        let mut left = self.highlighted;
        for group in &groups {
            child += 1;
            if left < group.options.len() {
                child += left;
                break;
            }
            left -= group.options.len();
            child += group.options.len();
        }
        self.list_scroll.scroll_to_item(child);
        cx.stop_propagation();
        cx.notify();
    }

    /// Picking the session's own folder is going back to it.
    fn pick(&mut self, option: &ProjectOption, window: &mut Window, cx: &mut Context<Self>) {
        let session = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from);
        let own = session.as_deref().is_some_and(|session| {
            Sessions::try_global(cx).is_some_and(|sessions| {
                sessions.read(cx).items().iter().any(|item| {
                    item.session_id == session
                        && item
                            .machine
                            .as_deref()
                            .filter(|name| !name.is_empty())
                            .unwrap_or(LOCAL_MACHINE)
                            == option.machine
                        && item.checkout_root.as_deref().or(item.root.as_deref())
                            == Some(option.folder.as_str())
                })
            })
        });
        let viewed = (!own).then(|| Viewed {
            project_id: option.project_id.clone(),
            machine: option.machine.clone(),
            folder: option.folder.clone(),
            title: option.label.clone(),
        });
        ViewedProject::set(session, viewed, cx);
        WorkspaceCoordinator::changed(cx);
        self.set_open(false, window, cx);
    }

    fn back(&mut self, cx: &mut Context<Self>) {
        let session = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from);
        ViewedProject::set(session, None, cx);
        WorkspaceCoordinator::changed(cx);
        cx.notify();
    }

    /// The new session form on the viewed project, which the old app offered only for a project's own folder.
    fn project_of(viewed: &Viewed, cx: &mut App) -> Option<ProjectRecord> {
        Self::rows(cx)
            .into_iter()
            .find(|project| project.id == viewed.project_id && project.root_path == viewed.folder)
    }
}

impl Render for OpenAnotherProject {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if SelectedSession::global(cx).read(cx).session_id().is_none() {
            return Empty.into_any_element();
        }
        let viewed = ViewedProject::viewed(cx);
        let this = cx.entity().downgrade();
        let (open_this, key_this, pick_this, back_this) =
            (this.clone(), this.clone(), this.clone(), this);
        let start_here = viewed
            .as_ref()
            .and_then(|viewed| Self::project_of(viewed, cx));
        let can_start_here = start_here.is_some();
        let more_this = cx.entity().downgrade();
        OpenProjectBar {
            viewing: viewed.as_ref().map(|viewed| {
                format!(
                    "Viewing {} on {}",
                    viewed.title,
                    machine_label(&viewed.machine)
                )
            }),
            read_only_reason: ViewedProject::read_only_reason(cx),
            can_start_here,
            open: self.open,
            filter: self.filter.clone(),
            groups: if self.open {
                self.shown_groups(cx)
            } else {
                Vec::new()
            },
            more: self.open && self.has_more(cx),
            on_more: Rc::new(move |_, cx| {
                _ = more_this.update(cx, |bar, cx| bar.load_more(cx));
            }),
            highlighted: self.highlighted,
            list_scroll: self.list_scroll.clone(),
            on_open_change: Rc::new(move |open, window, cx| {
                _ = open_this.update(cx, |bar, cx| bar.set_open(open, window, cx));
            }),
            on_key: Rc::new(move |event, _, cx| {
                _ = key_this.update(cx, |bar, cx| bar.key_down(event, cx));
            }),
            on_pick: Rc::new(move |option, window, cx| {
                _ = pick_this.update(cx, |bar, cx| bar.pick(option, window, cx));
            }),
            on_back: Rc::new(move |_, cx| {
                _ = back_this.update(cx, |bar, cx| bar.back(cx));
            }),
            on_new_session: Rc::new(move |window, cx| {
                if let Some(project) = &start_here {
                    window.dispatch_action(
                        Box::new(OpenProject {
                            project_id: project.id.clone(),
                            machine: project.machine.clone(),
                            root_path: project.root_path.clone(),
                        }),
                        cx,
                    );
                }
            }),
        }
        .into_any_element()
    }
}

/// Saves what's viewed with the session's workspace, so it comes back with the session.
pub struct ViewedProjectCodec;

impl crate::core::state::workspace_panes::PaneCodec for ViewedProjectCodec {
    fn name(&self) -> &'static str {
        "viewedProject"
    }
    fn capture(&self, _: &Window, cx: &App) -> Result<serde_json::Value, String> {
        serde_json::to_value(ViewedProject::viewed(cx)).map_err(|error| error.to_string())
    }
    fn validate(
        &self,
        value: &serde_json::Value,
        _: &crate::core::state::workspace_panes::WorkspaceKey,
        _: &serde_json::Value,
        _: &App,
    ) -> Result<(), String> {
        serde_json::from_value::<Option<Viewed>>(value.clone())
            .map(|_| ())
            .map_err(|_| "The saved viewed project is invalid.".to_string())
    }
    fn restore(&self, value: &serde_json::Value, _: &mut Window, cx: &mut App) {
        let viewed = serde_json::from_value::<Option<Viewed>>(value.clone()).unwrap_or(None);
        let session = crate::core::state::workspace_panes::WorkspacePanes::key(cx)
            .map(|key| key.session_id.clone());
        ViewedProject::set(session, viewed, cx);
    }
    fn deactivate(&self, _: &mut Window, cx: &mut App) {
        ViewedProject::set(None, None, cx);
    }
}

#[cfg(test)]
#[path = "open_another_project_tests.rs"]
mod tests;
