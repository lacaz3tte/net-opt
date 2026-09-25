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
/// Curated winws profiles similar to zapret blockcheck + common YouTube/Discord packs.
/// AUTO DISCOVER tries them one by one until YouTube and Discord both work.
/// </summary>
public static class ZapretProfiles
{
    public static IReadOnlyList<ZapretProfile> Build(ZapretPaths paths)
    {
        // All profiles stay in stage 1 so AUTO DISCOVER tries every variant even if early ones fail.
        var list = new List<ZapretProfile>
        {
            Profile(paths, "fake-multisplit-md5sig-badseq", "zapret: fake + multisplit midsld + md5sig,badseq", 1,
                "fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=md5sig,badseq", "--dpi-desync-repeats=8"),
            Profile(paths, "fake-multisplit-midsld", "zapret: fake + multisplit midsld + badseq", 2,
                "fake,multisplit", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=badseq", "--dpi-desync-repeats=8"),
            Profile(paths, "fake-split2-md5sig", "zapret: fake,split2 + autottl + md5sig", 3,
                "fake,split2", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig"),
            Profile(paths, "fake-split2-badseq", "zapret: fake,split2 + autottl + badseq", 4,
                "fake,split2", "--dpi-desync-autottl=2", "--dpi-desync-fooling=badseq"),
            Profile(paths, "multisplit-midsld", "zapret: multisplit midsld", 5,
                "multisplit", "--dpi-desync-split-pos=midsld"),
            Profile(paths, "multisplit-sniext", "zapret: multisplit sniext+1", 6,
                "multisplit", "--dpi-desync-split-pos=sniext+1"),
            Profile(paths, "fake-tlsmod", "zapret: fake TLS-mod + autottl", 7,
                "fake", "--dpi-desync-ttl=1", "--dpi-desync-autottl=2", "--dpi-desync-fake-tls-mod=rnd,dupsid,rndsni,padencap"),
            Profile(paths, "split2", "zapret: split2 pos=2", 8,
                "split2", "--dpi-desync-split-pos=2"),
            Profile(paths, "fake-multidisorder", "zapret: fake,multidisorder + midsld", 9,
                "fake,multidisorder", "--dpi-desync-split-pos=1,midsld", "--dpi-desync-fooling=badseq", "--dpi-desync-repeats=11"),
            Profile(paths, "fake-ttl1", "zapret: fake ttl=1", 10,
                "fake", "--dpi-desync-ttl=1", "--dpi-desync-fake-tls-mod=rnd,rndsni"),
            Profile(paths, "fake-ttl2", "zapret: fake ttl=2", 11,
                "fake", "--dpi-desync-ttl=2", "--dpi-desync-fake-tls-mod=rnd,rndsni"),
            Profile(paths, "fake-ttl4", "zapret: fake ttl=4", 12,
                "fake", "--dpi-desync-ttl=4"),
            Profile(paths, "multisplit-1-midsld", "zapret: multisplit 1,midsld", 13,
                "multisplit", "--dpi-desync-split-pos=1,midsld"),
            Profile(paths, "fake-repeats11", "zapret: fake repeats=11", 14,
                "fake", "--dpi-desync-repeats=11"),
            Profile(paths, "split2-seqovl", "zapret: split2 seqovl", 15,
                "split2", "--dpi-desync-split-seqovl=652", "--dpi-desync-split-pos=2"),
            Profile(paths, "fake-badsum", "zapret: fake + badsum", 16,
                "fake", "--dpi-desync-fooling=badsum", "--dpi-desync-autottl=2"),
            Profile(paths, "multisplit-sniext4", "zapret: multisplit sniext+4", 17,
                "multisplit", "--dpi-desync-split-pos=10,sniext+4", "--dpi-desync-split-seqovl=1")
        };

        if (paths.TlsFake is not null)
        {
            list.Add(Profile(paths, "multisplit-seqovl-pattern", "zapret: multisplit + google ClientHello pattern", 18,
                "multisplit", "--dpi-desync-split-seqovl=681", "--dpi-desync-split-pos=1",
                $"--dpi-desync-split-seqovl-pattern={paths.TlsFake}"));
        }

        return list;
    }

    private static ZapretProfile Profile(
        ZapretPaths paths,
        string id,
        string display,
        int rank,
        string tcp443Desync,
        params string[] extra)
    {
        var args = new List<string>
        {
            "--wf-tcp=80,443",
            "--wf-udp=443,19294-19344,50000-50100",
            "--filter-tcp=443",
            "--hostlist=" + paths.Hostlist,
            "--dpi-desync=" + tcp443Desync
        };
        args.AddRange(extra);
        args.Add("--new");
        args.Add("--filter-tcp=80");
        args.Add("--hostlist=" + paths.Hostlist);
        args.Add("--dpi-desync=fake,split2");
        args.Add("--dpi-desync-autottl=2");
        args.Add("--new");
        args.Add("--filter-udp=443");
        args.Add("--hostlist=" + paths.Hostlist);
        args.Add("--dpi-desync=fake");
        args.Add("--dpi-desync-repeats=6");
        if (paths.QuicFake is not null)
        {
            args.Add("--dpi-desync-fake-quic=" + paths.QuicFake);
        }

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
            Notes = tcp443Desync
        };
    }
}
