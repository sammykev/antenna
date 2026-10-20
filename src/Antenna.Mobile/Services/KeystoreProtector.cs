using System.Text;
using Android.Security.Keystore;
using Antenna.Core.Library;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Antenna.Mobile.Services;

/// <summary>
/// Encrypts secrets (playlist passwords) with an AES key held in the Android Keystore, so they can't be
/// read from a copy of the database. The Windows app uses DPAPI for the same job.
/// </summary>
internal sealed class KeystoreProtector : ISecretProtector
{
    private const string Alias = "antenna.secrets";
    private const string Transformation = "AES/GCM/NoPadding";

    public string Protect(string plaintext)
    {
        using var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, Key());
        var iv = cipher.GetIV()!;
        var sealedBytes = cipher.DoFinal(Encoding.UTF8.GetBytes(plaintext))!;
        // [iv length][iv][ciphertext + tag]
        var blob = new byte[1 + iv.Length + sealedBytes.Length];
        blob[0] = (byte)iv.Length;
        iv.CopyTo(blob, 1);
        sealedBytes.CopyTo(blob, 1 + iv.Length);
        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string ciphertext)
    {
        var blob = Convert.FromBase64String(ciphertext);
        int ivLength = blob[0];
        using var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, blob[1..(1 + ivLength)]));
        return Encoding.UTF8.GetString(cipher.DoFinal(blob[(1 + ivLength)..])!);
    }

    private static IKey Key()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(Alias, null) is { } existing)
            return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }
}
