using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Calls;

using Assembly.Infrastructure.Mcp;
namespace Assembly.Infrastructure.Http;

/// <summary>
/// What the routes work with. <c>Machines</c> runs each session's turns; <c>Token</c> guards every route but the MCP
/// server's, which <c>Helpers</c> serves when given,
/// and the socket's, whose <c>hello</c> carries it. <c>Writer</c> and <c>Topics</c>, when given, serve the socket: its
/// writes, and its subscriptions to what the writer publishes, so they must be the database's own. <c>Features</c> are
/// the host's features on the socket.
/// </summary>
public sealed record ApiServices(LiveHub Hub, SessionStore SessionStore, MachineSet Machines, AttachmentStore Attachments, string Token,
    HelperSessions? Helpers = null, IAtomicWriter? Writer = null, Topics.Topics? Topics = null, IReadOnlyList<IFeature>? Features = null);
