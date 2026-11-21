//! The app's look as colour tokens, by the names on the rule sheet ("Assembly look: the rule sheet",
//! https://app.notion.com/p/41f394b0689d81f6bd5dd1a4bae1b24e). Views read them with `Look::of(cx)`.
//!
//! Each token has one home in the theme file. Most live on one of GPUI Kit's own keys (the kit's components read
//! those too). The tokens the kit has no key for live in the file's own `assembly` section, which the kit skips and
//! `OwnKeys` reads here. A theme without that section (the copied themes) gives each such token the kit colour named
//! beside it in `Look::of`, so every theme still draws. `apply::set` reads the section when a theme goes in force, and
//! the window root reads it again when the kit reloads the theme folder.

use std::fs;
use std::path::{Path, PathBuf};

use gpui_kit::component::{ActiveTheme as _, try_parse_color};
use gpui_kit::{App, Global, Hsla, transparent_black};
use serde::{Deserialize, Deserializer};

use super::theme_id::ThemeId;

/// The theme folder `init` was given, so the `assembly` section is read from the same files the kit loaded.
pub(super) struct ThemesDir(pub PathBuf);

impl Global for ThemesDir {}

/// One colour in the `assembly` section, parsed the way the kit parses its own keys (hex with optional alpha).
#[derive(Clone, Copy)]
struct Colour(Hsla);

impl<'de> Deserialize<'de> for Colour {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        try_parse_color(&text)
            .map(Colour)
            .map_err(serde::de::Error::custom)
    }
}

/// The theme file's `assembly` section: the tokens GPUI Kit has no key for. A missing key is `None`.
#[derive(Default, Deserialize)]
#[serde(default)]
struct OwnColours {
    #[serde(rename = "on_glass.icon")]
    on_glass_icon: Option<Colour>,
    #[serde(rename = "on_glass.line.selected")]
    on_glass_line_selected: Option<Colour>,
    #[serde(rename = "on_glass.line.chip")]
    on_glass_line_chip: Option<Colour>,
    #[serde(rename = "on_glass.inset")]
    on_glass_inset: Option<Colour>,
    #[serde(rename = "card.raised")]
    card_raised: Option<Colour>,
    bubble: Option<Colour>,
    #[serde(rename = "bubble.border")]
    bubble_border: Option<Colour>,
    composer: Option<Colour>,
    #[serde(rename = "composer.border")]
    composer_border: Option<Colour>,
    question: Option<Colour>,
    picker: Option<Colour>,
    #[serde(rename = "picker.border")]
    picker_border: Option<Colour>,
    #[serde(rename = "popover.border")]
    popover_border: Option<Colour>,
    #[serde(rename = "popover.card")]
    popover_card: Option<Colour>,
    #[serde(rename = "popover.card.border")]
    popover_card_border: Option<Colour>,
    #[serde(rename = "popover.inset")]
    popover_inset: Option<Colour>,
    #[serde(rename = "popover.inset.border")]
    popover_inset_border: Option<Colour>,
    #[serde(rename = "line.chip")]
    line_chip: Option<Colour>,
    #[serde(rename = "hairline.strong")]
    hairline_strong: Option<Colour>,
    tree: Option<Colour>,
    #[serde(rename = "text.secondary")]
    text_2: Option<Colour>,
    #[serde(rename = "accent_fill.edge")]
    accent_fill_edge: Option<Colour>,
    #[serde(rename = "link.border")]
    link_border: Option<Colour>,
    #[serde(rename = "ring.halo")]
    focus_halo: Option<Colour>,
    #[serde(rename = "ring.button")]
    focus_ring_button: Option<Colour>,
    #[serde(rename = "state.idle")]
    idle: Option<Colour>,
    #[serde(rename = "state.working.track")]
    working_track: Option<Colour>,
    #[serde(rename = "need.line.row")]
    needs_you_line_row: Option<Colour>,
    #[serde(rename = "need.line")]
    needs_you_line: Option<Colour>,
    #[serde(rename = "need.wash.row")]
    needs_you_wash_row: Option<Colour>,
    #[serde(rename = "need.line.card")]
    needs_you_line_card: Option<Colour>,
    #[serde(rename = "need.wash.banner")]
    needs_you_wash_banner: Option<Colour>,
    #[serde(rename = "need.line.button")]
    needs_you_line_button: Option<Colour>,
    #[serde(rename = "need.text.button")]
    needs_you_text_button: Option<Colour>,
    #[serde(rename = "danger.line")]
    problem_line: Option<Colour>,
    #[serde(rename = "diff.added.background")]
    diff_added_background: Option<Colour>,
    #[serde(rename = "diff.removed.background")]
    diff_removed_background: Option<Colour>,
}

