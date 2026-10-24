namespace Assembly.Remote.Record;

/// <summary>
/// One recorded conversation: the files it starts with, and what to do once the session exists.
/// Each scenario gets its own adapter process, folder and transcript, so each fixture stands alone.
/// <c>Air</c> is the AIR extensions the client opts into at initialize.
/// </summary>
public sealed record Scenario(
    string Name,
    IReadOnlyDictionary<string, string> Files,
    Func<ScenarioContext, Task<string>> RunAsync,
    IReadOnlyList<string>? Air = null);
