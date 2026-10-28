//! `Row` is one setting on a Settings page: its title and one-line description, the card it sits in, and the words
//! someone is likely to search for that it doesn't say itself. Every row is listed once here, so the search and the
//! page read the same wording. The wording is the old app's (SettingsDialog.svelte:128-207).

pub struct Row {
    pub id: &'static str,
    pub section: &'static str,
    pub card: &'static str,
    pub title: &'static str,
    pub description: &'static str,
    pub keywords: &'static str,
}

pub const ROWS: [Row; 11] = [
    Row {
        id: "theme",
        section: "appearance",
        card: "Colors",
        title: "Theme",
        description: "Overall color scheme for the app.",
        keywords: "dark light palette houston",
    },
    Row {
        id: "ui-font",
        section: "appearance",
        card: "Text",
        title: "Interface font",
        description: "The face for labels, menus and messages.",
        keywords: "typeface family sans",
    },
    Row {
        id: "code-font",
        section: "appearance",
        card: "Text",
        title: "Code font",
        description: "The face for paths, commands and code outside the editor.",
        keywords: "typeface family monospace",
    },
    Row {
        id: "ui-font-size",
        section: "appearance",
        card: "Text",
        title: "Interface font size",
        description: "Base size for chrome, labels and menus.",
        keywords: "type scale bigger smaller",
    },
    Row {
        id: "editor-font",
        section: "editor",
        card: "Text",
        title: "Font family",
        description: "Typeface used for files and diffs.",
        keywords: "monospace typeface code",
    },
    Row {
        id: "editor-font-size",
        section: "editor",
        card: "Text",
        title: "Font size",
        description: "Editor text size in pixels.",
        keywords: "type scale bigger smaller",
    },
    Row {
        id: "editor-line-height",
        section: "editor",
        card: "Text",
        title: "Line height",
        description: "Absolute line height in pixels.",
        keywords: "spacing leading rows",
    },
    Row {
        id: "terminal-font",
        section: "terminal",
        card: "Text",
        title: "Font family",
        description: "Typeface used in the embedded terminal.",
        keywords: "monospace typeface",
    },
    Row {
        id: "terminal-font-size",
        section: "terminal",
        card: "Text",
        title: "Font size",
        description: "Terminal text size in pixels.",
        keywords: "type scale bigger smaller",
    },
    Row {
        id: "terminal-line-height",
        section: "terminal",
        card: "Text",
        title: "Line height",
        description: "Line spacing multiplier (relative to font size).",
        keywords: "spacing leading",
    },
    Row {
        id: "machines",
        section: "connections",
        card: "Remote machines",
        title: "Machines",
        description: "This Mac and the remote machines the backend knows.",
        keywords: "ssh workbox server remote reconnect connect disconnect add edit remove port token",
    },
];
