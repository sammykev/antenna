using System.Security.Cryptography;
using System.Text;
using Antenna.Core.Library;

namespace Antenna.App.Services;

/// <summary>Encrypts secrets with Windows DPAPI, so only this Windows user on this PC can read them.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Antenna.v1"u8.ToArray();

    public string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string ciphertext) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser));
}
