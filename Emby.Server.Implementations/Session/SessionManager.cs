﻿using MediaBrowser.Common.Events;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Devices;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Threading;
using MediaBrowser.Model.Extensions;

namespace Emby.Server.Implementations.Session
{
    /// <summary>
    /// Class SessionManager
    /// </summary>
    public class SessionManager : ISessionManager, IDisposable
    {
        /// <summary>
        /// The _user data repository
        /// </summary>
        private readonly IUserDataManager _userDataManager;

        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IMusicManager _musicManager;
        private readonly IDtoService _dtoService;
        private readonly IImageProcessor _imageProcessor;
        private readonly IMediaSourceManager _mediaSourceManager;

        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IServerApplicationHost _appHost;

        private readonly IAuthenticationRepository _authRepo;
        private readonly IDeviceManager _deviceManager;
        private readonly ITimerFactory _timerFactory;

        /// <summary>
        /// The _active connections
        /// </summary>
        private readonly ConcurrentDictionary<string, SessionInfo> _activeConnections =
            new ConcurrentDictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<GenericEventArgs<AuthenticationRequest>> AuthenticationFailed;

        public event EventHandler<GenericEventArgs<AuthenticationRequest>> AuthenticationSucceeded;

        /// <summary>
        /// Occurs when [playback start].
        /// </summary>
        public event EventHandler<PlaybackProgressEventArgs> PlaybackStart;
        /// <summary>
        /// Occurs when [playback progress].
        /// </summary>
        public event EventHandler<PlaybackProgressEventArgs> PlaybackProgress;
        /// <summary>
        /// Occurs when [playback stopped].
        /// </summary>
        public event EventHandler<PlaybackStopEventArgs> PlaybackStopped;

        public event EventHandler<SessionEventArgs> SessionStarted;
        public event EventHandler<SessionEventArgs> CapabilitiesChanged;
        public event EventHandler<SessionEventArgs> SessionEnded;
        public event EventHandler<SessionEventArgs> SessionActivity;

        private IEnumerable<ISessionControllerFactory> _sessionFactories = new List<ISessionControllerFactory>();

        private readonly SemaphoreSlim _sessionLock = new SemaphoreSlim(1, 1);

        public SessionManager(IUserDataManager userDataManager, ILogger logger, ILibraryManager libraryManager, IUserManager userManager, IMusicManager musicManager, IDtoService dtoService, IImageProcessor imageProcessor, IJsonSerializer jsonSerializer, IServerApplicationHost appHost, IHttpClient httpClient, IAuthenticationRepository authRepo, IDeviceManager deviceManager, IMediaSourceManager mediaSourceManager, ITimerFactory timerFactory)
        {
            _userDataManager = userDataManager;
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _musicManager = musicManager;
            _dtoService = dtoService;
            _imageProcessor = imageProcessor;
            _jsonSerializer = jsonSerializer;
            _appHost = appHost;
            _httpClient = httpClient;
            _authRepo = authRepo;
            _deviceManager = deviceManager;
            _mediaSourceManager = mediaSourceManager;
            _timerFactory = timerFactory;

            _deviceManager.DeviceOptionsUpdated += _deviceManager_DeviceOptionsUpdated;
        }

        private bool _disposed;
        public void Dispose()
        {
            _disposed = true;
        }

        public void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        void _deviceManager_DeviceOptionsUpdated(object sender, GenericEventArgs<DeviceInfo> e)
        {
            foreach (var session in Sessions)
            {
                if (string.Equals(session.DeviceId, e.Argument.Id))
                {
                    session.DeviceName = e.Argument.Name;
                }
            }
        }

        /// <summary>
        /// Adds the parts.
        /// </summary>
        /// <param name="sessionFactories">The session factories.</param>
        public void AddParts(IEnumerable<ISessionControllerFactory> sessionFactories)
        {
            _sessionFactories = sessionFactories.ToList();
        }

        /// <summary>
        /// Gets all connections.
        /// </summary>
        /// <value>All connections.</value>
        public IEnumerable<SessionInfo> Sessions
        {
            get { return _activeConnections.Values.OrderByDescending(c => c.LastActivityDate).ToList(); }
        }

        private void OnSessionStarted(SessionInfo info)
        {
            EventHelper.QueueEventIfNotNull(SessionStarted, this, new SessionEventArgs
            {
                SessionInfo = info

            }, _logger);

            if (!string.IsNullOrWhiteSpace(info.DeviceId))
            {
                var capabilities = GetSavedCapabilities(info.DeviceId);

                if (capabilities != null)
                {
                    info.AppIconUrl = capabilities.IconUrl;
                    ReportCapabilities(info, capabilities, false);
                }
            }
        }

        private async void OnSessionEnded(SessionInfo info)
        {
            try
            {
                await SendSessionEndedNotification(info, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error in SendSessionEndedNotification", ex);
            }

            EventHelper.QueueEventIfNotNull(SessionEnded, this, new SessionEventArgs
            {
                SessionInfo = info

            }, _logger);

            var disposable = info.SessionController as IDisposable;

            if (disposable != null)
            {
                _logger.Debug("Disposing session controller {0}", disposable.GetType().Name);

                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error disposing session controller", ex);
                }
            }

            info.Dispose();
        }

        /// <summary>
        /// Logs the user activity.
        /// </summary>
        /// <param name="appName">Type of the client.</param>
        /// <param name="appVersion">The app version.</param>
        /// <param name="deviceId">The device id.</param>
        /// <param name="deviceName">Name of the device.</param>
        /// <param name="remoteEndPoint">The remote end point.</param>
        /// <param name="user">The user.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">user</exception>
        /// <exception cref="System.UnauthorizedAccessException"></exception>
        public async Task<SessionInfo> LogSessionActivity(string appName,
            string appVersion,
            string deviceId,
            string deviceName,
            string remoteEndPoint,
            User user)
        {
            CheckDisposed();

            if (string.IsNullOrEmpty(appName))
            {
                throw new ArgumentNullException("appName");
            }
            if (string.IsNullOrEmpty(appVersion))
            {
                throw new ArgumentNullException("appVersion");
            }
            if (string.IsNullOrEmpty(deviceId))
            {
                throw new ArgumentNullException("deviceId");
            }
            if (string.IsNullOrEmpty(deviceName))
            {
                throw new ArgumentNullException("deviceName");
            }

            var activityDate = DateTime.UtcNow;
            var session = await GetSessionInfo(appName, appVersion, deviceId, deviceName, remoteEndPoint, user).ConfigureAwait(false);
            var lastActivityDate = session.LastActivityDate;
            session.LastActivityDate = activityDate;

            if (user != null)
            {
                var userLastActivityDate = user.LastActivityDate ?? DateTime.MinValue;
                user.LastActivityDate = activityDate;

                if ((activityDate - userLastActivityDate).TotalSeconds > 60)
                {
                    try
                    {
                        _userManager.UpdateUser(user);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Error updating user", ex);
                    }
                }
            }

            if ((activityDate - lastActivityDate).TotalSeconds > 10)
            {
                EventHelper.FireEventIfNotNull(SessionActivity, this, new SessionEventArgs
                {
                    SessionInfo = session

                }, _logger);
            }

            var controller = session.SessionController;
            if (controller != null)
            {
                controller.OnActivity();
            }

            return session;
        }

