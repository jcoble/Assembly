//! The Assembly MCP server on rmcp: the bearer token says who is calling, and every tool call goes
//! to `MessagingEndpoint::handle`. Each tool's input schema comes from its argument type below.
#![allow(dead_code)] // The argument types only describe the arguments; `handle` reads them.
use super::{board, MessagingEndpoint, OWNER_AUTHORITY};
use axum::{extract::{Request, State}, http::{request::Parts, HeaderMap, StatusCode}, middleware::Next, response::{IntoResponse, Response}};
use rmcp::{handler::server::common::{schema_for_empty_input, schema_for_input}, schemars::{self, JsonSchema},
    model::{CallToolRequestParams, CallToolResponse, CallToolResult, ContentBlock, Implementation, ListToolsResult, PaginatedRequestParams, ServerCapabilities, ServerConfig, Tool},
    service::RequestContext, transport::streamable_http_server::{session::never::NeverSessionManager, StreamableHttpServerConfig, StreamableHttpService},
    ErrorData, RoleServer, ServerHandler};
use serde_json::{json, Map, Value};
use std::sync::{Arc, Weak};

/// `/mcp`, answered statelessly: every POST stands alone, and only a live session token gets past the door.
pub(super) fn router(endpoint: Weak<MessagingEndpoint>) -> axum::Router {
    let server = Server(endpoint.clone());
    let config = StreamableHttpServerConfig::default().with_legacy_session_mode(false).with_json_response(true);
    let service = StreamableHttpService::new(move || Ok(server.clone()), Arc::new(NeverSessionManager::default()), config);
    axum::Router::new().route_service("/mcp", service).layer(axum::middleware::from_fn_with_state(endpoint, authorize))
}

fn bearer(headers: &HeaderMap) -> &str {
    headers.get("Authorization").and_then(|h| h.to_str().ok()).and_then(|h| h.strip_prefix("Bearer ")).unwrap_or("")
}

async fn authorize(State(endpoint): State<Weak<MessagingEndpoint>>, request: Request, next: Next) -> Response {
    let Some(endpoint) = endpoint.upgrade() else { return StatusCode::SERVICE_UNAVAILABLE.into_response() };
    if endpoint.caller(bearer(request.headers())).is_err() { return StatusCode::UNAUTHORIZED.into_response(); }
    next.run(request).await
}

#[derive(Clone)]
struct Server(Weak<MessagingEndpoint>);

impl ServerHandler for Server {
    fn get_info(&self) -> ServerConfig {
        ServerConfig::new(ServerCapabilities::builder().enable_tools().build())
            .with_server_info(Implementation::new("assembly", "1"))
            .with_instructions(format!("{OWNER_AUTHORITY} Report work with receipts: commit hashes, run ids, step results from get_workflow_run, test output."))
    }

    async fn list_tools(&self, _: Option<PaginatedRequestParams>, _: RequestContext<RoleServer>) -> Result<ListToolsResult, ErrorData> {
        Ok(ListToolsResult::with_all_items(tools()))
    }

    async fn call_tool(&self, request: CallToolRequestParams, context: RequestContext<RoleServer>) -> Result<CallToolResponse, ErrorData> {
        let endpoint = self.0.upgrade().ok_or_else(|| ErrorData::internal_error("Assembly is shutting down", None))?;
        let token = context.extensions.get::<Parts>().map(|parts| bearer(&parts.headers)).unwrap_or("");
        let result = match endpoint.handle(token, json!({"name": request.name, "arguments": request.arguments})).await {
            Ok(result) => serde_json::from_value(result).map_err(|error| ErrorData::internal_error(error.to_string(), None))?,
            Err(error) => CallToolResult::error(vec![ContentBlock::text(error)]),
        };
        Ok(result.into())
    }
}

fn tool<T: JsonSchema + 'static>(name: &'static str, description: impl Into<String>) -> Tool {
    let mut schema = schema_for_input::<T>().expect("tool arguments are an object").as_ref().clone();
    // An `Option` argument is left out, never null: `handle` reads a null it is sent as a value.
    for property in schema.get_mut("properties").and_then(Value::as_object_mut).into_iter().flat_map(|properties| properties.values_mut()) {
        let types = property["type"].as_array().map(|types| types.iter().filter(|kind| *kind != "null").cloned().collect::<Vec<_>>());
        if let Some(mut types) = types { property["type"] = if types.len() == 1 { types.remove(0) } else { Value::Array(types) }; }
        if let Some(values) = property.get_mut("enum").and_then(Value::as_array_mut) { values.retain(|value| !value.is_null()); }
    }
    Tool::new(name, description.into(), schema)
}

