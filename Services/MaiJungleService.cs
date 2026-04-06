using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using VoiceTimerBot.Interfaces;

namespace VoiceTimerBot.Services;

public class MaiJungleService(
    GatewayClient gatewayClient,
    IOptions<VoiceTimerSettings> options,
    ILogger<MaiJungleService> logger)
    : IMaiJungleService, IAsyncDisposable
{
    private static readonly byte[] SilencePcmFrame = new byte[3840];
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(1);

    private readonly VoiceTimerSettings _settings = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private VoiceClient? _voiceClient;
    private Stream? _voiceStream;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Func<ValueTask>? _voiceCloseHandler;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            logger.LogInformation("MaiJungle: Joining voice channel (guild={GuildId}, channel={ChannelId})",
                _settings.GuildId, _settings.ChannelId);

            _voiceClient = await gatewayClient.JoinVoiceChannelAsync(
                _settings.GuildId,
                _settings.ChannelId,
                new VoiceClientConfiguration(),
                cancellationToken);

            var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _voiceClient.Ready += () =>
            {
                readyTcs.TrySetResult();
                return ValueTask.CompletedTask;
            };

            try
            {
                await _voiceClient.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("VoiceClient.StartAsync did not complete within 20 s.");
            }

            if (!readyTcs.Task.IsCompleted)
            {
                try { await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (TimeoutException) { logger.LogWarning("MaiJungle: Ready event did not fire after StartAsync — proceeding anyway"); }
            }

            await _voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: cancellationToken);

            _voiceStream = _voiceClient.CreateVoiceStream(new VoiceStreamConfiguration { NormalizeSpeed = true });

            _voiceCloseHandler = OnVoiceClientClosedAsync;
            _voiceClient.Close += _voiceCloseHandler;

            _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);

            logger.LogInformation("MaiJungle: Loop started");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MaiJungle: StartAsync failed");
            await StopCoreAsync();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* cancellation or timeout — expected */ }
            _loopTask = null;
        }

        if (_voiceStream is not null)
        {
            try { await _voiceStream.DisposeAsync(); } catch { /* ignore */ }
            _voiceStream = null;
        }

        var hadClient = _voiceClient is not null;

        if (_voiceClient is not null)
        {
            if (_voiceCloseHandler is not null)
                _voiceClient.Close -= _voiceCloseHandler;
            try { _voiceClient.Dispose(); } catch { /* ignore */ }
            _voiceClient = null;
        }

        _voiceCloseHandler = null;

        if (!hadClient) return;

        try
        {
            await gatewayClient.UpdateVoiceStateAsync(new VoiceStateProperties(_settings.GuildId, null));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MaiJungle: Failed to send voice leave state to Discord");
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await PlayClipAsync(_settings.MaiJungle, ct);
                await Task.Delay(RepeatInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop via StopAsync.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MaiJungle: Loop encountered an unhandled error");
        }
    }

    private ValueTask OnVoiceClientClosedAsync()
    {
        if (_cts is null || _cts.IsCancellationRequested) return ValueTask.CompletedTask;
        logger.LogWarning("MaiJungle: Voice connection closed unexpectedly — scheduling reconnect");
        _ = Task.Run(ReconnectVoiceAsync);
        return ValueTask.CompletedTask;
    }

    private async Task ReconnectVoiceAsync()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;

        await _lock.WaitAsync();
        try
        {
            if (_cts is null || _cts.IsCancellationRequested) return;
            var token = _cts.Token;

            logger.LogInformation("MaiJungle: Re-establishing voice connection after disconnect");

            if (_voiceStream is not null)
            {
                try { await _voiceStream.DisposeAsync(); } catch { /* ignore */ }
                _voiceStream = null;
            }

            if (_voiceClient is not null)
            {
                try { _voiceClient.Dispose(); } catch { /* ignore */ }
                _voiceClient = null;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);

            _voiceClient = await gatewayClient.JoinVoiceChannelAsync(
                _settings.GuildId, _settings.ChannelId, new VoiceClientConfiguration(), token);

            var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _voiceClient.Ready += () =>
            {
                readyTcs.TrySetResult();
                return ValueTask.CompletedTask;
            };

            await _voiceClient.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), token);

            if (!readyTcs.Task.IsCompleted)
            {
                try { await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
                catch (TimeoutException) { logger.LogWarning("MaiJungle: Ready event did not fire after voice reconnect"); }
            }

            await _voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: token);

            _voiceStream = _voiceClient.CreateVoiceStream(new VoiceStreamConfiguration { NormalizeSpeed = true });

            _voiceCloseHandler = OnVoiceClientClosedAsync;
            _voiceClient.Close += _voiceCloseHandler;

            logger.LogInformation("MaiJungle: Voice reconnected successfully");
        }
        catch (OperationCanceledException)
        {
            // Stopped during reconnect.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MaiJungle: Failed to reconnect voice");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SendSilenceFramesAsync(int count, CancellationToken ct)
    {
        if (_voiceStream is null) return;
        await using var enc = new OpusEncodeStream(
            _voiceStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio,
            new OpusEncodeStreamConfiguration { FrameDuration = 20.0f }, leaveOpen: true);
        for (var i = 0; i < count; i++)
            await enc.WriteAsync(SilencePcmFrame, ct);
    }

    private async Task PlayClipAsync(string filePath, CancellationToken ct)
    {
        if (_voiceStream is null)
        {
            logger.LogWarning("MaiJungle: Skipping clip — voice stream is null");
            return;
        }

        if (!File.Exists(filePath))
        {
            logger.LogWarning("MaiJungle: Audio file not found, skipping: {FilePath}", filePath);
            return;
        }

        if (_voiceClient is not null)
            await _voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: ct);

        await SendSilenceFramesAsync(5, ct);

        Process ffmpeg;
        try
        {
            ffmpeg = Process.Start(new ProcessStartInfo
            {
                FileName = _settings.FfmpegPath,
                Arguments = $"-hide_banner -loglevel error -i \"{filePath}\" -af volume=2 -ac 2 -ar 48000 -f s16le pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MaiJungle: Failed to start FFmpeg for {FilePath}", filePath);
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
                var stderrTask = ffmpeg.StandardError.ReadToEndAsync(CancellationToken.None);

                try
                {
                    await using var encodeStream = new OpusEncodeStream(
                        _voiceStream,
                        PcmFormat.Short,
                        VoiceChannels.Stereo,
                        OpusApplication.Audio,
                        new OpusEncodeStreamConfiguration { FrameDuration = 20.0f },
                        leaveOpen: true);

                    await ffmpeg.StandardOutput.BaseStream.CopyToAsync(encodeStream, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MaiJungle: Error while streaming audio from {FilePath}", filePath);
                }
                finally
                {
                    try { await ffmpeg.WaitForExitAsync(CancellationToken.None); } catch { /* ignore */ }
                    var stderr = await stderrTask;
                    if (!string.IsNullOrWhiteSpace(stderr))
                        logger.LogWarning("MaiJungle: FFmpeg stderr for {FilePath}: {Stderr}", filePath, stderr);
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
        await StopCoreAsync();
        _lock.Dispose();
    }
}
