using System;
using System.Security.Cryptography;
using System.Text;

namespace RenderByte.Sync.Agent.Configuration;

public class WindowsDpapiSecretProtector : ISecretProtector
{
    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
#pragma warning disable CA1416
        var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.LocalMachine);
#pragma warning restore CA1416
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return protectedValue;
        var protectedBytes = Convert.FromBase64String(protectedValue);
#pragma warning disable CA1416
        var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
#pragma warning restore CA1416
        return Encoding.UTF8.GetString(plainBytes);
    }
}