/// The shape of a theme file, as far as the `assembly` section: one theme in a set, as every file here has.
#[derive(Deserialize)]
struct ThemeFile {
    themes: Vec<ThemeEntry>,
}

#[derive(Deserialize)]
struct ThemeEntry {
    #[serde(default)]
    assembly: OwnColours,
}

/// The `assembly` section of the theme in force, and the file it came from, so a reload of the folder re-reads it.
#[derive(Default)]
pub struct OwnKeys {
    path: Option<PathBuf>,
    colours: OwnColours,
}

impl Global for OwnKeys {}

impl OwnKeys {
    /// Reads the section of `id`'s file in the folder `init` was given. With no folder (the kit's default theme) or no
    /// readable file, every token falls back to its kit colour.
    pub(super) fn load(id: ThemeId, cx: &mut App) {
        let path = cx
            .try_global::<ThemesDir>()
            .map(|dir| dir.0.join(format!("{}.json", id.as_str())));
        let colours = path.as_deref().map(read).unwrap_or_default();
        cx.set_global(OwnKeys { path, colours });
    }

    /// Reads the same file again, after the kit reloaded the theme folder because a file there changed.
    pub fn reload(cx: &mut App) {
        let Some(path) = cx
            .try_global::<OwnKeys>()
            .and_then(|keys| keys.path.clone())
        else {
            return;
        };
        let colours = read(&path);
        cx.set_global(OwnKeys {
            path: Some(path),
            colours,
        });
    }
}

/// The section in the file at `path`. A file that can't be read or parsed gives an empty section: the kit has already
/// logged a broken file, and its theme draws with the fallbacks.
fn read(path: &Path) -> OwnColours {
    fs::read_to_string(path)
        .ok()
        .and_then(|text| serde_json::from_str::<ThemeFile>(&text).ok())
        .and_then(|file| file.themes.into_iter().next())
        .map(|theme| theme.assembly)
        .unwrap_or_default()
}

/// Every colour token on the rule sheet, resolved for the theme in force. The field names are the sheet's token names;
/// each comment names the token's home key in the theme file.
// Later lanes read most of these as they rebuild each screen in the look, so not every field is read yet.
#[allow(dead_code)]
#[derive(Clone, Copy, Debug)]
pub struct Look {
    // Frame and top bar, on glass.
    /// `title_bar.background`: the window's glass. Opaque off macOS, where there's no blur behind it.
    pub glass: Hsla,
    /// `window.border`: the window's 1px edge.
    pub window_line: Hsla,
    /// `tab.active.foreground`: top-bar labels.
    pub on_glass_text_1: Hsla,
    /// `tab.foreground`: inactive tab names and top-bar icons.
    pub on_glass_text_2: Hsla,
    /// `assembly` `on_glass.icon`: icons only, never text.
    pub on_glass_icon_3: Hsla,
    /// `tab.active.background`: the active tab and pressed top-bar buttons.
    pub on_glass_fill_selected: Hsla,
    /// `title_bar.border`: top-bar pill outlines.
    pub on_glass_line: Hsla,
    /// `assembly` `on_glass.line.selected`: the active tab's outline.
    pub on_glass_line_selected: Hsla,
    /// `assembly` `on_glass.line.chip`: top-bar chips.
    pub on_glass_line_chip: Hsla,
    /// `assembly` `on_glass.inset`: the "N need you" pill's fill.
    pub on_glass_inset: Hsla,

    // Tiles and surfaces.
    /// `background`: the left list, the chat sheet, the drawer, editor panes.
    pub tile: Hsla,
    /// `sidebar.border`: a tile's 1px edge.
    pub tile_edge: Hsla,
    /// `tab_bar.segmented.background`: inset fields, the segmented track, code bodies, the terminal.
    pub inset: Hsla,
    /// `muted.background`: cards and raised pieces on a tile.
    pub card: Hsla,
    /// `assembly` `card.raised`: a card on a card.
    pub card_raised: Hsla,
    /// `assembly` `bubble`: the user's message bubble.
    pub bubble: Hsla,
    /// `assembly` `bubble.border`.
    pub bubble_border: Hsla,
    /// `assembly` `composer`.
    pub composer: Hsla,
    /// `assembly` `composer.border`.
    pub composer_border: Hsla,
    /// `assembly` `question`: the agent's question card.
    pub question: Hsla,
    /// `assembly` `picker`: composer pickers.
    pub picker_pill: Hsla,
    /// `assembly` `picker.border`.
    pub picker_pill_border: Hsla,
    /// `popover.background`: the Home panel, the search, menus, pickers, dialogs.
    pub popover: Hsla,
    /// `assembly` `popover.border`: a popover's edge.
    pub popover_edge: Hsla,
    /// `assembly` `popover.card`: cards inside a popover.
    pub popover_card: Hsla,
    /// `assembly` `popover.card.border`.
    pub popover_card_border: Hsla,
    /// `assembly` `popover.inset`: inset lists and mono tags in a popover card.
    pub popover_inset: Hsla,
    /// `assembly` `popover.inset.border`.
    pub popover_inset_border: Hsla,
    /// `overlay`: behind an open popover, dialog or sheet.
    pub scrim: Hsla,

