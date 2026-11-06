use super::*;

#[test]
fn duplicate_name_is_scoped_to_machine_and_refusal_preserves_selection_and_draft() {
    let mut sample = SampleProjects::default();
    sample.select(Some(1)).unwrap();
    sample.draft = "Keep my unsent idea".into();
    let before = sample.projects.clone();
    assert_eq!(
        sample.create(
            Machine::Mac,
            "Assembly",
            "/Users/sample/dev",
            StartWith::Git,
            ""
        ),
        Err("That folder is already in the sample projects.")
    );
    assert_eq!(sample.projects, before);
    assert_eq!(sample.selected, Some(1));
    assert_eq!(sample.draft, "Keep my unsent idea");
}

#[test]
fn create_checks_name_folder_and_clone_source_without_mutation() {
    let mut sample = SampleProjects::default();
    let count = sample.projects.len();
    for name in ["", "..", "one/two", "one\\two"] {
        assert!(
            sample
                .create(
                    Machine::Mac,
                    name,
                    "/Users/sample/dev",
                    StartWith::Empty,
                    ""
                )
                .is_err()
        );
    }
    assert!(
        sample
            .create(Machine::Mac, "notes", "relative", StartWith::Empty, "")
            .is_err()
    );
    assert!(
        sample
            .create(
                Machine::Mac,
                "notes",
                "/Users/sample/dev",
                StartWith::Clone,
                "nonsense"
            )
            .is_err()
    );
    assert_eq!(sample.projects.len(), count);
    let id = sample
        .create(
            Machine::Mac,
            "  notes  ",
            "/Users/sample/dev",
            StartWith::Git,
            "",
        )
        .unwrap();
    assert_eq!(
        sample.projects.iter().find(|p| p.id == id).unwrap().root,
        "/Users/sample/dev/notes"
    );
}

#[test]
fn machine_filter_and_search_distinguish_copies_and_preserve_selection() {
    let mut sample = SampleProjects::default();
    sample.select(Some(1)).unwrap();
    sample.draft = "Unsent".into();
    let copies = sample.visible("assembly", None);
    assert_eq!(copies.len(), 2);
    assert_eq!(sample.visible("assembly", Some(Machine::Workbox)).len(), 1);
    assert_eq!(sample.visible("no-match", None).len(), 0);
    assert_eq!(
        sample.visible("/home/sample", Some(Machine::Workbox)).len(),
        2
    );
    assert_eq!(sample.selected, Some(1));
    assert_eq!(sample.draft, "Unsent");
}

#[test]
fn folder_navigation_uses_only_fixture_children_and_rejects_unknown_paths() {
    assert_eq!(
        folders(Machine::Mac, "/Users/sample/dev").unwrap(),
        vec!["Assembly", "ClipNSave", "notes", "playground"]
    );
    assert!(
        folders(Machine::Mac, "/Users/sample/dev/notes")
            .unwrap()
            .is_empty()
    );
    assert!(folders(Machine::Mac, "/private/real-data").is_err());
    assert!(
        SampleProjects::default()
            .list_folders(Machine::Offline, "/home/sample/dev")
            .is_err()
    );
    assert_eq!(parent_path("/Users/sample/dev"), "/Users/sample");
    assert_eq!(parent_path("/"), "/");
}

#[test]
fn adding_fixture_folder_reuses_existing_id_and_never_changes_draft() {
    let mut sample = SampleProjects::default();
    sample.select(Some(2)).unwrap();
    sample.draft = "Keep this".into();
    let count = sample.projects.len();
    assert_eq!(
        sample.add_folder(Machine::Mac, "/Users/sample/dev/Assembly"),
        Ok(1)
    );
    assert_eq!(sample.projects.len(), count);
    let id = sample
        .add_folder(Machine::Mac, "/Users/sample/dev/notes")
        .unwrap();
    assert!(
        sample
            .projects
            .iter()
            .any(|p| p.id == id && p.title == "notes")
    );
    assert_eq!(sample.selected, Some(2));
    assert_eq!(sample.draft, "Keep this");
}

#[test]
fn project_selection_keeps_draft_and_offline_choice_is_refused() {
    let mut sample = SampleProjects {
        draft: "Build a calendar".into(),
        ..Default::default()
    };
    sample.select(Some(2)).unwrap();
    sample.select(None).unwrap();
    assert_eq!(sample.draft, "Build a calendar");
    assert!(sample.select(Some(5)).is_err());
    assert_eq!(sample.selected, None);
}

#[test]
fn rename_and_pin_preserve_identity_root_and_existing_pin_timestamp() {
    let mut sample = SampleProjects::default();
    let root = sample.projects[0].root.clone();
    sample.rename(1, "  Assembly desktop  ").unwrap();
    assert_eq!(sample.projects[0].title, "Assembly desktop");
    assert_eq!(sample.projects[0].root, root);
    sample.pin(1, true).unwrap();
    let stamp = sample.projects[0].pinned_at;
    sample.pin(1, true).unwrap();
    assert_eq!(sample.projects[0].pinned_at, stamp);
    sample.pin(1, false).unwrap();
    sample.pin(1, true).unwrap();
    assert!(sample.projects[0].pinned_at > stamp);
    assert!(sample.rename(1, "   ").is_err());
}

