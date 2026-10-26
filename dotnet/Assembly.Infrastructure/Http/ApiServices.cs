using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// What the routes work with. <c>Machines</c> runs each thread's turns; <c>Token</c> guards every route but the MCP
/// server's, which <c>Helpers</c> serves when given.
/// </summary>
public sealed record ApiServices(LiveHub Hub, ThreadStore Threads, MachineSet Machines, AttachmentStore Attachments, string Token,
    HelperSessions? Helpers = null);
