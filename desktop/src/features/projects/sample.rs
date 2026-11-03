//! Deterministic, memory-only fixtures. Nothing here reads or writes the filesystem or backend.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Machine {
    Mac,
    Workbox,
    Offline,
}
impl Machine {
    pub const ALL: [Self; 3] = [Self::Mac, Self::Workbox, Self::Offline];
    pub fn label(self) -> &'static str {
        match self {
            Self::Mac => "This Mac",
            Self::Workbox => "Workbox",
            Self::Offline => "Travel laptop",
        }
    }
    pub fn home(self) -> &'static str {
        match self {
            Self::Mac => "/Users/sample/dev",
            _ => "/home/sample/dev",
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StartWith {
    Empty,
    Git,
    Clone,
}
impl StartWith {
    pub const ALL: [Self; 3] = [Self::Empty, Self::Git, Self::Clone];
    pub fn label(self) -> &'static str {
        match self {
            Self::Empty => "Empty folder",
            Self::Git => "Empty folder with git",
            Self::Clone => "Copy of a repository",
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SampleProject {
    pub id: usize,
    pub title: String,
    pub machine: Machine,
    pub root: String,
    pub pinned: bool,
    pub pinned_at: Option<u64>,
    pub removed: bool,
    pub last_used: &'static str,
}
pub struct SampleProjects {
    pub projects: Vec<SampleProject>,
    pub selected: Option<usize>,
    pub draft: String,
    disconnected: Vec<Machine>,
    next_stamp: u64,
}
impl Default for SampleProjects {
    fn default() -> Self {
        let rows = [
            ("Assembly", Machine::Mac, "Assembly", true, "2 minutes ago"),
            (
                "Assembly",
                Machine::Workbox,
                "Assembly",
                false,
                "20 minutes ago",
            ),
            ("ClipNSave", Machine::Mac, "ClipNSave", false, "Yesterday"),
            (
                "Retailer onboarding research and implementation notes",
                Machine::Workbox,
                "retailer-notes",
                false,
                "2 days ago",
            ),
            (
                "Travel journal",
                Machine::Offline,
                "journal",
                false,
                "Last week",
            ),
        ];
        Self {
            projects: rows
                .into_iter()
                .enumerate()
                .map(
                    |(i, (title, machine, folder, pinned, last_used))| SampleProject {
                        id: i + 1,
                        title: title.into(),
                        machine,
                        root: format!("{}/{folder}", machine.home()),
                        pinned,
                        pinned_at: pinned.then_some(1),
                        removed: false,
                        last_used,
                    },
                )
                .collect(),
            selected: None,
            draft: String::new(),
            disconnected: vec![Machine::Offline],
            next_stamp: 2,
        }
    }
}
impl SampleProjects {
    pub fn visible(&self, query: &str, machine: Option<Machine>) -> Vec<SampleProject> {
        let query = query.trim().to_lowercase();
        let mut rows: Vec<_> = self
            .projects
            .iter()
            .filter(|p| {
                !p.removed
                    && machine.is_none_or(|m| p.machine == m)
                    && (p.title.to_lowercase().contains(&query)
                        || p.root.to_lowercase().contains(&query))
            })
            .cloned()
            .collect();
        rows.sort_by_key(|p| !p.pinned);
        rows
    }
    pub fn select(&mut self, id: Option<usize>) -> Result<(), &'static str> {
        if let Some(id) = id {
            let project = self
                .projects
                .iter()
                .find(|p| p.id == id)
                .ok_or("That sample project is unavailable.")?;
            if project.removed {
                return Err("That sample project was removed.");
            }
            if !self.online(project.machine) {
                return Err("This sample machine is offline.");
            }
        }
        self.selected = id;
        Ok(())
    }
    pub fn registration_completed(
        &mut self,
        id: usize,
        from_composer: bool,
    ) -> Result<(), &'static str> {
        if from_composer {
            self.select(Some(id))?;
        }
        Ok(())
    }
    pub fn selected_title(&self) -> String {
        self.projects
            .iter()
            .find(|p| Some(p.id) == self.selected)
            .map(|p| {
                format!(
                    "{}{} · {}",
                    p.title,
                    if p.removed { " (removed)" } else { "" },
                    p.machine.label()
                )
            })
            .unwrap_or_else(|| "No project".into())
    }
    pub fn create(
        &mut self,
        machine: Machine,
        name: &str,
        parent: &str,
        start: StartWith,
        source: &str,
    ) -> Result<usize, &'static str> {
        let parent = normalize_path(machine, parent);
        let parent = parent.as_str();
        let name = name.trim();
        if name.is_empty() || name == "." || name == ".." || name.contains(['/', '\\']) {
            return Err("Enter a project name without folder separators.");
        }
        if !self.online(machine) {
            return Err("This sample machine is offline.");
        }
        if !parent.starts_with('/') {
            return Err("Enter an absolute parent folder path.");
        }
        self.list_folders(machine, parent)?;
        if start == StartWith::Clone
            && !(source.trim().starts_with("https://")
                || source.trim().starts_with("git@")
                || source.trim().starts_with('/'))
        {
            return Err("Enter a repository URL or an absolute repository path.");
        }
        let root = format!("{}/{name}", parent.trim_end_matches('/'));
        if self
            .projects
            .iter()
            .any(|p| p.machine == machine && p.root == root)
        {
            return Err("That folder is already in the sample projects.");
        }
        Ok(self.insert(machine, name, root))
    }
    pub fn add_folder(&mut self, machine: Machine, path: &str) -> Result<usize, &'static str> {
        let path = normalize_path(machine, path);
        let path = path.as_str();
        self.list_folders(machine, path)?;
        if let Some(p) = self
            .projects
            .iter_mut()
            .find(|p| p.machine == machine && p.root == path)
        {
            p.removed = false;
            return Ok(p.id);
        }
        let title = path.rsplit('/').find(|s| !s.is_empty()).unwrap_or("Root");
        Ok(self.insert(machine, title, path.into()))
    }
    pub fn online(&self, machine: Machine) -> bool {
        !self.disconnected.contains(&machine)
    }
    pub fn set_connected(&mut self, machine: Machine, connected: bool) -> Result<(), &'static str> {
        if machine == Machine::Mac {
            return Err("This Mac is always available locally.");
        }
        self.disconnected.retain(|m| *m != machine);
        if !connected {
            self.disconnected.push(machine);
        }
        Ok(())
    }
    pub fn list_folders(
        &self,
        machine: Machine,
        path: &str,
    ) -> Result<Vec<&'static str>, &'static str> {
        if !self.online(machine) {
            return Err("This sample machine is offline. Connect it in the sample machine menu.");
        }
        let path = normalize_path(machine, path);
        let path = path.as_str();
        match folders(machine, path) {
            Ok(children) => Ok(children),
            Err(_)
                if self
                    .projects
                    .iter()
                    .any(|p| p.machine == machine && p.root == path) =>
            {
                Ok(vec![])
            }
            Err(error) => Err(error),
        }
    }
    fn editable(&mut self, id: usize) -> Result<&mut SampleProject, &'static str> {
        self.projects
            .iter_mut()
            .find(|p| p.id == id && !p.removed)
            .ok_or("That sample project was removed or is unavailable.")
    }
    pub fn rename(&mut self, id: usize, title: &str) -> Result<(), &'static str> {
        let title = title.trim();
        if title.is_empty() {
            return Err("Enter a project title.");
        }
        self.editable(id)?.title = title.into();
        Ok(())
    }
    pub fn pin(&mut self, id: usize, pinned: bool) -> Result<(), &'static str> {
        let stamp = self.next_stamp;
        let project = self.editable(id)?;
        if project.pinned == pinned {
            return Ok(());
        }
        project.pinned = pinned;
        project.pinned_at = pinned.then_some(stamp);
        self.next_stamp += 1;
        Ok(())
    }
    pub fn change_folder(&mut self, id: usize, path: &str) -> Result<(), &'static str> {
        let project = self
            .projects
            .iter()
            .find(|p| p.id == id && !p.removed)
            .ok_or("That sample project was removed or is unavailable.")?;
        let path = normalize_path(project.machine, path);
        let path = path.as_str();
        self.list_folders(project.machine, path)?;
        if self
            .projects
            .iter()
            .any(|p| p.id != id && p.machine == project.machine && p.root == path)
        {
            return Err("Another project already uses that folder.");
        }
        self.editable(id)?.root = path.into();
        Ok(())
    }
    pub fn remove(&mut self, id: usize) -> Result<(), &'static str> {
        let project = self
            .projects
            .iter_mut()
            .find(|p| p.id == id)
            .ok_or("That sample project is unavailable.")?;
        project.removed = true;
        Ok(())
    }
    fn insert(&mut self, machine: Machine, title: &str, root: String) -> usize {
        let id = self.projects.iter().map(|p| p.id).max().unwrap_or(0) + 1;
        self.projects.push(SampleProject {
            id,
            title: title.into(),
            machine,
            root,
            pinned: false,
            pinned_at: None,
            removed: false,
            last_used: "Just now · sample",
        });
        id
    }
}
pub fn parent_path(path: &str) -> String {
    let trimmed = path.trim_end_matches('/');
    trimmed
        .rsplit_once('/')
        .map(|(parent, _)| {
            if parent.is_empty() {
                "/".into()
            } else {
                parent.into()
            }
        })
        .unwrap_or_else(|| "/".into())
}
/// Resolve only sample-home shorthand and trailing separators; never access the filesystem.
pub fn normalize_path(machine: Machine, path: &str) -> String {
    let path = path.trim();
    let user_home = parent_path(machine.home());
    let expanded = if path == "~" {
        user_home
    } else if let Some(suffix) = path.strip_prefix("~/") {
        format!("{user_home}/{suffix}")
    } else {
        path.into()
    };
    let trimmed = expanded.trim_end_matches('/');
    if trimmed.is_empty() && expanded.starts_with('/') {
        "/".into()
    } else {
        trimmed.into()
    }
}
fn sample_children(machine: Machine) -> Vec<&'static str> {
    match machine {
        Machine::Mac => vec!["Assembly", "ClipNSave", "notes", "playground"],
        Machine::Workbox => vec!["Assembly", "retailer-notes", "notes", "playground"],
        Machine::Offline => vec!["journal", "notes", "playground"],
    }
}
pub const SAMPLE_FOLDER_LIMITATION: &str =
    "This preview cannot browse real folders. Choose a sample folder below.";

pub fn folders(machine: Machine, path: &str) -> Result<Vec<&'static str>, &'static str> {
    let path = normalize_path(machine, path);
    let home = machine.home();
    if path == home {
        return Ok(sample_children(machine));
    }
    if path == parent_path(home) {
        return Ok(vec!["dev"]);
    }
    if path == parent_path(&parent_path(home)) {
        return Ok(vec!["sample"]);
    }
    if path == "/" {
        return Ok(vec![if machine == Machine::Mac {
            "Users"
        } else {
            "home"
        }]);
    }
    if path
        .strip_prefix(&format!("{home}/"))
        .is_some_and(|s| sample_children(machine).contains(&s))
    {
        return Ok(vec![]);
    }
    Err(SAMPLE_FOLDER_LIMITATION)
}
#[cfg(test)]
#[path = "sample_tests.rs"]
mod tests;
