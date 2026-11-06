use super::{ProjectGroup, ProjectOption, filtered, middle_truncate_path, short_path};

fn option(label: &str, folder: &str) -> ProjectOption {
    ProjectOption {
        project_id: "p".into(),
        machine: "local".into(),
        label: label.into(),
        folder: folder.into(),
        worktree: false,
    }
}
fn groups() -> Vec<ProjectGroup> {
    vec![
        ProjectGroup {
            machine: "local".into(),
            local: true,
            label: "This Mac".into(),
            options: vec![
                option("alpha", "/home/me/alpha"),
                option("beta", "/home/me/beta"),
            ],
        },
        ProjectGroup {
            machine: "workbox-test".into(),
            local: false,
            label: "workbox-test".into(),
            options: vec![option("alpha", "/srv/alpha")],
        },
    ]
}

#[test]
fn a_home_folder_is_shown_as_a_tilde() {
    assert_eq!(short_path("/home/me/dev/x"), "~/dev/x");
    assert_eq!(short_path("/Users/me/dev/x"), "~/dev/x");
    assert_eq!(short_path("/home/me"), "~");
    assert_eq!(short_path("/srv/dev/x"), "/srv/dev/x");
}

// The old app's middleTruncatePath, on the worktree paths in jesse-14.
#[test]
fn a_long_path_keeps_its_start_and_its_last_folder() {
    let short = "~/dev/work/mac-command-bar";
    assert_eq!(middle_truncate_path(short, 46), short);
    let long = "~/dev/work/worktrees/memory-growth/mac-command-bar";
    let cut = middle_truncate_path(long, 46);
    assert_eq!(cut, "~/dev/work/worktrees/memory-g…/mac-command-bar");
    assert_eq!(cut.chars().count(), 46);
    assert_eq!(middle_truncate_path(long, 10), "…/mac-command-bar");
}

#[test]
fn the_filter_matches_the_machine_the_name_or_the_path_and_drops_empty_groups() {
    assert_eq!(filtered(&groups(), ""), groups());
    let beta = filtered(&groups(), " BETA ");
    assert_eq!(beta.len(), 1);
    assert_eq!(beta[0].options, vec![option("beta", "/home/me/beta")]);
    let remote = filtered(&groups(), "workbox");
    assert_eq!(
        (remote.len(), remote[0].machine.as_str()),
        (1, "workbox-test")
    );
    assert_eq!(
        filtered(&groups(), "/srv")[0].options[0].folder,
        "/srv/alpha"
    );
    assert!(filtered(&groups(), "nothing like it").is_empty());
}