        public async void ReportSessionEnded(string sessionId)
        {
            CheckDisposed();
            await _sessionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                var session = GetSession(sessionId, false);

                if (session != null)
                {
                    var key = GetSessionKey(session.Client, session.DeviceId);

                    SessionInfo removed;
                    _activeConnections.TryRemove(key, out removed);

                    OnSessionEnded(session);
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private Task<MediaSourceInfo> GetMediaSource(IHasMediaSources item, string mediaSourceId, string liveStreamId)
        {
            return _mediaSourceManager.GetMediaSource(item, mediaSourceId, liveStreamId, false, CancellationToken.None);
        }

        /// <summary>
        /// Updates the now playing item id.
        /// </summary>
        private async Task UpdateNowPlayingItem(SessionInfo session, PlaybackProgressInfo info, BaseItem libraryItem, bool updateLastCheckInTime)
        {
            if (string.IsNullOrWhiteSpace(info.MediaSourceId))
            {
                info.MediaSourceId = info.ItemId;
            }

            if (!string.IsNullOrWhiteSpace(info.ItemId) && info.Item == null && libraryItem != null)
            {
                var current = session.NowPlayingItem;

                if (current == null || !string.Equals(current.Id, info.ItemId, StringComparison.OrdinalIgnoreCase))
                {
                    var runtimeTicks = libraryItem.RunTimeTicks;

                    MediaSourceInfo mediaSource = null;
                    var hasMediaSources = libraryItem as IHasMediaSources;
                    if (hasMediaSources != null)
                    {
                        mediaSource = await GetMediaSource(hasMediaSources, info.MediaSourceId, info.LiveStreamId).ConfigureAwait(false);

                        if (mediaSource != null)
                        {
                            runtimeTicks = mediaSource.RunTimeTicks;
                        }
                    }

                    info.Item = GetItemInfo(libraryItem, mediaSource);

                    info.Item.RunTimeTicks = runtimeTicks;
                }
                else
                {
                    info.Item = current;
                }
            }

            session.NowPlayingItem = info.Item;
            session.LastActivityDate = DateTime.UtcNow;

            if (updateLastCheckInTime)
            {
                session.LastPlaybackCheckIn = DateTime.UtcNow;
            }

            session.PlayState.IsPaused = info.IsPaused;
            session.PlayState.PositionTicks = info.PositionTicks;
            session.PlayState.MediaSourceId = info.MediaSourceId;
            session.PlayState.CanSeek = info.CanSeek;
            session.PlayState.IsMuted = info.IsMuted;
            session.PlayState.VolumeLevel = info.VolumeLevel;
            session.PlayState.AudioStreamIndex = info.AudioStreamIndex;
            session.PlayState.SubtitleStreamIndex = info.SubtitleStreamIndex;
            session.PlayState.PlayMethod = info.PlayMethod;
            session.PlayState.RepeatMode = info.RepeatMode;
        }

        /// <summary>
        /// Removes the now playing item id.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <exception cref="System.ArgumentNullException">item</exception>
        private void RemoveNowPlayingItem(SessionInfo session)
        {
            session.NowPlayingItem = null;
            session.PlayState = new PlayerStateInfo();

            if (!string.IsNullOrEmpty(session.DeviceId))
            {
                ClearTranscodingInfo(session.DeviceId);
            }
        }

        private string GetSessionKey(string appName, string deviceId)
        {
            return appName + deviceId;
        }

        /// <summary>
        /// Gets the connection.
        /// </summary>
        /// <param name="appName">Type of the client.</param>
        /// <param name="appVersion">The app version.</param>
        /// <param name="deviceId">The device id.</param>
        /// <param name="deviceName">Name of the device.</param>
        /// <param name="remoteEndPoint">The remote end point.</param>
        /// <param name="user">The user.</param>
        /// <returns>SessionInfo.</returns>
        private async Task<SessionInfo> GetSessionInfo(string appName, string appVersion, string deviceId, string deviceName, string remoteEndPoint, User user)
        {
            CheckDisposed();

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentNullException("deviceId");
            }
            var key = GetSessionKey(appName, deviceId);

            await _sessionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            var userId = user == null ? (Guid?)null : user.Id;
            var username = user == null ? null : user.Name;

            try
            {
                CheckDisposed();

                SessionInfo sessionInfo;
                DeviceInfo device = null;

                if (!_activeConnections.TryGetValue(key, out sessionInfo))
                {
                    sessionInfo = new SessionInfo(this, _logger)
                    {
                        Client = appName,
                        DeviceId = deviceId,
                        ApplicationVersion = appVersion,
                        Id = key.GetMD5().ToString("N")
                    };

                    sessionInfo.DeviceName = deviceName;
                    sessionInfo.UserId = userId;
                    sessionInfo.UserName = username;
                    sessionInfo.RemoteEndPoint = remoteEndPoint;

                    OnSessionStarted(sessionInfo);

                    _activeConnections.TryAdd(key, sessionInfo);

                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        var userIdString = userId.HasValue ? userId.Value.ToString("N") : null;
                        device = _deviceManager.RegisterDevice(deviceId, deviceName, appName, appVersion, userIdString);
                    }
                }

                device = device ?? _deviceManager.GetDevice(deviceId);

                if (device == null)
                {
                    var userIdString = userId.HasValue ? userId.Value.ToString("N") : null;
                    device = _deviceManager.RegisterDevice(deviceId, deviceName, appName, appVersion, userIdString);
                }

                if (device != null)
                {
                    if (!string.IsNullOrEmpty(device.CustomName))
                    {
                        deviceName = device.CustomName;
                    }
                }

                sessionInfo.DeviceName = deviceName;
                sessionInfo.UserId = userId;
                sessionInfo.UserName = username;
                sessionInfo.RemoteEndPoint = remoteEndPoint;
                sessionInfo.ApplicationVersion = appVersion;

                if (!userId.HasValue)
                {
                    sessionInfo.AdditionalUsers = new SessionUserInfo[] { };
                }

                if (sessionInfo.SessionController == null)
                {
                    sessionInfo.SessionController = _sessionFactories
                        .Select(i => i.GetSessionController(sessionInfo))
                        .FirstOrDefault(i => i != null);
                }

                return sessionInfo;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private List<User> GetUsers(SessionInfo session)
        {
            var users = new List<User>();

            if (session.UserId.HasValue)
            {
                var user = _userManager.GetUserById(session.UserId.Value);

                if (user == null)
                {
                    throw new InvalidOperationException("User not found");
                }

                users.Add(user);

                var additionalUsers = session.AdditionalUsers
                    .Select(i => _userManager.GetUserById(i.UserId))
                    .Where(i => i != null);

                users.AddRange(additionalUsers);
            }

            return users;
        }

        private ITimer _idleTimer;

        private void StartIdleCheckTimer()
        {
            if (_idleTimer == null)
            {
                _idleTimer = _timerFactory.Create(CheckForIdlePlayback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            }
        }
        private void StopIdleCheckTimer()
        {
            if (_idleTimer != null)
            {
                _idleTimer.Dispose();
                _idleTimer = null;
            }
        }

        private async void CheckForIdlePlayback(object state)
        {
            var playingSessions = Sessions.Where(i => i.NowPlayingItem != null)
                .ToList();

            if (playingSessions.Count > 0)
            {
                var idle = playingSessions
                    .Where(i => (DateTime.UtcNow - i.LastPlaybackCheckIn).TotalMinutes > 5)
                    .ToList();

                foreach (var session in idle)
                {
                    _logger.Debug("Session {0} has gone idle while playing", session.Id);

                    try
                    {
                        await OnPlaybackStopped(new PlaybackStopInfo
                        {
                            Item = session.NowPlayingItem,
                            ItemId = session.NowPlayingItem == null ? null : session.NowPlayingItem.Id,
                            SessionId = session.Id,
                            MediaSourceId = session.PlayState == null ? null : session.PlayState.MediaSourceId,
                            PositionTicks = session.PlayState == null ? null : session.PlayState.PositionTicks
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Error calling OnPlaybackStopped", ex);
                    }
                }

                playingSessions = Sessions.Where(i => i.NowPlayingItem != null)
                    .ToList();
            }

            if (playingSessions.Count == 0)
            {
                StopIdleCheckTimer();
            }
        }

        private BaseItem GetNowPlayingItem(SessionInfo session, string itemId)
        {
            var idGuid = new Guid(itemId);

            var item = session.FullNowPlayingItem;
            if (item != null && item.Id == idGuid)
            {
                return item;
            }

            item = _libraryManager.GetItemById(itemId);

            session.FullNowPlayingItem = item;

            return item;
        }

        /// <summary>
        /// Used to report that playback has started for an item
        /// </summary>
        /// <param name="info">The info.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">info</exception>
        public async Task OnPlaybackStart(PlaybackStartInfo info)
        {
            CheckDisposed();

            if (info == null)
            {
                throw new ArgumentNullException("info");
            }

            var session = GetSession(info.SessionId);

            var libraryItem = string.IsNullOrWhiteSpace(info.ItemId)
                ? null
                : GetNowPlayingItem(session, info.ItemId);

            await UpdateNowPlayingItem(session, info, libraryItem, true).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(session.DeviceId) && info.PlayMethod != PlayMethod.Transcode)
            {
                ClearTranscodingInfo(session.DeviceId);
            }

            session.StartAutomaticProgress(_timerFactory, info);

            var users = GetUsers(session);

            if (libraryItem != null)
            {
                foreach (var user in users)
                {
                    OnPlaybackStart(user.Id, libraryItem);
                }
            }

            // Nothing to save here
            // Fire events to inform plugins
            EventHelper.QueueEventIfNotNull(PlaybackStart, this, new PlaybackProgressEventArgs
            {
                Item = libraryItem,
                Users = users,
                MediaSourceId = info.MediaSourceId,
                MediaInfo = info.Item,
                DeviceName = session.DeviceName,
                ClientName = session.Client,
                DeviceId = session.DeviceId

            }, _logger);

            await SendPlaybackStartNotification(session, CancellationToken.None).ConfigureAwait(false);

            StartIdleCheckTimer();
        }

        /// <summary>
        /// Called when [playback start].
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="item">The item.</param>
        private void OnPlaybackStart(Guid userId, IHasUserData item)
        {
            var data = _userDataManager.GetUserData(userId, item);

            data.PlayCount++;
            data.LastPlayedDate = DateTime.UtcNow;

            if (item.SupportsPlayedStatus)
            {
                if (!(item is Video))
                {
                    data.Played = true;
                }
            }
            else
            {
                data.Played = false;
            }

            _userDataManager.SaveUserData(userId, item, data, UserDataSaveReason.PlaybackStart, CancellationToken.None);
        }

        public Task OnPlaybackProgress(PlaybackProgressInfo info)
        {
            return OnPlaybackProgress(info, false);
        }

        /// <summary>
        /// Used to report playback progress for an item
        /// </summary>
        public async Task OnPlaybackProgress(PlaybackProgressInfo info, bool isAutomated)
        {
            CheckDisposed();

            if (info == null)
            {
                throw new ArgumentNullException("info");
            }

            var session = GetSession(info.SessionId);

            var libraryItem = string.IsNullOrWhiteSpace(info.ItemId)
                ? null
                : GetNowPlayingItem(session, info.ItemId);

            await UpdateNowPlayingItem(session, info, libraryItem, !isAutomated).ConfigureAwait(false);

            var users = GetUsers(session);

            if (libraryItem != null)
            {
                foreach (var user in users)
                {
                    OnPlaybackProgress(user, libraryItem, info);
                }
            }

            EventHelper.FireEventIfNotNull(PlaybackProgress, this, new PlaybackProgressEventArgs
            {
                Item = libraryItem,
                Users = users,
                PlaybackPositionTicks = session.PlayState.PositionTicks,
                MediaSourceId = session.PlayState.MediaSourceId,
                MediaInfo = info.Item,
                DeviceName = session.DeviceName,
                ClientName = session.Client,
                DeviceId = session.DeviceId,
                IsPaused = info.IsPaused,
                PlaySessionId = info.PlaySessionId,
                IsAutomated = isAutomated

            }, _logger);

            if (!isAutomated)
            {
                session.StartAutomaticProgress(_timerFactory, info);
            }

            StartIdleCheckTimer();
        }

        private void OnPlaybackProgress(User user, BaseItem item, PlaybackProgressInfo info)
        {
            var data = _userDataManager.GetUserData(user.Id, item);

            var positionTicks = info.PositionTicks;

            if (positionTicks.HasValue)
            {
                _userDataManager.UpdatePlayState(item, data, positionTicks.Value);

                UpdatePlaybackSettings(user, info, data);

                _userDataManager.SaveUserData(user.Id, item, data, UserDataSaveReason.PlaybackProgress, CancellationToken.None);
            }
        }

        private void UpdatePlaybackSettings(User user, PlaybackProgressInfo info, UserItemData data)
        {
            if (user.Configuration.RememberAudioSelections)
            {
                data.AudioStreamIndex = info.AudioStreamIndex;
            }
            else
            {
                data.AudioStreamIndex = null;
            }

            if (user.Configuration.RememberSubtitleSelections)
            {
                data.SubtitleStreamIndex = info.SubtitleStreamIndex;
            }
            else
            {
                data.SubtitleStreamIndex = null;
            }
        }

        /// <summary>
        /// Used to report that playback has ended for an item
        /// </summary>
        /// <param name="info">The info.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">info</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">positionTicks</exception>
        public async Task OnPlaybackStopped(PlaybackStopInfo info)
        {
            CheckDisposed();

            if (info == null)
            {
                throw new ArgumentNullException("info");
            }

            if (info.PositionTicks.HasValue && info.PositionTicks.Value < 0)
            {
                throw new ArgumentOutOfRangeException("positionTicks");
            }

            var session = GetSession(info.SessionId);

            session.StopAutomaticProgress();

            var libraryItem = string.IsNullOrWhiteSpace(info.ItemId)
                ? null
                : GetNowPlayingItem(session, info.ItemId);

            // Normalize
            if (string.IsNullOrWhiteSpace(info.MediaSourceId))
            {
                info.MediaSourceId = info.ItemId;
            }

            if (!string.IsNullOrWhiteSpace(info.ItemId) && info.Item == null && libraryItem != null)
            {
                var current = session.NowPlayingItem;

                if (current == null || !string.Equals(current.Id, info.ItemId, StringComparison.OrdinalIgnoreCase))
                {
                    MediaSourceInfo mediaSource = null;

                    var hasMediaSources = libraryItem as IHasMediaSources;
                    if (hasMediaSources != null)
                    {
                        mediaSource = await GetMediaSource(hasMediaSources, info.MediaSourceId, info.LiveStreamId).ConfigureAwait(false);
                    }

                    info.Item = GetItemInfo(libraryItem, mediaSource);
                }
                else
                {
                    info.Item = current;
                }
            }

            if (info.Item != null)
            {
                var msString = info.PositionTicks.HasValue ? (info.PositionTicks.Value / 10000).ToString(CultureInfo.InvariantCulture) : "unknown";

                _logger.Info("Playback stopped reported by app {0} {1} playing {2}. Stopped at {3} ms",
                    session.Client,
                    session.ApplicationVersion,
                    info.Item.Name,
                    msString);
            }

            RemoveNowPlayingItem(session);

            var users = GetUsers(session);
            var playedToCompletion = false;

            if (libraryItem != null)
            {
                foreach (var user in users)
                {
                    playedToCompletion = OnPlaybackStopped(user.Id, libraryItem, info.PositionTicks, info.Failed);
                }
            }

            if (!string.IsNullOrWhiteSpace(info.LiveStreamId))
            {
                try
                {
                    await _mediaSourceManager.CloseLiveStream(info.LiveStreamId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error closing live stream", ex);
                }
            }

            EventHelper.QueueEventIfNotNull(PlaybackStopped, this, new PlaybackStopEventArgs
            {
                Item = libraryItem,
                Users = users,
                PlaybackPositionTicks = info.PositionTicks,
                PlayedToCompletion = playedToCompletion,
                MediaSourceId = info.MediaSourceId,
                MediaInfo = info.Item,
                DeviceName = session.DeviceName,
                ClientName = session.Client,
                DeviceId = session.DeviceId

            }, _logger);

            await SendPlaybackStoppedNotification(session, CancellationToken.None).ConfigureAwait(false);
        }

        private bool OnPlaybackStopped(Guid userId, BaseItem item, long? positionTicks, bool playbackFailed)
        {
            bool playedToCompletion = false;

            if (!playbackFailed)
            {
                var data = _userDataManager.GetUserData(userId, item);

                if (positionTicks.HasValue)
                {
                    playedToCompletion = _userDataManager.UpdatePlayState(item, data, positionTicks.Value);
                }
                else
                {
                    // If the client isn't able to report this, then we'll just have to make an assumption
                    data.PlayCount++;
                    data.Played = item.SupportsPlayedStatus;
                    data.PlaybackPositionTicks = 0;
                    playedToCompletion = true;
                }

                _userDataManager.SaveUserData(userId, item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
            }

            return playedToCompletion;
        }

        /// <summary>
        /// Gets the session.
        /// </summary>
        /// <param name="sessionId">The session identifier.</param>
        /// <param name="throwOnMissing">if set to <c>true</c> [throw on missing].</param>
        /// <returns>SessionInfo.</returns>
        /// <exception cref="ResourceNotFoundException"></exception>
        private SessionInfo GetSession(string sessionId, bool throwOnMissing = true)
        {
            var session = Sessions.FirstOrDefault(i => string.Equals(i.Id, sessionId));

            if (session == null && throwOnMissing)
            {
                throw new ResourceNotFoundException(string.Format("Session {0} not found.", sessionId));
            }

            return session;
        }

        private SessionInfo GetSessionToRemoteControl(string sessionId)
        {
            // Accept either device id or session id
            var session = Sessions.FirstOrDefault(i => string.Equals(i.Id, sessionId));

            if (session == null)
            {
                throw new ResourceNotFoundException(string.Format("Session {0} not found.", sessionId));
            }

            return session;
        }

        public Task SendMessageCommand(string controllingSessionId, string sessionId, MessageCommand command, CancellationToken cancellationToken)
        {
            CheckDisposed();

            var generalCommand = new GeneralCommand
            {
                Name = GeneralCommandType.DisplayMessage.ToString()
            };

            generalCommand.Arguments["Header"] = command.Header;
            generalCommand.Arguments["Text"] = command.Text;

            if (command.TimeoutMs.HasValue)
            {
                generalCommand.Arguments["TimeoutMs"] = command.TimeoutMs.Value.ToString(CultureInfo.InvariantCulture);
            }

            return SendGeneralCommand(controllingSessionId, sessionId, generalCommand, cancellationToken);
        }

        public Task SendGeneralCommand(string controllingSessionId, string sessionId, GeneralCommand command, CancellationToken cancellationToken)
        {
            CheckDisposed();

            var session = GetSessionToRemoteControl(sessionId);

            if (!string.IsNullOrWhiteSpace(controllingSessionId))
            {
                var controllingSession = GetSession(controllingSessionId);
                AssertCanControl(session, controllingSession);
            }

            return session.SessionController.SendGeneralCommand(command, cancellationToken);
        }

        public async Task SendPlayCommand(string controllingSessionId, string sessionId, PlayRequest command, CancellationToken cancellationToken)
        {
            CheckDisposed();

            var session = GetSessionToRemoteControl(sessionId);

            var user = session.UserId.HasValue ? _userManager.GetUserById(session.UserId.Value) : null;

            List<BaseItem> items;

            if (command.PlayCommand == PlayCommand.PlayInstantMix)
            {
                items = command.ItemIds.SelectMany(i => TranslateItemForInstantMix(i, user))
                    .ToList();

                command.PlayCommand = PlayCommand.PlayNow;
            }
            else
            {
                var list = new List<BaseItem>();
                foreach (var itemId in command.ItemIds)
                {
                    var subItems = TranslateItemForPlayback(itemId, user);
                    list.AddRange(subItems);
                }

                items = list;
            }

            if (command.PlayCommand == PlayCommand.PlayShuffle)
            {
                items = items.OrderBy(i => Guid.NewGuid()).ToList();
                command.PlayCommand = PlayCommand.PlayNow;
            }

            command.ItemIds = items.Select(i => i.Id.ToString("N")).ToArray(items.Count);

            if (user != null)
            {
                if (items.Any(i => i.GetPlayAccess(user) != PlayAccess.Full))
                {
                    throw new ArgumentException(string.Format("{0} is not allowed to play media.", user.Name));
                }
            }

            if (items.Any(i => !session.PlayableMediaTypes.Contains(i.MediaType, StringComparer.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(string.Format("{0} is unable to play the requested media type.", session.DeviceName ?? session.Id));
            }

            if (user != null && command.ItemIds.Length == 1 && user.Configuration.EnableNextEpisodeAutoPlay)
            {
                var episode = _libraryManager.GetItemById(command.ItemIds[0]) as Episode;
                if (episode != null)
                {
                    var series = episode.Series;
                    if (series != null)
                    {
                        var episodes = series.GetEpisodes(user, new DtoOptions(false)
                        {
                            EnableImages = false
                        })
                            .Where(i => !i.IsVirtualItem)
                            .SkipWhile(i => i.Id != episode.Id)
                            .ToList();

                        if (episodes.Count > 0)
                        {
                            command.ItemIds = episodes.Select(i => i.Id.ToString("N")).ToArray(episodes.Count);
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(controllingSessionId))
            {
                var controllingSession = GetSession(controllingSessionId);
                AssertCanControl(session, controllingSession);
                if (controllingSession.UserId.HasValue)
                {
                    command.ControllingUserId = controllingSession.UserId.Value.ToString("N");
                }
            }

            await session.SessionController.SendPlayCommand(command, cancellationToken).ConfigureAwait(false);
        }

        private List<BaseItem> TranslateItemForPlayback(string id, User user)
        {
            var item = _libraryManager.GetItemById(id);

            if (item == null)
            {
                _logger.Error("A non-existant item Id {0} was passed into TranslateItemForPlayback", id);
                return new List<BaseItem>();
            }

            var byName = item as IItemByName;

            if (byName != null)
            {
                var items = byName.GetTaggedItems(new InternalItemsQuery(user)
                {
                    IsFolder = false,
                    Recursive = true,
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false,
                        Fields = new ItemFields[]
                        {
                            ItemFields.SortName
                        }
                    },
                    IsVirtualItem = false
                });

                return FilterToSingleMediaType(items)
                    .OrderBy(i => i.SortName)
                    .ToList();
            }

            if (item.IsFolder)
            {
                var folder = (Folder)item;

                var itemsResult = folder.GetItemList(new InternalItemsQuery(user)
                {
                    Recursive = true,
                    IsFolder = false,
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false,
                        Fields = new ItemFields[]
                        {
                            ItemFields.SortName
                        }
                    },
                    IsVirtualItem = false

                });

                return FilterToSingleMediaType(itemsResult)
                    .OrderBy(i => i.SortName)
                    .ToList();
            }

            return new List<BaseItem> { item };
        }

        private IEnumerable<BaseItem> FilterToSingleMediaType(IEnumerable<BaseItem> items)
        {
            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.MediaType))
                .ToLookup(i => i.MediaType, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(i => i.Count())
                .FirstOrDefault();
        }

        private IEnumerable<BaseItem> TranslateItemForInstantMix(string id, User user)
        {
            var item = _libraryManager.GetItemById(id);

            if (item == null)
            {
                _logger.Error("A non-existant item Id {0} was passed into TranslateItemForInstantMix", id);
                return new List<BaseItem>();
            }

            return _musicManager.GetInstantMixFromItem(item, user, new DtoOptions(false) { EnableImages = false });
        }

        public Task SendBrowseCommand(string controllingSessionId, string sessionId, BrowseRequest command, CancellationToken cancellationToken)
        {
            var generalCommand = new GeneralCommand
            {
                Name = GeneralCommandType.DisplayContent.ToString()
            };

            generalCommand.Arguments["ItemId"] = command.ItemId;
            generalCommand.Arguments["ItemName"] = command.ItemName;
            generalCommand.Arguments["ItemType"] = command.ItemType;

            return SendGeneralCommand(controllingSessionId, sessionId, generalCommand, cancellationToken);
        }

        public Task SendPlaystateCommand(string controllingSessionId, string sessionId, PlaystateRequest command, CancellationToken cancellationToken)
        {
            CheckDisposed();

            var session = GetSessionToRemoteControl(sessionId);

            if (!string.IsNullOrWhiteSpace(controllingSessionId))
            {
                var controllingSession = GetSession(controllingSessionId);
                AssertCanControl(session, controllingSession);
                if (controllingSession.UserId.HasValue)
                {
                    command.ControllingUserId = controllingSession.UserId.Value.ToString("N");
                }
            }

            return session.SessionController.SendPlaystateCommand(command, cancellationToken);
        }

        private void AssertCanControl(SessionInfo session, SessionInfo controllingSession)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }
            if (controllingSession == null)
            {
                throw new ArgumentNullException("controllingSession");
            }
        }

        /// <summary>
        /// Sends the restart required message.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task SendRestartRequiredNotification(CancellationToken cancellationToken)
        {
            CheckDisposed();

            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null).ToList();

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendRestartRequiredNotification(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in SendRestartRequiredNotification.", ex);
                }

            }, cancellationToken));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends the server shutdown notification.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public Task SendServerShutdownNotification(CancellationToken cancellationToken)
        {
            CheckDisposed();

            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null).ToList();

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendServerShutdownNotification(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in SendServerShutdownNotification.", ex);
                }

            }, cancellationToken));

