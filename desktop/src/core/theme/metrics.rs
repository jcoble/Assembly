//! The look's space, sizes and corners, from the rule sheet's "Space, size and corners" (Assembly look: the rule
//! sheet, https://app.notion.com/p/41f394b0689d81f6bd5dd1a4bae1b24e). Shape is the app's, not a theme's, so these
//! are the same in every theme and live here rather than in the theme files. The kit's own `radius` (12, fields and
//! rows) and `radius.lg` (18, dialogs) are set in assembly.json, because the kit's components read them from there.
//!
//! The sizes are exact as the mockup draws them, including its few odd values; anything new takes a `SPACE` step.
//! Text sizes are `TextRole` (text.rs); motion is the kit's and `grow.rs`.

// Later lanes read most of these as they rebuild each screen in the look, so not every one is read yet.
#![allow(dead_code)]

use gpui_kit::{BoxShadow, Pixels, hsla, point, px};

/// The spacing steps for anything the sheet doesn't size.
pub const SPACE: [Pixels; 10] = [
    px(2.),
    px(4.),
    px(6.),
    px(8.),
    px(10.),
    px(12.),
    px(14.),
    px(16.),
    px(20.),
    px(24.),
];

// Corners.
/// Buttons, chips, tabs, pickers, segmented controls, icon buttons, count badges.
pub const CORNER_PILL: Pixels = px(999.);
/// Fields, inset lists, session rows, file rows.
pub const CORNER_FIELD: Pixels = px(12.);
/// Tiles, the sheet, cards, code blocks, the question card, the error bar.
pub const CORNER_TILE: Pixels = px(14.);
/// Message bubbles.
pub const CORNER_BUBBLE: Pixels = px(18.);
/// Popovers, the panel, dialogs.
pub const CORNER_POPOVER: Pixels = px(18.);
/// The composer.
pub const CORNER_COMPOSER: Pixels = px(20.);
/// Mono tags and provider marks.
pub const CORNER_TAG: Pixels = px(6.);
/// A large provider mark.
pub const CORNER_TAG_LARGE: Pixels = px(8.);
/// Keys, inline code and file-type marks.
pub const CORNER_KEY: Pixels = px(4.);

// The window.
/// The top bar, on glass.
pub const TOP_BAR_HEIGHT: Pixels = px(52.);
/// Between a tile and the window's sides and bottom, and between tiles; the glass shows in it.
pub const TILE_GAP: Pixels = px(8.);
/// The left list tile.
pub const LEFT_LIST_WIDTH: Pixels = px(312.);
/// The right drawer tile.
pub const DRAWER_WIDTH: Pixels = px(332.);
/// The chat's text column, centred in the sheet.
pub const TEXT_COLUMN_WIDTH: Pixels = px(704.);
/// The composer.
pub const COMPOSER_WIDTH: Pixels = px(724.);
/// The chat sheet's crumb and the left list's footer.
pub const CRUMB_HEIGHT: Pixels = px(46.);
/// The drawer's header.
pub const DRAWER_HEADER_HEIGHT: Pixels = px(44.);
/// The Home panel.
pub const HOME_PANEL_WIDTH: Pixels = px(820.);
/// The ⌘K search.
pub const SEARCH_WIDTH: Pixels = px(840.);

// Controls and rows.
/// A button, and an icon button's side.
pub const BUTTON_HEIGHT: Pixels = px(32.);
/// A small button.
pub const BUTTON_SMALL_HEIGHT: Pixels = px(28.);
/// A chip or picker.
pub const CHIP_HEIGHT: Pixels = px(28.);
/// A field.
pub const FIELD_HEIGHT: Pixels = px(34.);
/// The ⌘K search field.
pub const SEARCH_FIELD_HEIGHT: Pixels = px(48.);
/// A container card's header.
pub const CONTAINER_HEADER_HEIGHT: Pixels = px(48.);
/// A row in an inset list.
pub const INSET_ROW_HEIGHT: Pixels = px(44.);
/// A key-value line, at least.
pub const KEY_VALUE_HEIGHT: Pixels = px(40.);
/// A key-value line's key column.
pub const KEY_COLUMN_WIDTH: Pixels = px(108.);
/// A connection row.
pub const CONNECTION_ROW_HEIGHT: Pixels = px(56.);
/// A file row, in source control and in search results: as dense as the old app's 28 px list rows, and still a
/// comfortable target.
pub const FILE_ROW_HEIGHT: Pixels = px(28.);
/// Between a file row's mark, name and folder, the old app's 8.
pub const FILE_ROW_GAP: Pixels = SPACE[3];
/// A row in the Files tab's tree.
pub const TREE_ROW_HEIGHT: Pixels = px(30.);
/// A count badge's height and narrowest width, as Board's count.
pub const COUNT_HEIGHT: Pixels = px(20.);
pub const COUNT_MIN_WIDTH: Pixels = px(22.);
/// A group header in the left list.
pub const GROUP_HEIGHT: Pixels = px(32.);

/// The composer's shadow, black at 30% 10 below and 28 blurred: one of the three things that cast a shadow, with
/// popovers and dialogs. Black whatever the theme, since a shadow only darkens.
pub fn composer_shadow() -> Vec<BoxShadow> {
    vec![BoxShadow {
        color: hsla(0., 0., 0., 0.3),
        offset: point(px(0.), px(10.)),
        blur_radius: px(28.),
        spread_radius: px(0.),
        inset: false,
    }]
}

/// How a disabled control is drawn: the whole control at this opacity.
pub const DISABLED_OPACITY: f32 = 0.45;

/// A status colour laid behind its own text, as a chip's or a ref pill's fill: the colour at this opacity.
pub const TINT: f32 = 0.12;
