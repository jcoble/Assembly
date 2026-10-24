namespace Assembly.Remote.Smoke;

public sealed record SmokeCheck(string Name, bool Passed, string Detail)
{
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")}  {Name}: {Detail}";
}
