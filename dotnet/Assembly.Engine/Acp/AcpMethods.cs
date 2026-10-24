namespace Assembly.Engine.Acp;

/// <summary>The ACP v1 method names this client uses.</summary>
public static class AcpMethods
{
    public const string Initialize = "initialize";
    public const string SessionNew = "session/new";
    public const string SessionResume = "session/resume";
    public const string SessionClose = "session/close";
    public const string SessionSetConfigOption = "session/set_config_option";
    public const string SessionPrompt = "session/prompt";
    public const string SessionCancel = "session/cancel";
    public const string SessionRename = "_session/rename";
    public const string SessionSteering = "_session/steering";
    public const string SessionStopTask = "_session/async_task/stop";
    public const string SessionUpdate = "session/update";
    public const string SessionRequestPermission = "session/request_permission";
    public const string ElicitationCreate = "elicitation/create";
}
