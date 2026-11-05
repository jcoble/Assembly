//! `Section` is one page of Settings, listed in the kit's sidebar. The order, names and icons are the old app's
//! (SettingsDialog.svelte:116-126); its group labels have no place in the kit's flat sidebar.

use gpui_kit::assets::IconName;

pub struct Section {
    pub id: &'static str,
    pub label: &'static str,
    pub icon: IconName,
}

pub const SECTIONS: [Section; 9] = [
    Section {
        id: "appearance",
        label: "Appearance",
        icon: IconName::Palette,
    },
    Section {
        id: "editor",
        label: "Editor",
        icon: IconName::Type,
    },
    Section {
        id: "terminal",
        label: "Terminal",
        icon: IconName::SquareTerminal,
    },
    Section {
        id: "general",
        label: "General",
        icon: IconName::SlidersHorizontal,
    },
    Section {
        id: "connections",
        label: "Connections",
        icon: IconName::Server,
    },
    Section {
        id: "updates",
        label: "Updates",
        icon: IconName::Download,
    },
    Section {
        id: "agents",
        label: "Agents",
        icon: IconName::Sparkles,
    },
    Section {
        id: "plugins",
        label: "Plugins and Providers",
        icon: IconName::Puzzle,
    },
    Section {
        id: "helper",
        label: "Utility model",
        icon: IconName::Sparkles,
    },
];