    // Lines.
    /// `border`: the default outline inside a tile.
    pub line: Hsla,
    /// `assembly` `line.chip`: filter chips and "+ New" pills.
    pub line_chip: Hsla,
    /// `list.active.border`: the selected row, file or segment.
    pub line_selected: Hsla,
    /// `table.row.border`: dividers inside the sheet.
    pub hairline: Hsla,
    /// `assembly` `hairline.strong`: the thought rule, thumbnails.
    pub hairline_strong: Hsla,
    /// `assembly` `tree`: the helper tree line in the rail.
    pub tree: Hsla,
    /// `list.active.background`: the selected row's or pill's fill.
    pub fill_selected: Hsla,
    /// `list.hover.background`: a row under the pointer, and an open row.
    pub fill_hover: Hsla,

    // Text inside tiles, popovers and cards.
    /// `foreground`: titles, body, chat prose, values.
    pub text_1: Hsla,
    /// `assembly` `text.secondary`: meta lines, labels, thoughts, inactive controls.
    pub text_2: Hsla,
    /// `muted.foreground`: times, counts, hints, placeholders. Never on glass.
    pub text_3: Hsla,

    // Accent.
    /// `primary.background`: the one main action in a place.
    pub accent_fill: Hsla,
    /// `primary.hover.background`.
    pub accent_fill_hover: Hsla,
    /// `primary.active.background`.
    pub accent_fill_pressed: Hsla,
    /// `primary.foreground`: text on the accent fill.
    pub on_accent_fill: Hsla,
    /// `assembly` `accent_fill.edge`: the accent fill's inner edge.
    pub accent_fill_edge: Hsla,
    /// `caret`: the working arc, focus, code keywords, link icons.
    pub accent: Hsla,
    /// `link`: text links and link pills.
    pub link: Hsla,
    /// `assembly` `link.border`: a link pill's outline.
    pub link_border: Hsla,
    /// `ring`: the 1px focus ring on fields.
    pub focus_ring: Hsla,
    /// `assembly` `ring.halo`: the 3px halo around a focused field.
    pub focus_halo: Hsla,
    /// `assembly` `ring.button`: a focused button's ring.
    pub focus_ring_button: Hsla,

    // States. Each also has its glyph's shape; colour never carries a state alone.
    /// `success.background`: ready, done, signed in; diff lines added.
    pub ready: Hsla,
    /// `warning.background`: needs you, and update ready.
    pub needs_you: Hsla,
    /// `danger.background`: a problem; diff lines removed.
    pub problem: Hsla,
    /// `assembly` `state.idle`: idle, offline, and a finished sub-task's tick.
    pub idle: Hsla,
    /// `assembly` `state.working.track`: the working arc's track.
    pub working_track: Hsla,
    /// `assembly` `need.line.row`: a session row in the Needs you group.
    pub needs_you_line_row: Hsla,
    /// `assembly` `need.line`: that row selected, the "N need you" pill, amber Home, the Needs you filter.
    pub needs_you_line: Hsla,
    /// `assembly` `need.wash.row`: a selected Needs you row's fill.
    pub needs_you_wash_row: Hsla,
    /// `assembly` `need.line.card`: the question card and the update banner.
    pub needs_you_line_card: Hsla,
    /// `assembly` `need.wash.banner`: the update banner's fill.
    pub needs_you_wash_banner: Hsla,
    /// `assembly` `need.line.button`: the Restart to update button, the strongest coloured line.
    pub needs_you_line_button: Hsla,
    /// `assembly` `need.text.button`: that button's text.
    pub needs_you_text_button: Hsla,
    /// `assembly` `danger.line`: the conversation error bar.
    pub problem_line: Hsla,
    /// `assembly` `diff.added.background`: an added line's fill.
    pub diff_added_background: Hsla,
    /// `assembly` `diff.removed.background`: a removed line's fill.
    pub diff_removed_background: Hsla,
}

