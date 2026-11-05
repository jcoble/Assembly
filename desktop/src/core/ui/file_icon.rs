//! Which glyph and colour a file name gets in Git and pull request lists. Whole names win over extensions, so
//! `pnpm-lock.yaml` reads as a lock file. Replaces fileIcons.ts and FileIcon.svelte; tones come from the shared theme.
use gpui_kit::assets::IconName;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon};
use gpui_kit::*;

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Tone {
    Red,
    Blue,
    Yellow,
    Orange,
    Magenta,
    Cyan,
    Green,
    Muted,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Glyph {
    Code,
    Flame,
    Gear,
    Sharp,
    Palette,
    Markup,
    Braces,
    Document,
    Database,
    Terminal,
    Sliders,
    Image,
    Lock,
    Bird,
    Gem,
    Coffee,
    Container,
    Table,
    Binary,
    Page,
}
impl Glyph {
    pub const ALL: [Glyph; 20] = [
        Glyph::Code,
        Glyph::Flame,
        Glyph::Gear,
        Glyph::Sharp,
        Glyph::Palette,
        Glyph::Markup,
        Glyph::Braces,
        Glyph::Document,
        Glyph::Database,
        Glyph::Terminal,
        Glyph::Sliders,
        Glyph::Image,
        Glyph::Lock,
        Glyph::Bird,
        Glyph::Gem,
        Glyph::Coffee,
        Glyph::Container,
        Glyph::Table,
        Glyph::Binary,
        Glyph::Page,
    ];
    pub fn icon(self) -> IconName {
        match self {
            Glyph::Code => IconName::Code,
            Glyph::Flame => IconName::Flame,
            Glyph::Gear => IconName::Cog,
            Glyph::Sharp => IconName::Hash,
            Glyph::Palette => IconName::Palette,
            Glyph::Markup => IconName::CodeXml,
            Glyph::Braces => IconName::Braces,
            Glyph::Document => IconName::FileText,
            Glyph::Database => IconName::Database,
            Glyph::Terminal => IconName::SquareTerminal,
            Glyph::Sliders => IconName::SlidersHorizontal,
            Glyph::Image => IconName::Image,
            Glyph::Lock => IconName::Lock,
            Glyph::Bird => IconName::Bird,
            Glyph::Gem => IconName::Gem,
            Glyph::Coffee => IconName::Coffee,
            Glyph::Container => IconName::Container,
            Glyph::Table => IconName::Table,
            Glyph::Binary => IconName::Binary,
            Glyph::Page => IconName::File,
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FileLook {
    pub glyph: Glyph,
    pub tone: Tone,
    /// What the file is, in words anyone can read.
    pub label: &'static str,
}
const fn look(glyph: Glyph, tone: Tone, label: &'static str) -> FileLook {
    FileLook { glyph, tone, label }
}
const LOCK: FileLook = look(
    Glyph::Lock,
    Tone::Muted,
    "Lock file — the exact versions installed",
);
const DEFAULT: FileLook = look(Glyph::Page, Tone::Muted, "File");

fn by_name(name: &str) -> Option<FileLook> {
    Some(match name {
        "package.json" => look(Glyph::Braces, Tone::Green, "Project manifest"),
        "package-lock.json" | "pnpm-lock.yaml" | "yarn.lock" | "cargo.lock" => LOCK,
        "cargo.toml" => look(Glyph::Gear, Tone::Orange, "Rust project settings"),
        "dockerfile" => look(Glyph::Container, Tone::Muted, "Docker build recipe"),
        "makefile" => look(Glyph::Terminal, Tone::Green, "Make build rules"),
        "procfile" => look(Glyph::Terminal, Tone::Green, "Process list"),
        ".gitignore" => look(Glyph::Sliders, Tone::Muted, "Files git should ignore"),
        ".gitattributes" => look(Glyph::Sliders, Tone::Muted, "Git file settings"),
        ".env" => look(Glyph::Sliders, Tone::Muted, "Environment settings"),
        ".editorconfig" => look(Glyph::Sliders, Tone::Muted, "Editor settings"),
        ".npmrc" => look(Glyph::Sliders, Tone::Muted, "Package manager settings"),
        ".prettierrc" => look(Glyph::Sliders, Tone::Muted, "Formatting settings"),
        "license" => look(Glyph::Document, Tone::Muted, "Licence"),
        "readme.md" => look(Glyph::Document, Tone::Muted, "Read me first"),
        _ => return None,
    })
}
fn by_extension(extension: &str) -> Option<FileLook> {
    Some(match extension {
        "svelte" => look(Glyph::Flame, Tone::Red, "Svelte component"),
        "ts" | "mts" | "cts" => look(Glyph::Code, Tone::Blue, "TypeScript"),
        "tsx" => look(Glyph::Code, Tone::Blue, "TypeScript with markup"),
        "js" | "mjs" | "cjs" => look(Glyph::Code, Tone::Yellow, "JavaScript"),
        "jsx" => look(Glyph::Code, Tone::Yellow, "JavaScript with markup"),
        "rs" => look(Glyph::Gear, Tone::Orange, "Rust"),
        "cs" => look(Glyph::Sharp, Tone::Magenta, "C#"),
        "csproj" => look(Glyph::Sliders, Tone::Magenta, "C# project settings"),
        "sln" => look(Glyph::Sliders, Tone::Magenta, "C# solution"),
        "fs" => look(Glyph::Sharp, Tone::Magenta, "F#"),
        "razor" | "cshtml" => look(Glyph::Markup, Tone::Magenta, "Razor page"),
        "css" | "scss" | "sass" | "less" => look(Glyph::Palette, Tone::Cyan, "Stylesheet"),
        "html" | "htm" => look(Glyph::Markup, Tone::Orange, "Web page"),
        "xml" => look(Glyph::Markup, Tone::Orange, "XML"),
        "xaml" => look(Glyph::Markup, Tone::Orange, "XAML layout"),
        "vue" => look(Glyph::Markup, Tone::Orange, "Vue component"),
        "json" | "jsonc" | "json5" => look(Glyph::Braces, Tone::Green, "JSON data"),
        "yaml" | "yml" => look(Glyph::Sliders, Tone::Muted, "YAML settings"),
        "toml" => look(Glyph::Sliders, Tone::Muted, "TOML settings"),
        "ini" | "conf" | "config" | "properties" => look(Glyph::Sliders, Tone::Muted, "Settings"),
        "lock" => LOCK,
        "md" | "mdx" => look(Glyph::Document, Tone::Muted, "Markdown notes"),
        "txt" | "rst" => look(Glyph::Document, Tone::Muted, "Plain text"),
        "pdf" => look(Glyph::Document, Tone::Muted, "PDF"),
        "sql" => look(Glyph::Database, Tone::Cyan, "SQL"),
        "db" | "sqlite" => look(Glyph::Database, Tone::Cyan, "Database file"),
        "csv" | "tsv" => look(Glyph::Table, Tone::Green, "Spreadsheet data"),
        "sh" | "bash" | "zsh" | "fish" => look(Glyph::Terminal, Tone::Green, "Shell script"),
        "ps1" => look(Glyph::Terminal, Tone::Green, "PowerShell script"),
        "bat" | "cmd" => look(Glyph::Terminal, Tone::Green, "Batch script"),
        "py" => look(Glyph::Code, Tone::Blue, "Python"),
        "go" => look(Glyph::Code, Tone::Cyan, "Go"),
        "java" => look(Glyph::Coffee, Tone::Orange, "Java"),
        "kt" | "kts" => look(Glyph::Coffee, Tone::Orange, "Kotlin"),
        "rb" => look(Glyph::Gem, Tone::Red, "Ruby"),
        "swift" => look(Glyph::Bird, Tone::Orange, "Swift"),
        "php" => look(Glyph::Code, Tone::Magenta, "PHP"),
        "c" => look(Glyph::Code, Tone::Muted, "C"),
        "h" => look(Glyph::Code, Tone::Muted, "C header"),
        "cpp" => look(Glyph::Code, Tone::Muted, "C++"),
        "hpp" => look(Glyph::Code, Tone::Muted, "C++ header"),
        "m" => look(Glyph::Code, Tone::Muted, "Objective-C"),
        "mm" => look(Glyph::Code, Tone::Muted, "Objective-C++"),
        "lua" => look(Glyph::Code, Tone::Muted, "Lua"),
        "dart" => look(Glyph::Code, Tone::Muted, "Dart"),
        "graphql" | "gql" => look(Glyph::Braces, Tone::Green, "GraphQL"),
        "proto" => look(Glyph::Braces, Tone::Green, "Protocol buffer"),
        "png" | "jpg" | "jpeg" | "gif" | "webp" | "avif" => {
            look(Glyph::Image, Tone::Magenta, "Image")
        }
        "svg" => look(Glyph::Image, Tone::Magenta, "Vector image"),
        "ico" | "icns" => look(Glyph::Image, Tone::Magenta, "Icon"),
        "wasm" | "bin" => look(Glyph::Binary, Tone::Muted, "Compiled program"),
        "zip" | "gz" | "tar" => look(Glyph::Binary, Tone::Muted, "Archive"),
        _ => return None,
    })
}
/// The look for a file name or a path to one. A leading dot names the file, it doesn't start an extension.
pub fn file_look(path: &str) -> FileLook {
    let name = path
        .rsplit('/')
        .next()
        .unwrap_or(path)
        .trim()
        .to_lowercase();
    if let Some(found) = by_name(&name) {
        return found;
    }
    match name.rfind('.') {
        Some(dot) if dot > 0 => by_extension(&name[dot + 1..]).unwrap_or(DEFAULT),
        _ => DEFAULT,
    }
}
fn tone_color(tone: Tone, cx: &App) -> Hsla {
    let theme = cx.theme();
    match tone {
        Tone::Red => theme.red,
        Tone::Blue => theme.blue,
        Tone::Yellow => theme.yellow,
        Tone::Orange => theme.warning,
        Tone::Magenta => theme.magenta,
        Tone::Cyan => theme.cyan,
        Tone::Green => theme.green,
        Tone::Muted => theme.muted_foreground,
    }
}
/// The file's glyph in its colour, for a row whose tooltip says something else.
pub fn file_glyph(path: &str, cx: &App) -> Icon {
    let look = file_look(path);
    Icon::new(look.glyph.icon())
        .size(px(14.))
        .text_color(tone_color(look.tone, cx))
}
/// The file's glyph with what it is as its tooltip. `id` only has to be unique among its siblings.
pub fn file_icon(id: impl Into<SharedString>, path: &str, cx: &App) -> Stateful<Div> {
    let label = file_look(path).label;
    div()
        .id(ElementId::Name(id.into()))
        .flex_none()
        .child(file_glyph(path, cx))
        .tooltip(move |window, cx| Tooltip::new(label).build(window, cx))
}

#[cfg(test)]
#[path = "file_icon_tests.rs"]
mod tests;