#[test]
fn remove_retains_selected_identity_and_draft_and_readd_restores_same_title() {
    let mut sample = SampleProjects::default();
    sample.select(Some(1)).unwrap();
    sample.draft = "Do not lose this".into();
    sample.rename(1, "Desktop workbench").unwrap();
    sample.remove(1).unwrap();
    assert!(!sample.visible("", None).iter().any(|p| p.id == 1));
    assert_eq!(sample.selected, Some(1));
    assert!(sample.selected_title().contains("(removed)"));
    assert!(sample.rename(1, "New title").is_err());
    assert_eq!(
        sample.add_folder(Machine::Mac, "/Users/sample/dev/Assembly"),
        Ok(1)
    );
    assert_eq!(sample.projects[0].title, "Desktop workbench");
    assert_eq!(sample.draft, "Do not lose this");
}

#[test]
fn change_folder_refuses_other_live_or_removed_root_and_keeps_machine_and_draft() {
    let mut sample = SampleProjects {
        draft: "Unsent".into(),
        ..Default::default()
    };
    sample.remove(3).unwrap();
    let before = sample.projects[0].clone();
    assert!(
        sample
            .change_folder(1, "/Users/sample/dev/ClipNSave")
            .is_err()
    );
    assert_eq!(sample.projects[0], before);
    sample.change_folder(1, "/Users/sample/dev/notes").unwrap();
    assert_eq!(sample.projects[0].machine, Machine::Mac);
    assert_eq!(sample.projects[0].title, before.title);
    assert_eq!(sample.draft, "Unsent");
}

#[test]
fn connection_toggle_changes_only_sample_availability_and_keeps_selected_draft() {
    let mut sample = SampleProjects::default();
    sample.select(Some(2)).unwrap();
    sample.draft = "Keep remote draft".into();
    sample.set_connected(Machine::Workbox, false).unwrap();
    assert!(!sample.online(Machine::Workbox));
    assert!(
        sample
            .create(
                Machine::Workbox,
                "new",
                "/home/sample/dev",
                StartWith::Empty,
                ""
            )
            .is_err()
    );
    assert_eq!(sample.selected, Some(2));
    assert_eq!(sample.draft, "Keep remote draft");
    sample.set_connected(Machine::Offline, true).unwrap();
    assert!(sample.select(Some(5)).is_ok());
    assert!(sample.set_connected(Machine::Mac, false).is_err());
}

#[test]
fn home_registration_keeps_selection_but_composer_registration_selects_new_project_with_draft() {
    let mut sample = SampleProjects::default();
    sample.select(Some(1)).unwrap();
    sample.draft = "Continue this idea".into();
    let id = sample
        .create(
            Machine::Mac,
            "fresh",
            "/Users/sample/dev",
            StartWith::Empty,
            "",
        )
        .unwrap();
    sample.registration_completed(id, false).unwrap();
    assert_eq!(sample.selected, Some(1));
    sample.registration_completed(id, true).unwrap();
    assert_eq!(sample.selected, Some(id));
    assert_eq!(sample.draft, "Continue this idea");
}

#[test]
fn folder_paths_accept_trailing_slash_and_machine_home_shorthand() {
    assert_eq!(folders(Machine::Mac, "/").unwrap(), vec!["Users"]);
    assert_eq!(folders(Machine::Mac, "/Users/").unwrap(), vec!["sample"]);
    assert_eq!(
        folders(Machine::Mac, "/Users/sample/").unwrap(),
        vec!["dev"]
    );
    assert_eq!(folders(Machine::Mac, "~/").unwrap(), vec!["dev"]);
    assert_eq!(folders(Machine::Workbox, "/").unwrap(), vec!["home"]);
    assert_eq!(folders(Machine::Workbox, "~/").unwrap(), vec!["dev"]);
    assert_eq!(
        folders(Machine::Workbox, "/home/sample/dev/").unwrap(),
        folders(Machine::Workbox, "/home/sample/dev").unwrap()
    );
    assert!(folders(Machine::Mac, "/home/sample/dev/").is_err());
    assert!(folders(Machine::Workbox, "/Users/sample/").is_err());
    assert!(folders(Machine::Workbox, "~/dev/ClipNSave/").is_err());
    assert!(folders(Machine::Mac, "~/dev/retailer-notes/").is_err());
    assert_eq!(
        folders(Machine::Mac, "/Users/unknown/"),
        Err(SAMPLE_FOLDER_LIMITATION)
    );
}
#[test]
fn normalized_registration_and_folder_changes_keep_identity_and_draft() {
    let mut sample = SampleProjects {
        draft: "Keep this draft".into(),
        ..Default::default()
    };
    let selected = sample.selected;
    let existing = sample.add_folder(Machine::Mac, "~/dev/Assembly/").unwrap();
    assert_eq!(
        sample
            .add_folder(Machine::Mac, "/Users/sample/dev/Assembly")
            .unwrap(),
        existing
    );
    let added = sample
        .add_folder(Machine::Workbox, "/home/sample/dev/notes/")
        .unwrap();
    assert_eq!(
        sample.projects.iter().find(|p| p.id == added).unwrap().root,
        "/home/sample/dev/notes"
    );
    sample.change_folder(existing, "~/dev/playground/").unwrap();
    assert_eq!(
        sample
            .projects
            .iter()
            .find(|p| p.id == existing)
            .unwrap()
            .root,
        "/Users/sample/dev/playground"
    );
    assert_eq!(sample.selected, selected);
    assert_eq!(sample.draft, "Keep this draft");
}
