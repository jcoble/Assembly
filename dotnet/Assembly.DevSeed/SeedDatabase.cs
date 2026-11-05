using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Projects;
using Assembly.Application.Features.Workflows;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Topics;

namespace Assembly.DevSeed;

/// <summary>Builds the database beside its final name and renames it into place, so a profile has all of it or none.</summary>
public static class SeedDatabase
{
    public const string OfflineMachine = "test-box";
    public const string OfflineHost = "test-box.invalid";

    public static (int Sessions, int Runs) Write(SeedWorld world, IReadOnlyList<SeedChat> chats, CancellationToken cancel)
    {
        var final = Path.Combine(world.Home, "assembly.db");
        var staged = final + ".seeding";
        int runs;
        using (var db = AppDatabase.Open(staged, TimeProvider.System, new Topics()))
        {
            WorkflowSchema.Open(db);
            RowStore.Open(db);
            SessionStore.Open(db, TimeProvider.System, Path.Combine(world.Home, "sessions"));
            _ = new ProjectStore(db, TimeProvider.System);
            _ = new MachineStore(db);
            var machine = EngineSchema.Open(db, world.NowMs, SessionStore.EnsureEngineProjectionSchema);
            runs = db.Writer.Write("dev-seed", scope =>
            {
                scope.Execute("INSERT INTO saved_machines(name,ssh,port,token_file) VALUES($name,$ssh,9797,'.config/assembly-seed/env')",
                    new SqlArg("$name", OfflineMachine), new("$ssh", OfflineHost));
                foreach (var repo in SeedScenario.Projects) InsertProject(scope, world, repo);
                foreach (var chat in chats)
                {
                    cancel.ThrowIfCancellationRequested();
                    InsertChat(scope, world, machine, chat);
                    foreach (var child in chat.Children) InsertChat(scope, world, machine, child);
                }
                return SeedWorkflows.Write(scope, world, machine, SeedScenario.Runs, chats);
            });
        }
        foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(staged + suffix);
        File.Move(staged, final);
        return (chats.Count, runs);
    }

    public static string ProjectId(string key) => SeedWorld.GuidOf("project/" + key).ToString("D");

    private static void InsertProject(IWriteScope scope, SeedWorld world, SeedRepo repo) => scope.Execute("""
        INSERT INTO projects(id,machine,root_path,title,repo_key,created_at,last_used_at)
        VALUES($id,$machine,$root,$title,NULL,$at,$at)
        """, new SqlArg("$id", ProjectId(repo.Key)), new("$machine", SeedWorld.Machine), new("$root", world.Project(repo.Key)),
        new("$title", repo.Title), new("$at", world.Ago(2880)));

    private static void InsertChat(IWriteScope scope, SeedWorld world, string engineMachine, SeedChat chat)
    {
        var remote = chat.Machine is not null;
        var root = remote ? "/srv/" + chat.Project : world.Project(chat.Project);
        var folder = remote ? root : world.Folder(chat.Project, chat.Branch);
        var (rows, details) = chat.Build(world, folder);
        var created = rows[0].CreatedAt; var updated = rows[^1].CreatedAt;
        var done = chat.Status is SeedStatus.Done or SeedStatus.Settled or SeedStatus.Ended ? updated : (long?)null;
        var settled = chat.Status == SeedStatus.Settled ? updated : (long?)null;
        var read = chat.Unread ? 0 : rows[^1].Seq;
        var branch = chat.Branch ?? "main";
        scope.Execute("""
            INSERT INTO session_info(session_id,agent,repo_url,title,created_at,updated_at,machine,session_cost,parent_session_id,helper,read_seq,
              completed_at,settled_at,title_source,working_folder,project_id,checkout_root,branch,pinned_at)
            VALUES($id,$agent,$repo,$title,$created,$updated,$machine,$cost,$parent,$helper,$read,$done,$settled,'prompt',$folder,$project,$root,$branch,$pinned)
            """, new SqlArg("$id", chat.Id), new("$agent", chat.Provider), new("$repo", root), new("$title", chat.Title), new("$created", created), new("$updated", updated),
            new("$machine", chat.Machine), new("$cost", chat.Cost), new("$parent", chat.Parent), new("$helper", chat.Parent is null ? 0 : 1), new("$read", read), new("$done", done), new("$settled", settled),
            new("$folder", remote ? null : folder), new("$project", remote ? null : ProjectId(chat.Project)), new("$root", remote ? null : folder), new("$branch", branch),
            new("$pinned", chat.Pinned ? updated : null));
        scope.Execute("INSERT INTO sessions(session_id,next_seq) VALUES($id,$next)", new SqlArg("$id", chat.Id), new("$next", rows[^1].Seq + 1));
        foreach (var row in rows)
            scope.Execute("INSERT INTO rows(session_id,seq,run_id,item_id,kind,payload,created_at) VALUES($id,$seq,$run,$item,$kind,$payload,$at)",
                new SqlArg("$id", chat.Id), new("$seq", row.Seq), new("$run", row.RunId), new("$item", row.ItemId), new("$kind", row.Kind), new("$payload", row.Payload.GetRawText()), new("$at", row.CreatedAt));
        foreach (var detail in details)
            scope.Execute("INSERT INTO tool_details(session_id,item_id,input,output,diff,path) VALUES($id,$item,$input,$output,$diff,$path)",
                new SqlArg("$id", chat.Id), new("$item", detail.ItemId), new("$input", detail.Input), new("$output", detail.Output), new("$diff", detail.Diff), new("$path", detail.Path));
        if (remote) return;
        // The engine's own record, so the Board and the rail read this session as they read any other. Idle or ended only:
        // nothing here asks a provider to resume, and no provider identity is recorded for one to find.
        scope.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,account_context,project_id,branch,
              state,activity_state,parent_id,holder,epoch,cancel_requested,managed_checkout)
            VALUES($id,1,$machine,$created,$updated,$provider,$model,$folder,CAST($title AS TEXT),'',$project,$branch,$state,'idle',$parent,'local',0,0,0)
            """, new SqlArg("$id", chat.Id), new("$machine", engineMachine), new("$created", created), new("$updated", updated), new("$provider", chat.Provider),
            new("$model", SeedWorld.ModelOf[chat.Provider]), new("$folder", folder), new("$title", System.Text.Encoding.UTF8.GetBytes(chat.Title)),
            new("$project", ProjectId(chat.Project)), new("$branch", branch), new("$state", chat.Status == SeedStatus.Ended ? "ended" : "idle"), new("$parent", chat.Parent));
    }
}
