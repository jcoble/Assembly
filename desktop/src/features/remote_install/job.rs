//! What the backend says about a bundle and an install, and the rules for reading it: which updates count, what can
//! still be cancelled, and whether a success proves what was asked for.

use serde::Deserialize;

pub const TARGET: &str = "linux-x64";
pub const DEVELOPMENT: &str = "development";

/// A bundle's strict manifest, as `inspect` read it.
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BundleFacts {
    pub sha256: String,
    pub bytes: u64,
    pub commit: String,
    pub target: String,
    pub protocol: i64,
    pub format: i64,
    pub build_kind: String,
}

impl BundleFacts {
    /// Why this control won't deploy the bundle. Anything but a development build for the test machine's platform.
    pub fn refusal(&self) -> Option<String> {
        if self.build_kind != DEVELOPMENT {
            return Some(format!(
                "This is a {} build, not a development one.",
                self.build_kind
            ));
        }
        if self.target != TARGET {
            return Some(format!("This bundle is for {}, not {TARGET}.", self.target));
        }
        None
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum JobState {
    Accepted,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Uncertain,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    Checking,
    Uploading,
    Validating,
    Activating,
    Connecting,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct JobResult {
    pub commit: String,
    pub machine: String,
    pub engine: String,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Job {
    pub operation_id: String,
    pub version: i64,
    pub machine: String,
    pub state: JobState,
    pub phase: Phase,
    pub bytes_done: u64,
    pub total_bytes: u64,
    pub error_code: Option<String>,
    pub result: Option<JobResult>,
}

/// What this install was asked to do, kept to check its success against.
#[derive(Clone, Debug, PartialEq)]
pub struct Expected {
    pub operation_id: String,
    pub machine: String,
    pub engine: String,
    pub commit: String,
}

/// How an install ended, as far as this window can tell.
#[derive(Clone, Debug, PartialEq)]
pub enum Ending {
    /// The machine runs the declared commit on the same Engine.
    Confirmed(JobResult),
    /// The backend said it succeeded, and its result doesn't prove what was asked for.
    Unproven,
}

impl Job {
    /// Whether `update` replaces this job: the same install, and newer.
    pub fn takes(current: Option<&Job>, update: &Job, operation_id: &str) -> bool {
        update.operation_id.eq_ignore_ascii_case(operation_id)
            && current.is_none_or(|job| update.version > job.version)
    }

    pub fn settled(&self) -> bool {
        matches!(
            self.state,
            JobState::Succeeded | JobState::Failed | JobState::Cancelled | JobState::Uncertain
        )
    }

    /// Cancel is offered until the install starts replacing the running backend; after that it has to play out.
    pub fn cancellable(&self) -> bool {
        !self.settled()
            && matches!(
                self.phase,
                Phase::Checking | Phase::Uploading | Phase::Validating
            )
    }

    /// How far through, 0 to 100, while there are bytes to count.
    pub fn percent(&self) -> Option<f32> {
        (self.total_bytes > 0).then(|| {
            (self.bytes_done.min(self.total_bytes) as f64 / self.total_bytes as f64 * 100.) as f32
        })
    }

    /// A success counts once its result matches. `expected` is gone after an app restart, and then only the job's own
    /// machine can be compared.
    pub fn ending(&self, expected: Option<&Expected>) -> Option<Ending> {
        if self.state != JobState::Succeeded {
            return None;
        }
        let Some(result) = &self.result else {
            return Some(Ending::Unproven);
        };
        let matches = result.machine == self.machine
            && expected.is_none_or(|expected| {
                result.machine == expected.machine
                    && result.engine == expected.engine
                    && result.commit == expected.commit
            });
        Some(if matches {
            Ending::Confirmed(result.clone())
        } else {
            Ending::Unproven
        })
    }
}

pub fn phase_words(phase: Phase) -> &'static str {
    match phase {
        Phase::Checking => "Checking the bundle and the machine",
        Phase::Uploading => "Uploading",
        Phase::Validating => "Validating on the machine",
        Phase::Activating => "Restarting the test backend",
        Phase::Connecting => "Waiting for the backend to answer",
    }
}

pub fn state_words(job: &Job) -> String {
    match job.state {
        JobState::Accepted => "Waiting to start".into(),
        JobState::Running => phase_words(job.phase).into(),
        JobState::Succeeded => "Deployed".into(),
        JobState::Failed => error_words(job.error_code.as_deref().unwrap_or_default()).into(),
        JobState::Cancelled => "Cancelled".into(),
        JobState::Uncertain => match job.error_code.as_deref() {
            Some(code) => format!("The outcome is unknown. {}", error_words(code)),
            None => "The outcome is unknown. Check the machine before deploying again.".into(),
        },
    }
}

/// A failed job's reason, from the backend's closed list. The code shows beside it, and process output never does.
pub fn error_words(code: &str) -> &'static str {
    match code {
        "invalid_bundle" => "The bundle isn't a valid development bundle.",
        "bundle_changed" => "The bundle file changed after it was inspected. Inspect it again.",
        "target_changed" => "The machine isn't the one that was checked. Choose it again.",
        "unsupported_target" => {
            "This machine isn't the owned test backend this control can update."
        }
        "remote_busy" => "The test machine is busy with another install.",
        "ssh_unavailable" => "The machine couldn't be reached over SSH.",
        "stage_failed" => {
            "The bundle couldn't be put in place on the machine. Nothing was restarted."
        }
        "maintenance_unavailable" => "The machine's maintenance step isn't available.",
        "activation_refused" => "The machine refused the restart before anything changed.",
        "activation_rolled_back" => "The new backend didn't start, so the old one was put back.",
        "activation_uncertain" => {
            "The restart's outcome is unknown. Check the machine before deploying again."
        }
        "cleanup_unconfirmed" => "The deploy finished, but its cleanup couldn't be confirmed.",
        "cancelled" => "The deploy was cancelled.",
        _ => "The deploy failed.",
    }
}

/// Why `start` or `cancel` was turned away, for a code the backend promises means nothing began.
pub fn refusal_words(code: &str) -> &'static str {
    match code {
        "forbidden" => "This connection isn't allowed to deploy.",
        "operation_id_conflict" => {
            "That install id is already in use with different details. Try again."
        }
        "install_busy" => "An install is already running on this machine.",
        "operation_capacity" => {
            "The backend is holding too many installs. Restart it or try later."
        }
        "operation_unknown" => "The backend has no record of this install.",
        _ => "The backend refused this deploy.",
    }
}

/// `2.4 MB`, or bytes below a kilobyte's worth.
pub fn size_words(bytes: u64) -> String {
    const KILO: f64 = 1024.;
    let bytes = bytes as f64;
    if bytes < KILO {
        format!("{bytes} B")
    } else if bytes < KILO * KILO {
        format!("{:.1} KB", bytes / KILO)
    } else if bytes < KILO * KILO * KILO {
        format!("{:.1} MB", bytes / (KILO * KILO))
    } else {
        format!("{:.2} GB", bytes / (KILO * KILO * KILO))
    }
}

#[cfg(test)]
#[path = "job_tests.rs"]
mod tests;
