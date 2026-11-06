using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>A throwaway Minisign key, signing the way <c>tauri signer sign</c> does. Never the release key.</summary>
internal sealed class ReleaseSigner
{
    private readonly Ed25519PrivateKeyParameters _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly byte[] _id = RandomNumberGenerator.GetBytes(8);

    internal string PublicKey => Convert.ToBase64String([(byte)'E', (byte)'d', .. _id, .. _key.GeneratePublicKey().GetEncoded()]);

    internal byte[] Sign(byte[] data)
    {
        var digest = new Blake2bDigest(512);
        digest.BlockUpdate(data, 0, data.Length);
        var hash = new byte[64];
        digest.DoFinal(hash, 0);
        var raw = Raw(hash);
        const string comment = "file:x";
        var global = Raw([.. raw, .. Encoding.UTF8.GetBytes(comment)]);
        var text = "untrusted comment: test\n" + Convert.ToBase64String([(byte)'E', (byte)'D', .. _id, .. raw]) + "\n" +
            "trusted comment: " + comment + "\n" + Convert.ToBase64String(global) + "\n";
        return Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
    }

    private byte[] Raw(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }
}
