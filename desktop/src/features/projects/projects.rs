use crate::core::socket::{client::SocketClient, outcome::Outcome, update::Update};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::HashSet;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectRecord {
    pub id: String,
    pub machine: String,
    pub root_path: String,
    pub title: String,
    pub repo_key: Option<String>,
    // The backend's record; nothing orders or shows by this yet.
    #[allow(dead_code)]
    pub pinned_at: Option<i64>,
    pub removed_at: Option<i64>,
    // The backend's record; nothing orders or shows by this yet.
    #[allow(dead_code)]
    pub created_at: i64,
    pub last_used_at: Option<i64>,
}
impl ProjectRecord {
    pub fn github_repository(&self) -> Option<&str> {
        let repository = self.repo_key.as_deref()?.strip_prefix("github.com/")?;
        let mut parts = repository.split('/');
        match (parts.next(), parts.next(), parts.next()) {
            (Some(owner), Some(name), None) if !owner.is_empty() && !name.is_empty() => {
                Some(repository)
            }
            _ => None,
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProjectPage {
    projects: Vec<ProjectRecord>,
    next: Option<Value>,
}
struct GlobalProjects(Entity<Projects>);
impl Global for GlobalProjects {}
/// One pushed desktop registry, shared by the rail, project selection and Git. SQL owns page ordering.
pub struct Projects {
    rows: Vec<ProjectRecord>,
    pub next: Option<Value>,
    pub loading: bool,
    pub failure: Option<String>,
    revision: Option<i64>,
    generation: u64,
    read: Option<Task<()>>,
    _feed: Task<()>,
}
impl Projects {
    pub fn get(cx: &mut App) -> Option<Entity<Self>> {
        if let Some(global) = cx.try_global::<GlobalProjects>() {
            return Some(global.0.clone());
        }
        let socket = SocketClient::try_global(cx)?;
        let projects = cx.new(|cx| Self::new(socket, cx));
        cx.set_global(GlobalProjects(projects.clone()));
        Some(projects)
    }
    fn new(socket: Entity<SocketClient>, cx: &mut Context<Self>) -> Self {
        let mut subscription = socket.read(cx).subscribe("projects", None);
        let feed = cx.spawn(async move |projects, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                if projects
                    .update(cx, |projects, cx| projects.apply(update, cx))
                    .is_err()
                {
                    return;
                }
            }
        });
        Self {
            rows: Vec::new(),
            next: None,
            loading: false,
            failure: None,
            revision: None,
            generation: 0,
            read: None,
            _feed: feed,
        }
    }
    /// Re-resolve the clicked registry tuple, so a changed or removed row cannot retarget a draft.
    pub fn resolve_open(
        &self,
        intent: &crate::core::intents::open_project::OpenProject,
    ) -> Option<ProjectRecord> {
        self.rows
            .iter()
            .find(|project| {
                project.id == intent.project_id
                    && project.machine == intent.machine
                    && project.root_path == intent.root_path
                    && project.removed_at.is_none()
            })
            .cloned()
    }
    pub fn revision(&self) -> Option<i64> {
        self.revision
    }
    /// A bounded chooser query; the desktop registry remains the sole collection and SQL owns filtering/order.
    pub fn search(
        &self,
        filter: &str,
        cx: &mut Context<Self>,
    ) -> Task<Result<Vec<ProjectRecord>, String>> {
        let Some(socket) = SocketClient::try_global(cx) else {
            return Task::ready(Err("The backend isn't running.".into()));
        };
        let call = socket.read(cx).call::<ProjectPage>(
            "projects",
            "list",
            json!({"filter":filter,"limit":100}),
            None,
        );
        cx.spawn(async move |_, _| match call.await {
            Outcome::Ok(page) => Ok(page.projects),
            Outcome::Failed(error) => Err(error.message),
            Outcome::Aborted => Err("The projects read was cancelled.".into()),
        })
    }
    pub fn rows(&self) -> &[ProjectRecord] {
        &self.rows
    }
    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        let (state, items) = match update {
            Update::State(items) => (
                true,
                items.into_iter().map(|(_, item)| item).collect::<Vec<_>>(),
            ),
            Update::Change { item, .. } => (false, vec![item]),
        };
        let revision = items
            .iter()
            .filter_map(|item| item["revision"].as_i64())
            .max();
        if state
            || revision
                .is_some_and(|revision| self.revision.is_none_or(|previous| revision > previous))
        {
            self.revision = revision.or(self.revision);
            self.refresh(cx);
        }
    }
    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        self.read = None;
        self.loading = false;
        self.next = None;
        self.request(false, cx);
    }
    pub fn load_more(&mut self, cx: &mut Context<Self>) {
        if self.next.is_some() {
            self.request(true, cx);
        }
    }
    fn request(&mut self, more: bool, cx: &mut Context<Self>) {
        if self.loading {
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.failure = Some("The backend isn't running.".into());
            cx.notify();
            return;
        };
        self.generation += 1;
        let generation = self.generation;
        self.loading = true;
        self.failure = None;
        let call = socket.read(cx).call::<ProjectPage>(
            "projects",
            "list",
            json!({"limit":100,"after":if more{self.next.clone()}else{None}}),
            None,
        );
        self.read = Some(cx.spawn(async move |projects, cx| {
            let answer = call.await;
            _ = projects.update(cx, |projects, cx| {
                if projects.generation != generation {
                    return;
                }
                projects.loading = false;
                projects.read = None;
                match answer {
                    Outcome::Ok(page) => {
                        if !more {
                            projects.rows.clear();
                        }
                        let mut seen: HashSet<String> = projects
                            .rows
                            .iter()
                            .map(|project| project.id.clone())
                            .collect();
                        projects
                            .rows
                            .extend(page.projects.into_iter().filter(|project| {
                                project.removed_at.is_none() && seen.insert(project.id.clone())
                            }));
                        projects.next = page.next;
                    }
                    Outcome::Failed(error) => projects.failure = Some(error.message),
                    Outcome::Aborted => {
                        projects.failure = Some("The projects read was cancelled.".into())
                    }
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
}
#[cfg(test)]
#[path = "projects_tests.rs"]
mod tests;
