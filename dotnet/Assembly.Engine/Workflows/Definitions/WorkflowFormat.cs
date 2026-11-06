namespace Assembly.Engine.Workflows.Definitions;

public static class WorkflowFormat
{
    public const string Text = """
        Workflows are one YAML document in <project>/.assembly/workflows/*.yml.
        Check reads and snapshots the definition and prompt files; it executes nothing.
        Existing valid workflow fields and templates are preserved. Unsupported keys,
        duplicate keys, aliases/custom tags, missing dependencies, cycles and path or
        symlink escapes are field errors. There is no YAML version/roles/nodes/edges key.

        name: nonempty one-line display name
        inputs.<name>: {description?: string, required?: boolean=false, default?: JSON value}
        Required inputs are enforced at start. Engine-resolved task/cwd cannot be overridden.
        limits: {max_sessions: 3, step_timeout_minutes: 60, max_rounds: 5}
        Limits must be positive integers; timeouts must fit checked milliseconds.
        policy: {commands: {allow: [wildcard], ask: [wildcard], deny: [wildcard]}, default: ask}
        Policy precedence is fixed deny > deny > ask > allow > default. Wildcards use *.
        Policy observes actual provider permission requests; it is not a sandbox.
        YAML never grants owner merge authorization or overrides fixed worker denies.

        steps.<id> IDs use ASCII letters, digits, underscore and hyphen.
        needs: dependency ID list, default []
        Exactly one step kind:
          agent: {provider: configured-launcher-id, model?: string, permission?: string}
                 prompt: prompts/build.md
                 result: build|spec|plan|recon|proof
          review: {of: dependency-id, agent: {provider, model?, permission?}, prompt: prompts/review.md}
                  result: review
          run: [nonempty authored shell commands]
          gate: arbitrary JSON-compatible YAML display context; always owner approval/revise/deny
        Model omission remains null until the real provider open uses its default.
        Permission omission retains provider Auto selection. Providers/models are checked at dispatch.
        Agent nodes must form one ordered lineage; independent reviews may run concurrently.
        on_fail: direct/transitive dependency to redo
        repeat: boolean=false; non-repeatable command uncertainty requires resolution
        expect_files: contained file glob list; absolute proof patterns must be under this run's proof root
        if: restricted template condition rendering exactly a boolean
        workspace: new|parent, default new
        required: boolean=true
        max_attempts: positive integer=1; review redo is separately bounded by max_rounds
        display: {present?: string (120), past?: string (120), stage?: string (40), detail?: string (300)}
        display.label remains a legacy label input; display fields do not set runtime state

        Templates use ${{ ... }}:
        inputs.<name>, task.key/title/body, steps.<id>.<receiptField>, steps.<id>.files,
        redo.findings, redo.output, run.proof_dir, run.pr_body.
        Only boolean values support unary !. No expressions or arbitrary property walking.
        A taskless prompt uses inputs.request; task references without a task fail by field.
        Missing redo values render empty on the first attempt. Prompt substitution is literal.
        Command preparation replaces placeholders with ${ASSEMBLY_Vn}; actual values are
        separate environment bindings, never shell code. Double quotes keep one exact argument,
        unquoted expansions retain shell word splitting/globbing, single quotes remain literal.
        PR create/edit uses the authored --body-file "${{ run.pr_body }}".
        Snapshots preserve full YAML, parsed definition and ordered prompt content with SHA-256;
        source edits affect new runs only. Inputs have a separate immutable hash.

        Tools and assignment requirements:
        start_workflow/stop_workflow/decide_step: root/controller manages its own run;
        workers cannot approve owner gates or change another run.
        report: current assigned step worker reports its own result/proof/PR at current
        attempt/generation/epoch; owner PR reporting uses the checked run-owner route.
        Repository observation establishes merged, never a worker claim.
        ReportStepProgress: assigned worker reports bounded progress, not Board patches or success.
        send_message/list_sessions: existing reachable-directory and relationship permissions;
        eligible running-turn steering reaches the next tool boundary.
        ask_owner/answer_need: children route through parent; only actual answerer/owner resolves.
        link_task: existing own-session task linking, available without starting a workflow.
        A shared tools/list is discovery; host/provider/role checks at invocation remain authoritative.
        Each step prompt states its needed tools and assignment. Forbidden/unavailable operations
        return plain refusals and cannot count as success. A missing producer cannot be claimed done.
        """;
}
