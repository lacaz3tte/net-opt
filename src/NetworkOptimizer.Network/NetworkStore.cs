using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Network;

public sealed class NetworkStoreFactory
{
    public static INetworkConfigurationStore Create() =>
        OperatingSystem.IsWindows() ? new WindowsNetworkStore() : new PortableNetworkStore();
}

public sealed class PortableNetworkStore : INetworkConfigurationStore
{
    private NetworkSnapshot? _lastApplied;
    private ProxySettings _internet = ProxySettings.Disabled;
    private readonly Dictionary<string, IReadOnlyList<string>> _dns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _metrics = new(StringComparer.OrdinalIgnoreCase);

    public bool IsWindows => false;
    public bool IsAdministrator => false;

    public Task<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = GetAdapters();
        return Task.FromResult(new NetworkSnapshot
        {
            InternetSettings = Clone(_internet),
            WinHttp = ProxySettings.Disabled,
            ProxyEnvironment = GetProxyEnvironment().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            Dns = adapters.Select(a => new AdapterDnsSettings
            {
                InterfaceId = a.Id,
                Name = a.Name,
                DnsServers = a.DnsServers
            }).ToList(),
            InterfaceMetrics = new Dictionary<string, int>(_metrics, StringComparer.OrdinalIgnoreCase),
            Notes = "portable"
        });
    }

    public Task RestoreAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _internet = Clone(snapshot.InternetSettings);
        _dns.Clear();
        foreach (var d in snapshot.Dns)
        {
            _dns[d.InterfaceId] = d.DnsServers;
        }

        _metrics.Clear();
        foreach (var kv in snapshot.InterfaceMetrics)
        {
            _metrics[kv.Key] = kv.Value;
        }

        _lastApplied = snapshot;
        return Task.CompletedTask;
    }

    public Task ApplyInternetProxyAsync(ProxySettings proxy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _internet = Clone(proxy);
        return Task.CompletedTask;
    }

    public Task ApplyDnsAsync(string interfaceId, string interfaceName, IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _dns[interfaceId] = servers.ToArray();
        return Task.CompletedTask;
    }

    public Task ApplyInterfaceMetricAsync(string interfaceName, int metric, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _metrics[interfaceName] = metric;
        return Task.CompletedTask;
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var list = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = nic.GetIPProperties();
            var ipv4 = nic.Supports(NetworkInterfaceComponent.IPv4);
            var ipv6 = nic.Supports(NetworkInterfaceComponent.IPv6);
            var type = Classify(nic);
            list.Add(new NetworkAdapterInfo
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = nic.Description,
                Type = type,
                Operational = nic.OperationalStatus == OperationalStatus.Up,
                SupportsIPv4 = ipv4,
                SupportsIPv6 = ipv6,
                IsLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                IsVpnOrTunnel = type is "vpn" or "tunnel",
                UnicastAddresses = props.UnicastAddresses.Select(a => a.Address.ToString()).ToArray(),
                Gateways = props.GatewayAddresses.Select(g => g.Address.ToString()).ToArray(),
                DnsServers = props.DnsAddresses.Select(d => d.ToString()).ToArray(),
                SpeedBps = nic.Speed > 0 ? nic.Speed : null
            });
        }

        return list;
    }

    public IReadOnlyList<RouteEntry> GetRoutes()
    {
        var routes = new List<RouteEntry>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                var gateways = nic.GetIPProperties().GatewayAddresses;
                foreach (var gw in gateways)
                {
                    routes.Add(new RouteEntry
                    {
                        Destination = gw.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "::" : "0.0.0.0",
                        MaskOrPrefix = gw.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "0" : "0.0.0.0",
                        Gateway = gw.Address.ToString(),
                        Interface = nic.Name,
                        Metric = ipv4?.Index ?? 0
                    });
                }
            }
        }
        catch
        {
            // ignore
        }

        return routes;
    }

    public ProxySettings GetInternetSettingsProxy() => Clone(_internet);
    public ProxySettings GetWinHttpProxy() => ProxySettings.Disabled;

    public IReadOnlyDictionary<string, string> GetProxyEnvironment()
    {
        var keys = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value) && !map.ContainsKey(key.ToUpperInvariant()))
            {
                map[key.ToUpperInvariant()] = value;
            }
        }

        return map;
    }

    public IReadOnlyList<int> GetLoopbackListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(ep => IPAddress.IsLoopback(ep.Address) || Equals(ep.Address, IPAddress.Any) || Equals(ep.Address, IPAddress.IPv6Any))
                .Select(ep => ep.Port)
                .Distinct()
                .OrderBy(p => p)
                .ToArray();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    public IReadOnlyList<InstalledTool> DetectInstalledTools() => InstalledToolDetector.Detect(GetLoopbackListeningPorts());

    public Task RefreshProxyNotificationAsync() => Task.CompletedTask;

    internal static string Classify(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return "loopback";
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "wifi";
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT)
        {
            return "ethernet";
        }

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
            nic.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
            nic.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains("TUN", StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
            nic.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
            nic.Name.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
            nic.Name.Contains("wg", StringComparison.OrdinalIgnoreCase))
        {
            return nic.NetworkInterfaceType == NetworkInterfaceType.Ppp ? "vpn" : "tunnel";
        }

        return nic.NetworkInterfaceType.ToString().ToLowerInvariant();
    }

    private static ProxySettings Clone(ProxySettings proxy) => new()
    {
        Enabled = proxy.Enabled,
        Server = proxy.Server,
        Override = proxy.Override,
        AutoDetect = proxy.AutoDetect,
        AutoConfigUrl = proxy.AutoConfigUrl,
        Source = proxy.Source
    };
}

