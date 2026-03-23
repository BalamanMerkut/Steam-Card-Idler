using SteamKit2;
using SteamKit2.Authentication;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamCardIdler.Services
{
    /// <summary>
    /// Handles Steam connection, authentication, and game playback.
    /// Web scraping is delegated to SteamWebService.
    /// </summary>
    public class SteamService
    {
        private readonly SteamClient _client;
        private readonly SteamUser _user;
        private readonly SteamFriends _friends;
        private readonly CallbackManager _manager;
        private readonly SteamAuthentication _auth;
        private readonly SteamWebService _webService;

        private bool _isRunning;
        private string _refreshToken = "";
        private TaskCompletionSource<string>? _guardCodeTcs;
        private TaskCompletionSource<bool> _connectedTcs = new();
        private bool _hasSentLoginSuccess = false;

        public event Action<string>? OnLog;

        /// <summary>Localization lookup — injected by MainViewModel.</summary>
        private Func<string, string> _l = key => key;
        public Func<string, string> L
        {
            get => _l;
            set { _l = value; if (_webService != null) _webService.L = value; }
        }

        private string Fmt(string key, params object[] args)
        {
            var template = L(key);
            return args.Length > 0 ? string.Format(template, args) : template;
        }
        public event Action<int, int, int>? OnScanProgress;
        public event Action<UserProfile>? OnLoginSuccess;
        public event Action<UserProfile>? OnUserProfileUpdated;
        public event Action<string>? OnLoginFailure;
        public event Action? OnGuardRequired;

        public SteamService()
        {
            _client = new SteamClient();
            _manager = new CallbackManager(_client);
            _user = _client.GetHandler<SteamUser>()!;
            _friends = _client.GetHandler<SteamFriends>()!;
            _auth = _client.Authentication;

            _webService = new SteamWebService(_client);
            _webService.OnLog += msg => OnLog?.Invoke(msg);
            _webService.OnScanProgress += (s, t, f) => OnScanProgress?.Invoke(s, t, f);

            _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _manager.Subscribe<SteamFriends.PersonaStateCallback>(async cb => {
                if (cb.FriendID == _client.SteamID)
                {
                    var profileData = await _webService.FetchProfileData(_client.SteamID!);
                    string name = string.IsNullOrEmpty(cb.Name) ? profileData.Name : cb.Name;
                    if (string.IsNullOrEmpty(name)) name = "Steam Kullanıcısı";
                    OnUserProfileUpdated?.Invoke(new UserProfile { PersonaName = name, AvatarUrl = profileData.Avatar });
                }
            });

            _ = Task.Run(() => {
                while (true)
                {
                    try { _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50)); }
                    catch { }
                }
            });
        }

        public async Task ConnectAndLogin(string user, string pass, string guard = "")
        {
            try
            {
                _hasSentLoginSuccess = false;
                
                if (!_client.IsConnected)
                {
                    _client.Connect();
                    OnLog?.Invoke(Fmt("SvcConnecting"));
                }

                if (!await WaitForConnection(TimeSpan.FromSeconds(30)))
                {
                    OnLog?.Invoke(Fmt("SvcConnTimeout"));
                    OnLoginFailure?.Invoke(Fmt("SvcConnFailed"));
                    _isRunning = false;
                    _client.Disconnect();
                    return;
                }

                OnLog?.Invoke(Fmt("SvcAuthenticating"));

                var authSession = await _auth.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = user,
                    Password = pass,
                    IsPersistentSession = true,
                    Authenticator = new SteamAuthenticator(this, guard)
                });

                var steamID = authSession.SteamID;
                var result = await authSession.PollingWaitForResultAsync();
                _refreshToken = result.RefreshToken;

                // AccessToken: used for Steam Web API calls (GetOwnedGames etc.)
                await _webService.FetchWebCookies(steamID, result.RefreshToken, result.AccessToken);

                OnLog?.Invoke(Fmt("SvcLoggingIn"));

                _user.LogOn(new SteamUser.LogOnDetails
                {
                    Username = user,
                    AccessToken = result.RefreshToken,
                    ShouldRememberPassword = true
                });
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(Fmt("SvcLoginFailed", ex.Message));
                // failure already logged above
                _isRunning = false;
            }
        }

        public Task<List<SteamGame>> GetGamesWithCardDrops(bool showZeroCardGames = false)
            => _webService.GetGamesWithCardDrops(showZeroCardGames);

        public Task PopulateVacStatus(List<SteamGame> games)
            => _webService.PopulateVacStatus(games);

        public void PlayGames(IEnumerable<int> appIds)
        {
            var list = new List<int>(appIds);
            if (list.Count == 0)
            {
                OnLog?.Invoke(Fmt("SvcPlayGamesEmpty"));
                return;
            }

            // debug removed

            try
            {
                // Ensure user appears Online on Steam
                _friends.SetPersonaState(EPersonaState.Online);

                var gamesPlayed = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(
                    EMsg.ClientGamesPlayed);

                foreach (var appId in list)
                {
                    gamesPlayed.Body.games_played.Add(new SteamKit2.Internal.CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = new GameID((uint)appId).ToUInt64()
                    });
                }

                _client.Send(gamesPlayed);
                OnLog?.Invoke(Fmt("SvcGamesStarted", list.Count));
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(Fmt("SvcApiError", ex.Message));
            }
        }

        public void StopPlaying()
        {
            var gamesPlayed = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(
                EMsg.ClientGamesPlayed);

            _client.Send(gamesPlayed);
            OnLog?.Invoke(Fmt("SvcGamesStopped"));
        }

        public void Logout()
        {
            try
            {
                if (_client.IsConnected)
                {
                    StopPlaying();
                    _user.LogOff();
                    _client.Disconnect();
                }
                _isRunning = false;
                _refreshToken = "";
                _hasSentLoginSuccess = false;
            }
            catch { }
            _webService.ClearSession();
            OnLog?.Invoke(Fmt("SvcDisconnected"));
        }

        public void SubmitGuardCode(string code) => _guardCodeTcs?.TrySetResult(code);

        public Task<string> WaitForGuardCode()
        {
            _guardCodeTcs = new TaskCompletionSource<string>();
            return _guardCodeTcs.Task;
        }

        private void OnConnected(SteamClient.ConnectedCallback cb)
        {
            OnLog?.Invoke(Fmt("SvcConnected"));
            _connectedTcs.TrySetResult(true);
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback cb)
        {
            OnLog?.Invoke(Fmt("SvcDisconnected"));
            _connectedTcs.TrySetResult(false);
            _isRunning = false;
        }

        private async Task<bool> WaitForConnection(TimeSpan timeout)
        {
            _connectedTcs = new TaskCompletionSource<bool>();
            var completedTask = await Task.WhenAny(_connectedTcs.Task, Task.Delay(timeout));
            return completedTask == _connectedTcs.Task && await _connectedTcs.Task;
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                if (!_hasSentLoginSuccess)
                {
                    _hasSentLoginSuccess = true;
                    
                    // Fetch avatar and name in background, then fire the event
                    _ = Task.Run(async () =>
                    {
                        var profileData = await _webService.FetchProfileData(_client.SteamID!);
                        string name = profileData.Name;
                        if (string.IsNullOrEmpty(name)) name = _friends.GetPersonaName();
                        if (string.IsNullOrEmpty(name)) name = "Steam Kullanıcısı";

                        System.Windows.Application.Current.Dispatcher.Invoke(() => 
                        {
                            OnLoginSuccess?.Invoke(new UserProfile { PersonaName = name, AvatarUrl = profileData.Avatar });
                        });
                    });
                }
                
                // Removed: _ = GetGamesWithCardDrops(); RefreshGames is called when Dashboard loads.
            }
            else
            {
                OnLoginFailure?.Invoke(Fmt("SvcLoginFailed", callback.Result));
                _isRunning = false;
            }
        }

        private class SteamAuthenticator : IAuthenticator
        {
            private readonly SteamService _service;
            private readonly string _prefilledCode;

            public SteamAuthenticator(SteamService service, string code)
            {
                _service = service;
                _prefilledCode = code;
            }

            public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            {
                if (!string.IsNullOrEmpty(_prefilledCode) && !previousCodeWasIncorrect)
                    return Task.FromResult(_prefilledCode);
                _service.OnGuardRequired?.Invoke();
                return _service.WaitForGuardCode();
            }

            public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            {
                _service.OnGuardRequired?.Invoke();
                return _service.WaitForGuardCode();
            }

            public Task<bool> AcceptDeviceConfirmationAsync()
            {
                _service.OnLog?.Invoke(_service.Fmt("SvcMobileApprove"));
                return Task.FromResult(true);
            }
        }
    }

    public class UserProfile
    {
        public string PersonaName { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
    }

    public partial class SteamGame : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private bool _isSelected = false;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private bool _isBlacklisted = false;

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(CardsRemainingText))]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ProgressText))]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ProgressPercent))]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(DroppedCards))]
        private int _remainingCards;

        public string Name { get; set; } = "";
        public int AppId { get; set; }
        public double PlaytimeHours { get; set; }
        public bool IsVacProtected { get; set; }

        /// <summary>
        /// Initial card count recorded when idling starts (used for progress bar).
        /// </summary>
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ProgressText))]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ProgressPercent))]
        [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(DroppedCards))]
        private int _initialCards;

        /// <summary>
        /// Estimated card drop value fetched from Steam Market (USD).
        /// </summary>
        public double EstimatedValue { get; set; }

        public string HeaderUrl => $"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/header.jpg";
        public string CardsRemainingText => $"{RemainingCards} Kart Kaldı";

        /// <summary>Progress text: how many cards dropped vs. initial count.</summary>
        public string ProgressText => InitialCards > 0
            ? $"{InitialCards - RemainingCards} / {InitialCards} düştü"
            : $"{RemainingCards} kart kaldı";

        public int DroppedCards => InitialCards > 0 ? (InitialCards - RemainingCards) : 0;

        /// <summary>Progress percentage (0-100) for the progress bar.</summary>
        public double ProgressPercent
        {
            get
            {
                if (InitialCards <= 0) return 0;
                double val = Math.Round((double)(InitialCards - RemainingCards) / InitialCards * 100, 1);
                if (val < 0) return 0;
                if (val > 100) return 100;
                if (double.IsNaN(val) || double.IsInfinity(val)) return 0;
                return val;
            }
        }

        /// <summary>Estimated earnings label text.</summary>
        public string EstimatedValueText => EstimatedValue > 0
            ? $"~${EstimatedValue:F2}"
            : "";
    }
}
