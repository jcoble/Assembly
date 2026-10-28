//! `Palette` owns the terminal's colors, read from the window's theme, never hard-coded, so a light theme gets a light
//! terminal: the 16 ANSI colors from the theme's base colors, the text, background and cursor from its own. The 256
//! color cube and true color are what the program asks for, and a color the program set (OSC 4) wins. Replaces
//! terminalPalettes.ts.

use alacritty_terminal::term::color::Colors;
use alacritty_terminal::vte::ansi::{Color, NamedColor, Rgb};
use gpui_kit::component::Theme;
use gpui_kit::*;

#[derive(Clone)]
pub struct Palette {
    ansi: [Hsla; 16],
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
        Self {
            ansi: [
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
            ],
            foreground: c.foreground,
            background: c.background,
            cursor: c.caret,
        }
    }

    /// A cell's color: the program's own (OSC 4 and friends) if it set one, else the theme's.
    pub fn color(&self, color: Color, set: &Colors) -> Hsla {
        match color {
            Color::Spec(rgb) => hsla(rgb),
            Color::Indexed(index) => match set[index as usize] {
                Some(rgb) => hsla(rgb),
                None => self.indexed(index),
            },
            Color::Named(name) => match set[name as usize] {
                Some(rgb) => hsla(rgb),
                None => self.named(name),
            },
        }
    }

    fn named(&self, name: NamedColor) -> Hsla {
        match name {
            NamedColor::Foreground | NamedColor::BrightForeground => self.foreground,
            NamedColor::DimForeground => self.foreground.opacity(0.7),
            NamedColor::Background => self.background,
            NamedColor::Cursor => self.cursor,
            NamedColor::DimBlack => self.ansi[0].opacity(0.7),
            NamedColor::DimRed => self.ansi[1].opacity(0.7),
            NamedColor::DimGreen => self.ansi[2].opacity(0.7),
            NamedColor::DimYellow => self.ansi[3].opacity(0.7),
            NamedColor::DimBlue => self.ansi[4].opacity(0.7),
            NamedColor::DimMagenta => self.ansi[5].opacity(0.7),
            NamedColor::DimCyan => self.ansi[6].opacity(0.7),
            NamedColor::DimWhite => self.ansi[7].opacity(0.7),
            other => self.ansi[other as usize],
        }
    }

    /// The 16 ANSI colors, then xterm's 6x6x6 cube and its 24 grays.
    fn indexed(&self, index: u8) -> Hsla {
        match index {
            0..16 => self.ansi[index as usize],
            16..232 => {
                let level = |v: u8| if v == 0 { 0 } else { 55 + v * 40 };
                let i = index - 16;
                hsla(Rgb {
                    r: level(i / 36),
                    g: level(i / 6 % 6),
                    b: level(i % 6),
                })
            }
            _ => {
                let gray = 8 + (index - 232) * 10;
                hsla(Rgb {
                    r: gray,
                    g: gray,
                    b: gray,
                })
            }
        }
    }
}

fn hsla(rgb: Rgb) -> Hsla {
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
