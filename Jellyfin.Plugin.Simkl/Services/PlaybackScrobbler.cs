using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.API.Exceptions;
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
        private const int ScrobbleThrottleSeconds = 30;
        private const int NowWatchingThrottleSeconds = 10;

        private readonly ISessionManager _sessionManager;
        private readonly ILogger<PlaybackScrobbler> _logger;
        private readonly SimklApi _simklApi;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Tracks the last successfully scrobbled item per session,
        /// so we don't scrobble the same item twice in one session.
        /// </summary>
        private readonly Dictionary<string, Guid> _lastScrobbled;

        /// <summary>
        /// Per-session throttle for scrobble checks during playback progress.
        /// Scrobble eligibility is re-evaluated at most every <see cref="ScrobbleThrottleSeconds"/> seconds.
        /// </summary>
        private readonly Dictionary<string, DateTime> _nextScrobbleTry;

        /// <summary>
        /// Per-session throttle for now-watching updates.
        /// Updates are sent at most every <see cref="NowWatchingThrottleSeconds"/> seconds.
        /// </summary>
        private readonly Dictionary<string, DateTime> _lastNowWatching;

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
            _lastScrobbled = new Dictionary<string, Guid>();
            _nextScrobbleTry = new Dictionary<string, DateTime>();
            _lastNowWatching = new Dictionary<string, DateTime>();
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

        private static bool CanBeScrobbled(UserConfig config, PlaybackProgressEventArgs playbackProgress)
        {
            var position = playbackProgress.PlaybackPositionTicks;
            var runtime = playbackProgress.MediaInfo.RunTimeTicks;

            // Must have a known runtime above the configured minimum length
            if (!runtime.HasValue || runtime.Value < 60 * 10000 * config.MinLength)
            {
                return false;
            }

            var percentageWatched = position / (float)runtime.Value * 100f;

            if (percentageWatched < config.ScrobblePercentage)
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
            // Now-watching has its own per-session throttle inside SendNowWatchingAsync,
            // so it is checked independently of the scrobble throttle.
            await SendNowWatchingAsync(e).ConfigureAwait(false);

            // Scrobble checks are throttled per session to avoid hammering the API.
            var sessionId = e.Session.Id;
            if (_nextScrobbleTry.TryGetValue(sessionId, out var next) && DateTime.UtcNow < next)
            {
                return;
            }

            _nextScrobbleTry[sessionId] = DateTime.UtcNow.AddSeconds(ScrobbleThrottleSeconds);
            await ScrobbleSession(e).ConfigureAwait(false);
        }

        private async Task OnPlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            // On stop, attempt a final scrobble unconditionally (no throttle).
            // ScrobbleSession will still respect _lastScrobbled to avoid double-scrobbling
            // if the item was already marked during progress.
            //
            // Note: if the user stops before reaching ScrobblePercentage, this will also
            // not scrobble — CanBeScrobbled uses the position at the time of the event.
            // This is intentional; add a separate "scrobble on stop" threshold in config
            // if more aggressive behavior is desired.
            await ScrobbleSession(e).ConfigureAwait(false);
        }

        private async Task SendNowWatchingAsync(PlaybackProgressEventArgs eventArgs)
        {
            try
            {
                var userId = eventArgs.Session.UserId;
                var userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
                if (userConfig == null || string.IsNullOrEmpty(userConfig.UserToken))
                {
                    return;
                }

                var position = eventArgs.PlaybackPositionTicks;
                var runtime = eventArgs.MediaInfo.RunTimeTicks;

                if (!runtime.HasValue || runtime.Value == 0 || !position.HasValue)
                {
                    return;
                }

                var runtimeValue = runtime.Value;
                var percentageWatched = (float)position / runtimeValue * 100f;

                if (percentageWatched < userConfig.ScrobbleNowWatchingPercentage)
                {
                    return;
                }

                // Per-session throttle: don't send more than once every NowWatchingThrottleSeconds.
                var sessionKey = eventArgs.Session.Id;
                if (_lastNowWatching.TryGetValue(sessionKey, out var lastUpdate) &&
                    (DateTime.UtcNow - lastUpdate).TotalSeconds < NowWatchingThrottleSeconds)
                {
                    return;
                }

                _logger.LogDebug(
                    "Sending now watching for {Name} ({Progress:F1}%) for {UserName}",
                    eventArgs.MediaInfo.Name,
                    percentageWatched,
                    eventArgs.Session.UserName);

                // Get corrected item with proper series metadata for episodes
                var scrobbleItem = await GetScrobbleItemAsync(eventArgs.MediaInfo);

                var response = await _simklApi
                    .ScrobbleStartAsync(scrobbleItem, userConfig.UserToken, percentageWatched)
                    .ConfigureAwait(false);

                if (response != null && string.IsNullOrEmpty(response.Error))
                {
                    _lastNowWatching[sessionKey] = DateTime.UtcNow;
                    _logger.LogDebug("Scrobble start sent (action: {Action})", response.Action);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Couldn't deserialize now watching response. Raw body: {Body}", ex.Message);
            }
            catch (InvalidTokenException)
            {
                _logger.LogWarning(
                    "Invalid token for user {UserName} while sending now watching; token should be cleared",
                    eventArgs.Session.UserName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Couldn't send now watching update");
            }
        }

        private async Task ScrobbleSession(PlaybackProgressEventArgs eventArgs)
        {
            try
            {
                var userId = eventArgs.Session.UserId;
                var userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
                if (userConfig == null || string.IsNullOrEmpty(userConfig.UserToken))
                {
                    _logger.LogInformation(
                        "Can't scrobble: User {UserName} not logged in (userConfig is null: {IsNull})",
                        eventArgs.Session.UserName,
                        userConfig == null);
                    return;
                }

                if (!CanBeScrobbled(userConfig, eventArgs))
                {
                    return;
                }

                // Don't scrobble the same item twice in the same session.
                if (_lastScrobbled.TryGetValue(eventArgs.Session.Id, out var lastId) &&
                    lastId == eventArgs.MediaInfo.Id)
                {
                    _logger.LogDebug(
                        "Already scrobbled {ItemName} for {UserName}, skipping",
                        eventArgs.MediaInfo.Name,
                        eventArgs.Session.UserName);
                    return;
                }

                _logger.LogInformation(
                    "Scrobbling {Name} ({ItemId}) for {UserName} ({UserId}) - {Path} on session {SessionId}",
                    eventArgs.MediaInfo.Name,
                    eventArgs.MediaInfo.Id,
                    eventArgs.Session.UserName,
                    userId,
                    eventArgs.MediaInfo.Path,
                    eventArgs.Session.Id);

                var position = eventArgs.PlaybackPositionTicks;
                var runtime = eventArgs.MediaInfo.RunTimeTicks;
                var percentageWatched = position.HasValue && runtime.HasValue && runtime.Value > 0
                    ? (float)position.Value / runtime.Value * 100f
                    : userConfig.ScrobblePercentage;

                // Get corrected item with proper series metadata for episodes
                var scrobbleItem = await GetScrobbleItemAsync(eventArgs.MediaInfo);

                var response = await _simklApi
                    .ScrobbleStopAsync(scrobbleItem, userConfig.UserToken, percentageWatched)
                    .ConfigureAwait(false);

                if (response != null && string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogInformation(
                        "Successfully scrobbled {Name} for {UserName} (action: {Action})",
                        eventArgs.MediaInfo.Name,
                        eventArgs.Session.UserName,
                        response.Action);
                    _lastScrobbled[eventArgs.Session.Id] = eventArgs.MediaInfo.Id;
                }
            }
            catch (InvalidTokenException)
            {
                _logger.LogWarning(
                    "Invalid token for user {UserName} while scrobbling; token should be cleared",
                    eventArgs.Session.UserName);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError(
                    ex,
                    "Couldn't scrobble {ItemName} — bad data; marking as scrobbled to prevent retry loop",
                    eventArgs.MediaInfo.Name);
                // Mark as scrobbled to prevent an infinite retry loop on permanently bad data.
                _lastScrobbled[eventArgs.Session.Id] = eventArgs.MediaInfo.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Caught unknown exception while trying to scrobble {ItemName}",
                    eventArgs.MediaInfo.Name);
            }
        }

        /// <summary>
        /// Gets the appropriate BaseItemDto for scrobbling with corrected metadata.
        ///
        /// Problem: For episodes, Jellyfin's BaseItemDto contains episode-level metadata
        /// (episode production year, episode provider IDs) but Simkl's API requires
        /// show-level metadata to identify the series. Sending episode IDs as show IDs
        /// causes "id_err" responses from Simkl because it can't find the show.
        ///
        /// Solution: For episodes, fetch the parent series entity and use its metadata
        /// (series production year, series provider IDs) for show identification while
        /// keeping episode-specific data (season/episode numbers) for episode identification.
        /// </summary>
        /// <param name="item">The original media item from Jellyfin.</param>
        /// <returns>BaseItemDto with corrected metadata for scrobbling.</returns>
        private async Task<BaseItemDto> GetScrobbleItemAsync(BaseItemDto item)
        {
            // For non-episodes, return as-is
            if (item.Type != BaseItemKind.Episode)
            {
                return item;
            }

            try
            {
                // For episodes, fetch the parent series to get correct show-level metadata
                // Using Task.Run to avoid blocking the thread during library access
                var episodeEntity = await Task.Run(() => _libraryManager.GetItemById(item.Id));
                if (episodeEntity is Episode episode && episode.Series != null)
                {
                    var seriesEntity = episode.Series;

                    // Create corrected BaseItemDto: episode data + series metadata for proper Simkl identification
                    var correctedItem = new BaseItemDto
                    {
                        // Keep episode-specific properties for episode identification
                        Id = item.Id,
                        Name = item.Name,
                        Type = item.Type,
                        IndexNumber = item.IndexNumber,          // Episode number
                        ParentIndexNumber = item.ParentIndexNumber, // Season number
                        SeriesName = item.SeriesName,

                        // Replace episode metadata with series metadata for show identification
                        // This fixes "id_err" by sending correct series IDs instead of episode IDs
                        ProductionYear = seriesEntity.ProductionYear,  // Series year, not episode year
                        ProviderIds = seriesEntity.ProviderIds,        // Series IDs (TVDB, IMDb, etc.)

                        // Copy other necessary properties
                        RunTimeTicks = item.RunTimeTicks,
                        Path = item.Path
                    };

                    _logger.LogDebug(
                        "Corrected episode metadata for Simkl: {SeriesName} ({SeriesYear}) S{Season}E{Episode} - Using series IDs instead of episode IDs",
                        correctedItem.SeriesName,
                        correctedItem.ProductionYear,
                        correctedItem.ParentIndexNumber,
                        correctedItem.IndexNumber);

                    return correctedItem;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch series metadata for episode {EpisodeName}, using original item (may cause id_err)",
                    item.Name);
            }

            // Fallback to original item if series lookup fails
            // Note: This may still cause "id_err" from Simkl due to incorrect metadata
            return item;
        }
    }
}
