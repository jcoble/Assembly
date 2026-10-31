//! `Appearance` is what Settings saves for how the window looks and how code reads: the theme, the interface and code
//! faces, the interface size, and the editor's and terminal's own text. Each value is one key among the backend's
//! preferences; a missing, reset (null) or unreadable value reads as its default. Replaces settingsStore.svelte.ts's
//! appearance, editor and terminal sections.

use std::collections::HashMap;

use gpui_kit::{Pixels, SharedString, px};
use serde_json::Value;

pub use super::number_setting::NumberSetting;
use crate::core::theme::theme_id::ThemeId;

pub const THEME: &str = "appearance.theme";
pub const UI_FONT: &str = "appearance.uiFont";
pub const CODE_FONT: &str = "appearance.codeFont";
pub const EDITOR_FONT: &str = "editor.fontFamily";
pub const TERMINAL_FONT: &str = "terminal.fontFamily";

/// The ranges are the old app's sliders (SettingsDialog.svelte:932-1017).
pub const UI_FONT_SIZE: NumberSetting = NumberSetting {
    key: "appearance.uiFontSize",
    min: 11.,
    max: 18.,
    step: 1.,
    default: 13.,
    unit: "px",
};
pub const EDITOR_FONT_SIZE: NumberSetting = NumberSetting {
    key: "editor.fontSize",
    min: 10.,
    max: 24.,
    step: 1.,
    default: 13.,
    unit: "px",
};
/// In pixels. The default is the file view's and the diff's row height before this setting.
pub const EDITOR_LINE_HEIGHT: NumberSetting = NumberSetting {
    key: "editor.lineHeight",
    min: 14.,
    max: 36.,
    step: 1.,
    default: 20.,
    unit: "px",
};
pub const TERMINAL_FONT_SIZE: NumberSetting = NumberSetting {
    key: "terminal.fontSize",
    min: 10.,
    max: 24.,
    step: 1.,
    default: 15.,
    unit: "px",
};
/// A multiplier of the font size, as the terminal takes it.
pub const TERMINAL_LINE_HEIGHT: NumberSetting = NumberSetting {
    key: "terminal.lineHeight",
    min: 1.,
    max: 2.,
    step: 0.1,
    default: 1.2,
    unit: "×",
};

/// GPUI's name for the face the system draws its own interface in.
pub const SYSTEM_FONT: &str = ".SystemUIFont";
/// The kit's code face on the Mac.
pub const DEFAULT_CODE_FONT: &str = "Menlo";

#[derive(Clone, Debug, Default)]
pub struct Appearance {
    saved: HashMap<String, Value>,
}

impl Appearance {
    /// Takes one saved preference, from the backend's topic or a save just made.
    pub fn set(&mut self, key: String, value: Value) {
        self.saved.insert(key, value);
    }

    pub fn number(&self, setting: &NumberSetting) -> f32 {
        setting.read(self.saved.get(setting.key))
    }

    pub fn theme(&self) -> ThemeId {
        self.saved
            .get(THEME)
            .and_then(Value::as_str)
            .map_or(ThemeId::DEFAULT, ThemeId::resolve)
    }

    /// The face saved under `key`, when one is.
    pub fn font(&self, key: &str) -> Option<SharedString> {
        let name = self.saved.get(key)?.as_str()?.trim();
        (!name.is_empty()).then(|| SharedString::from(name.to_string()))
    }

    pub fn ui_font(&self) -> SharedString {
        self.font(UI_FONT).unwrap_or(SYSTEM_FONT.into())
    }

    /// The shared code face: paths, commands and code outside the editor, and the editor's and terminal's until
    /// they're given their own.
    pub fn code_font(&self) -> SharedString {
        self.font(CODE_FONT).unwrap_or(DEFAULT_CODE_FONT.into())
    }

    pub fn editor_font(&self) -> SharedString {
        self.font(EDITOR_FONT).unwrap_or_else(|| self.code_font())
    }

    pub fn terminal_font(&self) -> SharedString {
        self.font(TERMINAL_FONT).unwrap_or_else(|| self.code_font())
    }

    /// The terminal's face, text size and line height.
    pub fn terminal_text(&self) -> (SharedString, Pixels, Pixels) {
        let size = self.number(&TERMINAL_FONT_SIZE);
        let line_height = size * self.number(&TERMINAL_LINE_HEIGHT);
        (self.terminal_font(), px(size), px(line_height))
    }
}

#[cfg(test)]
#[path = "appearance_tests.rs"]
mod tests;
