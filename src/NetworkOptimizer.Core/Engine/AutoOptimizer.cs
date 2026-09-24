namespace NetworkOptimizer.Core;

public sealed class AutoOptimizer
{
    private readonly IReadOnlyList<INetworkStrategy> _strategies;
    private readonly IDiscoveryService _discovery;
    private readonly IConnectivityProbe _probe;
    private readonly INetworkConfigurationStore _store;
    private readonly IStateStore _state;
    private readonly AppConfiguration _config;
    private readonly IAppLog _log;
    private readonly CandidateGenerator _generator;

    public AutoOptimizer(
        IReadOnlyList<INetworkStrategy> strategies,
        IDiscoveryService discovery,
        IConnectivityProbe probe,
        INetworkConfigurationStore store,
        IStateStore state,
        AppConfiguration config,
        IAppLog? log = null)
    {
        _strategies = strategies;
        _discovery = discovery;
        _probe = probe;
        _store = store;
        _state = state;
        _config = config;
        _log = log ?? NullLog.Instance;
        _generator = new CandidateGenerator(config, _log);
    }

    public async Task<OptimizationResult> RunAsync(
        IProgress<OptimizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var attempts = new List<CandidateAttempt>();
        CandidateAttempt? bestSuccess = null;

        progress?.Report(new OptimizationProgress { Phase = "snapshot", Tag = "SNAP", Message = "Creating network snapshot...", Status = OperationStatus.Applying });
        var original = await _store.CaptureAsync(cancellationToken);
        await _state.SaveOriginalSnapshotAsync(original, cancellationToken);
        _log.Info("snapshot captured");
        progress?.Report(new OptimizationProgress { Phase = "snapshot", Tag = "SNAP", Message = "Snapshot saved. Original settings can be restored at any time.", Status = OperationStatus.Success });

        try
        {
            progress?.Report(new OptimizationProgress { Phase = "probe-current", Tag = "TEST", Message = "Testing current connection — YouTube + Discord (DNS, TCP, TLS, HTTP)", Status = OperationStatus.Testing });
            CombinedProbeResult currentProbe;
            try
            {
                currentProbe = await ProbeWithTimeoutAsync(ProbeTransport.SystemDefault, cancellationToken, progress, null, 0, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"current probe error: {ex.Message}");
                currentProbe = FailedProbe("Current connection", ex.Message);
            }

            progress?.Report(new OptimizationProgress
            {
                Phase = "probe-current",
                Tag = currentProbe.IsFullSuccess ? "OK" : "FAIL",
                Message = $"Current path: YouTube {(currentProbe.YouTubeOk ? "OK" : "fail")} {currentProbe.YouTube.TotalMs} ms · Discord {(currentProbe.DiscordOk ? "OK" : "fail")} {currentProbe.Discord.TotalMs} ms",
                Status = currentProbe.IsFullSuccess ? OperationStatus.Success : OperationStatus.Failed,
                LiveProbe = currentProbe
            });

            progress?.Report(new OptimizationProgress
            {
                Phase = "discover",
                Tag = "DISC",
                Message = "Discovering adapters, routes, proxy ports, and local tools...",
                Status = OperationStatus.Testing,
                LiveProbe = currentProbe
            });

            var discovery = await _discovery.DiscoverAsync(cancellationToken);
            foreach (var aware in _strategies.OfType<IDiscoveryAware>())
            {
                aware.Bind(discovery);
            }

            _log.Info($"discovery: adapters={discovery.Adapters.Count} ipv4={discovery.HasIPv4} ipv6={discovery.HasIPv6} vpn={discovery.HasVpnOrTunnel} localProxies={discovery.LocalProxies.Count} tools={discovery.Tools.Count(t => t.Present)} admin={discovery.IsAdministrator}");
            progress?.Report(new OptimizationProgress
            {
                Phase = "discover",
                Tag = "DISC",
                Message = $"Found {discovery.Adapters.Count} adapters · IPv4 {(discovery.HasIPv4 ? "yes" : "no")} · IPv6 {(discovery.HasIPv6 ? "yes" : "no")} · VPN {(discovery.HasVpnOrTunnel ? "yes" : "no")} · proxies {discovery.LocalProxies.Count(p => p.HandshakeOk)} · tools {discovery.Tools.Count(t => t.Present)}",
                Status = OperationStatus.Success,
                LiveProbe = currentProbe
            });
            foreach (var strategy in _strategies)
            {
                var available = strategy.IsAvailable();
                var reason = strategy is IDescribedStrategy d ? d.AvailabilityReason : null;
                progress?.Report(new OptimizationProgress
                {
                    Phase = "discover",
                    Tag = available ? "OK" : "SKIP",
                    Message = available
                        ? $"Strategy ready: {strategy.Name}"
                        : $"Strategy skipped: {strategy.Name} — {reason ?? "UNAVAILABLE"}",
                    Status = available ? OperationStatus.Available : OperationStatus.Unavailable
                });
            }

            var failedStrategyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var promising = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var stage1 = _generator.Generate(_strategies, discovery, stage: 1);
            progress?.Report(new OptimizationProgress
            {
                Phase = "generate",
                Tag = "GEN",
                Message = $"Stage 1 queued {stage1.Count} candidates. Applying one at a time.",
                Status = OperationStatus.Testing,
                Total = Math.Max(stage1.Count, 1)
            });
            var all = new List<Candidate>(stage1);

            // Current/direct is always first if present; otherwise we still tested current above.
            var totalEstimate = Math.Max(all.Count, 1);
            var index = 0;

            async Task<bool> RunListAsync(IReadOnlyList<Candidate> list)
            {
                totalEstimate = Math.Max(totalEstimate, index + list.Count);
                foreach (var candidate in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;
                    var attempt = await EvaluateCandidateAsync(
                        candidate,
                        index,
                        totalEstimate,
                        original,
                        progress,
                        cancellationToken);
                    attempts.Add(attempt);

                    if (attempt.Result == OperationStatus.Failed || attempt.Result == OperationStatus.Timeout ||
                        attempt.Result == OperationStatus.Unavailable)
                    {
                        if (attempt.Probe is null || attempt.Probe.IsFailed)
                        {
                            failedStrategyIds.Add(candidate.StrategyId);
                        }
                    }

                    if (attempt.Result is OperationStatus.Success or OperationStatus.Partial)
                    {
                        failedStrategyIds.Remove(candidate.StrategyId);
                        promising.Add(candidate.StrategyId);
                    }

                    if (attempt.Result == OperationStatus.Success)
                    {
                        if (bestSuccess is null || attempt.Score.Total > bestSuccess.Score.Total)
                        {
                            bestSuccess = attempt;
                        }

                        if (_config.Search.Mode == SearchMode.Fast)
                        {
                            return true;
                        }
                    }
                }

                return bestSuccess is not null && _config.Search.Mode == SearchMode.Fast;
            }

            var done = await RunListAsync(stage1);
            if (!done)
            {
                var skip = new HashSet<string>(failedStrategyIds, StringComparer.OrdinalIgnoreCase);
                skip.ExceptWith(promising);
                var stage2 = _generator.Generate(_strategies, discovery, stage: 2, skip)
                    .GroupBy(c => c.StrategyId)
                    .SelectMany(g => g.Take(_config.Search.MaxStage2PerStrategy))
                    .ToList();
                _log.Info($"stage 2 candidates: {stage2.Count}");
                progress?.Report(new OptimizationProgress
                {
                    Phase = "generate",
                    Tag = "GEN",
                    Message = stage2.Count == 0
                        ? "Stage 2 empty — no extra parameters to try."
                        : $"Stage 2 queued {stage2.Count} deeper candidates.",
                    Status = OperationStatus.Testing,
                    Total = index + stage2.Count
                });
                await RunListAsync(stage2);
            }

            if (bestSuccess is not null && bestSuccess.Result == OperationStatus.Success)
            {
                if (bestSuccess.RolledBack)
                {
                    progress?.Report(new OptimizationProgress
                    {
                        Phase = "apply-winner",
                        Message = "Re-applying the best configuration...",
                        Status = OperationStatus.Applying,
                        Candidate = bestSuccess.Candidate
                    });
                    var winnerStrategy = _strategies.FirstOrDefault(s => s.Id == bestSuccess.Candidate.StrategyId);
                    if (winnerStrategy is null)
                    {
                        await SafeRestoreOriginalAsync(original);
                        return new OptimizationResult
                        {
                            Success = false,
                            Attempts = attempts,
                            Discovery = discovery,
                            Message = "Winning strategy is no longer available. Original settings restored."
                        };
                    }

                    var reapply = await winnerStrategy.ApplyAsync(bestSuccess.Candidate, cancellationToken);
                    if (!reapply.Applied)
                    {
                        await SafeRestoreOriginalAsync(original);
                        return new OptimizationResult
                        {
                            Success = false,
                            Attempts = attempts,
                            Discovery = discovery,
                            Message = "Could not re-apply the best configuration. Original settings restored."
                        };
                    }
                }

                var working = new WorkingConfiguration
                {
                    StrategyId = bestSuccess.Candidate.StrategyId,
                    StrategyName = bestSuccess.Candidate.StrategyName,
                    Candidate = bestSuccess.Candidate,
                    YouTubeLatencyMs = bestSuccess.Probe?.YouTube.TotalMs ?? 0,
                    DiscordLatencyMs = bestSuccess.Probe?.Discord.TotalMs ?? 0,
                    Score = bestSuccess.Score.Total
                };
                await _state.SaveWorkingAsync(working, CancellationToken.None);
                await _state.ClearPendingAsync(CancellationToken.None);
                _log.Info($"SUCCESS strategy={working.StrategyName} candidate={working.Candidate.DisplayName} score={working.Score}");

                progress?.Report(new OptimizationProgress
                {
                    Phase = "success",
                    Tag = "OK",
                    Message = "WORKING CONFIGURATION FOUND",
                    Status = OperationStatus.Success,
                    Candidate = bestSuccess.Candidate,
                    LiveProbe = bestSuccess.Probe,
                    CurrentIndex = bestSuccess.Index,
                    Total = Math.Max(totalEstimate, attempts.Count)
                });

                return new OptimizationResult
                {
                    Success = true,
                    WinningCandidate = bestSuccess.Candidate,
                    WinningProbe = bestSuccess.Probe,
                    WinningScore = bestSuccess.Score,
                    Attempts = attempts,
                    Discovery = discovery,
                    Message = "WORKING CONFIGURATION FOUND"
                };
            }

            progress?.Report(new OptimizationProgress
            {
                Phase = "rollback-original",
                Message = "Restoring original network configuration...",
                Status = OperationStatus.RollingBack
            });
            await SafeRestoreOriginalAsync(original);

            var partial = attempts
                .Where(a => a.Result == OperationStatus.Partial)
                .OrderByDescending(a => a.Score.Total)
                .FirstOrDefault();

            return new OptimizationResult
            {
                Success = false,
                WinningCandidate = partial?.Candidate,
                WinningProbe = partial?.Probe,
                WinningScore = partial?.Score,
                Attempts = attempts,
                Discovery = discovery,
                Message = partial is null
                    ? "No working configuration found. Original settings restored."
                    : "Only a partial configuration was found. Original settings restored."
            };
        }
        catch (OperationCanceledException)
        {
            _log.Info("search cancelled — restoring original snapshot");
            await SafeRestoreOriginalAsync(original);
            progress?.Report(new OptimizationProgress
            {
                Phase = "stopped",
                Tag = "STOP",
                Message = "Stopped. Original configuration restored.",
                Status = OperationStatus.Failed
            });
            return new OptimizationResult
            {
                Cancelled = true,
                Success = false,
                Attempts = attempts,
                Message = "Stopped. Original configuration restored."
            };
        }
        catch (Exception ex)
        {
            _log.Error("optimizer failed", ex);
            await SafeRestoreOriginalAsync(original);
            return new OptimizationResult
            {
                Success = false,
                Attempts = attempts,
                Message = "Search failed. Original configuration restored."
            };
        }
    }

