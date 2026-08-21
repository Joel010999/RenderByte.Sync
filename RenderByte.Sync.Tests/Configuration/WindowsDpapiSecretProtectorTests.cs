using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RenderByte.Sync.Agent.Configuration;
using Xunit;

namespace RenderByte.Sync.Tests.Configuration;

[SupportedOSPlatform("windows")]
public class WindowsDpapiSecretProtectorTests
{
    [Fact]
    public void WindowsDpapiSecretProtector_RoundTrip_LocalMachine()
    {
        // Skip on non-Windows
        if (!OperatingSystem.IsWindows()) return;

        var protector = new WindowsDpapiSecretProtector();
        var plaintext = "super-secret-password-123";
        
        var protectedText = protector.Protect(plaintext);
        
        Assert.NotEqual(plaintext, protectedText);
        Assert.NotEmpty(protectedText);

        var unprotectedText = protector.Unprotect(protectedText);
        Assert.Equal(plaintext, unprotectedText);
    }
}
