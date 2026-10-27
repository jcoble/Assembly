namespace Assembly.Application.Common.Calls;

/// <summary>A feature: it lists its calls once, when the host starts.</summary>
public interface IFeature
{
    void Register(ICallRegistry calls);
}
