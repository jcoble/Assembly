use super::{opened,NewSessionForm,TestAppContext,json};
use crate::features::projects::{projects::ProjectRecord,project_chooser::ProjectChoice};
fn project(id:&str,machine:&str,root:&str)->ProjectRecord {
    serde_json::from_value(json!({"id":id,"machine":machine,"rootPath":root,"title":"Same title","repoKey":null,
        "pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":null})).unwrap()
}
#[gpui::test]
fn opening_a_project_draft_keeps_the_exact_copy_and_never_creates_a_session(cx:&mut TestAppContext) {
    let (form,client,cx)=opened(cx);
    client.fake.queue_answer(Ok(json!({"agents":[]})));
    form.update_in(cx,|form,window,cx| {
        form.fill_project(project("local-copy","local","/local/one"),window,cx);
        form.fill_project(project("remote-copy","Workbox Test","/remote/two"),window,cx);
    });
    cx.run_until_parked();
    form.read_with(cx,|form,cx| {
        let selected=form.selected_project.as_ref().unwrap();
        assert_eq!(selected.id,"remote-copy");assert_eq!(selected.root_path,"/remote/two");
        assert_eq!(selected.machine,"Workbox Test");assert_eq!(form.machine.as_deref(),Some("Workbox Test"));
        assert_eq!(form.project_chooser.read(cx).choice(),&ProjectChoice::Project("remote-copy".into()));
        assert!(form.attempt.is_none());assert!(form.cwd.is_none(),"registry path alone does not authorize provider configuration");
    });
    assert!(!client.fake.calls().iter().any(|(method,_)|method=="sessions/create"));
}
#[gpui::test]
fn selecting_no_project_clears_the_registry_choice_without_creating_a_session(cx:&mut TestAppContext) {
    let (form,client,cx)=opened(cx);
    form.update_in(cx,|form,window,cx| {
        form.fill_project(project("one","local","/original"),window,cx);
        form.select_project(None,window,cx);
    });
    form.read_with(cx,|form,_|assert!(form.selected_project.is_none()));
    assert!(!client.fake.calls().iter().any(|(method,_)|method=="sessions/create"));
}
