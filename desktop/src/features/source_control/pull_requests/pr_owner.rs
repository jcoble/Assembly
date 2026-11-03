//! Who answers for a pull request: a session, or a project read through the source captured when it opened. Exactly
//! one, so a project never borrows a session or sends a root of its own to read from.
use crate::core::socket::{client::SocketClient, outcome::Outcome, row_source::ExpectedRowSource};
use crate::features::projects::projects::ProjectRecord;
use crate::features::session::client::source_resolution;
use crate::features::source_control::git_target::{GitTarget, OperationScope, ProjectTarget};
use gpui_kit::*;
use serde_json::{Value, json};

const LOCAL: &str = "local";

#[derive(Clone, Debug, PartialEq)]
pub enum PrOwner {
    Session {
        id: String,
        source: Option<ExpectedRowSource>,
    },
    Project {
        id: String,
        source: ExpectedRowSource,
    },
}
impl PrOwner {
    pub fn session_id(&self) -> Option<&str> {
        match self {
            Self::Session { id, .. } => Some(id),
            Self::Project { .. } => None,
        }
    }
    pub fn read_args(&self, mut args: Value) -> Value {
        if let Some(fields) = args.as_object_mut() {
            match self {
                Self::Session { id, source } => {
                    fields.insert("session".into(), json!(id));
                    if let Some(source) = source {
                        fields.insert("expectedSource".into(), json!(source));
                    }
                }
                Self::Project { id, source } => {
                    fields.insert("session".into(), Value::Null);
                    fields.insert("projectId".into(), json!(id));
                    fields.insert("expectedSource".into(), json!(source));
                }
            }
        }
        args
    }
    /// Where an action runs, from the repository and root the shown read reported. A project can't act before it has read.
    pub fn scope(&self, shown: Option<(&str, &str)>) -> Option<OperationScope> {
        match self {
            Self::Session { id, source } => Some(OperationScope::Session(GitTarget {
                session: id.clone(),
                source: source.clone(),
                checkout_root: shown.map(|(_, root)| root.to_string()),
            })),
            Self::Project { id, source } => {
                let (repository, root) = shown?;
                Some(OperationScope::Project(ProjectTarget {
                    project_id: id.clone(),
                    source: source.clone(),
                    repository: repository.to_string(),
                    checkout_root: root.to_string(),
                }))
            }
        }
    }
}
/// A repository held by more than one project: which one answers is the person's to say.
pub struct ProjectChoice {
    pub projects: Vec<ProjectRecord>,
    pub repository: Option<String>,
    pub number: u64,
}
/// The projects a pull request could open under: the one the person chose, else those holding its repository. A row
/// under "All local projects" was listed from this computer, so only this computer's copies can answer for it.
pub fn holders(
    rows: &[ProjectRecord],
    chosen: Option<&str>,
    repository: Option<&str>,
    listed_local: bool,
) -> Result<Vec<ProjectRecord>, String> {
    let holds = |project: &&ProjectRecord| {
        repository.is_none_or(|repository| {
            (project.github_repository()).is_some_and(|held| held.eq_ignore_ascii_case(repository))
        })
    };
    let live = || rows.iter().filter(|project| project.removed_at.is_none());
    if let Some(chosen) = chosen {
        return live()
            .filter(|project| project.id == chosen)
            .find(holds)
            .map(|project| vec![project.clone()])
            .ok_or("This pull request isn't in the chosen project's repository.".into());
    }
    if repository.is_none() {
        return Err("Choose a project to open this pull request.".into());
    }
    let found: Vec<ProjectRecord> = live()
        .filter(holds)
        .filter(|project| !listed_local || project.machine == LOCAL)
        .cloned()
        .collect();
    if found.is_empty() {
        return Err("That PR's repository isn't a project in Assembly.".into());
    }
    Ok(found)
}
/// Asks which Engine owns this project's machine right now; every later read and action asserts that answer.
pub fn resolve(project: &ProjectRecord, cx: &App) -> Task<Result<PrOwner, String>> {
    let Some(socket) = SocketClient::try_global(cx) else {
        return Task::ready(Err("The backend isn't running.".into()));
    };
    let route = (project.machine != LOCAL).then(|| project.machine.clone());
    let args = match source_resolution::build_args(route.as_deref()) {
        Ok(args) => args,
        Err(error) => return Task::ready(Err(error.message)),
    };
    let local = socket.read(cx).engine_machine();
    let call = socket
        .read(cx)
        .call::<Value>("sessions", "resolve_source", args, None);
    let id = project.id.clone();
    cx.spawn(async move |_| match call.await {
        Outcome::Ok(value) => {
            let source = source_resolution::parse_reply(value, route.as_deref(), local.as_deref())
                .map_err(|error| error.message)?;
            Ok(PrOwner::Project { id, source })
        }
        Outcome::Failed(error) => Err(error.message),
        Outcome::Aborted => Err("The project's machine did not answer.".into()),
    })
}
#[cfg(test)]
#[path = "pr_owner_tests.rs"]
mod tests;
