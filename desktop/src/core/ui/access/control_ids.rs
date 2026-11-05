//! The ids that name Assembly's controls in the accessibility tree: the one list a script (`agent-desktop find
//! --native-id <id>`) or a test can rely on. An id is `<area>.<control>`, lower-case words joined by `-`, and never
//! changes once listed; a control that repeats (a tab, a row, a machine's button) gets its area's function below,
//! which adds what tells its copies apart.
//!
//! A control is named with `access::ControlButton::control` or `access::ExposedElement::exposed`, using one of
//! these. Nothing else in the app may set an accessibility id, so this file is the whole list.

/// `ids! { area { NAME = "area.control", .. } .. }` makes `area::NAME` for each and `ALL`, every listed id.
macro_rules! ids {
    ($($area:ident { $($name:ident = $id:literal,)* })*) => {
        $(pub mod $area { $(pub const $name: &str = $id;)* })*
        /// Every fixed id, for the test that keeps them unique and well-formed.
        pub const ALL: &[&str] = &[$($($id),*),*];
    };
}

ids! {
    topbar {
        HOME = "topbar.home",
        BOARD = "topbar.board",
        BOARD_BADGE = "topbar.board-badge",
        DRAWER_TOGGLE = "topbar.drawer-toggle",
        RESTART_TO_UPDATE = "topbar.restart-to-update",
    }
    drawer {
        MORE_PANELS = "drawer.more-panels",
        CLOSE = "drawer.close",
    }
    sessions {
        SEARCH = "sessions.search",
        VIEW_OPTIONS = "sessions.view-options",
        NEW_SESSION = "sessions.new-session",
        NEEDS_YOU = "sessions.needs-you",
        EMPTY = "sessions.empty",
        ROW_ERROR = "sessions.row-error",
        RAIL_ERROR = "sessions.rail-error",
        RETRY = "sessions.retry",
    }
    utility {
        SETTINGS = "utility.settings",
        RESOURCES = "utility.resources",
        USAGE = "utility.usage",
        RESOURCES_EMPTY = "utility.resources-empty",
        USAGE_EMPTY = "utility.usage-empty",
    }
    composer {
        INPUT = "composer.input",
        ATTACH = "composer.attach",
        SEND = "composer.send",
        STOP = "composer.stop",
        MIC = "composer.mic",
        CONNECT = "composer.connect",
    }
    settings {
        MACHINE_NAME = "settings.machine-name",
        MACHINE_SSH = "settings.machine-ssh",
        MACHINE_SSH_RESOLVED = "settings.machine-ssh-resolved",
        MACHINE_HOST_SEARCH = "settings.machine-host-search",
        MACHINE_PORT = "settings.machine-port",
        MACHINE_TOKEN_FILE = "settings.machine-token-file",
        MACHINE_SAVE = "settings.machine-save",
        MACHINE_CANCEL = "settings.machine-cancel",
        MACHINE_FORM_TITLE = "settings.machine-form-title",
        MACHINE_FORM_ERROR = "settings.machine-form-error",
    }
    changes {
        REFRESH = "changes.refresh",
    }
    commits {
        REFRESH = "commits.refresh",
    }
    git {
        ACTIONS = "git.actions",
    }
    history {
        PATH_CLEAR = "history.path-clear",
        REFRESH = "history.refresh",
    }
    pr {
        BACK = "pr.back",
        OPEN_WEB = "pr.open-web",
        QUEUE_REFRESH = "pr.queue-refresh",
        REFRESH = "pr.refresh",
        SECTION_REFRESH = "pr.section-refresh",
    }
    task {
        BACK = "task.back",
        OPEN = "task.open",
    }
    tasks {
        REFRESH = "tasks.refresh",
        SETTINGS = "tasks.settings",
    }
    board {
        HEADING = "board.heading",
        SUMMARY = "board.summary",
        OPEN_FAILED = "board.open-failed",
    }
}

/// A lower-case, `-`-joined form of a name, so "Source Control" is `source-control`.
pub fn slug(name: &str) -> String {
    let mut out = String::new();
    for word in name.split(|c: char| !c.is_alphanumeric()) {
        if word.is_empty() {
            continue;
        }
        if !out.is_empty() {
            out.push('-');
        }
        out.extend(word.chars().flat_map(char::to_lowercase));
    }
    out
}

/// A tab in the window chrome (the content tabs): `tabs.chat`, `tabs.changes`.
pub fn content_tab(name: &str) -> String {
    format!("tabs.{}", slug(name))
}

/// A tab over the sessions column: `side-tabs.sessions`, `side-tabs.files`.
pub fn side_tab(name: &str) -> String {
    format!("side-tabs.{}", slug(name))
}

/// A tool button in the drawer's capsule: `drawer.pane.source-control`.
pub fn drawer_pane(name: &str) -> String {
    format!("drawer.pane.{}", slug(name))
}

/// A quick filter's group pill, in the list `area` (`sessions`, `tasks`): `sessions.filter.provider`.
pub fn filter_group(area: &str, group: &str) -> String {
    format!("{area}.filter.{}", slug(group))
}

/// The round × that clears every quick filter of the list `area`: `sessions.filter-clear`.
pub fn filter_clear(area: &str) -> String {
    format!("{area}.filter-clear")
}

/// The "← Back" pill of an open quick filter: `sessions.filter-back`.
pub fn filter_back(area: &str) -> String {
    format!("{area}.filter-back")
}

/// One value of an open quick filter: `sessions.filter.provider.claude`.
pub fn filter_value(area: &str, group: &str, value: &str) -> String {
    format!("{}.{}", filter_group(area, group), slug(value))
}

/// A row of a list, by the key its owner gave it (`session:<id>`): `rows.session.<id>`.
pub fn row(key: &str) -> String {
    format!("rows.{}", key.replace(':', "."))
}

/// A button on a remote machine's row in Settings > Connections: `settings.machine.workbox-test.connect`
/// (`connect`, `disconnect`, `edit`, `remove`, `update`, `cancel`, `restart`).
pub fn machine_button(machine: &str, action: &str) -> String {
    format!("settings.machine.{}.{action}", slug(machine))
}

/// A host in Add a machine's search box: `settings.host.workbox-test`.
pub fn machine_host(host: &str) -> String {
    format!("settings.host.{}", slug(host))
}

/// What a remote machine's row says in Settings > Connections: `settings.machine.workbox-test.status`
/// (`name`, `status`, `error`). This machine's row is `local`.
pub fn machine_line(machine: &str, line: &str) -> String {
    format!("settings.machine.{}.{line}", slug(machine))
}

/// A button of a question's form, on whichever surface draws it: `ask.a1.continue` (`cancel`, `skip`, `previous`,
/// `blank`, `continue`).
pub fn ask_control(key: &str, action: &str) -> String {
    format!("ask.{}.{action}", slug(key))
}

/// A tab of the Build Board's panel: `board.tab.needs-you`, `board.tab.workflows`.
pub fn board_tab(name: &str) -> String {
    format!("board.tab.{}", slug(name))
}