            return Task.WhenAll(tasks);
        }

        /// <summary>
        /// Sends the server restart notification.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public Task SendServerRestartNotification(CancellationToken cancellationToken)
        {
            CheckDisposed();

            _logger.Debug("Beginning SendServerRestartNotification");

            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null).ToList();

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendServerRestartNotification(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in SendServerRestartNotification.", ex);
                }

            }, cancellationToken));

            return Task.WhenAll(tasks);
        }

        private Task SendSessionEndedNotification(SessionInfo sessionInfo, CancellationToken cancellationToken)
        {
            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null).ToList();
            var dto = GetSessionInfoDto(sessionInfo);

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendSessionEndedNotification(dto, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in SendSessionEndedNotification.", ex);
                }

            }, cancellationToken));

            return Task.WhenAll(tasks);
        }

        private Task SendPlaybackStartNotification(SessionInfo sessionInfo, CancellationToken cancellationToken)
        {
            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null).ToList();
            var dto = GetSessionInfoDto(sessionInfo);

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendPlaybackStartNotification(dto, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in SendPlaybackStartNotification.", ex);
                }

            }, cancellationToken));

            return Task.WhenAll(tasks);
        }

        private Task SendPlaybackStoppedNotification(SessionInfo sessionInfo, CancellationToken cancellationToken)
        {
            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null).ToList();
            var dto = GetSessionInfoDto(sessionInfo);

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendPlaybackStoppedNotification(dto, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error in SendPlaybackStoppedNotification.", ex);
                }

            }, cancellationToken));

            return Task.WhenAll(tasks);
        }

        /// <summary>
        /// Adds the additional user.
        /// </summary>
        /// <param name="sessionId">The session identifier.</param>
        /// <param name="userId">The user identifier.</param>
        /// <exception cref="System.UnauthorizedAccessException">Cannot modify additional users without authenticating first.</exception>
        /// <exception cref="System.ArgumentException">The requested user is already the primary user of the session.</exception>
        public void AddAdditionalUser(string sessionId, string userId)
        {
            CheckDisposed();

            var session = GetSession(sessionId);

            if (session.UserId.HasValue && session.UserId.Value == new Guid(userId))
            {
                throw new ArgumentException("The requested user is already the primary user of the session.");
            }

            if (session.AdditionalUsers.All(i => new Guid(i.UserId) != new Guid(userId)))
            {
                var user = _userManager.GetUserById(userId);

                var list = session.AdditionalUsers.ToList();

                list.Add(new SessionUserInfo
                {
                    UserId = userId,
                    UserName = user.Name
                });

                session.AdditionalUsers = list.ToArray(list.Count);
            }
        }

        /// <summary>
        /// Removes the additional user.
        /// </summary>
        /// <param name="sessionId">The session identifier.</param>
        /// <param name="userId">The user identifier.</param>
        /// <exception cref="System.UnauthorizedAccessException">Cannot modify additional users without authenticating first.</exception>
        /// <exception cref="System.ArgumentException">The requested user is already the primary user of the session.</exception>
        public void RemoveAdditionalUser(string sessionId, string userId)
        {
            CheckDisposed();

            var session = GetSession(sessionId);

            if (session.UserId.HasValue && session.UserId.Value == new Guid(userId))
            {
                throw new ArgumentException("The requested user is already the primary user of the session.");
            }

            var user = session.AdditionalUsers.FirstOrDefault(i => new Guid(i.UserId) == new Guid(userId));

            if (user != null)
            {
                var list = session.AdditionalUsers.ToList();
                list.Remove(user);

                session.AdditionalUsers = list.ToArray(list.Count);
            }
        }

        /// <summary>
        /// Authenticates the new session.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>Task{SessionInfo}.</returns>
        public Task<AuthenticationResult> AuthenticateNewSession(AuthenticationRequest request)
        {
            return AuthenticateNewSessionInternal(request, true);
        }

        public Task<AuthenticationResult> CreateNewSession(AuthenticationRequest request)
        {
            return AuthenticateNewSessionInternal(request, false);
        }

        private async Task<AuthenticationResult> AuthenticateNewSessionInternal(AuthenticationRequest request, bool enforcePassword)
        {
            CheckDisposed();

            User user = null;
            if (!string.IsNullOrWhiteSpace(request.UserId))
            {
                var idGuid = new Guid(request.UserId);
                user = _userManager.Users
                    .FirstOrDefault(i => i.Id == idGuid);
            }

            if (user == null)
            {
                user = _userManager.Users
                    .FirstOrDefault(i => string.Equals(request.Username, i.Name, StringComparison.OrdinalIgnoreCase));
            }

            if (user != null)
            {
                if (!user.IsParentalScheduleAllowed())
                {
                    throw new SecurityException("User is not allowed access at this time.");
                }

                if (!string.IsNullOrWhiteSpace(request.DeviceId))
                {
                    if (!_deviceManager.CanAccessDevice(user.Id.ToString("N"), request.DeviceId))
                    {
                        throw new SecurityException("User is not allowed access from this device.");
                    }
                }
            }

            if (enforcePassword)
            {
                var result = await _userManager.AuthenticateUser(request.Username, request.Password, request.PasswordSha1, request.PasswordMd5, request.RemoteEndPoint, true).ConfigureAwait(false);

                if (result == null)
                {
                    EventHelper.FireEventIfNotNull(AuthenticationFailed, this, new GenericEventArgs<AuthenticationRequest>(request), _logger);

                    throw new SecurityException("Invalid user or password entered.");
                }

                user = result;
            }

            var token = GetAuthorizationToken(user.Id.ToString("N"), request.DeviceId, request.App, request.AppVersion, request.DeviceName);

            EventHelper.FireEventIfNotNull(AuthenticationSucceeded, this, new GenericEventArgs<AuthenticationRequest>(request), _logger);

            var session = await LogSessionActivity(request.App,
                request.AppVersion,
                request.DeviceId,
                request.DeviceName,
                request.RemoteEndPoint,
                user)
                .ConfigureAwait(false);

            return new AuthenticationResult
            {
                User = _userManager.GetUserDto(user, request.RemoteEndPoint),
                SessionInfo = GetSessionInfoDto(session),
                AccessToken = token,
                ServerId = _appHost.SystemId
            };
        }


        private string GetAuthorizationToken(string userId, string deviceId, string app, string appVersion, string deviceName)
        {
            var existing = _authRepo.Get(new AuthenticationInfoQuery
            {
                DeviceId = deviceId,
                IsActive = true,
                UserId = userId,
                Limit = 1
            });

            if (existing.Items.Length > 0)
            {
                var token = existing.Items[0].AccessToken;
                _logger.Info("Reissuing access token: " + token);
                return token;
            }

            var newToken = new AuthenticationInfo
            {
                AppName = app,
                AppVersion = appVersion,
                DateCreated = DateTime.UtcNow,
                DeviceId = deviceId,
                DeviceName = deviceName,
                UserId = userId,
                IsActive = true,
                AccessToken = Guid.NewGuid().ToString("N")
            };

            _logger.Info("Creating new access token for user {0}", userId);
            _authRepo.Create(newToken, CancellationToken.None);

            return newToken.AccessToken;
        }

        public void Logout(string accessToken)
        {
            CheckDisposed();

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new ArgumentNullException("accessToken");
            }

            _logger.Info("Logging out access token {0}", accessToken);

            var existing = _authRepo.Get(new AuthenticationInfoQuery
            {
                Limit = 1,
                AccessToken = accessToken

            }).Items.FirstOrDefault();

            if (existing != null)
            {
                existing.IsActive = false;

                _authRepo.Update(existing, CancellationToken.None);

                var sessions = Sessions
                    .Where(i => string.Equals(i.DeviceId, existing.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var session in sessions)
                {
                    try
                    {
                        ReportSessionEnded(session.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Error reporting session ended", ex);
                    }
                }
            }
        }

        public void RevokeUserTokens(string userId, string currentAccessToken)
        {
            CheckDisposed();

            var existing = _authRepo.Get(new AuthenticationInfoQuery
            {
                IsActive = true,
                UserId = userId
            });

            foreach (var info in existing.Items)
            {
                if (!string.Equals(currentAccessToken, info.AccessToken, StringComparison.OrdinalIgnoreCase))
                {
                    Logout(info.AccessToken);
                }
            }
        }

        public void RevokeToken(string token)
        {
            Logout(token);
        }

        /// <summary>
        /// Reports the capabilities.
        /// </summary>
        /// <param name="sessionId">The session identifier.</param>
        /// <param name="capabilities">The capabilities.</param>
        public void ReportCapabilities(string sessionId, ClientCapabilities capabilities)
        {
            CheckDisposed();

            var session = GetSession(sessionId);

            ReportCapabilities(session, capabilities, true);
        }

        private void ReportCapabilities(SessionInfo session,
            ClientCapabilities capabilities,
            bool saveCapabilities)
        {
            session.Capabilities = capabilities;

            if (!string.IsNullOrWhiteSpace(capabilities.MessageCallbackUrl))
            {
                var controller = session.SessionController as HttpSessionController;

                if (controller == null)
                {
                    session.SessionController = new HttpSessionController(_httpClient, _jsonSerializer, session, capabilities.MessageCallbackUrl, this);
                }
            }

            EventHelper.FireEventIfNotNull(CapabilitiesChanged, this, new SessionEventArgs
            {
                SessionInfo = session

            }, _logger);

            if (saveCapabilities)
            {
                try
                {
                    SaveCapabilities(session.DeviceId, capabilities);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error saving device capabilities", ex);
                }
            }
        }

        private ClientCapabilities GetSavedCapabilities(string deviceId)
        {
            return _deviceManager.GetCapabilities(deviceId);
        }

        private void SaveCapabilities(string deviceId, ClientCapabilities capabilities)
        {
            _deviceManager.SaveCapabilities(deviceId, capabilities);
        }

        public SessionInfoDto GetSessionInfoDto(SessionInfo session)
        {
            var dto = new SessionInfoDto
            {
                Client = session.Client,
                DeviceId = session.DeviceId,
                DeviceName = session.DeviceName,
                Id = session.Id,
                LastActivityDate = session.LastActivityDate,
                NowViewingItem = session.NowViewingItem,
                ApplicationVersion = session.ApplicationVersion,
                PlayableMediaTypes = session.PlayableMediaTypes,
                AdditionalUsers = session.AdditionalUsers,
                SupportedCommands = session.SupportedCommands,
                UserName = session.UserName,
                NowPlayingItem = session.NowPlayingItem,
                SupportsRemoteControl = session.SupportsMediaControl,
                PlayState = session.PlayState,
                AppIconUrl = session.AppIconUrl,
                TranscodingInfo = session.NowPlayingItem == null ? null : session.TranscodingInfo
            };

            dto.ServerId = _appHost.SystemId;

            if (session.UserId.HasValue)
            {
                dto.UserId = session.UserId.Value.ToString("N");

                var user = _userManager.GetUserById(session.UserId.Value);

                if (user != null)
                {
                    dto.UserPrimaryImageTag = GetImageCacheTag(user, ImageType.Primary);
                }
            }

            return dto;
        }

        private DtoOptions _itemInfoDtoOptions;

        /// <summary>
        /// Converts a BaseItem to a BaseItemInfo
        /// </summary>
        private BaseItemDto GetItemInfo(BaseItem item, MediaSourceInfo mediaSource)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }

            var dtoOptions = _itemInfoDtoOptions;

            if (_itemInfoDtoOptions == null)
            {
                dtoOptions = new DtoOptions
                {
                    AddProgramRecordingInfo = false
                };

                var fields = dtoOptions.Fields.ToList();

                fields.Remove(ItemFields.BasicSyncInfo);
                fields.Remove(ItemFields.SyncInfo);
                fields.Remove(ItemFields.CanDelete);
                fields.Remove(ItemFields.CanDownload);
                fields.Remove(ItemFields.ChildCount);
                fields.Remove(ItemFields.CustomRating);
                fields.Remove(ItemFields.DateLastMediaAdded);
                fields.Remove(ItemFields.DateLastRefreshed);
                fields.Remove(ItemFields.DateLastSaved);
                fields.Remove(ItemFields.DisplayPreferencesId);
                fields.Remove(ItemFields.Etag);
                fields.Remove(ItemFields.ExternalEtag);
                fields.Remove(ItemFields.InheritedParentalRatingValue);
                fields.Remove(ItemFields.ItemCounts);
                fields.Remove(ItemFields.MediaSourceCount);
                fields.Remove(ItemFields.MediaStreams);
                fields.Remove(ItemFields.MediaSources);
                fields.Remove(ItemFields.People);
                fields.Remove(ItemFields.PlayAccess);
                fields.Remove(ItemFields.People);
                fields.Remove(ItemFields.ProductionLocations);
                fields.Remove(ItemFields.RecursiveItemCount);
                fields.Remove(ItemFields.RemoteTrailers);
                fields.Remove(ItemFields.SeasonUserData);
                fields.Remove(ItemFields.Settings);
                fields.Remove(ItemFields.SortName);
                fields.Remove(ItemFields.Tags);
                fields.Remove(ItemFields.ThemeSongIds);
                fields.Remove(ItemFields.ThemeVideoIds);

                dtoOptions.Fields = fields.ToArray(fields.Count);

                _itemInfoDtoOptions = dtoOptions;
            }

            var info = _dtoService.GetBaseItemDto(item, dtoOptions);

            if (mediaSource != null)
            {
                info.MediaStreams = mediaSource.MediaStreams.ToArray();
            }

            return info;
        }

        private string GetImageCacheTag(BaseItem item, ImageType type)
        {
            try
            {
                return _imageProcessor.GetImageCacheTag(item, type);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting {0} image info", ex, type);
                return null;
            }
        }

        private string GetDtoId(BaseItem item)
        {
            return _dtoService.GetDtoId(item);
        }

        public void ReportNowViewingItem(string sessionId, string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                throw new ArgumentNullException("itemId");
            }

            //var item = _libraryManager.GetItemById(new Guid(itemId));

            //var info = GetItemInfo(item, null, null);

            //ReportNowViewingItem(sessionId, info);
        }

        public void ReportNowViewingItem(string sessionId, BaseItemDto item)
        {
            //var session = GetSession(sessionId);

            //session.NowViewingItem = item;
        }

        public void ReportTranscodingInfo(string deviceId, TranscodingInfo info)
        {
            var session = Sessions.FirstOrDefault(i => string.Equals(i.DeviceId, deviceId));

            if (session != null)
            {
                session.TranscodingInfo = info;
            }
        }

        public void ClearTranscodingInfo(string deviceId)
        {
            ReportTranscodingInfo(deviceId, null);
        }

        public SessionInfo GetSession(string deviceId, string client, string version)
        {
            return Sessions.FirstOrDefault(i => string.Equals(i.DeviceId, deviceId) &&
                string.Equals(i.Client, client));
        }

        public Task<SessionInfo> GetSessionByAuthenticationToken(AuthenticationInfo info, string deviceId, string remoteEndpoint, string appVersion)
        {
            if (info == null)
            {
                throw new ArgumentNullException("info");
            }

            var user = string.IsNullOrWhiteSpace(info.UserId)
                ? null
                : _userManager.GetUserById(info.UserId);

            appVersion = string.IsNullOrWhiteSpace(appVersion)
                ? info.AppVersion
                : appVersion;

            var deviceName = info.DeviceName;
            var appName = info.AppName;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                // Replace the info from the token with more recent info
                var device = _deviceManager.GetDevice(deviceId);
                if (device != null)
                {
                    deviceName = device.Name;
                    appName = device.AppName;

                    if (!string.IsNullOrWhiteSpace(device.AppVersion))
                    {
                        appVersion = device.AppVersion;
                    }
                }
            }
            else
            {
                deviceId = info.DeviceId;
            }

            // Prevent argument exception
            if (string.IsNullOrWhiteSpace(appVersion))
            {
                appVersion = "1";
            }

            return LogSessionActivity(appName, appVersion, deviceId, deviceName, remoteEndpoint, user);
        }

        public Task<SessionInfo> GetSessionByAuthenticationToken(string token, string deviceId, string remoteEndpoint)
        {
            var result = _authRepo.Get(new AuthenticationInfoQuery
            {
                AccessToken = token
            });

            var info = result.Items.FirstOrDefault();

            if (info == null)
            {
                return Task.FromResult<SessionInfo>(null);
            }

            return GetSessionByAuthenticationToken(info, deviceId, remoteEndpoint, null);
        }

        public Task SendMessageToAdminSessions<T>(string name, T data, CancellationToken cancellationToken)
        {
            CheckDisposed();

            var adminUserIds = _userManager.Users.Where(i => i.Policy.IsAdministrator).Select(i => i.Id.ToString("N")).ToList();

            return SendMessageToUserSessions(adminUserIds, name, data, cancellationToken);
        }

        public Task SendMessageToUserSessions<T>(List<string> userIds, string name, T data,
            CancellationToken cancellationToken)
        {
            CheckDisposed();

            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null && userIds.Any(i.ContainsUser)).ToList();

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendMessage(name, data, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error sending message", ex);
                }

            }, cancellationToken));

            return Task.WhenAll(tasks);
        }

        public Task SendMessageToUserDeviceSessions<T>(string deviceId, string name, T data,
            CancellationToken cancellationToken)
        {
            CheckDisposed();

            var sessions = Sessions.Where(i => i.IsActive && i.SessionController != null && string.Equals(i.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToList();

            var tasks = sessions.Select(session => Task.Run(async () =>
            {
                try
                {
                    await session.SessionController.SendMessage(name, data, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error sending message", ex);
                }

            }, cancellationToken));

            return Task.WhenAll(tasks);
        }
    }
}