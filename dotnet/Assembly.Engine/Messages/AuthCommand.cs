using Assembly.Domain.Rows;

namespace Assembly.Engine.Messages;

public sealed record BlockSignInNeeded(string SessionId, AuthTerminalProof Binding) : MessageCommand;
public sealed record ReportSignInNeeded(string SessionId, long TerminalSeq, AuthTerminalProof Proof) : MessageCommand;
public sealed record RecordAuthRetryStarted(string SessionId, AuthRetryExecution Execution) : MessageCommand;