impl Look {
    /// The tokens for the theme in force. Cheap: a copy of colours already parsed, so a view reads it in `render`.
    pub fn of(cx: &App) -> Look {
        let theme = cx.theme();
        let own = cx.try_global::<OwnKeys>().map(|keys| &keys.colours);
        let none = OwnColours::default();
        let own = own.unwrap_or(&none);
        // A token from the `assembly` section, or the kit colour a theme without the section stands it on.
        let or = |colour: Option<Colour>, fallback: Hsla| colour.map_or(fallback, |c| c.0);
        let clear = transparent_black();
        Look {
            glass: if cfg!(target_os = "macos") {
                theme.title_bar
            } else {
                // No blur off macOS, so the glass is drawn solid instead of showing the desktop through it.
                theme.title_bar.alpha(1.)
            },
            window_line: theme.window_border,
            on_glass_text_1: theme.tab_active_foreground,
            on_glass_text_2: theme.tab_foreground,
            on_glass_icon_3: or(own.on_glass_icon, theme.tab_foreground),
            on_glass_fill_selected: theme.tab_active,
            on_glass_line: theme.title_bar_border,
            on_glass_line_selected: or(own.on_glass_line_selected, theme.title_bar_border),
            on_glass_line_chip: or(own.on_glass_line_chip, theme.title_bar_border),
            on_glass_inset: or(own.on_glass_inset, theme.muted),

            tile: theme.background,
            tile_edge: theme.sidebar_border,
            inset: theme.tab_bar_segmented,
            card: theme.muted,
            card_raised: or(own.card_raised, theme.secondary_hover),
            bubble: or(own.bubble, theme.muted),
            bubble_border: or(own.bubble_border, theme.border),
            composer: or(own.composer, theme.muted),
            composer_border: or(own.composer_border, theme.border),
            question: or(own.question, theme.muted),
            picker_pill: or(own.picker, theme.tab_bar_segmented),
            picker_pill_border: or(own.picker_border, theme.border),
            popover: theme.popover,
            popover_edge: or(own.popover_border, theme.border),
            popover_card: or(own.popover_card, theme.muted),
            popover_card_border: or(own.popover_card_border, theme.border),
            popover_inset: or(own.popover_inset, theme.tab_bar_segmented),
            popover_inset_border: or(own.popover_inset_border, theme.border),
            scrim: theme.overlay,

            line: theme.border,
            line_chip: or(own.line_chip, theme.border),
            line_selected: theme.list_active_border,
            hairline: theme.table_row_border,
            hairline_strong: or(own.hairline_strong, theme.border),
            tree: or(own.tree, theme.border),
            fill_selected: theme.list_active,
            fill_hover: theme.list_hover,

            text_1: theme.foreground,
            text_2: or(own.text_2, theme.sidebar_foreground),
            text_3: theme.muted_foreground,

            accent_fill: theme.primary,
            accent_fill_hover: theme.primary_hover,
            accent_fill_pressed: theme.primary_active,
            on_accent_fill: theme.primary_foreground,
            accent_fill_edge: or(own.accent_fill_edge, clear),
            accent: theme.caret,
            link: theme.link,
            link_border: or(own.link_border, theme.border),
            focus_ring: theme.ring,
            focus_halo: or(own.focus_halo, clear),
            focus_ring_button: or(own.focus_ring_button, theme.ring),

            ready: theme.success,
            needs_you: theme.warning,
            problem: theme.danger,
            idle: or(own.idle, theme.muted_foreground),
            working_track: or(own.working_track, theme.border),
            needs_you_line_row: or(own.needs_you_line_row, theme.warning),
            needs_you_line: or(own.needs_you_line, theme.warning),
            needs_you_wash_row: or(own.needs_you_wash_row, clear),
            needs_you_line_card: or(own.needs_you_line_card, theme.warning),
            needs_you_wash_banner: or(own.needs_you_wash_banner, clear),
            needs_you_line_button: or(own.needs_you_line_button, theme.warning),
            needs_you_text_button: or(own.needs_you_text_button, theme.warning),
            problem_line: or(own.problem_line, theme.danger),
            diff_added_background: or(own.diff_added_background, clear),
            diff_removed_background: or(own.diff_removed_background, clear),
        }
    }
}