    public async Task RestoreOriginalAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _state.LoadOriginalSnapshotAsync(cancellationToken)
                       ?? await _store.CaptureAsync(cancellationToken);
        await _store.RestoreAsync(snapshot, cancellationToken);
        foreach (var strategy in _strategies)
        {
            try
            {
                await strategy.RollbackAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warn($"strategy rollback {strategy.Id}: {ex.Message}");
            }
        }

        await _state.ClearPendingAsync(cancellationToken);
        _log.Info("original configuration restored");
    }

    private async Task<CandidateAttempt> EvaluateCandidateAsync(
        Candidate candidate,
        int index,
        int total,
        NetworkSnapshot original,
        IProgress<OptimizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var strategy = _strategies.FirstOrDefault(s => s.Id == candidate.StrategyId);
        if (strategy is null)
        {
            return new CandidateAttempt
            {
                Index = index,
                Total = total,
                Candidate = candidate,
                ApplyStatus = OperationStatus.Unavailable,
                Result = OperationStatus.Unavailable,
                Reason = "Strategy not registered",
                Duration = DateTime.UtcNow - started
            };
        }

        _log.Info($"candidate {index}/{total} strategy={candidate.StrategyName} name={candidate.DisplayName} apply");
        progress?.Report(new OptimizationProgress
        {
            Phase = "apply",
            Tag = "APPLY",
            CurrentIndex = index,
            Total = total,
            Candidate = candidate,
            Status = OperationStatus.Applying,
            Message = $"[{index}/{total}] Applying {candidate.StrategyName} — {candidate.DisplayName}"
        });

        await _state.SavePendingAsync(new PendingOperation
        {
            StrategyId = candidate.StrategyId,
            CandidateId = candidate.Id,
            CandidateName = candidate.DisplayName,
            SnapshotFile = "original-snapshot.json"
        }, cancellationToken);

        ApplyResult apply;
        try
        {
            using var applyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            applyCts.CancelAfter(TimeSpan.FromSeconds(_config.Timeouts.CandidateSeconds));
            apply = await strategy.ApplyAsync(candidate, applyCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            apply = ApplyResult.Timeout("Apply timed out");
        }
        catch (OperationCanceledException)
        {
            await RollbackCandidateAsync(strategy, original);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"apply failed strategy={candidate.StrategyName}", ex);
            apply = ApplyResult.Fail(UserReason(ex));
        }

        if (!apply.Applied)
        {
            _log.Info($"candidate {index} apply={apply.Status.ToLabel()} reason={apply.Reason}");
            progress?.Report(new OptimizationProgress
            {
                Phase = "apply",
                Tag = "SKIP",
                CurrentIndex = index,
                Total = total,
                Candidate = candidate,
                Status = apply.Status,
                Message = $"Apply skipped: {candidate.DisplayName} — {apply.Reason ?? apply.Status.ToLabel()}"
            });
            await _state.ClearPendingAsync(CancellationToken.None);
            try { await strategy.RollbackAsync(CancellationToken.None); } catch { /* ignore */ }
            return new CandidateAttempt
            {
                Index = index,
                Total = total,
                Candidate = candidate,
                ApplyStatus = apply.Status,
                Result = apply.Status,
                Reason = apply.Reason,
                Duration = DateTime.UtcNow - started
            };
        }

        if (_config.Timeouts.StabilizationMilliseconds > 0)
        {
            progress?.Report(new OptimizationProgress
            {
                Phase = "stabilize",
                Tag = "WAIT",
                CurrentIndex = index,
                Total = total,
                Candidate = candidate,
                Status = OperationStatus.Testing,
                Message = $"Waiting {_config.Timeouts.StabilizationMilliseconds} ms for the network to settle..."
            });
            try
            {
                await Task.Delay(_config.Timeouts.StabilizationMilliseconds, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await RollbackCandidateAsync(strategy, original);
                throw;
            }
        }

        progress?.Report(new OptimizationProgress
        {
            Phase = "test",
            Tag = "TEST",
            CurrentIndex = index,
            Total = total,
            Candidate = candidate,
            Status = OperationStatus.Testing,
            Message = $"[{index}/{total}] Testing YouTube + Discord through {candidate.DisplayName}"
        });

        CombinedProbeResult probe;
        var stable = false;
        try
        {
            probe = await ProbeWithTimeoutAsync(apply.Transport, cancellationToken, progress, candidate, index, total);
            if (probe.IsFullSuccess && _config.Search.RepeatSuccessProbes > 1)
            {
                CombinedProbeResult? last = probe;
                var allOk = true;
                for (var i = 1; i < _config.Search.RepeatSuccessProbes; i++)
                {
                    progress?.Report(new OptimizationProgress
                    {
                        Phase = "test",
                        Tag = "TEST",
                        CurrentIndex = index,
                        Total = total,
                        Candidate = candidate,
                        Status = OperationStatus.Testing,
                        Message = $"Repeat probe {i + 1}/{_config.Search.RepeatSuccessProbes} for stability..."
                    });
                    var again = await ProbeWithTimeoutAsync(apply.Transport, cancellationToken, progress, candidate, index, total);
                    last = MergeWorse(probe, again);
                    if (!again.IsFullSuccess)
                    {
                        allOk = false;
                        probe = again;
                        break;
                    }
                }

                if (allOk)
                {
                    stable = true;
                    probe = last ?? probe;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new OptimizationProgress
            {
                Phase = "rollback",
                Tag = "ROLL",
                CurrentIndex = index,
                Total = total,
                Candidate = candidate,
                Status = OperationStatus.RollingBack,
                Message = "Rolling back..."
            });
            await RollbackCandidateAsync(strategy, original);
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"probe error candidate={candidate.DisplayName}: {ex.Message}");
            probe = FailedProbe(candidate.DisplayName, UserReason(ex));
        }

        progress?.Report(new OptimizationProgress
        {
            Phase = "test",
            CurrentIndex = index,
            Total = total,
            Candidate = candidate,
            Status = OperationStatus.Testing,
            LiveProbe = probe,
            Message = probe.IsFullSuccess ? "Both services reachable." : "Evaluating result..."
        });

        var result = Scorer.Classify(probe);
        var score = Scorer.Score(probe, stable);
        _log.Info($"candidate {index} result={result.ToLabel()} yt={probe.YouTubeOk} dc={probe.DiscordOk} score={score.Total} ytMs={probe.YouTube.TotalMs} dcMs={probe.Discord.TotalMs}");

        var keep = result == OperationStatus.Success && _config.Search.Mode == SearchMode.Fast;
        if (keep)
        {
            await _state.ClearPendingAsync(CancellationToken.None);
            return new CandidateAttempt
            {
                Index = index,
                Total = total,
                Candidate = candidate,
                ApplyStatus = OperationStatus.Success,
                Result = result,
                Probe = probe,
                Score = score,
                Duration = DateTime.UtcNow - started,
                RolledBack = false
            };
        }

        if (result == OperationStatus.Success && _config.Search.Mode == SearchMode.Best)
        {
            // Keep this working config only if it is the best so far; otherwise roll back and continue.
            // Caller tracks best; we roll back here so the next candidate starts clean, then re-apply winner at the end.
            progress?.Report(new OptimizationProgress
            {
                Phase = "rollback",
                Tag = "ROLL",
                CurrentIndex = index,
                Total = total,
                Candidate = candidate,
                Status = OperationStatus.RollingBack,
                LiveProbe = probe,
                Message = "Recording success, rolling back to try more candidates..."
            });
            await RollbackCandidateAsync(strategy, original);
            return new CandidateAttempt
            {
                Index = index,
                Total = total,
                Candidate = candidate,
                ApplyStatus = OperationStatus.Success,
                Result = result,
                Probe = probe,
                Score = score,
                Duration = DateTime.UtcNow - started,
                RolledBack = true
            };
        }

        progress?.Report(new OptimizationProgress
        {
            Phase = "rollback",
            Tag = "ROLL",
            CurrentIndex = index,
            Total = total,
            Candidate = candidate,
            Status = OperationStatus.RollingBack,
            LiveProbe = probe,
            Message = "Rolling back this candidate and restoring the snapshot..."
        });
        await RollbackCandidateAsync(strategy, original);
        _log.Info($"candidate {index} rollback done");

        return new CandidateAttempt
        {
            Index = index,
            Total = total,
            Candidate = candidate,
            ApplyStatus = OperationStatus.Success,
            Result = result,
            Probe = probe,
            Score = score,
            Reason = probe.IsFailed ? SummarizeFailure(probe) : null,
            Duration = DateTime.UtcNow - started,
            RolledBack = true
        };
    }

    private async Task RollbackCandidateAsync(INetworkStrategy strategy, NetworkSnapshot original)
    {
        try
        {
            await strategy.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warn($"strategy rollback failed {strategy.Id}: {ex.Message}");
        }

        try
        {
            await _store.RestoreAsync(original, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error("store restore failed", ex);
        }

        try
        {
            await _state.ClearPendingAsync(CancellationToken.None);
        }
        catch
        {
            // ignore
        }
    }

    private async Task SafeRestoreOriginalAsync(NetworkSnapshot original)
    {
        foreach (var strategy in _strategies)
        {
            try { await strategy.RollbackAsync(CancellationToken.None); }
            catch (Exception ex) { _log.Warn($"final strategy rollback {strategy.Id}: {ex.Message}"); }
        }

        try { await _store.RestoreAsync(original, CancellationToken.None); }
        catch (Exception ex) { _log.Error("final store restore failed", ex); }

        try { await _state.ClearPendingAsync(CancellationToken.None); }
        catch { /* ignore */ }
    }

    private async Task<CombinedProbeResult> ProbeWithTimeoutAsync(
        ProbeTransport transport,
        CancellationToken ct,
        IProgress<OptimizationProgress>? progress,
        Candidate? candidate,
        int index,
        int total)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.Timeouts.CandidateSeconds));
        var stepped = transport.WithStep(msg => progress?.Report(new OptimizationProgress
        {
            Phase = "probe-step",
            Tag = "TEST",
            Message = msg,
            Status = OperationStatus.Testing,
            Candidate = candidate,
            CurrentIndex = index,
            Total = total
        }));
        try
        {
            return await _probe.ProbeAsync(stepped, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            progress?.Report(new OptimizationProgress
            {
                Phase = "probe-step",
                Tag = "FAIL",
                Message = "Probe timed out",
                Status = OperationStatus.Timeout,
                Candidate = candidate,
                CurrentIndex = index,
                Total = total
            });
            return FailedProbe("timeout", "Candidate timed out");
        }
    }

    private static CombinedProbeResult MergeWorse(CombinedProbeResult a, CombinedProbeResult b) =>
        new()
        {
            YouTube = a.YouTube.TotalMs >= b.YouTube.TotalMs ? a.YouTube : b.YouTube,
            Discord = a.Discord.TotalMs >= b.Discord.TotalMs ? a.Discord : b.Discord
        };

    private static CombinedProbeResult FailedProbe(string service, string reason) =>
        new()
        {
            YouTube = new ServiceProbeResult
            {
                Service = "YouTube",
                Http = LayerResult.Fail(reason)
            },
            Discord = new ServiceProbeResult
            {
                Service = "Discord",
                Http = LayerResult.Fail(reason)
            }
        };

    private static string SummarizeFailure(CombinedProbeResult probe)
    {
        var yt = probe.YouTube.Http.Reason ?? probe.YouTube.Tls.Reason ?? probe.YouTube.Tcp.Reason ?? probe.YouTube.Dns.Reason;
        var dc = probe.Discord.Http.Reason ?? probe.Discord.Tls.Reason ?? probe.Discord.Tcp.Reason ?? probe.Discord.Dns.Reason;
        if (!string.IsNullOrWhiteSpace(yt) && !string.IsNullOrWhiteSpace(dc) && yt != dc)
        {
            return $"YouTube: {yt}; Discord: {dc}";
        }

        return yt ?? dc ?? "Connection failed";
    }

    private static string UserReason(Exception ex) => ex switch
    {
        TimeoutException => "Timed out",
        OperationCanceledException => "Timed out",
        HttpRequestException http => string.IsNullOrWhiteSpace(http.Message) ? "HTTP request failed" : TrimReason(http.Message),
        System.Net.Sockets.SocketException sock => sock.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.ConnectionRefused => "Connection refused",
            System.Net.Sockets.SocketError.TimedOut => "Timed out",
            System.Net.Sockets.SocketError.HostUnreachable => "Host unreachable",
            System.Net.Sockets.SocketError.NetworkUnreachable => "Network unreachable",
            _ => "Connection failed"
        },
        UnauthorizedAccessException => "Administrator permission required",
        _ => "Operation failed"
    };

    private static string TrimReason(string message)
    {
        if (message.Length <= 140) return message;
        return message[..137] + "...";
    }
}
