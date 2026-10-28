//! `Section` is one page of Settings, listed in the left column under its group. The order, groups, names and icons are
//! the old app's (SettingsDialog.svelte:116-126).

use gpui_kit::assets::IconName;

pub struct Section {
    pub id: &'static str,
    /// The label the left column lists it under; a label only, nothing collapses.
    pub group: &'static str,
    pub label: &'static str,
    pub icon: IconName,
}

pub const SECTIONS: [Section; 9] = [
    Section {
        id: "appearance",
        group: "Interface",
        label: "Appearance",
        icon: IconName::Palette,
    },
    Section {
        id: "editor",
        group: "Workspace",
        label: "Editor",
        icon: IconName::Type,
    },
    Section {
        id: "terminal",
        group: "Workspace",
        label: "Terminal",
        icon: IconName::SquareTerminal,
    },
    Section {
        id: "general",
        group: "Application",
        label: "General",
        icon: IconName::SlidersHorizontal,
    },
    Section {
        id: "connections",
        group: "Application",
        label: "Connections",
        icon: IconName::Server,
    },
    Section {
        id: "updates",
        group: "Application",
        label: "Updates",
        icon: IconName::Download,
    },
    Section {
        id: "agents",
        group: "Application",
        label: "Agents",
        icon: IconName::Sparkles,
    },
    Section {
        id: "plugins",
        group: "Application",
        label: "Plugins and Providers",
        icon: IconName::Puzzle,
    },
    Section {
        id: "helper",
        group: "Application",
        label: "Utility model",
        icon: IconName::Sparkles,
    },
];
