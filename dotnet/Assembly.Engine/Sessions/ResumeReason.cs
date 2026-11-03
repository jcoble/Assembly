namespace Assembly.Engine.Sessions;

/// <summary>The shared native-resume reasons used by imports and handoff continuation.</summary>
public static class ResumeReason
{
    public const string Unsupported = "unsupported";
    public const string Missing = "missing";
    public const string Denied = "denied";
    public const string WriterElsewhere = "writer_elsewhere";
    public const string Transient = "transient";
    public const string NoFolder = "no_folder";
}
