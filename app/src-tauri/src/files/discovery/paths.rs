use std::path::Path;
use crate::files::discovery::{
    DEFAULT_SOURCE_LIST_LIMIT, DEFAULT_SOURCE_SEARCH_LIMIT, MAX_SOURCE_LIST_LIMIT, MAX_SOURCE_SEARCH_LIMIT,
};
use crate::files::types::{
    SourceRecord,
};

pub(crate) fn detect_language(path: &Path) -> String {
    let file_name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_lowercase();
    if file_name == "dockerfile" || file_name.ends_with(".dockerfile") {
        return "dockerfile".to_string();
    }
    if file_name == "makefile" {
        return "makefile".to_string();
    }

    let extension = path
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_lowercase();

    match extension.as_str() {
        "cs" => "csharp",
        "csproj" | "fsproj" | "vbproj" | "props" | "targets" | "xaml" | "xml" => "xml",
        "swift" => "swift",
        "rs" => "rust",
        "ts" => "typescript",
        "tsx" => "tsx",
        "js" | "mjs" | "cjs" => "javascript",
        "jsx" => "jsx",
        "svelte" => "svelte",
        "astro" | "html" | "htm" | "vue" => "html",
        "css" => "css",
        "scss" => "scss",
        "less" => "less",
        "json" | "jsonc" => "json",
        "md" | "markdown" => "markdown",
        "mdx" => "mdx",
        "yaml" | "yml" => "yaml",
        "toml" => "toml",
        "ini" | "env" => "ini",
        "sh" | "bash" | "zsh" => "shell",
        "ps1" | "psm1" => "powershell",
        "py" => "python",
        "rb" => "ruby",
        "php" => "php",
        "dart" => "dart",
        "lua" => "lua",
        "go" => "go",
        "java" => "java",
        "kt" | "kts" => "kotlin",
        "c" | "cc" | "cpp" | "cxx" | "h" | "hh" | "hpp" | "hxx" => "cpp",
        "tf" | "tfvars" => "hcl",
        "proto" => "protobuf",
        "sql" => "sql",
        "graphql" | "gql" => "graphql",
        "fs" | "fsx" => "fsharp",
        "razor" => "razor",
        _ => "plain",
    }
    .to_string()
}

pub(super) fn is_source_file(path: &Path) -> bool {
    !matches!(detect_language(path).as_str(), "plain")
}

pub(crate) fn source_collection_limit(limit: usize) -> usize {
    limit
        .max(DEFAULT_SOURCE_LIST_LIMIT)
        .min(MAX_SOURCE_LIST_LIMIT)
        .saturating_add(1)
}

pub(super) fn clamp_source_search_page_size(page_size: Option<usize>) -> usize {
    page_size
        .unwrap_or(DEFAULT_SOURCE_SEARCH_LIMIT)
        .clamp(1, MAX_SOURCE_SEARCH_LIMIT)
}

pub(super) fn compare_source_walk_entries(
    root: &Path,
    left: &std::fs::DirEntry,
    right: &std::fs::DirEntry,
) -> std::cmp::Ordering {
    let left_path = left.path();
    let right_path = right.path();
    let left_file_type = left.file_type().ok();
    let right_file_type = right.file_type().ok();
    let left_is_directory = left_file_type
        .as_ref()
        .map(|file_type| file_type.is_dir())
        .unwrap_or(false);
    let right_is_directory = right_file_type
        .as_ref()
        .map(|file_type| file_type.is_dir())
        .unwrap_or(false);
    let left_language = if left_file_type
        .as_ref()
        .map(|file_type| file_type.is_file())
        .unwrap_or(false)
    {
        detect_language(&left_path)
    } else {
        String::new()
    };
    let right_language = if right_file_type
        .as_ref()
        .map(|file_type| file_type.is_file())
        .unwrap_or(false)
    {
        detect_language(&right_path)
    } else {
        String::new()
    };
    let left_relative_path = normalized_relative_source_path(root, &left_path);
    let right_relative_path = normalized_relative_source_path(root, &right_path);

    source_path_priority(&left_relative_path, &left_language, left_is_directory, root)
        .cmp(&source_path_priority(
            &right_relative_path,
            &right_language,
            right_is_directory,
            root,
        ))
        .then_with(|| localized_path_compare(&left_relative_path, &right_relative_path))
}

pub(super) fn compare_source_records(left: &SourceRecord, right: &SourceRecord) -> std::cmp::Ordering {
    source_path_priority(&left.relative_path, &left.language, false, Path::new(""))
        .cmp(&source_path_priority(
            &right.relative_path,
            &right.language,
            false,
            Path::new(""),
        ))
        .then_with(|| localized_path_compare(&left.relative_path, &right.relative_path))
}

pub(super) fn normalized_relative_source_path(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .unwrap_or(path)
        .to_string_lossy()
        .replace('\\', "/")
}

fn source_path_priority(
    relative_path: &str,
    language: &str,
    is_directory: bool,
    root: &Path,
) -> i32 {
    let normalized_path = relative_path.replace('\\', "/").to_ascii_lowercase();
    let segments = normalized_path
        .split('/')
        .filter(|segment| !segment.is_empty())
        .collect::<Vec<_>>();
    let first_segment = segments.first().copied().unwrap_or_default();
    let project_name = root
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    let mut score = if is_directory { -6 } else { 0 };

    score += source_root_priority(first_segment, &project_name);
    score += source_language_priority(language);
    score += source_path_segment_adjustment(&segments);

    if first_segment.starts_with('.') {
        score += 80;
    }

    score
}

