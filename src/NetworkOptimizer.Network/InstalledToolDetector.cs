using System.Diagnostics;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Network;

public static class InstalledToolDetector
{
    private static readonly ToolSpec[] Specs =
    {
        new("clash", "Clash / Clash Meta / Clash Verge", new[] { "clash", "clash-meta", "Clash for Windows", "clash-verge", "ClashVerge", "verge-mihomo" }, new[] { 7890, 7891, 7892 }),
        new("v2ray", "v2ray / v2rayN", new[] { "v2ray", "v2rayN", "wv2ray" }, new[] { 10808, 10809 }),
        new("xray", "Xray", new[] { "xray", "Xray" }, new[] { 10808, 10809 }),
        new("singbox", "sing-box / NekoRay", new[] { "sing-box", "singbox", "nekobox", "nekoray" }, new[] { 2080, 7890, 1080 }),
        new("shadowsocks", "Shadowsocks", new[] { "ss-local", "shadowsocks", "Shadowsocks" }, new[] { 1080 }),
        new("hysteria", "Hysteria", new[] { "hysteria", "hysteria2", "hy-client" }, new[] { 1080, 10808 }),
        new("byedpi", "ByeDPI / ciadpi", new[] { "ciadpi", "byedpi", "ByeDPI" }, new[] { 1080, 10809 }),
        new("goodbyedpi", "GoodbyeDPI", new[] { "goodbyedpi", "GoodbyeDPI" }, Array.Empty<int>()),
        new("zapret", "zapret / winws", new[] { "winws", "zapret", "blockcheck" }, Array.Empty<int>()),
        new("tor", "Tor", new[] { "tor", "firefox" }, new[] { 9050, 9150 }),
        new("outline", "Outline", new[] { "Outline", "ss-local" }, new[] { 2080, 1080 }),
        new("warp", "Cloudflare WARP", new[] { "warp-svc", "CloudflareWARP", "warp" }, Array.Empty<int>()),
        new("openvpn", "OpenVPN", new[] { "openvpn", "OpenVPNConnect" }, Array.Empty<int>()),
        new("wireguard", "WireGuard", new[] { "wireguard", "wg" }, Array.Empty<int>()),
        new("hiddify", "Hiddify", new[] { "Hiddify", "hiddify" }, new[] { 12334, 20170 }),
        new("proxifier", "Proxifier", new[] { "Proxifier" }, Array.Empty<int>())
    };

    public static IReadOnlyList<InstalledTool> Detect(IReadOnlyList<int> listeningPorts)
    {
        var processes = SafeGetProcesses();
        var result = new List<InstalledTool>();
        foreach (var spec in Specs)
        {
            var match = processes.FirstOrDefault(p => spec.ProcessNames.Any(n =>
                p.Equals(n, StringComparison.OrdinalIgnoreCase)));
            var present = match is not null;
            var ports = spec.DefaultPorts.Where(listeningPorts.Contains).ToArray();
            result.Add(new InstalledTool
            {
                Id = spec.Id,
                Name = spec.Name,
                Present = present,
                ProcessName = match,
                ListeningPorts = ports
            });
        }

        return result;
    }

    private static IReadOnlyList<string> SafeGetProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return ""; }
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private sealed record ToolSpec(string Id, string Name, string[] ProcessNames, int[] DefaultPorts);
}
