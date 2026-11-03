//! Source-derived wire fixtures exercise the real navigator/coordinator. They prove no provider or native authority.
use std::{cell::RefCell,collections::HashMap,rc::Rc,sync::{Arc,Mutex}};
use futures_channel::mpsc::{unbounded,UnboundedSender,UnboundedReceiver};
use gpui_kit::{App,AppContext as _,Context,Entity,IntoElement,ParentElement as _,Render,Styled as _,Subscription,VisualTestContext,Window,div};
use gpui_kit::component::dock::{DockArea,DockPlacement,PanelId,panel_handle};
use serde_json::{Value,json};
use crate::core::{engine::SessionKey,intents::session_open::{SessionOpenRequest,SessionOpenResult,SessionOpenTarget},socket::{client::SocketClient,endpoint::Endpoint,fake_backend::{FakeBackend,Reply},outcome::Outcome,test_wait::until},state::selected_session::SelectedSession,ui::center_panes::CenterPanes};
use crate::features::{session::client::socket_session_client::SocketSessionClient,workspaces::workspace_coordinator::WorkspaceCoordinator};
use super::SessionPane;
#[derive(Default)]
struct Server {rows:HashMap<String,Vec<Value>>,subscriptions:HashMap<String,Value>,workspaces:HashMap<String,(u64,Value)>,calls:Vec<Value>,hold:bool,held:Option<Value>,operational:bool,owner:Option<String>,fail_warm:bool}
pub(crate) struct NavigationFixture {
    pub pane:Entity<SessionPane>,pub main:Entity<SocketClient>,bulk:Entity<SocketClient>,
    _backend:FakeBackend,_dock:Entity<DockArea>,_heard:Vec<Subscription>,
    state:Arc<Mutex<Server>>,results:Rc<RefCell<Vec<SessionOpenResult>>>,changed:UnboundedSender<()>,changes:UnboundedReceiver<()>,
}
#[derive(Default)]
pub(crate) struct NavigationHost {pub pane:Option<Entity<SessionPane>>}
impl Render for NavigationHost {
    fn render(&mut self,_:&mut Window,_:&mut Context<Self>)->impl IntoElement {div().size_full().children(self.pane.clone())}
}
impl NavigationFixture {
    pub fn new(checkout:bool,window:&mut Window,cx:&mut App)->Self {
        let state=Arc::new(Mutex::new(Server::default()));let server=state.clone();
        let(changed,changes)=unbounded();let tell=changed.clone();
        let backend=FakeBackend::start(move|frame| {
            let mut state=server.lock().unwrap();state.calls.push(frame.clone());let _=tell.unbounded_send(());
            let id=&frame["id"];let args=&frame["args"];
            let result=|value|Reply::Frame(json!({"t":"result","id":id,"result":value}));
            match(frame["t"].as_str(),frame["feature"].as_str(),frame["method"].as_str(),frame["topic"].as_str()) {
                (Some("hello"),..)=>vec![Reply::Frame(json!({"t":"welcome","protocol":1,"machine":"box","engine":state.owner.as_deref().unwrap_or("owner")}))],
                (Some("call"),Some("sessions"),Some("resolve_source"),_)=>vec![result(json!({"machine":"owner","generation":0,"routeMachine":null}))],
                (Some("call"),Some("engine"),Some("entities"),_)=>{
                    let items=args["refs"].as_array().unwrap().iter().map(|reference|json!({"entityKind":"session","id":reference["id"],"machine":"owner","version":0,"provider":"source-derived-fixture","folder":"/display-only","state":if state.operational{"idle"}else{"ended"},"activityState":"idle","epoch":0})).collect::<Vec<_>>();
                    vec![result(json!({"items":items,"next":null}))]
                },
                (Some("call"),Some("sessions"),Some("open_target"),_)=>{
                    let session=&args["session"];let root=checkout.then(||format!("/source-derived/{}",session.as_str().unwrap()));
                    let reply=json!({"t":"result","id":id,"result":{"kind":args["kind"],"machine":"owner","generation":0,"routeMachine":null,"sessionId":session,"parentSessionId":null,"itemId":null,"seq":null,"payloadSessionId":null,"readOnly":args["kind"]!="engine","hasHistoryRows":true,"originalKey":{"machine":"owner","id":session},"requestId":args["requestId"],"checkoutRoot":root,"expectedSource":{"machine":"owner","generation":0,"routeMachine":null},"transcriptOnly":!checkout}});
                    if std::mem::take(&mut state.hold){state.held=Some(reply);vec![]}else{vec![Reply::Frame(reply)]}
                },
                (Some("sub"),_,_,Some("rows"))=>{
                    let session=frame["key"].as_str().unwrap().to_string();state.subscriptions.insert(session.clone(),id.clone());
                    let rows=state.rows.entry(session.clone()).or_insert_with(||(1..=40).map(|seq|row(&session,seq)).collect());
                    let top=rows.last().and_then(|row|row["seq"].as_i64()).unwrap_or(0);
                    vec![Reply::Frame(json!({"t":"state","sub":id,"seq":top,"items":[]}))]
                },
                (Some("call"),Some("rows"),Some("page"),_)=>vec![result(json!({"rows":state.rows[args["session"].as_str().unwrap()],"hasBefore":false,"activeRunId":null}))],
                (Some("call"),Some("workspaces"),Some("read"),_)=>{
                    let stored=state.workspaces.get(args["sessionId"].as_str().unwrap());
                    vec![result(json!({"revision":stored.map_or(0,|entry|entry.0),"snapshot":stored.map(|entry|entry.1.clone())}))]
                },
                (Some("call"),Some("workspaces"),Some("save"),_)=>{
                    let key=args["sessionId"].as_str().unwrap().to_string();let revision=state.workspaces.get(&key).map_or(0,|entry|entry.0);
                    assert_eq!(args["expectedRevision"],revision);state.workspaces.insert(key,(revision+1,args["snapshot"].clone()));vec![result(json!({"revision":revision+1}))]
                },
                (Some("call"),Some("config"),Some("warm"),_) if std::mem::take(&mut state.fail_warm)=>vec![Reply::Frame(json!({"t":"error","id":id,"code":"failed","message":"provider setup required"}))],
                (Some("call"),Some("config"),_,_)=>vec![result(json!({"options":null,"choices":{},"commands":null}))],
                (Some("call"),Some("engine"),Some("owner_prompts"),_)=>vec![result(json!({"engineMachine":"owner","items":[]}))],
                (Some("call"),Some("machines"),Some("agents"),_)=>vec![result(json!({"agents":[{"id":"source-derived-fixture","name":"Fixture","targetId":"fixture-target"}]}))],
                (Some("call"),Some("fixture"),Some("drop"),_)=>vec![Reply::Drop],
                (Some("call"),Some("sessions"),Some("read"),_)=>vec![result(Value::Null)],
                (Some("call"),Some("fixture"),Some("release_target"),_)=>vec![Reply::Frame(state.held.take().expect("held descriptor")),result(Value::Null)],
                (Some("call"),Some("fixture"),Some("push"),_)=>{
                    let session=args["row"]["sessionId"].as_str().unwrap();let sub=state.subscriptions[session].clone();
                    vec![Reply::Frame(json!({"t":"rows","sub":sub,"row":args["row"]})),result(Value::Null)]
                },
                _=>vec![],
            }
        });
        let endpoint=Endpoint{port:backend.port,token:"secret".into()};
        let main=SocketClient::start(endpoint.clone(),cx);let bulk=SocketClient::start_bulk(endpoint,cx);
        SessionPane::register(cx);
        let pane=cx.new(|cx|SessionPane::new(Rc::new(SocketSessionClient),window,cx));
        let dock=cx.new(|cx|DockArea::new("navigator-fixture",None,window,cx));CenterPanes::init(&dock,cx);
        dock.update(cx,|dock,cx|dock.add_panel_view(panel_handle(pane.clone()),DockPlacement::Center,None,window,cx));
        CenterPanes::remember("Chat",PanelId::from(pane.entity_id()),cx);
        SessionPane::register_workspace(cx).unwrap();WorkspaceCoordinator::init(cx);
        let results=Rc::new(RefCell::new(Vec::new()));let got=results.clone();let result_tell=changed.clone();
        let mut heard=vec![cx.subscribe(&SelectedSession::global(cx),move|_,event:&SessionOpenResult,_|{got.borrow_mut().push(event.clone());let _=result_tell.unbounded_send(());})];
        for socket in [&main,&bulk] {let ready=changed.clone();heard.push(cx.observe(socket,move|_,_|{let _=ready.unbounded_send(());}));}
        let ready=changed.clone();heard.push(cx.observe(&pane,move|_,_|{let _=ready.unbounded_send(());}));
        Self{pane,main,bulk,_backend:backend,_dock:dock,_heard:heard,state,results,changed,changes}
    }
    pub async fn ready(&mut self,cx:&mut VisualTestContext) {
        until("both navigator sockets",&mut self.changes,&self.changed,||cx.read(|cx|self.main.read(cx).connection_generation().is_some()&&self.bulk.read(cx).connection_generation().is_some())).await;
    }
    pub async fn opened(&mut self,id:&str,cx:&mut VisualTestContext) {
        until("correlated complete navigator result",&mut self.changes,&self.changed,||self.results.borrow().iter().any(|result|result.key.as_ref().is_some_and(|key|key.id==id))).await;
        let result=self.results.borrow().iter().rev().find(|result|result.key.as_ref().is_some_and(|key|key.id==id)).unwrap().clone();assert!(result.result.is_ok(),"{result:?}");
        cx.run_until_parked();
    }
    pub async fn request(&mut self,id:&str,cx:&mut VisualTestContext) {
        self.results.borrow_mut().clear();self.pane.update_in(cx,|pane,window,cx|pane.request_open(SessionOpenRequest{key:SessionKey{machine:"owner".into(),id:id.into()},request_id:format!("fixture:{id}"),target:SessionOpenTarget::Historical},window,cx));self.opened(id,cx).await;
    }
    pub fn fail_next_warm(&self){self.state.lock().unwrap().fail_warm=true;}
    pub fn operational(&self){self.state.lock().unwrap().operational=true;}
    pub fn replace_owner(&self){self.state.lock().unwrap().owner=Some("replacement-owner".into());}
    pub async fn request_engine(&mut self,id:&str,cx:&mut VisualTestContext) {
        self.results.borrow_mut().clear();self.pane.update_in(cx,|pane,window,cx|pane.request_open(SessionOpenRequest{key:SessionKey{machine:"owner".into(),id:id.into()},request_id:format!("engine:{id}"),target:SessionOpenTarget::Engine},window,cx));self.opened(id,cx).await;
    }
    pub async fn drop_main(&mut self,cx:&mut VisualTestContext) {
        self.results.borrow_mut().clear();
        let call=cx.read(|cx|self.main.read(cx).call::<Value>("fixture","drop",json!({}),None));
        assert!(matches!(call.await,Outcome::Aborted|Outcome::Failed(_)));
    }
    pub async fn calls_at_least(&mut self,method:&str,count:usize) {
        until("correlated reconnect read",&mut self.changes,&self.changed,||self.state.lock().unwrap().calls.iter().filter(|call|call["method"]==method).count()>=count).await;
    }
    pub fn rows(&self,id:&str,rows:Vec<Value>){self.state.lock().unwrap().rows.insert(id.into(),rows);}
    pub fn hold_target(&self){self.state.lock().unwrap().hold=true;}
    pub fn calls(&self)->Vec<Value>{self.state.lock().unwrap().calls.clone()}
    pub async fn control(&self,method:&str,args:Value,cx:&mut VisualTestContext) {
        let call=cx.read(|cx|self.main.read(cx).call::<Value>("fixture",method,args,None));assert!(matches!(call.await,Outcome::Ok(_)));
    }
    pub async fn call_entered(&mut self,method:&str) {
        until("actual target read entered",&mut self.changes,&self.changed,||self.state.lock().unwrap().calls.iter().any(|call|call["method"]==method)).await;
    }
    pub async fn count_rows(&mut self,count:usize,cx:&mut VisualTestContext) {
        until("visible coherent rows",&mut self.changes,&self.changed,||self.pane.read_with(cx,|pane,_|pane.view.items.len()==count)).await;
    }
}
fn row(session:&str,seq:i64)->Value {json!({"sessionId":session,"seq":seq,"itemId":null,"runId":null,"kind":"user","payload":{"text":format!("{session}:{seq}")},"createdAt":seq})}
