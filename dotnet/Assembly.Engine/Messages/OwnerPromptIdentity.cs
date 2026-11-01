using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Assembly.Engine.Messages;

/// <summary>Stable creation identity shared by the owning door and checked Engine transition.</summary>
public static class OwnerPromptIdentity
{
    public static string MessageId(string session, string clientId) => $"prompt/{session}/{clientId}";

    public static Guid CommandId(string machine, string session, string clientId)
    {
        var identity = string.Create(CultureInfo.InvariantCulture,
            $"engine-owner-prompt/v1/{machine.Length}:{machine}{session.Length}:{session}{clientId.Length}:{clientId}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
}
