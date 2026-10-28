using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>Names a new session with the utility model, from its first exchange, once its first turn has ended.</summary>
public sealed class SessionTitler
{
    public SessionTitler(SessionStore sessionStore, LiveHub hub, IUtilityModel model)
    {
    }

    /// <summary>How much of the first message, and of the reply, the model is given.</summary>
    public const int InputLength = 1000;

    /// <summary>The longest title the model's answer gives.</summary>
    public const int TitleLength = 64;

    /// <summary>What the model is asked, around <paramref name="input"/>.</summary>
    public static string Prompt(string input) => throw new NotImplementedException();

    /// <summary>The answer's first line without its quotes, cut to <see cref="TitleLength"/>; null when that's empty.</summary>
    public static string? Clean(string? answer) => throw new NotImplementedException();
}
