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

#[cfg_attr(
    not(test),
    expect(
        dead_code,
        reason = "picked by the palette commands (on g0-host's registry) and preferences (the cut); until then only the default opens"
    )
)]
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

    /// The theme a saved id means: an unknown id (such as the old "dark") gives `DEFAULT`.
    pub fn resolve(raw: &str) -> ThemeId {
        ThemeId::ALL
            .into_iter()
            .find(|id| id.as_str() == raw)
            .unwrap_or(ThemeId::DEFAULT)
    }

    /// The id the setting saves, which is also the file's stem in desktop/themes/.
    pub fn as_str(self) -> &'static str {
        match self {
            ThemeId::Assembly => "assembly",
            ThemeId::AssemblyGlass => "assembly-glass",
            ThemeId::Houston => "houston",
            ThemeId::Dracula => "dracula",
            ThemeId::TokyoNight => "tokyo-night",
            ThemeId::Graphite => "graphite",
            ThemeId::Amy => "amy",
            ThemeId::AyuLight => "ayu-light",
            ThemeId::Cobalt => "cobalt",
            ThemeId::RosePineDawn => "rose-pine-dawn",
            ThemeId::Tomorrow => "tomorrow",
        }
    }

    /// The `name` inside the theme's file, which is how the kit's registry finds it.
    pub fn theme_name(self) -> &'static str {
        match self {
            ThemeId::Assembly => "Assembly",
            ThemeId::AssemblyGlass => "Assembly Glass",
            ThemeId::Houston => "Houston",
            ThemeId::Dracula => "Dracula",
            ThemeId::TokyoNight => "Tokyo Night",
            ThemeId::Graphite => "Graphite",
            ThemeId::Amy => "Amy",
            ThemeId::AyuLight => "Ayu Light",
            ThemeId::Cobalt => "Cobalt",
            ThemeId::RosePineDawn => "Rose Pine Dawn",
            ThemeId::Tomorrow => "Tomorrow",
        }
    }

    /// True for the theme whose window background is blurred.
    pub fn is_glass(self) -> bool {
        self == ThemeId::AssemblyGlass
    }
}

#[cfg(test)]
mod tests {
    use super::ThemeId;
    use gpui_kit::component::{ThemeMode, ThemeSet};
    use std::{collections::BTreeSet, fs, path::Path};

    #[test]
    fn an_id_that_isnt_a_theme_gives_assembly() {
        // "dark" is what the old app's settings saved before themes existed.
        for raw in ["dark", "light", "", "Assembly"] {
            assert_eq!(ThemeId::resolve(raw), ThemeId::Assembly, "{raw:?}");
        }
    }

    #[test]
    fn every_saved_id_reads_back_as_its_theme() {
        for id in ThemeId::ALL {
            assert_eq!(ThemeId::resolve(id.as_str()), id, "{}", id.as_str());
        }
    }

    #[test]
    fn the_themes_folder_has_one_file_per_id_and_nothing_else() {
        let dir = Path::new(super::super::apply::THEMES_DIR);
        let files: BTreeSet<String> = fs::read_dir(dir)
            .expect("read desktop/themes")
            .map(|entry| {
                entry
                    .expect("a folder entry")
                    .file_name()
                    .into_string()
                    .expect("a UTF-8 name")
            })
            .filter(|name| name.ends_with(".json"))
            .collect();
        let expected: BTreeSet<String> = ThemeId::ALL
            .iter()
            .map(|id| format!("{}.json", id.as_str()))
            .collect();
        assert_eq!(files, expected);

        let mut names = BTreeSet::new();
        for id in ThemeId::ALL {
            let path = dir.join(format!("{}.json", id.as_str()));
            let set: ThemeSet =
                serde_json::from_str(&fs::read_to_string(&path).expect("read the file"))
                    .unwrap_or_else(|err| {
                        panic!("{} isn't a kit theme file: {err}", path.display())
                    });
            assert_eq!(set.themes.len(), 1, "{}", path.display());
            let theme = &set.themes[0];
            assert_eq!(theme.name.as_ref(), id.theme_name(), "{}", path.display());
            // A file marked default is skipped when the kit reloads the folder (registry.rs:274-287).
            assert!(!theme.is_default, "{} says is_default", path.display());
            // Every theme is dark, so picking one never switches mode.
            assert_eq!(theme.mode, ThemeMode::Dark, "{}", path.display());
            assert!(
                names.insert(id.theme_name()),
                "two ids share the name {}",
                id.theme_name()
            );
        }
    }
}
