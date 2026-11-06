//! A copy of the real theme files, for tests that reload them. It stays apart from the tests that draw.

use std::fs;
use std::path::PathBuf;

/// Copies every theme in `from` to a fresh folder named for this test, each still saying `font.size` 16.
pub fn copied_themes(from: &str, name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "assembly-root-themes-{}-{name}",
        std::process::id()
    ));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).expect("make the test's theme folder");
    for entry in fs::read_dir(from).expect("read desktop/themes") {
        let path = entry.expect("a folder entry").path();
        if path.extension().is_some_and(|ext| ext == "json") {
            let text = fs::read_to_string(&path).expect("read a theme file");
            assert!(text.contains("\"font.size\": 16.0"), "{path:?}");
            fs::write(dir.join(path.file_name().expect("a file name")), text)
                .expect("copy a theme file");
        }
    }
    dir
}
