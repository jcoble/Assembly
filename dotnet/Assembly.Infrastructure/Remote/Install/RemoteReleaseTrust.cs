using System.Text;
using Assembly.Application.Common.Calls;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>
/// Checks a release signature made by <c>tauri signer sign</c>: a base64 envelope around Minisign text, with the BLAKE2b
/// prehash and the signed trusted comment both required. The old app's key, so the same secret signs for this one.
/// </summary>
internal static class RemoteReleaseTrust
{
    internal const string SigningPublicKey = "RWQZvQJuc5RPnp9xO8+V9ppE3cCiodEFHPYqJpIMMVRhAnaxo0udp8xh";
    private const int KeyIdLength = 8, KeyLength = 32, SignatureLength = 64;

    internal static void Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signatureFile, string publicKey = SigningPublicKey)
    {
        var digest = new ReleaseDigest();
        digest.Add(data);
        Verify(digest, signatureFile, publicKey);
    }

    /// <summary>The same check over bytes already fed to <paramref name="digest"/> as they arrived.</summary>
    internal static void Verify(ReleaseDigest digest, ReadOnlySpan<byte> signatureFile, string publicKey = SigningPublicKey)
    {
        try
        {
            var key = Convert.FromBase64String(publicKey);
            if (key.Length != 2 + KeyIdLength + KeyLength || key[0] != 'E' || key[1] != 'd') throw Invalid();
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(signatureFile).Trim()));
            var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            if (lines.Length < 4 || !lines[2].StartsWith("trusted comment: ", StringComparison.Ordinal)) throw Invalid();
            var signature = Convert.FromBase64String(lines[1]);
            if (signature.Length != 2 + KeyIdLength + SignatureLength || signature[0] != 'E' || signature[1] != 'D' ||
                !signature.AsSpan(2, KeyIdLength).SequenceEqual(key.AsSpan(2, KeyIdLength))) throw Invalid();
            var raw = signature.AsSpan(2 + KeyIdLength).ToArray();
            var verifier = new Ed25519PublicKeyParameters(key, 2 + KeyIdLength);
            if (!Check(verifier, digest.Finish(), raw)) throw Invalid();
            var comment = Encoding.UTF8.GetBytes(lines[2]["trusted comment: ".Length..]);
            var global = Convert.FromBase64String(lines[3]);
            if (global.Length != SignatureLength || !Check(verifier, [.. raw, .. comment], global)) throw Invalid();
        }
        catch (FormatException) { throw Invalid(); }
    }

    private static bool Check(Ed25519PublicKeyParameters key, byte[] message, byte[] signature)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.VerifySignature(signature);
    }

    private static CallFailed Invalid() => new("Release signature verification failed.", "release_untrusted");
}

/// <summary>The signature's BLAKE2b prehash, fed a piece at a time, so a large download is checked without holding all of it.</summary>
internal sealed class ReleaseDigest
{
    private readonly Blake2bDigest _digest = new(512);

    internal void Add(ReadOnlySpan<byte> piece) => _digest.BlockUpdate(piece);

    internal byte[] Finish()
    {
        var output = new byte[64];
        _digest.DoFinal(output);
        return output;
    }
}
