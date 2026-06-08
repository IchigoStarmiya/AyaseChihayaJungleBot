using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using VoiceTimerBot.Interfaces;

namespace VoiceTimerBot.Services;

public class VoiceTimerService(
    GatewayClient gatewayClient,
    IOptions<VoiceTimerSettings> options,
    ILogger<VoiceTimerService> logger)
    : IVoiceTimerService, IAsyncDisposable
{
    // 20 ms of silence at 48 kHz stereo 16-bit PCM (48000 * 2 channels * 2 bytes * 0.02 s).
    private static readonly byte[] SilencePcmFrame = new byte[3840];

    // This guild gets absolute priority for contended resources: it jumps to the front of the
    // voice-join queue and bypasses the FFmpeg decode concurrency cap, so a synchronized start by
    // many other guilds can never delay its join or its clip playback.
    private const ulong PriorityGuildId = 1315399824010514563UL;

    // Serialize all JoinVoiceChannelAsync calls across guilds to avoid flooding the gateway
    // with simultaneous VoiceStateUpdate handshakes when multiple guilds start at the same time.
    // A PriorityGate (not a plain SemaphoreSlim) so PriorityGuildId is served before any normal
    // guild already waiting in line.
    private readonly PriorityGate _voiceJoinGate = new(1);

    // #1: Cap concurrent FFmpeg decode processes across all guilds. Without this, a synchronized
    // cold start (many guilds hitting the same spawn warning at once) could spawn one FFmpeg per
    // guild simultaneously — each ~20-40 MB RSS — which OOMs a small VPS long before CPU saturates.
    // Only cache misses pass through here, so in steady state this is rarely contended.
    private const int MaxConcurrentFfmpeg = 4;
    private readonly SemaphoreSlim _ffmpegGate = new(MaxConcurrentFfmpeg, MaxConcurrentFfmpeg);

    // #2: Decoded-PCM cache. Each distinct (file, loudnorm) variant is decoded by FFmpeg exactly
    // once into 48 kHz/stereo/s16le PCM; every subsequent play streams the cached bytes directly,
    // with no per-play FFmpeg process. This removes the decode CPU spike and the process-RAM storm
    // when many guilds fire a warning at the same instant — loudnorm runs once per file, not per play.
    private readonly Dictionary<(string Path, bool SkipLoudnorm), byte[]> _pcmCache = new();
    private readonly Dictionary<(string Path, bool SkipLoudnorm), SemaphoreSlim> _pcmDecodeLocks = new();
    private readonly object _pcmCacheGuard = new();

    private static readonly TimeSpan TotalDuration = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan SpawnInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstSpawnElapsed = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Warn60s = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Warn40s = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan Warn20s = TimeSpan.FromSeconds(20);
    
    private static readonly TimeSpan ZealInterval = TimeSpan.FromMinutes(3);
    
    private const int ZealReminderCount = 7;

    // Only this guild gets the extra 60-second spawn warning.
    private const ulong Warn60GuildId = 1434117124833411215UL;

    private readonly VoiceTimerSettings _settings = options.Value;
    private readonly Dictionary<ulong, GuildTimerState> _guilds =
        options.Value.Servers.ToDictionary(s => s.GuildId, s => new GuildTimerState(s));

    public bool IsRunning(ulong guildId) =>
        _guilds.TryGetValue(guildId, out var state) && state.IsRunning;

    public bool IsConfigured(ulong guildId) => _guilds.ContainsKey(guildId);

    public async Task StartAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var state = _guilds[guildId];

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(state);

            // Give Discord time to process the voice-leave before immediately rejoining.
            // Without this, the VoiceStateUpdate + VoiceServerUpdate events that
            // JoinVoiceChannelAsync waits for may never arrive and the call times out.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

            state.Cts = new CancellationTokenSource();
            var token = state.Cts.Token;

            logger.LogInformation("VoiceTimer: [1] Sending voice join to gateway (guild={GuildId}, channel={ChannelId})",
                state.Settings.GuildId, state.Settings.ChannelId);

            await _voiceJoinGate.WaitAsync(guildId == PriorityGuildId, cancellationToken);
            try
            {
                state.VoiceClient = await gatewayClient.JoinVoiceChannelAsync(
                    state.Settings.GuildId,
                    state.Settings.ChannelId,
                    new VoiceClientConfiguration(),
                    cancellationToken);
            }
            finally
            {
                _voiceJoinGate.Release();
            }

            logger.LogInformation("VoiceTimer: [2] JoinVoiceChannelAsync returned — calling StartAsync (guild={GuildId})", state.Settings.GuildId);

            // Track whether Ready fires during or after StartAsync.
            var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.VoiceClient.Ready += () =>
            {
                logger.LogInformation("VoiceTimer: [Ready] event fired (guild={GuildId})", state.Settings.GuildId);
                readyTcs.TrySetResult();
                return ValueTask.CompletedTask;
            };

            // Await StartAsync directly (as shown in official docs).
            // Wrap it in a 20-second timeout so a silent hang surfaces as a real error.
            try
            {
                await state.VoiceClient.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    "VoiceClient.StartAsync did not complete within 20 s. " +
                    "The bot likely cannot reach Discord's voice servers — check outbound UDP/WSS connectivity.");
            }

            logger.LogInformation("VoiceTimer: [3] StartAsync returned (guild={GuildId})", state.Settings.GuildId);

            // If Ready has not fired yet, wait up to 5 more seconds for it.
            if (!readyTcs.Task.IsCompleted)
            {
                logger.LogInformation("VoiceTimer: [4] Ready not yet fired — waiting up to 5 s (guild={GuildId})", state.Settings.GuildId);
                try
                {
                    await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("VoiceTimer: Ready event did not fire after StartAsync — proceeding anyway (guild={GuildId})", state.Settings.GuildId);
                }
            }
            else
            {
                logger.LogInformation("VoiceTimer: [4] Ready had already fired during StartAsync (guild={GuildId})", state.Settings.GuildId);
            }

            logger.LogInformation("VoiceTimer: [5] Entering speaking state (guild={GuildId})", state.Settings.GuildId);

            await state.VoiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: cancellationToken);

            logger.LogInformation("VoiceTimer: [6] Creating voice stream and starting timer loop (guild={GuildId})", state.Settings.GuildId);

            state.VoiceStream = state.VoiceClient.CreateVoiceStream(new VoiceStreamConfiguration
            {
                NormalizeSpeed = true
            });

            state.VoiceCloseHandler = () => OnVoiceClientClosedAsync(state);
            state.VoiceClient.Close += state.VoiceCloseHandler;

            state.TimerTask = Task.Run(() => RunLoopAsync(state, token), CancellationToken.None);

            logger.LogInformation("VoiceTimer: [6] Timer loop started (guild={GuildId})", state.Settings.GuildId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VoiceTimer: StartAsync failed (guild={GuildId})", guildId);
            await StopCoreAsync(state);
            throw;
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public async Task StopAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var state = _guilds[guildId];

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(state);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private async Task StopCoreAsync(GuildTimerState state)
    {
        if (state.Cts is not null)
        {
            await state.Cts.CancelAsync();
            state.Cts.Dispose();
            state.Cts = null;
        }

        if (state.TimerTask is not null)
        {
            try { await state.TimerTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* cancellation or timeout — expected */ }
            state.TimerTask = null;
        }

        if (state.VoiceStream is not null)
        {
            try { await state.VoiceStream.DisposeAsync(); } catch { /* ignore */ }
            state.VoiceStream = null;
        }

        var hadClient = state.VoiceClient is not null;

        if (state.VoiceClient is not null)
        {
            if (state.VoiceCloseHandler is not null)
                state.VoiceClient.Close -= state.VoiceCloseHandler;
            try { state.VoiceClient.Dispose(); } catch { /* ignore */ }
            state.VoiceClient = null;
        }

        state.VoiceCloseHandler = null;

        if (!hadClient)
            return;

        try
        {
            await gatewayClient.UpdateVoiceStateAsync(new VoiceStateProperties(state.Settings.GuildId, null));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VoiceTimer: Failed to send voice leave state to Discord (guild={GuildId})", state.Settings.GuildId);
        }
    }

    private async Task RunLoopAsync(GuildTimerState state, CancellationToken ct)
    {
        var guildId = state.Settings.GuildId;
        try
        {
            await PlayClipResilientAsync(state, state.Settings.StartClipPath, TimeSpan.FromSeconds(30), ct);

            // Monotonic clock — immune to wall-clock adjustments (NTP, DST, VM time skew)
            // that DateTimeOffset.UtcNow would expose us to over a 25-minute window.
            var clock = Stopwatch.StartNew();

            // The jungle warnings and the zeal reminder share this guild's single voice stream, so
            // we merge them into one time-ordered schedule and play each cue in turn. A guild with no
            // zeal clip contributes no zeal cues, leaving only the jungle timer.
            foreach (var (target, label, clipPath) in BuildSchedule(state.Settings))
            {
                await SendSilenceUntilAsync(state, clock, target, ct);
                LogDrift(label, clock, target, guildId);
                // Cap retry budget so a slow reconnect can't push one cue past the next slot.
                await PlayClipResilientAsync(state, clipPath, TimeSpan.FromSeconds(10), ct);
            }

            // Natural end after last jungle spawn — wait for Discord's jitter buffer to drain before disconnecting.
            logger.LogInformation("VoiceTimer: Last jungle spawn warned, waiting for audio to finish (guild={GuildId})", guildId);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            logger.LogInformation("VoiceTimer: Disconnecting (guild={GuildId})", guildId);
            await CleanupVoiceAsync(state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop via StopAsync.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VoiceTimer: Timer loop encountered an unhandled error (guild={GuildId})", guildId);
        }
    }

    // Builds the full, time-ordered list of cues to play for one session: the jungle spawn warnings
    // (60s only for the opted-in guild, plus 40s and 20s) on the 5-minute spawn cycle, merged with the
    // "upgrade zeal" GvG reminder on its 3-minute cadence. Targets are elapsed offsets from the start
    // clip. When two cues land on the same instant the jungle warning wins the tie so the time-critical
    // spawn cue is never delayed behind the zeal reminder.
    private static List<(TimeSpan Target, string Label, string ClipPath)> BuildSchedule(VoiceTimerGuildSettings s)
    {
        var events = new List<(TimeSpan Target, int Tie, string Label, string ClipPath)>();

        for (var spawnElapsed = FirstSpawnElapsed; spawnElapsed <= TotalDuration; spawnElapsed += SpawnInterval)
        {
            if (s.GuildId == Warn60GuildId)
                events.Add((spawnElapsed - Warn60s, 0, "warn60", s.Warn60s));
            events.Add((spawnElapsed - Warn40s, 0, "warn40", s.Warn40s));
            events.Add((spawnElapsed - Warn20s, 0, "warn20", s.Warn20s));
        }

        if (!string.IsNullOrWhiteSpace(s.ZealClipPath))
        {
            // Exactly ZealReminderCount cues at 3-minute steps: 3:00, 6:00, … 21:00.
            for (var i = 1; i <= ZealReminderCount; i++)
                events.Add((ZealInterval * i, 1, "zeal", s.ZealClipPath));
        }

        return events
            .OrderBy(e => e.Target)
            .ThenBy(e => e.Tie)
            .Select(e => (e.Target, e.Label, e.ClipPath))
            .ToList();
    }

    // Replaces a passive Task.Delay between clips. NetCord's NormalizeSpeed paces these writes in real
    // time, producing one Opus packet every 20 ms — the cadence Discord expects. Long idle gaps over UDP
    // let intermediaries (NAT, host conntrack, the voice gateway itself) consider the path dead and
    // tear it down, which manifests as the bot suddenly being silent or disconnected mid-session.
    // Streaming silence also surfaces a broken connection within a couple of seconds (a write throws),
    // letting the reconnect retry start immediately instead of waiting for the next scheduled clip.
    private async Task SendSilenceUntilAsync(GuildTimerState state, Stopwatch clock, TimeSpan target, CancellationToken ct)
    {
        var guildId = state.Settings.GuildId;

        while (clock.Elapsed < target)
        {
            ct.ThrowIfCancellationRequested();

            var voiceStream = state.VoiceStream;
            if (voiceStream is null)
            {
                // Voice is down — wait briefly for reconnect to publish a fresh stream, then retry.
                await WaitForVoiceReadyAsync(state, TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                await using var encodeStream = new OpusEncodeStream(
                    voiceStream,
                    PcmFormat.Short,
                    VoiceChannels.Stereo,
                    OpusApplication.Audio,
                    new OpusEncodeStreamConfiguration { FrameDuration = 20.0f },
                    leaveOpen: true);

                while (clock.Elapsed < target)
                {
                    ct.ThrowIfCancellationRequested();
                    await encodeStream.WriteAsync(SilencePcmFrame, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "VoiceTimer: Silence keepalive interrupted, waiting for voice (guild={GuildId})", guildId);
                await WaitForVoiceReadyAsync(state, TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private void LogDrift(string label, Stopwatch clock, TimeSpan target, ulong guildId)
    {
        var driftMs = (long)(clock.Elapsed - target).TotalMilliseconds;
        logger.LogInformation(
            "VoiceTimer: Firing {Label} elapsed={Elapsed:c} target={Target:c} drift={DriftMs}ms (guild={GuildId})",
            label, clock.Elapsed, target, driftMs, guildId);
    }

    private async Task CleanupVoiceAsync(GuildTimerState state)
    {
        if (state.VoiceStream is not null)
        {
            try { await state.VoiceStream.DisposeAsync(); } catch { /* ignore */ }
            state.VoiceStream = null;
        }

        if (state.VoiceClient is not null)
        {
            if (state.VoiceCloseHandler is not null)
                state.VoiceClient.Close -= state.VoiceCloseHandler;
            try { state.VoiceClient.Dispose(); } catch { /* ignore */ }
            state.VoiceClient = null;
        }

        state.VoiceCloseHandler = null;

        try
        {
            await gatewayClient.UpdateVoiceStateAsync(new VoiceStateProperties(state.Settings.GuildId, null));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VoiceTimer: Failed to send voice leave state to Discord (guild={GuildId})", state.Settings.GuildId);
        }
    }

    private ValueTask OnVoiceClientClosedAsync(GuildTimerState state)
    {
        if (state.Cts is null || state.Cts.IsCancellationRequested) return ValueTask.CompletedTask;
        // Prevent multiple concurrent reconnect tasks from stacking when the connection
        // closes and re-closes rapidly before the first reconnect finishes.
        if (Interlocked.CompareExchange(ref state.IsReconnecting, 1, 0) != 0) return ValueTask.CompletedTask;
        logger.LogWarning("VoiceTimer: Voice connection closed unexpectedly (guild={GuildId}) — scheduling reconnect", state.Settings.GuildId);
        _ = Task.Run(() => ReconnectVoiceAsync(state));
        return ValueTask.CompletedTask;
    }

    private async Task ReconnectVoiceAsync(GuildTimerState state)
    {
        const int maxAttempts = 5;
        var backoff = TimeSpan.FromSeconds(2);

        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (state.Cts is null || state.Cts.IsCancellationRequested) return;

                try
                {
                    await TryReconnectOnceAsync(state, attempt, maxAttempts);
                    return; // success
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "VoiceTimer: Reconnect attempt {Attempt}/{Max} failed (guild={GuildId})",
                        attempt, maxAttempts, state.Settings.GuildId);
                }

                if (attempt < maxAttempts)
                {
                    var ctsToken = state.Cts?.Token ?? CancellationToken.None;
                    try { await Task.Delay(backoff, ctsToken); }
                    catch (OperationCanceledException) { return; }
                    backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
                }
            }

            logger.LogError("VoiceTimer: All {Max} reconnect attempts failed — voice will stay disconnected (guild={GuildId})",
                maxAttempts, state.Settings.GuildId);
        }
        finally
        {
            // Cleared only after the entire retry sequence ends so OnVoiceClientClosedAsync
            // doesn't spawn a parallel reconnect mid-retry.
            Volatile.Write(ref state.IsReconnecting, 0);
        }
    }

    private async Task TryReconnectOnceAsync(GuildTimerState state, int attempt, int max)
    {
        await state.Lock.WaitAsync();
        try
        {
            if (state.Cts is null || state.Cts.IsCancellationRequested)
                throw new OperationCanceledException();
            var token = state.Cts.Token;

            logger.LogInformation("VoiceTimer: Reconnect attempt {Attempt}/{Max} (guild={GuildId})",
                attempt, max, state.Settings.GuildId);

            if (state.VoiceStream is not null)
            {
                try { await state.VoiceStream.DisposeAsync(); } catch { /* ignore */ }
                state.VoiceStream = null;
            }

            if (state.VoiceClient is not null)
            {
                // Handler already fired — it's closed, no need to unsubscribe
                try { state.VoiceClient.Dispose(); } catch { /* ignore */ }
                state.VoiceClient = null;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);

            await _voiceJoinGate.WaitAsync(state.Settings.GuildId == PriorityGuildId, token);
            try
            {
                state.VoiceClient = await gatewayClient.JoinVoiceChannelAsync(
                    state.Settings.GuildId, state.Settings.ChannelId, new VoiceClientConfiguration(), token);
            }
            finally
            {
                _voiceJoinGate.Release();
            }

            var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.VoiceClient.Ready += () =>
            {
                logger.LogInformation("VoiceTimer: [Reconnect][Ready] fired (guild={GuildId})", state.Settings.GuildId);
                readyTcs.TrySetResult();
                return ValueTask.CompletedTask;
            };

            await state.VoiceClient.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), token);

            if (!readyTcs.Task.IsCompleted)
            {
                try { await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
                catch (TimeoutException) { logger.LogWarning("VoiceTimer: Ready event did not fire after voice reconnect (guild={GuildId})", state.Settings.GuildId); }
            }

            await state.VoiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: token);

            state.VoiceStream = state.VoiceClient.CreateVoiceStream(new VoiceStreamConfiguration { NormalizeSpeed = true });

            state.VoiceCloseHandler = () => OnVoiceClientClosedAsync(state);
            state.VoiceClient.Close += state.VoiceCloseHandler;

            logger.LogInformation("VoiceTimer: Voice reconnected successfully on attempt {Attempt}/{Max} (guild={GuildId})",
                attempt, max, state.Settings.GuildId);
        }
        catch
        {
            // Tear down any partial voice state from this failed attempt so the next attempt
            // (and the resilient clip player) sees a clean null-state instead of a half-connected client.
            if (state.VoiceStream is not null)
            {
                try { await state.VoiceStream.DisposeAsync(); } catch { /* ignore */ }
                state.VoiceStream = null;
            }
            if (state.VoiceClient is not null)
            {
                try { state.VoiceClient.Dispose(); } catch { /* ignore */ }
                state.VoiceClient = null;
            }
            throw;
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private async Task PlayClipResilientAsync(GuildTimerState state, string filePath, TimeSpan reconnectWait, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var guildId = state.Settings.GuildId;

        // File-existence is checked once up front: a missing file is non-retriable, no reason to spin.
        if (!File.Exists(filePath))
        {
            logger.LogWarning("VoiceTimer: Audio file not found, skipping: {FilePath} (guild={GuildId})", filePath, guildId);
            return;
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) return;

            var played = await PlayClipAsync(state, filePath, ct);
            if (played) return;

            if (attempt < maxAttempts)
            {
                logger.LogWarning(
                    "VoiceTimer: Clip {FilePath} did not complete (attempt {Attempt}/{Max}) — waiting up to {Wait}s for voice (guild={GuildId})",
                    filePath, attempt, maxAttempts, (int)reconnectWait.TotalSeconds, guildId);
                await WaitForVoiceReadyAsync(state, reconnectWait, ct);
            }
        }

        logger.LogError("VoiceTimer: Clip {FilePath} did not play after {Max} attempts (guild={GuildId})",
            filePath, maxAttempts, guildId);
    }

    private static async Task WaitForVoiceReadyAsync(GuildTimerState state, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return;
            if (state.VoiceStream is not null
                && state.VoiceClient is not null
                && Volatile.Read(ref state.IsReconnecting) == 0)
                return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> PlayClipAsync(GuildTimerState state, string filePath, CancellationToken ct)
    {
        var guildId = state.Settings.GuildId;

        // Snapshot both references before any await so a concurrent ReconnectVoiceAsync cannot
        // dispose+replace them between our null-check and our first write to the stream.
        var voiceStream = state.VoiceStream;
        var voiceClient = state.VoiceClient;

        if (voiceStream is null)
        {
            logger.LogWarning("VoiceTimer: Skipping clip — voice stream is null for {FilePath} (guild={GuildId})", filePath, guildId);
            return false;
        }

        // Decode-on-first-use, then serve from the in-memory cache. The expensive FFmpeg + loudnorm
        // work happens at most once per (file, loudnorm) variant; every later play is a memory read.
        var skipLoudnorm = guildId is 1315399824010514563UL or 1501672515058012250UL;
        var pcm = await GetOrDecodePcmAsync(filePath, skipLoudnorm, guildId, ct);
        if (pcm is null || pcm.Length == 0)
        {
            logger.LogWarning("VoiceTimer: No decoded audio available for {FilePath} (guild={GuildId})", filePath, guildId);
            return false;
        }

        if (voiceClient is not null)
            await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: ct);

        try
        {
            // One encoder per clip — covers both the silence prefix and the audio body.
            // Avoids the previous design's two encoder lifecycles per clip, which churned
            // Opus encoder state and added unnecessary CPU + GC pressure between guilds.
            await using var encodeStream = new OpusEncodeStream(
                voiceStream,
                PcmFormat.Short,
                VoiceChannels.Stereo,
                OpusApplication.Audio,
                new OpusEncodeStreamConfiguration { FrameDuration = 20.0f },
                leaveOpen: true);

            // Silence prefix gives Discord's jitter buffer a head start and prevents the
            // previous transmission's Opus tail from interpolating into this clip.
            for (var i = 0; i < 5; i++)
                await encodeStream.WriteAsync(SilencePcmFrame, ct);

            // MemoryStream + CopyToAsync mirrors the previous ffmpeg-stdout path exactly, so the
            // encoder's framing and NormalizeSpeed's real-time pacing behave identically.
            using var pcmStream = new MemoryStream(pcm, writable: false);
            await pcmStream.CopyToAsync(encodeStream, ct);

            logger.LogInformation(
                "VoiceTimer: Finished streaming {FilePath} ({Bytes} PCM bytes) (guild={GuildId})",
                filePath, pcm.Length, guildId);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VoiceTimer: Error while streaming audio from {FilePath} (guild={GuildId})", filePath, guildId);
            return false;
        }
    }

    // Returns cached PCM for the variant, decoding it via FFmpeg on the first request. A per-key
    // lock ensures a single decode even if many guilds request the same variant simultaneously on a
    // cold start; the global _ffmpegGate caps how many *distinct* decodes run at once. Returns null
    // if decoding fails (e.g. FFmpeg missing or a corrupt file), so the caller can retry/skip.
    private async Task<byte[]?> GetOrDecodePcmAsync(string filePath, bool skipLoudnorm, ulong guildId, CancellationToken ct)
    {
        var key = (filePath, skipLoudnorm);

        lock (_pcmCacheGuard)
        {
            if (_pcmCache.TryGetValue(key, out var hit))
                return hit;
        }

        SemaphoreSlim keyLock;
        lock (_pcmCacheGuard)
        {
            // Re-check under the lock in case another caller finished the decode while we waited.
            if (_pcmCache.TryGetValue(key, out var hit))
                return hit;
            if (!_pcmDecodeLocks.TryGetValue(key, out keyLock!))
            {
                keyLock = new SemaphoreSlim(1, 1);
                _pcmDecodeLocks[key] = keyLock;
            }
        }

        await keyLock.WaitAsync(ct);
        try
        {
            lock (_pcmCacheGuard)
            {
                if (_pcmCache.TryGetValue(key, out var hit))
                    return hit;
            }

            var pcm = await DecodeToPcmAsync(filePath, skipLoudnorm, guildId, ct);
            if (pcm is not null)
            {
                lock (_pcmCacheGuard)
                {
                    _pcmCache[key] = pcm;
                }
            }
            return pcm;
        }
        finally
        {
            keyLock.Release();
        }
    }

    private async Task<byte[]?> DecodeToPcmAsync(string filePath, bool skipLoudnorm, ulong guildId, CancellationToken ct)
    {
        // The priority guild bypasses the concurrency cap entirely so it never waits behind other
        // guilds' decodes. Its RunLoop plays clips sequentially, so this adds at most one extra
        // concurrent FFmpeg process — negligible RAM against the headroom the cap protects.
        var priority = guildId == PriorityGuildId;
        if (!priority)
            await _ffmpegGate.WaitAsync(ct);
        try
        {
            Process ffmpeg;
            try
            {
                var loudnormArg = skipLoudnorm ? "" : "-af loudnorm=I=-5 ";
                ffmpeg = Process.Start(new ProcessStartInfo
                {
                    FileName = _settings.FfmpegPath,
                    Arguments = $"-hide_banner -loglevel error -i \"{filePath}\" {loudnormArg}-ac 2 -ar 48000 -f s16le pipe:1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }) ?? throw new InvalidOperationException("Process.Start returned null.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "VoiceTimer: Failed to start FFmpeg to decode {FilePath} (guild={GuildId})", filePath, guildId);
                return null;
            }

            try
            {
                await using var cancelKill = ct.Register(() =>
                {
                    try { ffmpeg.Kill(entireProcessTree: true); }
                    catch { /* process already exited */ }
                });

                // Drain stderr in the background to prevent FFmpeg blocking on a full pipe buffer.
                var stderrTask = ffmpeg.StandardError.ReadToEndAsync(CancellationToken.None);

                using var pcmBuffer = new MemoryStream();
                await ffmpeg.StandardOutput.BaseStream.CopyToAsync(pcmBuffer, ct);

                try { await ffmpeg.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), ct); } catch { /* ignore */ }

                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                    logger.LogWarning("VoiceTimer: FFmpeg stderr decoding {FilePath}: {Stderr} (guild={GuildId})", filePath, stderr, guildId);

                if (ffmpeg.HasExited && ffmpeg.ExitCode != 0)
                {
                    logger.LogError("VoiceTimer: FFmpeg exit code {ExitCode} decoding {FilePath} (guild={GuildId})", ffmpeg.ExitCode, filePath, guildId);
                    return null;
                }

                var pcm = pcmBuffer.ToArray();
                logger.LogInformation(
                    "VoiceTimer: Cached {Bytes} PCM bytes for {FilePath} (loudnorm={Loudnorm}, guild={GuildId})",
                    pcm.Length, filePath, !skipLoudnorm, guildId);
                return pcm;
            }
            finally
            {
                ffmpeg.Dispose();
            }
        }
        finally
        {
            if (!priority)
                _ffmpegGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var state in _guilds.Values)
        {
            await state.Lock.WaitAsync();
            try { await StopCoreAsync(state); }
            finally { state.Lock.Release(); }
            state.Lock.Dispose();
        }
        _ffmpegGate.Dispose();
        lock (_pcmCacheGuard)
        {
            foreach (var decodeLock in _pcmDecodeLocks.Values)
                decodeLock.Dispose();
            _pcmDecodeLocks.Clear();
            _pcmCache.Clear();
        }
    }

    private sealed class GuildTimerState(VoiceTimerGuildSettings settings)
    {
        public VoiceTimerGuildSettings Settings { get; } = settings;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public VoiceClient? VoiceClient;
        public Stream? VoiceStream;
        public CancellationTokenSource? Cts;
        public Task? TimerTask;
        public Func<ValueTask>? VoiceCloseHandler;
        public int IsReconnecting;  // 0 = idle, 1 = reconnect task in flight; use Interlocked

        public bool IsRunning => TimerTask is { IsCompleted: false };
    }

    // A capacity-limited async gate that serves waiters in priority order rather than FIFO:
    // a high-priority waiter is admitted ahead of every normal waiter already in line (but behind
    // earlier high-priority waiters). SemaphoreSlim offers no such ordering, so we maintain the
    // wait list ourselves. Permits are non-reentrant; every WaitAsync must be paired with a Release.
    private sealed class PriorityGate
    {
        private readonly object _sync = new();
        private int _available;
        private readonly LinkedList<(TaskCompletionSource Tcs, bool High)> _waiters = new();

        public PriorityGate(int capacity) => _available = capacity;

        public Task WaitAsync(bool highPriority, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            lock (_sync)
            {
                if (_available > 0)
                {
                    _available--;
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                LinkedListNode<(TaskCompletionSource Tcs, bool High)> node;
                if (highPriority)
                {
                    // Insert ahead of the first normal waiter, preserving order among high-priority ones.
                    var cursor = _waiters.First;
                    while (cursor is not null && cursor.Value.High)
                        cursor = cursor.Next;
                    node = cursor is null ? _waiters.AddLast((tcs, true)) : _waiters.AddBefore(cursor, (tcs, true));
                }
                else
                {
                    node = _waiters.AddLast((tcs, false));
                }

                if (ct.CanBeCanceled)
                {
                    var registration = ct.Register(() =>
                    {
                        lock (_sync)
                        {
                            if (node.List is not null)
                                _waiters.Remove(node);
                        }
                        tcs.TrySetCanceled(ct);
                    });
                    // Dispose the registration once the wait settles, regardless of outcome.
                    tcs.Task.ContinueWith(
                        static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                        registration, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                return tcs.Task;
            }
        }

        public void Release()
        {
            lock (_sync)
            {
                while (_waiters.First is not null)
                {
                    var node = _waiters.First;
                    _waiters.RemoveFirst();
                    // RunContinuationsAsynchronously keeps the awaiter's continuation off this thread,
                    // so we never run user code while holding _sync. A cancelled waiter fails TrySetResult
                    // and we hand the permit to the next one instead.
                    if (node.Value.Tcs.TrySetResult())
                        return;
                }
                _available++;
            }
        }
    }
}
