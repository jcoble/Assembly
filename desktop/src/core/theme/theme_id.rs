//! The theme ids a person can pick, one per file in desktop/themes/, and the fallback for an id that isn't one.
//! Replaces themeRegistry.ts's THEMES, DEFAULT_THEME_ID, isThemeId, resolveThemeId and getTheme.

/// One theme. The setting saves `as_str`; the kit knows the theme by `theme_name`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ThemeId {
    Assembly,
    AssemblyGlass,
    Houston,
    Dracula,
    TokyoNight,
    Graphite,
    Amy,
    AyuLight,
    Cobalt,
    RosePineDawn,
    Tomorrow,
}

impl ThemeId {
    /// The theme the app opens with, and the one an unknown id gives.
    pub const DEFAULT: ThemeId = ThemeId::Assembly;

    /// Every theme, in the order a chooser lists them.
    pub const ALL: [ThemeId; 11] = [
        ThemeId::Assembly,
        ThemeId::AssemblyGlass,
        ThemeId::Houston,
        ThemeId::Dracula,
        ThemeId::TokyoNight,
        ThemeId::Graphite,
        ThemeId::Amy,
        ThemeId::AyuLight,
        ThemeId::Cobalt,
        ThemeId::RosePineDawn,
        ThemeId::Tomorrow,
    ];

    /// The theme a saved id means: an unknown id (such as the old "dark") gives `DEFAULT`, and glass gives Assembly
    /// off macOS, where there's no blur.
    pub fn resolve(raw: &str) -> ThemeId {
        todo!()
    }

    /// The id the setting saves, which is also the file's stem in desktop/themes/.
    pub fn as_str(self) -> &'static str {
        todo!()
    }

    /// The `name` inside the theme's file, which is how the kit's registry finds it.
    pub fn theme_name(self) -> &'static str {
        todo!()
    }

    /// True for the theme whose window background is blurred.
    pub fn is_glass(self) -> bool {
        todo!()
    }
}
