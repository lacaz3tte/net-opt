using NetworkOptimizer.Core;
using NetworkOptimizer.Network;
using Xunit;

namespace NetworkOptimizer.Tests;

public sealed class ProbeAndProxyTests
{
    [Fact]
    public void Log_sanitizes_secrets()
    {
        var text = FileAppLog.Sanitize("Authorization: Bearer abcdef.token.secret cookie=abc password=hunter2");
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abcdef.token.secret", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void WinHttp_parser_reads_direct_and_proxy()
    {
        var direct = WindowsProxyNative.ParseWinHttp("Current WinHTTP proxy settings:\r\n    Direct access (no proxy server).\r\n");
        Assert.False(direct.Enabled);
        var proxyParsed = WindowsProxyNative.ParseWinHttp("Proxy Server : 127.0.0.1:7890");
        Assert.True(proxyParsed.Enabled);
        Assert.Equal("127.0.0.1:7890", proxyParsed.Server);
    }
}
