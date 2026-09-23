using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using WinTAKTracker.Services.Tak;
using Xunit;

namespace WinTAKTracker.Core.Tests;

public class ConnectFailureClassificationTests
{
    [Fact]
    public void Timeout_IsNetwork_NotTls()
    {
        Assert.False(CotStreamClient.IsTlsOrCertFailure(
            new TimeoutException("Connection timed out — server unreachable or network blocked")));
    }

    [Fact]
    public void SocketException_IsNetwork()
    {
        Assert.False(CotStreamClient.IsTlsOrCertFailure(new SocketException((int)SocketError.HostUnreachable)));
    }

    [Fact]
    public void AuthenticationException_IsTls()
    {
        Assert.True(CotStreamClient.IsTlsOrCertFailure(new AuthenticationException("TLS authentication failed")));
    }

    [Fact]
    public void CryptographicException_IsTls()
    {
        Assert.True(CotStreamClient.IsTlsOrCertFailure(new CryptographicException("bad p12")));
    }

    [Fact]
    public void MissingCertMessage_IsTls()
    {
        Assert.True(CotStreamClient.IsTlsOrCertFailure(
            new InvalidOperationException("No client certificate — enroll first")));
    }

    [Fact]
    public void TimeoutHumanText_WithoutTlsWords_StaysNetwork()
    {
        var ex = new TimeoutException();
        Assert.False(CotStreamClient.IsTlsOrCertFailure(
            ex, "Connection timed out — server unreachable or network blocked"));
    }
}
