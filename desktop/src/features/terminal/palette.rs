//! `Palette` owns the terminal's colors, read from the window's theme, never hard-coded, so a light theme gets a light
//! terminal: the 16 ANSI colors from the theme's base colors, the text, background and cursor from its own, then
//! xterm's 256-color cube and grays. Ghostty takes them as its defaults, so a color the program sets (OSC 4) still
//! wins, and true color is what the program asks for. Replaces terminalPalettes.ts.

use gpui_kit::component::Theme;
use gpui_kit::*;
use libghostty_vt::style::RgbColor;

#[derive(Clone, PartialEq)]
pub struct Palette {
    /// The 256 indexed colors: the 16 ANSI ones, the 6x6x6 cube and 24 grays.
    pub colors: [RgbColor; 256],
    pub foreground: Hsla,
    pub background: Hsla,
    pub cursor: Hsla,
}

impl Palette {
    pub fn from_theme(theme: &Theme) -> Self {
        let c = &theme.colors;
        // Black is the darker of the text and background and white the lighter, in a light theme too, so a program's
        // black text stays dark.
        let (dark, light) = if c.background.l < c.foreground.l {
            (c.background, c.foreground)
        } else {
            (c.foreground, c.background)
        };
        // A dark theme's bright colors are its light ones; a light theme's would be too pale to read as text.
        let bright = |normal: Hsla, lighter: Hsla| if theme.is_dark() { lighter } else { normal };
        let ansi = [
            dark,
            c.red,
            c.green,
            c.yellow,
            c.blue,
            c.magenta,
            c.cyan,
            light.opacity(0.85),
            c.muted_foreground,
            bright(c.red, c.red_light),
            bright(c.green, c.green_light),
            bright(c.yellow, c.yellow_light),
            bright(c.blue, c.blue_light),
            bright(c.magenta, c.magenta_light),
            bright(c.cyan, c.cyan_light),
            light,
        ];
        let level = |v: usize| if v == 0 { 0 } else { 55 + v as u8 * 40 };
        let colors = std::array::from_fn(|index| match index {
            0..16 => rgb(ansi[index], c.background),
            16..232 => {
                let i = index - 16;
                RgbColor {
                    r: level(i / 36),
                    g: level(i / 6 % 6),
                    b: level(i % 6),
                }
            }
            _ => {
                let gray = 8 + (index as u8 - 232) * 10;
                RgbColor {
                    r: gray,
                    g: gray,
                    b: gray,
                }
            }
        });
        Self {
            colors,
            foreground: c.foreground,
            background: c.background,
            cursor: c.caret,
        }
    }
}

/// A theme color as Ghostty takes it, laid over `under` where it's see-through.
pub fn rgb(color: Hsla, under: Hsla) -> RgbColor {
    let (top, under) = (color.to_rgb(), under.to_rgb());
    let mix = |a: f32, b: f32| ((a * top.a + b * (1. - top.a)) * 255.).round() as u8;
    RgbColor {
        r: mix(top.r, under.r),
        g: mix(top.g, under.g),
        b: mix(top.b, under.b),
    }
}

pub fn hsla(rgb: RgbColor) -> Hsla {
    Rgba {
        r: rgb.r as f32 / 255.,
        g: rgb.g as f32 / 255.,
        b: rgb.b as f32 / 255.,
        a: 1.,
    }
    .into()
}

#[cfg(test)]
#[path = "palette_tests.rs"]
mod tests;
