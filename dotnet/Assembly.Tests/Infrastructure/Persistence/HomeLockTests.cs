using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Infrastructure.Persistence;

/// <summary>The lock both hosts (Assembly.Desktop and Assembly.Remote serve) take on their home before the database opens.</summary>
public sealed class HomeLockTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-lock-").FullName;

    [Fact]
    public void A_second_take_on_the_same_home_names_the_holder_and_a_take_after_it_is_released_succeeds()
    {
        using (HomeLock.Take(_home))
        {
            var error = Assert.Throws<InvalidOperationException>(() => HomeLock.Take(_home));
            Assert.Equal($"home in use by pid {Environment.ProcessId}", error.Message);
        }

        using var again = HomeLock.Take(_home);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
