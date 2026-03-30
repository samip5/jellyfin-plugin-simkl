using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.API.Exceptions;
using Jellyfin.Plugin.Simkl.API.Objects;
using Jellyfin.Plugin.Simkl.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Simkl.Services
{
    /// <summary>
    /// Playback progress scrobbler.
    /// </summary>
    public class PlaybackScrobbler : IHostedService
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<PlaybackScrobbler> _logger;
        private readonly SimklApi _simklApi;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Rate limiter to respect Simkl's API limits and prevent hammering.
        /// </summary>
        private readonly RateLimiter<Guid> _rateLimiter;

        /// <summary>
        /// Per-session semaphores to prevent race conditions within each session.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionSemaphores;

        /// <summary>
        /// Tracks the scrobble state per session to ensure proper API call sequence.
        /// </summary>
        private readonly ConcurrentDictionary<string, SessionScrobbleState> _sessionState;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackScrobbler"/> class.
        /// </summary>
        /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{PlaybackScrobbler}"/> interface.</param>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        public PlaybackScrobbler(
            ISessionManager sessionManager,
            ILogger<PlaybackScrobbler> logger,
            SimklApi simklApi,
            ILibraryManager libraryManager)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _simklApi = simklApi;
            _libraryManager = libraryManager;
            _sessionSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
            _sessionState = new ConcurrentDictionary<string, SessionScrobbleState>();
            _rateLimiter = new RateLimiter<Guid>(TimeSpan.FromSeconds(25));
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            return Task.CompletedTask;
        }

        private static bool CanStartScrobbling(UserConfig config, PlaybackProgressEventArgs playbackProgress)
        {
            long? runtime = playbackProgress.MediaInfo.RunTimeTicks;

            // Must have a known runtime above the configured minimum length
            if (!runtime.HasValue || runtime.Value < 60 * 10000 * config.MinLength)
            {
                return false;
            }

            return playbackProgress.MediaInfo.Type switch
            {
                BaseItemKind.Movie => config.ScrobbleMovies,
                BaseItemKind.Episode => config.ScrobbleShows,
                _ => false
            };
        }

        private static bool CanCompleteScrobble(UserConfig config, PlaybackProgressEventArgs playbackProgress)
        {
            if (!CanStartScrobbling(config, playbackProgress))
            {
                return false;
            }

            long? position = playbackProgress.PlaybackPositionTicks;
            long? runtime = playbackProgress.MediaInfo.RunTimeTicks;
            float percentageWatched = (float)(position ?? 0) / runtime!.Value * 100f;

            return percentageWatched >= config.ScrobblePercentage;
        }

        private static PlaybackProgressEventArgs CreateProgressArgsFromStop(PlaybackStopEventArgs stopArgs)
        {
            return new PlaybackProgressEventArgs
            {
                MediaInfo = stopArgs.MediaInfo,
                Session = stopArgs.Session,
                PlaybackPositionTicks = stopArgs.PlaybackPositionTicks
            };
        }

        // Sync wrapper — async void is avoided so that faults are observable.
        private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            _ = OnPlaybackProgressAsync(e).ContinueWith(
                t => _logger.LogError(t.Exception, "Unhandled exception in playback progress handler"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            _ = OnPlaybackStoppedAsync(e).ContinueWith(
                t => _logger.LogError(t.Exception, "Unhandled exception in playback stopped handler"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task OnPlaybackProgressAsync(PlaybackProgressEventArgs e)
        {
            var sessionId = e.Session.Id;
            var semaphore = _sessionSemaphores.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

            if (!await semaphore.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await HandlePlaybackStateChange(e).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task OnPlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            string? sessionId = e.Session.Id;
            SemaphoreSlim semaphore = _sessionSemaphores.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await HandlePlaybackStop(e).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
                _sessionState.TryRemove(sessionId, out _);

                if (_sessionSemaphores.TryRemove(sessionId, out SemaphoreSlim? removedSemaphore))
                {
                    removedSemaphore.Dispose();
                }
            }
        }

        private async Task HandlePlaybackStateChange(PlaybackProgressEventArgs e)
        {
            string sessionId = e.Session.Id;
            Guid userId = e.Session.UserId;
            bool isPaused = e.Session.PlayState?.IsPaused ?? false;
            SessionScrobbleState currentState = _sessionState.GetOrAdd(sessionId, SessionScrobbleState.NotStarted);
            UserConfig? userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(userId);

            if (userConfig == null || string.IsNullOrEmpty(userConfig.UserToken))
            {
                return;
            }

            if (!CanStartScrobbling(userConfig, e))
            {
                return;
            }

            switch (currentState)
            {
                case SessionScrobbleState.NotStarted:
                    if (!isPaused)
                    {
                        await SendScrobbleStart(e, userConfig).ConfigureAwait(false);
                        _sessionState[sessionId] = SessionScrobbleState.Started;
                    }

                    break;

                case SessionScrobbleState.Started:
                    if (isPaused)
                    {
                        await SendScrobblePause(e, userConfig).ConfigureAwait(false);
                        _sessionState[sessionId] = SessionScrobbleState.Paused;
                    }

                    break;

                case SessionScrobbleState.Paused:
                    if (!isPaused)
                    {
                        await SendScrobbleStart(e, userConfig).ConfigureAwait(false);
                        _sessionState[sessionId] = SessionScrobbleState.Started;
                    }

                    break;
            }
        }

        private async Task HandlePlaybackStop(PlaybackStopEventArgs e)
        {
            string sessionId = e.Session.Id;
            Guid userId = e.Session.UserId;
            SessionScrobbleState currentState = _sessionState.GetOrAdd(sessionId, SessionScrobbleState.NotStarted);
            UserConfig? userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(userId);

            if (userConfig == null || string.IsNullOrEmpty(userConfig.UserToken))
            {
                return;
            }

            if (currentState == SessionScrobbleState.Started || currentState == SessionScrobbleState.Paused)
            {
                // Only send stop if we've watched enough to complete the scrobble
                if (CanCompleteScrobble(userConfig, CreateProgressArgsFromStop(e)))
                {
                    await SendScrobbleStop(e, userConfig).ConfigureAwait(false);
                }

                _sessionState[sessionId] = SessionScrobbleState.Stopped;
            }
        }

        private async Task SendScrobbleStart(PlaybackProgressEventArgs e, UserConfig userConfig)
        {
            Guid userId = e.Session.UserId;

            if (!_rateLimiter.CanExecute(userId, DateTime.UtcNow))
            {
                _logger.LogDebug("Rate limit hit for user {UserName}, skipping API call", e.Session.UserName);
                return;
            }

            try
            {
                long? position = e.PlaybackPositionTicks;
                long? runtime = e.MediaInfo.RunTimeTicks;

                if (!runtime.HasValue || runtime.Value == 0 || !position.HasValue)
                {
                    return;
                }

                float percentageWatched = (float)position / runtime.Value * 100f;

                _logger.LogDebug(
                    "Sending scrobble start for {Name} ({Progress:F1}%) for {UserName}",
                    e.MediaInfo.Name,
                    percentageWatched,
                    e.Session.UserName);

                BaseItemDto scrobbleItem = await GetScrobbleItemAsync(e.MediaInfo);
                var response = await _simklApi.ScrobbleStartAsync(scrobbleItem, userConfig.UserToken, percentageWatched);

                _rateLimiter.MarkExecuted(userId, DateTime.UtcNow);

                if (response != null && string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogInformation("Scrobble start sent successfully for {Name} by user {UserName}", e.MediaInfo.Name, e.Session.UserName);
                }
                else if (response != null && !string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogWarning("Simkl API returned error for scrobble start of {Name}: {Error}", e.MediaInfo.Name, response.Error);
                }
                else
                {
                    _logger.LogWarning("Simkl API returned null response for scrobble start of {Name}", e.MediaInfo.Name);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogTrace(ex, "Couldn't deserialize scrobble start response. Raw body: {Body}", ex.Message);
            }
            catch (InvalidTokenException)
            {
                _rateLimiter.MarkExecuted(userId, DateTime.UtcNow);
                _logger.LogWarning(
                    "Invalid token for user {UserName} while sending scrobble start; token should be cleared",
                    e.Session.UserName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred while sending scrobble start for {Name} by user {UserName}", e.MediaInfo.Name, e.Session.UserName);
            }
        }

        private async Task SendScrobblePause(PlaybackProgressEventArgs e, UserConfig userConfig)
        {
            Guid userId = e.Session.UserId;

            if (!_rateLimiter.CanExecute(userId, DateTime.UtcNow))
            {
                _logger.LogDebug("Rate limit hit for user {UserName}, skipping API call", e.Session.UserName);
                return;
            }

            try
            {
                long? position = e.PlaybackPositionTicks;
                long? runtime = e.MediaInfo.RunTimeTicks;

                if (!runtime.HasValue || runtime.Value == 0 || !position.HasValue)
                {
                    return;
                }

                float percentageWatched = (float)position / runtime.Value * 100f;

                _logger.LogDebug(
                    "Sending scrobble pause for {Name} ({Progress:F1}%) for {UserName}",
                    e.MediaInfo.Name,
                    percentageWatched,
                    e.Session.UserName);

                BaseItemDto scrobbleItem = await GetScrobbleItemAsync(e.MediaInfo);
                var response = await _simklApi.ScrobblePauseAsync(scrobbleItem, userConfig.UserToken, percentageWatched);

                // Always mark as executed to prevent hammering on failures
                _rateLimiter.MarkExecuted(userId, DateTime.UtcNow);

                if (response != null && string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogInformation("Scrobble pause sent successfully for {Name} by user {UserName}", e.MediaInfo.Name, e.Session.UserName);
                }
                else if (response != null && !string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogWarning("Simkl API returned error for scrobble pause of {Name}: {Error}", e.MediaInfo.Name, response.Error);
                }
                else
                {
                    _logger.LogWarning("Simkl API returned null response for scrobble pause of {Name}", e.MediaInfo.Name);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogTrace(ex, "Couldn't deserialize scrobble pause response. Raw body: {Body}", ex.Message);
            }
            catch (InvalidTokenException)
            {
                _rateLimiter.MarkExecuted(userId, DateTime.UtcNow);
                _logger.LogWarning(
                    "Invalid token for user {UserName} while sending scrobble pause; token should be cleared",
                    e.Session.UserName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred while sending scrobble pause for {Name} by user {UserName}", e.MediaInfo.Name, e.Session.UserName);
            }
        }

        private async Task SendScrobbleStop(PlaybackStopEventArgs e, UserConfig userConfig)
        {
            Guid userId = e.Session.UserId;

            if (!_rateLimiter.CanExecute(userId, DateTime.UtcNow))
            {
                _logger.LogDebug("Rate limit hit for user {UserName}, skipping API call", e.Session.UserName);
                return;
            }

            try
            {
                long? position = e.PlaybackPositionTicks;
                long? runtime = e.MediaInfo.RunTimeTicks;

                if (!runtime.HasValue || runtime.Value == 0 || !position.HasValue)
                {
                    return;
                }

                float percentageWatched = (float)position / runtime.Value * 100f;

                _logger.LogDebug(
                    "Sending scrobble stop for {Name} ({Progress:F1}%) for {UserName}",
                    e.MediaInfo.Name,
                    percentageWatched,
                    e.Session.UserName);

                BaseItemDto scrobbleItem = await GetScrobbleItemAsync(e.MediaInfo);
                var response = await _simklApi.ScrobbleStopAsync(scrobbleItem, userConfig.UserToken, percentageWatched);

                _rateLimiter.MarkExecuted(userId, DateTime.UtcNow);

                if (response != null && string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogInformation(
                        "Successfully scrobbled {Name} for {UserName} (action: {Action})",
                        e.MediaInfo.Name,
                        e.Session.UserName,
                        response.Action);
                }
                else if (response != null && !string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogWarning("Simkl API returned error for scrobble stop of {Name}: {Error}", e.MediaInfo.Name, response.Error);
                }
                else
                {
                    _logger.LogWarning("Simkl API returned null response for scrobble stop of {Name}", e.MediaInfo.Name);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogTrace(ex, "Couldn't deserialize scrobble stop response. Raw body: {Body}", ex.Message);
            }
            catch (InvalidTokenException)
            {
                _rateLimiter.MarkExecuted(userId, DateTime.UtcNow);
                _logger.LogWarning(
                    "Invalid token for user {UserName} while sending scrobble stop; token should be cleared",
                    e.Session.UserName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred while sending scrobble stop for {Name} by user {UserName}", e.MediaInfo.Name, e.Session.UserName);
            }
        }

        private async Task<BaseItemDto> GetScrobbleItemAsync(BaseItemDto item)
        {
            if (item.Type != BaseItemKind.Episode)
            {
                return item;
            }

            try
            {
                var episodeEntity = await Task.Run(() => _libraryManager.GetItemById(item.Id));
                if (episodeEntity is Episode episode && episode.Series != null)
                {
                    Series seriesEntity = episode.Series;

                    var correctedItem = new BaseItemDto
                    {
                        Id = item.Id,
                        Name = item.Name,
                        Type = item.Type,
                        IndexNumber = item.IndexNumber,
                        ParentIndexNumber = item.ParentIndexNumber,
                        SeriesName = item.SeriesName,
                        ProductionYear = seriesEntity.ProductionYear,
                        ProviderIds = seriesEntity.ProviderIds?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>(),
                        RunTimeTicks = item.RunTimeTicks
                    };

                    return correctedItem;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to correct episode metadata for {ItemName}, using original", item.Name);
            }

            return item;
        }
    }
}