fn source_root_priority(first_segment: &str, project_name: &str) -> i32 {
    if first_segment.is_empty() {
        return 100;
    }

    let project_prefix = if project_name.is_empty() {
        String::new()
    } else {
        format!("{project_name}.")
    };
    if !project_name.is_empty()
        && (first_segment == project_name || first_segment.starts_with(&project_prefix))
    {
        return project_root_segment_adjustment(first_segment);
    }

    if matches!(
        first_segment,
        "src" | "source" | "sources" | "lib" | "app" | "apps" | "packages"
    ) {
        return 0;
    }

    if looks_like_source_project_segment(first_segment) {
        return 8 + project_root_segment_adjustment(first_segment);
    }
    if first_segment.contains("test") || first_segment.contains("spec") {
        return 26;
    }
    if matches!(first_segment, "scripts" | "tools") {
        return 34;
    }
    if matches!(
        first_segment,
        "config" | "deploy" | "infra" | "infrastructure" | ".config" | ".github"
    ) {
        return 60;
    }
    if matches!(first_segment, "docs" | "doc" | "documentation") {
        return 75;
    }

    45
}

fn looks_like_source_project_segment(segment: &str) -> bool {
    segment.contains('.')
        || segment.ends_with("-web")
        || segment.ends_with("-api")
        || segment.ends_with("-core")
        || segment.ends_with("-engine")
        || segment.ends_with("-data")
        || matches!(segment, "web" | "client" | "server")
}

fn project_root_segment_adjustment(segment: &str) -> i32 {
    if segment.ends_with(".core") || segment.ends_with("-core") {
        return -8;
    }
    if segment.ends_with(".api") || segment.ends_with("-api") {
        return -6;
    }
    if segment.ends_with(".engine") || segment.ends_with("-engine") {
        return -5;
    }
    if segment.ends_with(".data") || segment.ends_with("-data") {
        return -4;
    }
    if segment.ends_with("-web") || segment == "web" || segment == "client" {
        return -3;
    }
    if segment.contains("test") || segment.contains("spec") {
        return 18;
    }

    0
}

fn source_language_priority(language: &str) -> i32 {
    if matches!(
        language,
        "csharp"
            | "typescript"
            | "tsx"
            | "svelte"
            | "javascript"
            | "jsx"
            | "rust"
            | "swift"
            | "go"
            | "python"
            | "java"
            | "kotlin"
            | "cpp"
            | "dart"
            | "fsharp"
            | "razor"
    ) {
        return 0;
    }

    if matches!(
        language,
        "json"
            | "yaml"
            | "toml"
            | "xml"
            | "shell"
            | "powershell"
            | "sql"
            | "graphql"
            | "protobuf"
            | "hcl"
            | "dockerfile"
            | "makefile"
    ) {
        return 28;
    }

    if language == "markdown" || language == "mdx" {
        return 55;
    }

    80
}

fn source_path_segment_adjustment(segments: &[&str]) -> i32 {
    let mut score = 0;

    if [
        "services",
        "controllers",
        "routes",
        "components",
        "pages",
        "models",
        "entities",
        "features",
    ]
    .iter()
    .any(|segment| segments.contains(segment))
    {
        score -= 7;
    }

    if [
        "migrations",
        "generated",
        "snapshots",
        "fixtures",
        "samples",
        "docs",
        "documentation",
    ]
    .iter()
    .any(|segment| segments.contains(segment))
    {
        score += 22;
    }

    score
}

fn localized_path_compare(left: &str, right: &str) -> std::cmp::Ordering {
    left.to_ascii_lowercase()
        .cmp(&right.to_ascii_lowercase())
        .then_with(|| left.cmp(right))
}

pub(super) fn skip_dir_reason(name: &str) -> Option<&'static str> {
    let normalized = name.to_ascii_lowercase();
    if normalized.ends_with("_files") {
        return Some("saved web page asset directory");
    }

    match normalized.as_str() {
        ".git" | ".hg" | ".svn" => Some("version-control metadata directory"),
        ".agents" | ".claude" | ".codex" | ".dev" | ".history" | ".idea" | ".omx"
        | ".playwright" | ".playwright-cli" | ".run" | ".slots" | ".vscode" | ".zed" => {
            Some("agent/tool state directory")
        }
        "__pycache__" | ".cache" | ".build" | ".gradle" | ".next" | ".nuxt" | ".parcel-cache"
        | ".pytest_cache" | ".svelte-kit" | ".tmp" | ".turbo" | ".vite" | "bin" | "build"
        | "coverage" | "deriveddata" | "dist" | "obj" | "target" | "testresults" => {
            Some("build output/cache directory")
        }
        ".merge-backups" => Some("merge backup directory"),
        "node_modules" | "pods" | "vendor" => Some("dependency directory"),
        "worktrees" => Some("session worktree directory"),
        _ => None,
    }
}

pub(super) fn source_file_matches_query(relative_path: &str, file_name: &str, query: Option<&str>) -> bool {
    let Some(query) = query else {
        return true;
    };
    relative_path.to_lowercase().contains(query) || file_name.to_lowercase().contains(query)
}
