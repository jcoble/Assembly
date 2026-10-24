using Assembly.Engine.Attachments;
using Assembly.Engine.Helpers;
using Assembly.Engine.Live;
using Assembly.Engine.Machines;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Api;

/// <summary>
/// What the routes work with. <c>Machines</c> runs each thread's turns; <c>Token</c> guards every route but the MCP
/// server's, which <c>Helpers</c> serves when given.
/// </summary>
public sealed record ApiServices(LiveHub Hub, ThreadStore Threads, MachineSet Machines, AttachmentStore Attachments, string Token,
    HelperSessions? Helpers = null);
