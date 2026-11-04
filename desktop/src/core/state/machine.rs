//! `Machine` is one machine from the `machines` topic (dotnet/Assembly.Application/Common/Machines/MachineStatus.cs,
//! `MachineItem`): whether it answered last time, and why not, and for a saved remote how it's reached.

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Machine {
    /// `None` for this machine.
    pub name: Option<String>,
    pub kind: String,
    pub reachable: bool,
    /// Why it didn't answer, or another connection warning; may be present while reachable.
    pub reason: Option<String>,
    /// False for a remote until its first probe answers or fails: until then it's neither up nor down.
    #[serde(default)]
    pub checked: bool,
    /// A remote's SSH destination, port and token file (a path on the remote); `None` for this machine.
    #[serde(default)]
    pub ssh: Option<String>,
    #[serde(default)]
    pub port: Option<u16>,
    #[serde(default)]
    pub token_file: Option<String>,
    /// Which backend a remote runs, from its last welcome.
    #[serde(default)]
    pub backend: Option<RemoteBackend>,
    /// The update check's last answer: adapters, CLIs and the desktop or backend.
    #[serde(default)]
    pub updates: Vec<UpdateItem>,
    /// Whether the one update is running.
    #[serde(default)]
    pub updating: bool,
    /// What the update is doing, or why it failed.
    #[serde(default)]
    pub update_note: Option<String>,
}

/// Information only: nothing is refused on it.
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteBackend {
    pub commit: Option<String>,
    pub local_commit: Option<String>,
    /// Any difference in the backend's build inputs.
    pub differs: bool,
}

/// One piece the update check looked at. `kind` is desktop, backend, adapter or cli; `state` is current, behind or unknown.
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateItem {
    pub kind: String,
    pub name: String,
    pub current: Option<String>,
    pub wanted: Option<String>,
    pub state: String,
}

impl UpdateItem {
    pub fn is_behind(&self) -> bool {
        self.state == "behind"
    }
}

impl Machine {
    /// The pieces behind on this machine.
    pub fn behind(&self) -> impl Iterator<Item = &UpdateItem> {
        self.updates.iter().filter(|item| item.is_behind())
    }

    /// Which commit a remote's backend is on against this app's. Shown as information; only a check before a PR needs them equal.
    pub fn backend_note(&self) -> Option<String> {
        let backend = self.backend.as_ref()?;
        let short = |commit: &Option<String>| match commit {
            Some(commit) => commit.chars().take(7).collect::<String>(),
            None => "unknown".to_string(),
        };
        Some(if backend.differs {
            format!(
                "Backend {}, differs from this app's {}",
                short(&backend.commit),
                short(&backend.local_commit)
            )
        } else {
            format!("Backend {}, same as this app", short(&backend.commit))
        })
    }

    /// A probe found it down; one not probed yet hasn't failed to answer.
    pub fn didnt_answer(&self) -> bool {
        self.checked && !self.reachable
    }
}
