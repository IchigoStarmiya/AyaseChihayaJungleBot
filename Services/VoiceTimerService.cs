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

    // Serialize all JoinVoiceChannelAsync calls across guilds to avoid flooding the gateway
    // with simultaneous VoiceStateUpdate handshakes when multiple guilds start at the same time.
    private readonly SemaphoreSlim _voiceJoinGate = new(1, 1);

    private static readonly TimeSpan TotalDuration = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan SpawnInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstSpawnElapsed = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Warn40s = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan Warn20s = TimeSpan.FromSeconds(20);

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

            await _voiceJoinGate.WaitAsync(cancellationToken);
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
            await PlayClipAsync(state, state.Settings.StartClipPath, ct);

            // Monotonic clock — immune to wall-clock adjustments (NTP, DST, VM time skew)
            // that DateTimeOffset.UtcNow would expose us to over a 25-minute window.
            var clock = Stopwatch.StartNew();
            var spawnElapsed = FirstSpawnElapsed;

            while (spawnElapsed <= TotalDuration)
            {
                var warn40Target = spawnElapsed - Warn40s;
                await WaitUntilAsync(clock, warn40Target, ct);
                LogDrift("warn40", clock, warn40Target, guildId);
                await PlayClipAsync(state, state.Settings.Warn40s, ct);

                var warn20Target = spawnElapsed - Warn20s;
                await WaitUntilAsync(clock, warn20Target, ct);
                LogDrift("warn20", clock, warn20Target, guildId);
                await PlayClipAsync(state, state.Settings.Warn20s, ct);

                spawnElapsed += SpawnInterval;
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

    private static async Task WaitUntilAsync(Stopwatch clock, TimeSpan target, CancellationToken ct)
    {
        var remaining = target - clock.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ct);
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
        if (state.Cts is null || state.Cts.IsCancellationRequested) return;

        await state.Lock.WaitAsync();
        try
        {
            if (state.Cts is null || state.Cts.IsCancellationRequested) return;
            var token = state.Cts.Token;

            logger.LogInformation("VoiceTimer: Re-establishing voice connection after disconnect (guild={GuildId})", state.Settings.GuildId);

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

            await _voiceJoinGate.WaitAsync(token);
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

            logger.LogInformation("VoiceTimer: Voice reconnected successfully (guild={GuildId})", state.Settings.GuildId);
        }
        catch (OperationCanceledException)
        {
            // Timer was stopped during reconnect.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VoiceTimer: Failed to reconnect voice (guild={GuildId})", state.Settings.GuildId);
        }
        finally
        {
            Volatile.Write(ref state.IsReconnecting, 0);
            state.Lock.Release();
        }
    }

    private async Task PlayClipAsync(GuildTimerState state, string filePath, CancellationToken ct)
    {
        var guildId = state.Settings.GuildId;

        // Snapshot both references before any await so a concurrent ReconnectVoiceAsync cannot
        // dispose+replace them between our null-check and our first write to the stream.
        var voiceStream = state.VoiceStream;
        var voiceClient = state.VoiceClient;

        if (voiceStream is null)
        {
            logger.LogWarning("VoiceTimer: Skipping clip — voice stream is null (guild={GuildId})", guildId);
            return;
        }

        if (!File.Exists(filePath))
        {
            logger.LogWarning("VoiceTimer: Audio file not found, skipping: {FilePath} (guild={GuildId})", filePath, guildId);
            return;
        }

        if (voiceClient is not null)
            await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: ct);

        Process ffmpeg;
        try
        {
            ffmpeg = Process.Start(new ProcessStartInfo
            {
                FileName = _settings.FfmpegPath,
                Arguments = $"-hide_banner -loglevel error -i \"{filePath}\" -af loudnorm=I=-5 -ac 2 -ar 48000 -f s16le pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VoiceTimer: Failed to start FFmpeg for {FilePath} (guild={GuildId})", filePath, guildId);
            return;
        }

        try
        {
            await using (ct.Register(() =>
            {
                try { ffmpeg.Kill(entireProcessTree: true); }
                catch { /* process already exited */ }
            }))
            {
                // Drain stderr in the background to prevent FFmpeg blocking on a full pipe buffer.
                var stderrTask = ffmpeg.StandardError.ReadToEndAsync(CancellationToken.None);

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

                    var countingStream = new ByteCountingStream(ffmpeg.StandardOutput.BaseStream);
                    await countingStream.CopyToAsync(encodeStream, ct);

                    logger.LogInformation(
                        "VoiceTimer: Finished streaming {FilePath} ({Bytes} PCM bytes) (guild={GuildId})",
                        filePath, countingStream.BytesRead, guildId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "VoiceTimer: Error while streaming audio from {FilePath} (guild={GuildId})", filePath, guildId);
                }
                finally
                {
                    try { await ffmpeg.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
                    var stderr = await stderrTask;
                    if (!string.IsNullOrWhiteSpace(stderr))
                        logger.LogWarning("VoiceTimer: FFmpeg stderr for {FilePath}: {Stderr} (guild={GuildId})", filePath, stderr, guildId);
                    logger.LogInformation("VoiceTimer: FFmpeg exit code {ExitCode} for {FilePath} (guild={GuildId})", ffmpeg.ExitCode, filePath, guildId);
                }
            }
        }
        finally
        {
            ffmpeg.Dispose();
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
        _voiceJoinGate.Dispose();
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
}

file sealed class ByteCountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        BytesRead += n;
        return n;
    }
}