public sealed class WindowsNetworkStore : INetworkConfigurationStore
{
    public bool IsWindows => true;
    public bool IsAdministrator => WindowsElevation.IsAdministrator();

    public Task<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = GetAdapters();
        return Task.FromResult(new NetworkSnapshot
        {
            InternetSettings = GetInternetSettingsProxy(),
            WinHttp = GetWinHttpProxy(),
            ProxyEnvironment = GetProxyEnvironment().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            Dns = adapters.Select(a => new AdapterDnsSettings
            {
                InterfaceId = a.Id,
                Name = a.Name,
                DnsServers = a.DnsServers
            }).ToList(),
            InterfaceMetrics = adapters.ToDictionary(a => a.Name, _ => 0, StringComparer.OrdinalIgnoreCase),
            Notes = "windows"
        });
    }

    public async Task RestoreAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        await ApplyInternetProxyAsync(snapshot.InternetSettings, cancellationToken);
        foreach (var dns in snapshot.Dns)
        {
            if (dns.DnsServers.Count == 0) continue;
            try
            {
                await ApplyDnsAsync(dns.InterfaceId, dns.Name, dns.DnsServers, cancellationToken);
            }
            catch
            {
                // DNS restore may require admin; continue restoring other items.
            }
        }
    }

    public async Task ApplyInternetProxyAsync(ProxySettings proxy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return;
        WindowsProxyNative.ApplyInternetSettings(proxy);
        await RefreshProxyNotificationAsync();
    }

    public Task ApplyDnsAsync(string interfaceId, string interfaceName, IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(interfaceName) || servers.Count == 0)
        {
            return Task.CompletedTask;
        }

        var primary = servers[0];
        ProcessRunner.Run("netsh", $"interface ipv4 set dns name=\"{interfaceName}\" static {primary} primary validate=no");
        for (var i = 1; i < servers.Count; i++)
        {
            ProcessRunner.Run("netsh", $"interface ipv4 add dns name=\"{interfaceName}\" {servers[i]} index={i + 1} validate=no");
        }

        return Task.CompletedTask;
    }

    public Task ApplyInterfaceMetricAsync(string interfaceName, int metric, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        ProcessRunner.Run("netsh", $"interface ipv4 set interface \"{interfaceName}\" metric={metric}");
        return Task.CompletedTask;
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters() => new PortableNetworkStore().GetAdapters();
    public IReadOnlyList<RouteEntry> GetRoutes() => new PortableNetworkStore().GetRoutes();

    public ProxySettings GetInternetSettingsProxy()
    {
        if (!OperatingSystem.IsWindows()) return ProxySettings.Disabled;
        return WindowsProxyNative.ReadInternetSettings();
    }

    public ProxySettings GetWinHttpProxy()
    {
        if (!OperatingSystem.IsWindows()) return ProxySettings.Disabled;
        var parsed = ProcessRunner.Run("netsh", "winhttp show proxy");
        return WindowsProxyNative.ParseWinHttp(parsed.StdOut);
    }

    public IReadOnlyDictionary<string, string> GetProxyEnvironment() => new PortableNetworkStore().GetProxyEnvironment();
    public IReadOnlyList<int> GetLoopbackListeningPorts() => new PortableNetworkStore().GetLoopbackListeningPorts();
    public IReadOnlyList<InstalledTool> DetectInstalledTools() => InstalledToolDetector.Detect(GetLoopbackListeningPorts());

    public Task RefreshProxyNotificationAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsProxyNative.NotifyProxyChanged();
        }

        return Task.CompletedTask;
    }
}

internal static class WindowsElevation
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

internal static class ProcessRunner
{
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return new ProcessResult(1, "", "failed to start");
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new ProcessResult(-1, "", "timed out");
            }

            return new ProcessResult(p.ExitCode, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}

internal static class WindowsProxyNative
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [SupportedOSPlatform("windows")]
    public static ProxySettings ReadInternetSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null) return ProxySettings.Disabled;
        var enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
        return new ProxySettings
        {
            Enabled = enabled,
            Server = key.GetValue("ProxyServer") as string,
            Override = key.GetValue("ProxyOverride") as string,
            AutoDetect = Convert.ToInt32(key.GetValue("AutoDetect", 0)) != 0,
            AutoConfigUrl = key.GetValue("AutoConfigURL") as string,
            Source = "internet-settings"
        };
    }

    [SupportedOSPlatform("windows")]
    public static void ApplyInternetSettings(ProxySettings proxy)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        key.SetValue("ProxyEnable", proxy.Enabled ? 1 : 0, RegistryValueKind.DWord);
        if (string.IsNullOrWhiteSpace(proxy.Server))
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyServer", proxy.Server, RegistryValueKind.String);
        }

        if (proxy.Override is null)
        {
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyOverride", proxy.Override, RegistryValueKind.String);
        }

        if (!string.IsNullOrWhiteSpace(proxy.AutoConfigUrl))
        {
            key.SetValue("AutoConfigURL", proxy.AutoConfigUrl, RegistryValueKind.String);
        }
    }

    public static void NotifyProxyChanged()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch
        {
            // ignore on non-windows
        }
    }

    public static ProxySettings ParseWinHttp(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return ProxySettings.Disabled;
        if (output.Contains("Direct access", StringComparison.OrdinalIgnoreCase))
        {
            return new ProxySettings { Enabled = false, Source = "winhttp" };
        }

        string? server = null;
        foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Contains("Proxy Server", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                if (idx >= 0) server = line[(idx + 1)..].Trim();
            }
        }

        return new ProxySettings
        {
            Enabled = !string.IsNullOrWhiteSpace(server),
            Server = server,
            Source = "winhttp"
        };
    }
}
