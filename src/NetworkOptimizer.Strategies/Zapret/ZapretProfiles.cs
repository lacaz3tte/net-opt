using NetworkOptimizer.Core;

namespace NetworkOptimizer.Strategies;

public sealed record ZapretPaths(
    string Root,
    string WinwsExe,
    string Hostlist,
    string? QuicFake,
    string? TlsFake);

public interface IZapretRuntime
{
    bool TryResolve(out ZapretPaths paths, out string? reason);
    Task StartAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    Task StopAsync();
    bool IsRunning { get; }
}

public sealed class ZapretProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required int Rank { get; init; }
    public required int Stage { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// winws profiles for AUTO DISCOVER. First group covers the usual TSPU case:
/// TCP/TLS succeed, HTTP to YouTube still fails (needs fake-tls payload, wssize,
/// fakedsplit/syndata, or desync on all 443 instead of hostlist-only).
/// </summary>
public static class ZapretProfiles
{
    public const string YoutubeDomains =
        "youtube.com,youtu.be,ytimg.com,ggpht.com,googlevideo.com,googleapis.com,gvt1.com,gvt2.com,google.com,gstatic.com,googleusercontent.com,youtube-nocookie.com,youtubekids.com,youtu.be";

    public const string DiscordDomains =
        "discord.com,discordapp.com,discord.gg,discord.media,discordapp.net";

    public static IReadOnlyList<ZapretProfile> Build(ZapretPaths paths)
    {
        var list = new List<ZapretProfile>();
        var rank = 1;

        void Add(string id, string display, IReadOnlyList<string> tcp443, Opts? opts = null) =>
            list.Add(Compose(paths, id, display, rank++, tcp443, opts ?? new Opts()));

        // Payload + split: original catalog omitted --dpi-desync-fake-tls=file, so "fake" was weak.
        Add("fake-tls-multisplit-md5sig", "zapret: fake-tls + multisplit midsld + md5sig,badseq",
            Tcp("fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=md5sig,badseq", "--dpi-desync-repeats=11"),
            new Opts { FakeTls = true });
        Add("wssize-split-badseq", "zapret: wssize 1:6 + split + badseq",
            Tcp("split", "--dpi-desync-fooling=badseq", "--wssize=1:6"));
        Add("fakedsplit-ttl4", "zapret: fakedsplit midsld ttl=4",
            Tcp("fakedsplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-ttl=4", "--dpi-desync-repeats=16"),
            new Opts { FakeTls = true });
        Add("fakeddisorder-autottl", "zapret: fakeddisorder + autottl + md5sig",
            Tcp("fakeddisorder", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig", "--dpi-desync-repeats=11"),
            new Opts { FakeTls = true });
        Add("syndata-multisplit", "zapret: syndata + multisplit midsld",
            Tcp("syndata,multisplit", "--dpi-desync-split-pos=1,midsld"),
            new Opts { FakeTls = true, FakeSyndata = true });
        Add("ipv4-fake-tls-multisplit", "zapret: IPv4-only fake-tls + multisplit",
            Tcp("fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=md5sig,badseq", "--dpi-desync-repeats=8"),
            new Opts { FakeTls = true, Ipv4Only = true });
        Add("yt-wssize-dc-seqovl", "zapret: YouTube wssize + Discord seqovl",
            Tcp("split", "--dpi-desync-fooling=badseq", "--wssize=1:6"),
            new Opts { SplitDiscordSeqovl = true, FakeTls = true });
        Add("all443-fake-tls-multisplit", "zapret: all TCP/443 fake-tls + multisplit (no hostlist)",
            Tcp("fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=badseq", "--dpi-desync-repeats=11"),
            new Opts { FakeTls = true, Hostlist = false });
        Add("fake-tls-split2-seqovl", "zapret: fake-tls + split2 seqovl",
            Tcp("fake,split2", "--dpi-desync-split-seqovl=652", "--dpi-desync-split-pos=2", "--dpi-desync-repeats=8"),
            new Opts { FakeTls = true });
        Add("ip-id-zero-fake", "zapret: ip-id=zero + fake-tls ttl=1",
            Tcp("fake", "--dpi-desync-ttl=1", "--ip-id=zero", "--dpi-desync-fake-tls-mod=rnd,rndsni"),
            new Opts { FakeTls = true });
        Add("fooling-ts", "zapret: fake,split2 + fooling=ts",
            Tcp("fake,split2", "--dpi-desync-autottl=2", "--dpi-desync-fooling=ts"),
            new Opts { FakeTls = true });
        Add("badseq-increment", "zapret: fake,split2 + badseq increment",
            Tcp("fake,split2", "--dpi-desync-fooling=badseq", "--dpi-desync-badseq-increment=2", "--dpi-desync-autottl=2"),
            new Opts { FakeTls = true });
        Add("quic-ipfrag2", "zapret: fake-tls multisplit + QUIC ipfrag2",
            Tcp("fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=badseq"),
            new Opts { FakeTls = true, QuicIpfrag = true });
        Add("http-req-split", "zapret: split HTTP request + TLS midsld",
            Tcp("multisplit", "--dpi-desync-split-pos=method+2,midsld", "--dpi-desync-split-http-req=method+2"));
        Add("cutoff-n3-fake", "zapret: fake-tls + cutoff n3",
            Tcp("fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-cutoff=n3", "--dpi-desync-repeats=8"),
            new Opts { FakeTls = true });

        // Original set, now with fake-tls payload when the mode includes fake.
        Add("fake-multisplit-md5sig-badseq", "zapret: fake + multisplit midsld + md5sig,badseq",
            Tcp("fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=md5sig,badseq", "--dpi-desync-repeats=8"),
            new Opts { FakeTls = true });
        Add("fake-split2-md5sig", "zapret: fake,split2 + autottl + md5sig",
            Tcp("fake,split2", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig"),
            new Opts { FakeTls = true });
        Add("fake-split2-badseq", "zapret: fake,split2 + autottl + badseq",
            Tcp("fake,split2", "--dpi-desync-autottl=2", "--dpi-desync-fooling=badseq"),
            new Opts { FakeTls = true });
        Add("multisplit-midsld", "zapret: multisplit midsld",
            Tcp("multisplit", "--dpi-desync-split-pos=midsld"));
        Add("multisplit-sniext", "zapret: multisplit sniext+1",
            Tcp("multisplit", "--dpi-desync-split-pos=sniext+1"));
        Add("fake-tlsmod", "zapret: fake TLS-mod + autottl",
            Tcp("fake", "--dpi-desync-ttl=1", "--dpi-desync-autottl=2", "--dpi-desync-fake-tls-mod=rnd,dupsid,rndsni,padencap"),
            new Opts { FakeTls = true });
        Add("split2-seqovl", "zapret: split2 seqovl",
            Tcp("split2", "--dpi-desync-split-seqovl=652", "--dpi-desync-split-pos=2"));
        Add("fake-multidisorder", "zapret: fake,multidisorder + midsld",
            Tcp("fake,multidisorder", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=badseq", "--dpi-desync-repeats=11"),
            new Opts { FakeTls = true });
        Add("all443-fakedsplit", "zapret: all TCP/443 fakedsplit (no hostlist)",
            Tcp("fakedsplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-ttl=4", "--dpi-desync-repeats=11"),
            new Opts { FakeTls = true, Hostlist = false });
        Add("fake-ttl1", "zapret: fake ttl=1",
            Tcp("fake", "--dpi-desync-ttl=1", "--dpi-desync-fake-tls-mod=rnd,rndsni"),
            new Opts { FakeTls = true });
        Add("fake-ttl6", "zapret: fake ttl=6",
            Tcp("fake", "--dpi-desync-ttl=6"),
            new Opts { FakeTls = true });
        Add("split2", "zapret: split2 pos=2",
            Tcp("split2", "--dpi-desync-split-pos=2"));
        Add("fake-badsum", "zapret: fake + badsum",
            Tcp("fake", "--dpi-desync-fooling=badsum", "--dpi-desync-autottl=2"),
            new Opts { FakeTls = true });
        Add("multisplit-sniext4", "zapret: multisplit sniext+4",
            Tcp("multisplit", "--dpi-desync-split-pos=10,sniext+4", "--dpi-desync-split-seqovl=1"));

        if (paths.TlsFake is not null)
        {
            Add("multisplit-seqovl-pattern", "zapret: multisplit + google ClientHello pattern",
                Tcp("multisplit", "--dpi-desync-split-seqovl=681", "--dpi-desync-split-pos=1",
                    $"--dpi-desync-split-seqovl-pattern={paths.TlsFake}"));
        }

        return list;
    }

    private sealed class Opts
    {
        public bool FakeTls { get; init; }
        public bool FakeSyndata { get; init; }
        public bool Hostlist { get; init; } = true;
        public bool Ipv4Only { get; init; }
        public bool SplitDiscordSeqovl { get; init; }
        public bool QuicIpfrag { get; init; }
    }

    private static IReadOnlyList<string> Tcp(string desync, params string[] extra)
    {
        var list = new List<string> { "--dpi-desync=" + desync };
        list.AddRange(extra);
        return list;
    }

    private static ZapretProfile Compose(
        ZapretPaths paths,
        string id,
        string display,
        int rank,
        IReadOnlyList<string> tcp443,
        Opts opts)
    {
        var args = new List<string>();
        if (opts.Ipv4Only)
        {
            args.Add("--wf-l3=ipv4");
        }

        args.Add("--wf-tcp=80,443");
        args.Add("--wf-udp=443,19294-19344,50000-50100");

        args.Add("--filter-tcp=443");
        if (opts.Hostlist)
        {
            args.Add(opts.SplitDiscordSeqovl
                ? "--hostlist-domains=" + YoutubeDomains
                : "--hostlist=" + paths.Hostlist);
        }

        args.AddRange(tcp443);
        if (opts.FakeTls && paths.TlsFake is not null &&
            !args.Any(a => a.StartsWith("--dpi-desync-fake-tls=", StringComparison.Ordinal)))
        {
            args.Add("--dpi-desync-fake-tls=" + paths.TlsFake);
        }

        if (opts.FakeSyndata && paths.TlsFake is not null)
        {
            args.Add("--dpi-desync-fake-syndata=" + paths.TlsFake);
        }

        if (opts.SplitDiscordSeqovl)
        {
            args.Add("--new");
            args.Add("--filter-tcp=443");
            args.Add("--hostlist-domains=" + DiscordDomains);
            args.Add("--dpi-desync=split2");
            args.Add("--dpi-desync-split-seqovl=652");
            args.Add("--dpi-desync-split-pos=2");
        }

        args.Add("--new");
        args.Add("--filter-tcp=80");
        if (opts.Hostlist) args.Add("--hostlist=" + paths.Hostlist);
        args.Add("--dpi-desync=fake,split2");
        args.Add("--dpi-desync-autottl=2");

        args.Add("--new");
        args.Add("--filter-udp=443");
        if (opts.Hostlist) args.Add("--hostlist=" + paths.Hostlist);
        args.Add(opts.QuicIpfrag ? "--dpi-desync=fake,ipfrag2" : "--dpi-desync=fake");
        args.Add("--dpi-desync-repeats=6");
        if (opts.QuicIpfrag) args.Add("--dpi-desync-ipfrag-pos-udp=16");
        if (paths.QuicFake is not null) args.Add("--dpi-desync-fake-quic=" + paths.QuicFake);

        args.Add("--new");
        args.Add("--filter-udp=19294-19344,50000-50100");
        args.Add("--filter-l7=discord,stun");
        args.Add("--dpi-desync=fake");
        args.Add("--dpi-desync-repeats=6");

        return new ZapretProfile
        {
            Id = id,
            DisplayName = display,
            Rank = rank,
            Stage = 1,
            Arguments = args,
            Notes = tcp443[0]
        };
    }
}
