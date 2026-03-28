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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
        public PlaybackScrobbler(
            ISessionManager sessionManager,
            ILogger<PlaybackScrobbler> logger,
            SimklApi simklApi)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _simklApi = simklApi;
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

                var response = await _simklApi
                    .SyncPlaybackAsync(eventArgs.MediaInfo, userConfig.UserToken, percentageWatched)
                    .ConfigureAwait(false);

                if (response != null && string.IsNullOrEmpty(response.Error))
                {
                    _lastNowWatching[sessionKey] = DateTime.UtcNow;
                    _logger.LogDebug("Sent now watching without errors");
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

                var response = await _simklApi
                    .MarkAsWatched(eventArgs.MediaInfo, userConfig.UserToken)
                    .ConfigureAwait(false);

                if (response.Success)
                {
                    _logger.LogInformation(
                        "Successfully scrobbled {Name} for {UserName}",
                        eventArgs.MediaInfo.Name,
                        eventArgs.Session.UserName);
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
    }
}
