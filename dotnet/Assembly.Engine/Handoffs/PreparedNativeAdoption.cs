using Assembly.Application.Common.Engine;

namespace Assembly.Engine.Handoffs;

/// <summary>Nonwire evidence from the actual target file producer, never an owner command field.</summary>
public sealed class PreparedNativeAdoption
{
    internal HandoffAdoptArgs Args { get; }
    internal NativeSessionLaunch Launch { get; }
    internal NativeSessionCapture Capture { get; }
    internal PreparedNativeAdoption(HandoffAdoptArgs args, NativeSessionLaunch launch, NativeSessionCapture capture)
    { Args = args; Launch = launch; Capture = capture; }
}
