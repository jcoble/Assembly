using System.Globalization;
using System.Numerics;

namespace Assembly.Infrastructure.Updates;

internal static class CliVersion
{
    public static bool IsStable(string value)
    {
        var parts = value.Split('+');
        var numbers = parts[0].Split('.');
        return parts.Length <= 2 && numbers.Length == 3 && numbers.All(n => n.Length > 0 &&
            (n.Length == 1 || n[0] != '0') && n.All(c => c is >= '0' and <= '9')) &&
            (parts.Length == 1 || parts[1].Split('.').All(p => p.Length > 0 &&
                p.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-')));
    }

    public static string Compare(string installed, string candidate)
    {
        if (!IsStable(installed) || !IsStable(candidate)) return "unverified";
        var left = installed.Split('+')[0].Split('.'); var right = candidate.Split('+')[0].Split('.');
        for (var i = 0; i < 3; i++)
        {
            var order = BigInteger.Parse(left[i], CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(right[i], CultureInfo.InvariantCulture));
            if (order != 0) return order < 0 ? "update_available" : "ahead";
        }
        return "current";
    }
}
