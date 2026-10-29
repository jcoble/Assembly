//! `lines_of` turns a `git diff` text into the Changes tab's lines: each with its old and new line numbers and whether
//! it was added, removed or kept (scm#15). Git's header lines before the first run are left out, except the ones that
//! say what happened to the file (a rename, a new or deleted file, a binary change). A conflict's combined diff (`@@@`) has no numbers
//! and is shown as git prints it (scm#16). Replaces the old app's parseUnifiedDiff.ts.

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Kind {
    /// Git's `@@` line starting a run of changes.
    Run,
    Same,
    Added,
    Removed,
    /// A line from git about the file or the line above, such as "rename from" or "\ No newline at end of file".
    Note,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DiffLine {
    pub kind: Kind,
    pub old: Option<u32>,
    pub new: Option<u32>,
    /// Without its `+`, `-` or space.
    pub text: String,
}

/// The header lines worth keeping: what happened to the file.
const NOTES: [&str; 9] = [
    "Binary files",
    "old mode",
    "new mode",
    "rename from",
    "rename to",
    "copy from",
    "copy to",
    "new file mode",
    "deleted file mode",
];

pub fn lines_of(diff: &str) -> Vec<DiffLine> {
    let mut lines = Vec::new();
    let (mut old, mut new) = (0, 0);
    // Before the first run, and for a combined diff, lines aren't numbered.
    let mut numbered = false;
    let mut combined = false;
    let note = |text: &str| DiffLine {
        kind: Kind::Note,
        old: None,
        new: None,
        text: text.into(),
    };
    for text in diff.lines() {
        if text.starts_with("@@") {
            combined = text.starts_with("@@@");
            numbered = !combined;
            if let Some((from, to)) = starts(text) {
                (old, new) = (from, to);
            }
            lines.push(DiffLine {
                kind: Kind::Run,
                old: None,
                new: None,
                text: text.into(),
            });
        } else if combined {
            lines.push(DiffLine {
                kind: Kind::Same,
                old: None,
                new: None,
                text: text.into(),
            });
        } else if !numbered {
            if NOTES.iter().any(|start| text.starts_with(start)) {
                lines.push(note(text));
            }
        } else if text.starts_with('\\') {
            lines.push(note(text));
        } else if let Some(rest) = text.strip_prefix('+') {
            new += 1;
            lines.push(line(Kind::Added, None, Some(new), rest));
        } else if let Some(rest) = text.strip_prefix('-') {
            old += 1;
            lines.push(line(Kind::Removed, Some(old), None, rest));
        } else if text.starts_with("diff ") {
            // The next file's header, as in a rename shown with -M beside other files.
            numbered = false;
        } else {
            (old, new) = (old + 1, new + 1);
            lines.push(line(
                Kind::Same,
                Some(old),
                Some(new),
                text.strip_prefix(' ').unwrap_or(text),
            ));
        }
    }
    lines
}

fn line(kind: Kind, old: Option<u32>, new: Option<u32>, text: &str) -> DiffLine {
    DiffLine {
        kind,
        old,
        new,
        text: text.into(),
    }
}

/// The line before each side's first line in `@@ -3,4 +3,5 @@`, so counting on from it gives each line's number.
fn starts(header: &str) -> Option<(u32, u32)> {
    let mut parts = header.split(' ').skip(1);
    let side = |part: Option<&str>, sign: char| -> Option<u32> {
        let start: u32 = part?.strip_prefix(sign)?.split(',').next()?.parse().ok()?;
        Some(start.saturating_sub(1))
    };
    Some((side(parts.next(), '-')?, side(parts.next(), '+')?))
}

#[cfg(test)]
#[path = "diff_lines_tests.rs"]
mod tests;