fn no_arguments(name: &'static str, description: &'static str) -> Tool {
    Tool::new(name, description, schema_for_empty_input())
}

fn tools() -> Vec<Tool> {
    vec![
        no_arguments("list_sessions", "List Assembly sessions on this machine and on the other machines connected to the desktop. Each has a handle, a name and the machine it runs on; send to handle@machine."),
        tool::<SendMessage>("send_message", OWNER_AUTHORITY.to_string() + " Send a message to an Assembly session you may reach (see list_sessions), addressed by its handle (such as brisk-heron), its name, or its owned id; add @machine (brisk-heron@workbox-test) for a session on another machine. A handle never changes when the session is renamed."),
        tool::<StartSession>("start_session", "Start a Codex, Claude or Antigravity helper session. It works in folder (an absolute path on your machine, such as a worktree) or, without one, in your folder. It is listed with your agents, and its final reply is sent to you as a message when it finishes."),
        no_arguments("workflow_format", "Read the reference for writing an Assembly workflow (.assembly/workflows/*.yml)."),
        tool::<CheckWorkflow>("check_workflow", "Check a workflow file in a registered project on this machine, or your current project when project is omitted. Returns ok, its steps and any warnings, or the first error."),
        tool::<StartWorkflow>("start_workflow", "Start a workflow from a registered project's .assembly/workflows on this machine, or your current project when project is omitted. You are messaged when it waits for the owner or ends. At most 3 of your runs can be unfinished. `inputs` sets the workflow's inputs, e.g. {\"i_merge\": true} so the run leaves the merge to the owner, or {\"parent_task\": \"TSK-1582\"} for a slice of that project."),
        no_arguments("list_workflow_runs", "List the workflow runs on this machine."),
        tool::<RunId>("get_workflow_run", "Read what happened in a workflow run, step by step: state, attempt, worker, failure, command exit codes, review verdict and findings, pull request."),
        tool::<RunId>("stop_workflow_run", "Stop a workflow run you started."),
        tool::<DecideStep>("decide_step", "Main orchestrator only: decide a review step that is waiting (at its round limit, or because its turn gave no report) when its findings and the task spec give enough to decide. accept completes it as it is, another-round redoes it, stop ends the run. why, one or two sentences, is recorded in the run for the owner. Gates, and anything that needs the owner's judgment (scope, a product choice, risk), stay the owner's: post those on the Build Board and send the owner a phone notification."),
        tool::<ReportReview>("report_review", "Workflow review steps only: a verdict must be recorded once; retry a rejected or failed call. Use pass with no findings, or fail with each finding."),
        tool::<MessageId>("message_status", "Read whether a message you sent is queued, delivered or failed."),
        tool::<board::BoardUpdate>("board_update", board::DESCRIPTION),
    ]
}

#[derive(JsonSchema)]
struct SendMessage { to: String, text: String }

#[derive(JsonSchema)]
#[serde(rename_all = "lowercase")]
#[schemars(inline)]
enum Provider { Codex, Claude, Antigravity }

#[derive(JsonSchema)]
struct StartSession { provider: Provider, model: Option<String>, prompt: String, name: String, folder: Option<String> }

#[derive(JsonSchema)]
struct CheckWorkflow {
    path: String,
    /// Registered project title or root path on this machine; omit to use your current project.
    project: Option<String>,
}

#[derive(JsonSchema)]
#[schemars(inline)]
struct Task { key: String, title: String, body: String }

#[derive(JsonSchema)]
struct StartWorkflow {
    path: String,
    /// Registered project title or root path on this machine; omit to use your current project.
    project: Option<String>,
    task: Task,
    inputs: Option<Map<String, Value>>,
}

#[derive(JsonSchema)]
struct RunId { run_id: String }

#[derive(JsonSchema)]
#[serde(rename_all = "kebab-case")]
#[schemars(inline)]
enum Decision { Accept, AnotherRound, Stop }

#[derive(JsonSchema)]
struct DecideStep { run_id: String, step: String, decision: Decision, why: String }

#[derive(JsonSchema)]
#[serde(rename_all = "lowercase")]
#[schemars(inline)]
enum Verdict { Pass, Fail }

#[derive(JsonSchema)]
struct ReportReview { verdict: Verdict, findings: Vec<String> }

#[derive(JsonSchema)]
struct MessageId { id: String }
