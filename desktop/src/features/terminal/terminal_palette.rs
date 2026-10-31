//! The terminal's own colours for each theme that had them in the old app (terminalPalettes.ts): its text, cursor,
//! selection and 16 ANSI colours. The old app's other themes kept the Assembly shell and its terminal colours; the new
//! light themes have none and take theirs from the theme (terminal_draw.rs).

use crate::core::theme::theme_id::ThemeId;

pub struct Palette {
    pub foreground: u32,
    pub cursor: u32,
    /// The selection's colour and how opaque it is, over the cells.
    pub selection: (u32, f32),
    pub selection_text: u32,
    /// Black, red, green, yellow, blue, magenta, cyan and white, then their bright ones.
    pub ansi: [u32; 16],
}

pub fn palette(theme: ThemeId) -> Option<&'static Palette> {
    match theme {
        ThemeId::Assembly | ThemeId::AssemblyGlass | ThemeId::Amy | ThemeId::Cobalt => {
            Some(&ASSEMBLY)
        }
        ThemeId::Houston => Some(&HOUSTON),
        ThemeId::Dracula => Some(&DRACULA),
        ThemeId::TokyoNight => Some(&TOKYO_NIGHT),
        ThemeId::Graphite => Some(&GRAPHITE),
        ThemeId::AssemblyLight | ThemeId::RosePineDawn | ThemeId::AyuLight => None,
    }
}

const ASSEMBLY: Palette = Palette {
    foreground: 0xf5f5f5,
    cursor: 0x1ed760,
    selection: (0xff3898, 0.6),
    selection_text: 0xf5f5f5,
    ansi: [
        0x282828, 0xf15e6c, 0x1ed760, 0xf3c969, 0x6fb6ff, 0xa371f7, 0x56d4dd, 0xa7a7a7, //
        0x6e6e6e, 0xf15e6c, 0x1ed760, 0xf3c969, 0x6fb6ff, 0xa371f7, 0x56d4dd, 0xffffff,
    ],
};

const HOUSTON: Palette = Palette {
    foreground: 0xeef0f9,
    cursor: 0x4bf3c8,
    selection: (0x54b9ff, 0.5),
    selection_text: 0xeef0f9,
    ansi: [
        0x2a2d34, 0xff6d91, 0x4bf3c8, 0xffd493, 0x54b9ff, 0xb7afff, 0x00daef, 0xa0a6b4, //
        0x6c707b, 0xff6d91, 0x4bf3c8, 0xffd493, 0x54b9ff, 0xb7afff, 0x00daef, 0xffffff,
    ],
};

/// Dracula's own palette.
const DRACULA: Palette = Palette {
    foreground: 0xf8f8f2,
    cursor: 0xf8f8f2,
    selection: (0xff79c6, 0.6),
    selection_text: 0xffffff,
    ansi: [
        0x000000, 0xff5555, 0x50fa7b, 0xf1fa8c, 0xbd93f9, 0xff79c6, 0x8be9fd, 0xbbbbbb, //
        0x6272a4, 0xff5555, 0x50fa7b, 0xf1fa8c, 0xcaa9fa, 0xff79c6, 0x8be9fd, 0xffffff,
    ],
};

const TOKYO_NIGHT: Palette = Palette {
    foreground: 0xc0caf5,
    cursor: 0x7aa2f7,
    selection: (0xbb9af7, 0.55),
    selection_text: 0xffffff,
    ansi: [
        0x15161e, 0xf7768e, 0x9ece6a, 0xe0af68, 0x7aa2f7, 0xbb9af7, 0x7dcfff, 0xa9b1d6, //
        0x737aa2, 0xf7768e, 0x9ece6a, 0xe0af68, 0x7aa2f7, 0xbb9af7, 0x7dcfff, 0xc0caf5,
    ],
};

const GRAPHITE: Palette = Palette {
    foreground: 0xededed,
    cursor: 0xededed,
    selection: (0xe8c268, 0.45),
    selection_text: 0xededed,
    ansi: [
        0x222225, 0xf0616d, 0x5fd38d, 0xe8c268, 0x7ab8f5, 0xa371f7, 0x6cc4d0, 0x8f9096, //
        0x6a6b70, 0xf0616d, 0x5fd38d, 0xe8c268, 0x7ab8f5, 0xa371f7, 0x6cc4d0, 0xffffff,
    ],
};

#[cfg(test)]
#[path = "terminal_palette_tests.rs"]
mod tests;
