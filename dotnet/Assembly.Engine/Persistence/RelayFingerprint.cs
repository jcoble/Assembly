using System.Globalization;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Persistence;

/// <summary>Only new private relay sends carry the original requested epoch; no generic receipt changes.</summary>
internal static class RelayFingerprint
{
    private const string Prefix = "relay-send-fp/v1/";
    internal static string Encode(long requestedEpoch, string fingerprint)
    {
        if (requestedEpoch < 0 || !RelayHash.IsSha(fingerprint)) throw new RelayRefused("bad_args");
        return Prefix + requestedEpoch.ToString(CultureInfo.InvariantCulture) + "/" + fingerprint;
    }
    internal static bool TryDecode(string value, out long requestedEpoch, out string fingerprint)
    {
        requestedEpoch = 0; fingerprint = "";
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = value.AsSpan(Prefix.Length);
        var separator = rest.IndexOf('/');
        if (separator < 1) return false;
        var epoch = rest[..separator];
        if (epoch.Length > 1 && epoch[0] == '0' || !long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out requestedEpoch) || requestedEpoch < 0)
            return false;
        fingerprint = rest[(separator + 1)..].ToString();
        return RelayHash.IsSha(fingerprint);
    }
}
