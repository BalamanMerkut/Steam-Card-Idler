using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Threading;
using SteamCardIdler.Services;
using Hardcodet.Wpf.TaskbarNotification;

namespace SteamCardIdler.ViewModels
{
    public enum SortMode { Default, CardCountDesc, CardCountAsc, NameAsc, NameDesc }

    public partial class MainViewModel : ObservableObject
    {
        // UI Properties
        [ObservableProperty] private string _statusText = "Bağlantı Bekleniyor";
        [ObservableProperty] private Brush _statusBrush = Brushes.Yellow;
        [ObservableProperty] private string _pauseButtonText = "Duraklat";
        [ObservableProperty] private bool _filterVacOnly = false;
        [ObservableProperty] private ObservableCollection<string> _logEntries = new();
        [ObservableProperty] private bool _isIdling = false;
        [ObservableProperty] private bool _isPaused = false;

        // Login Properties
        [ObservableProperty] private bool _isLoggedIn = false;
        [ObservableProperty] private bool _isSettingsOpen = false;
        [ObservableProperty] private bool _isSummaryOpen = false;
        [ObservableProperty] private string _username = "";
        [ObservableProperty] private string _password = "";
        [ObservableProperty] private string _guardCode = "";
        [ObservableProperty] private bool _isGuardRequired = false;
        [ObservableProperty] private string _loginErrorMessage = "";
        [ObservableProperty] private bool _isLoggingIn = false;
        [ObservableProperty] private string _lastLogMessage = "";
        [ObservableProperty] private UserProfile _userProfile = new();
        [ObservableProperty] private bool _isRefreshingGames = false;
        [ObservableProperty] private string _scanProgressText = "";

        // Settings Properties
        [ObservableProperty] private bool _autoCloseApp = false;
        [ObservableProperty] private bool _autoShutdownPc = false;
        [ObservableProperty] private bool _showZeroCardGames = false;
        [ObservableProperty] private bool _minimizeToTrayOnClose = true;
        [ObservableProperty] private ObservableCollection<int> _blacklistAppIds = new();
        [ObservableProperty] private ObservableCollection<string> _languages = new() { "English", "Turkish", "Spanish", "Italian", "German", "French", "Russian", "Chinese" };
        [ObservableProperty] private string _selectedLanguage = "Turkish";

        // Summary Properties
        [ObservableProperty] private string _summaryTime = "00:00:00";
        [ObservableProperty] private string _summaryCards = "0 Kart Düşürüldü";
        [ObservableProperty] private string _summaryGames = "0 Oyun Tamamlandı";

        // Sorting
        [ObservableProperty] private SortMode _currentSortMode = SortMode.Default;
        partial void OnCurrentSortModeChanged(SortMode value) => OnPropertyChanged(nameof(SortedGames));

        // Total estimated value
        [ObservableProperty] private string _totalEstimatedValue = "";

        // Services
        private readonly SteamService _steamService;
        private readonly LocalizationService _locService = new();
        private TaskbarIcon? _trayIcon;

        public System.Collections.Generic.Dictionary<string, string> Strings => _locService.GetTranslations(SelectedLanguage);

        // ── Oyun cache ──────────────────────────────────────────────────────
        private List<SteamGame> _cardGames = new();  // Games with remaining card drops
        private List<SteamGame> _allGames  = new();  // Full library (playtime boost mode)
        private bool _libraryLoaded = false;
        // ────────────────────────────────────────────────────────────────────

        public ObservableCollection<SteamGame> Games { get; set; } = new();
        public bool HasNoGames => !IsRefreshingGames && Games.Count == 0;

        /// <summary>Filtered and sorted game list from cache. No API call is made.</summary>
        public IEnumerable<SteamGame> SortedGames
        {
            get
            {
                var baseSource = ShowZeroCardGames ? _allGames.AsEnumerable() : _cardGames.AsEnumerable();
                var source = FilterVacOnly ? baseSource.Where(g => g.IsVacProtected) : baseSource;
                return CurrentSortMode switch
                {
                    SortMode.CardCountDesc => source.OrderByDescending(g => g.RemainingCards).ToList(),
                    SortMode.CardCountAsc  => source.OrderBy(g => g.RemainingCards).ToList(),
                    SortMode.NameAsc       => source.OrderBy(g => g.Name).ToList(),
                    SortMode.NameDesc      => source.OrderByDescending(g => g.Name).ToList(),
                    _                      => source.ToList()
                };
            }
        }

        partial void OnFilterVacOnlyChanged(bool value) => OnPropertyChanged(nameof(SortedGames));

        partial void OnShowZeroCardGamesChanged(bool value)
        {
            if (value && !_libraryLoaded)
            {
                _ = LoadLibraryAsync();
                return;
            }
            RebuildGamesFromCache();
        }

        partial void OnSelectedLanguageChanged(string value)
        {
            OnPropertyChanged(nameof(Strings));
            _steamService.L = key => Strings.ContainsKey(key) ? Strings[key] : key;
            PauseButtonText = IsPaused
                ? Strings["Resume"]
                : Strings["Pause"];
            
            if (IsIdling) 
                StatusText = Strings["StatusRunning"];
            else if (IsLoggedIn)
                StatusText = Strings["StatusConnected"];
            else
                StatusText = Strings["StatusWaiting"];
        }

        // Summary tracking
        private DateTime _idleStartTime;
        private int _cardsBeforeIdle;
        private int _completedGamesCount;

        private CancellationTokenSource? _idleCts;

        public MainViewModel()
        {
            _steamService = new SteamService();
            _steamService.OnLog += AddLog;
            _steamService.L = key => Strings.ContainsKey(key) ? Strings[key] : key;
            _steamService.OnLoginSuccess += async (profile) =>
            {
                AddLog(string.Format(Strings["LoginSuccess"], profile.PersonaName));

                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    UserProfile = profile;
                    IsLoggedIn = true;
                    IsLoggingIn = false;
                    StatusText = Strings["StatusConnected"];
                    StatusBrush = Brushes.Green;
                    ShowTrayBalloon(Strings["LoginButton"], string.Format(Strings["TrayLoginSuccess"], profile.PersonaName), BalloonIcon.Info);
                });

                await RefreshGames();
            };
            _steamService.OnUserProfileUpdated += (profile) =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    UserProfile = new UserProfile { PersonaName = profile.PersonaName, AvatarUrl = profile.AvatarUrl };
                });
            };
            _steamService.OnScanProgress += (scanned, total, found) =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    ScanProgressText = $"Oyunlar taranıyor ({scanned}/{total}) - Kartlı Oyun: {found}";
                });
            };
            _steamService.OnLoginFailure += (err) =>
            {
                LoginErrorMessage = $"Giriş Hatası: {err}";
                IsLoggingIn = false;
                if (err.Contains("TwoFactorCodeMismatch") || err.Contains("AccountLogonDenied"))
                    IsGuardRequired = true;
            };
            _steamService.OnGuardRequired += () =>
            {
                IsLoggingIn = false;
                IsGuardRequired = true;
                LoginErrorMessage = "";
                AddLog(Strings["GuardRequired"]);
            };

            AddLog(Strings["AppStarted"]);
        }

        public void SetTrayIcon(TaskbarIcon trayIcon) => _trayIcon = trayIcon;

        private void ShowTrayBalloon(string title, string message, BalloonIcon icon = BalloonIcon.None)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                _trayIcon?.ShowBalloonTip(title, message, icon));
        }

        // ── Sort Commands ────────────────────────────────────────────────

        [RelayCommand] private void SortByCardCountDesc() => CurrentSortMode = SortMode.CardCountDesc;
        [RelayCommand] private void SortByCardCountAsc()  => CurrentSortMode = SortMode.CardCountAsc;
        [RelayCommand] private void SortByNameAsc()        => CurrentSortMode = SortMode.NameAsc;
        [RelayCommand] private void SortByNameDesc()       => CurrentSortMode = SortMode.NameDesc;
        [RelayCommand] private void SortDefault()          => CurrentSortMode = SortMode.Default;

        // ────────────────────────────────────────────────────────────────────

        [RelayCommand]
        private void OpenPatreon()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.patreon.com/posts/steam-card-idler-153691711") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AddLog($"Patreon linki açılamadı: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task Login()
        {
            if (IsLoggingIn) return;

            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                LoginErrorMessage = "Kullanıcı adı ve şifre gereklidir.";
                return;
            }

            try
            {
                IsLoggingIn = true;
                LoginErrorMessage = "";
                AddLog(Strings["Connecting"]);
                await _steamService.ConnectAndLogin(Username, Password, GuardCode);
            }
            catch (Exception ex)
            {
                IsLoggingIn = false;
                LoginErrorMessage = $"Beklenmedik Hata: {ex.Message}";
                AddLog($"KRİTİK HATA: {ex}");
            }
        }

        [RelayCommand] private void OpenSettings() { IsSettingsOpen = true; AddLog("Ayarlar menüsü açıldı."); }
        [RelayCommand] private void CloseSettings() => IsSettingsOpen = false;
        [RelayCommand] private void CloseSummary() => IsSummaryOpen = false;

        [RelayCommand]
        private void ShowWindow()
        {
            System.Windows.Application.Current.MainWindow.Show();
            System.Windows.Application.Current.MainWindow.WindowState = System.Windows.WindowState.Normal;
            System.Windows.Application.Current.MainWindow.Activate();
        }

        [RelayCommand]
        private void Exit()
        {
            _trayIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        [RelayCommand]
        private void Logout()
        {
            if (IsIdling)
            {
                _idleCts?.Cancel();
                IsIdling = false;
            }

            _steamService.Logout();

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Games.Clear();
                _cardGames.Clear();
                _allGames.Clear();
                _libraryLoaded = false;
                LogEntries.Clear();

                UserProfile = new UserProfile();
                Username = "";
                Password = "";
                GuardCode = "";
                IsGuardRequired = false;
                LoginErrorMessage = "";
                IsLoggingIn = false;
                ShowZeroCardGames = false;
                FilterVacOnly = false;
                BlacklistAppIds.Clear();
                StatusText = Strings["StatusWaiting"];
                StatusBrush = System.Windows.Media.Brushes.Yellow;
                TotalEstimatedValue = "";
                ScanProgressText = "";
                IsLoggedIn = false;

                OnPropertyChanged(nameof(SortedGames));
                OnPropertyChanged(nameof(HasNoGames));
                AddLog(Strings["AppStarted"]);
            });
        }

        [RelayCommand] private void SelectAll()   { foreach (var g in Games) g.IsSelected = true;  AddLog("Tüm oyunlar seçildi."); }
        [RelayCommand] private void DeselectAll() { foreach (var g in Games) g.IsSelected = false; AddLog("Seçim kaldırıldı."); }

        [RelayCommand]
        private void StartSingleGame(SteamGame game)
        {
            if (game == null || game.IsBlacklisted) return;

            if (IsIdling)
            {
                _steamService.StopPlaying();
                _idleCts?.Cancel();
                IsIdling = false;
            }

            AddLog(string.Format(Strings["SingleGameStarted"], game.Name));
            _steamService.PlayGames(new[] { game.AppId });
            IsIdling = true;
            StatusText = $"{game.Name} {Strings["Playing"]}...";
            StatusBrush = Brushes.Cyan;
        }

        [RelayCommand]
        private async Task StartIdle()
        {
            if (IsIdling) return;

            IsIdling = true;
            StatusBrush = Brushes.Green;
            StatusText = Strings["StatusRunning"];
            _idleCts = new CancellationTokenSource();

            _idleStartTime = DateTime.Now;
            _cardsBeforeIdle = Games.Sum(g => g.RemainingCards);
            _completedGamesCount = 0;

            // Record InitialCards at start (used by progress bar)
            foreach (var g in Games)
                g.InitialCards = g.RemainingCards;

            bool completedNaturally = false;

            try
            {
                completedNaturally = await RunIdleAlgorithm(_idleCts.Token);
            }
            catch (OperationCanceledException)
            {
                AddLog(Strings["ProcessStoppedByUser"]);
            }
            finally
            {
                IsIdling = false;
                StatusBrush = Brushes.Red;
                StatusText = Strings["StatusStopped"];
                UpdateSummary();

                if (completedNaturally)
                    TriggerAutoAction();
            }
        }

        [RelayCommand]
        private void PauseResume()
        {
            IsPaused = !IsPaused;
            PauseButtonText = IsPaused
                ? _locService.Get("Resume", SelectedLanguage)
                : _locService.Get("Pause", SelectedLanguage);
            
            if (IsPaused)
            {
                _steamService.StopPlaying();
                AddLog(Strings["ProcessPaused"]);
            }
            else
            {
                var remainingToIdle = Games.Where(g => (g.RemainingCards > 0 || ShowZeroCardGames) && g.IsSelected && !g.IsBlacklisted).ToList();
                if (remainingToIdle.Any())
                {
                    _steamService.PlayGames(remainingToIdle.Select(g => g.AppId).Take(32));
                }
                AddLog(Strings["ProcessResumed"]);
            }
        }

        [RelayCommand]
        private void StopIdle()
        {
            _steamService.StopPlaying();
            _idleCts?.Cancel();
            IsIdling = false;
            StatusBrush = Brushes.Red;
            StatusText = Strings["StatusStopped"];
            AddLog(Strings["StopIdle"]);
            UpdateSummary();
            IsSummaryOpen = true;
        }

        private async Task<bool> RunIdleAlgorithm(CancellationToken ct)
        {
            var selectedGames = Games.Where(g => g.IsSelected && !g.IsBlacklisted).ToList();

            if (!selectedGames.Any())
            {
                AddLog(Strings["NoGamesSelected"]);
                return false;
            }

            AddLog(string.Format(Strings["StartingIdle"], selectedGames.Count));
            _steamService.PlayGames(selectedGames.Select(g => g.AppId).Take(32));
            AddLog(Strings["WaitingCards"]);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Delay for 15 minutes roughly, split into 1-second ticks
                    for (int i = 0; i < 15 * 60; i++)
                    {
                        if (ct.IsCancellationRequested) return false;
                        
                        while (IsPaused)
                        {
                            if (ct.IsCancellationRequested) return false;
                            await Task.Delay(1000, ct);
                        }
                        
                        await Task.Delay(1000, ct);
                    }

                    AddLog(Strings["Rescanning"]);
                    // 15-min scan only updates card games, not the full library
                    var updated = await _steamService.GetGamesWithCardDrops(false);
                    _cardGames = updated;
                    foreach (var g in _cardGames) g.IsBlacklisted = BlacklistAppIds.Contains(g.AppId);
                    // Also update RemainingCards in _allGames cache
                    foreach (var u in updated)
                    {
                        var cached = _allGames.FirstOrDefault(a => a.AppId == u.AppId);
                        if (cached != null) cached.RemainingCards = u.RemainingCards;
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        // Detect dropped cards and send tray notification
                        foreach (var old in Games)
                        {
                            var newState = updated.FirstOrDefault(u => u.AppId == old.AppId);
                            
                            if (newState == null)
                            {
                                int dropped = old.RemainingCards;
                                if (dropped > 0)
                                {
                                    old.RemainingCards = 0;
                                    AddLog(string.Format(Strings["CardDropped"], old.Name, dropped, 0));
                                    ShowTrayBalloon(Strings["TrayCardDropped"], string.Format(Strings["TrayCardDroppedMsg"], old.Name, dropped), BalloonIcon.Info);
                                }
                            }
                            else
                            {
                                int dropped = old.RemainingCards - newState.RemainingCards;
                                if (dropped > 0)
                                {
                                    old.RemainingCards = newState.RemainingCards;
                                    AddLog(string.Format(Strings["CardDropped"], old.Name, dropped, newState.RemainingCards));
                                    ShowTrayBalloon(
                                        "Kart Düştü! 🃏",
                                        $"{old.Name}: {dropped} yeni kart",
                                        BalloonIcon.Info
                                    );
                                }
                            }
                        }

                        _completedGamesCount = Games.Count(g => g.RemainingCards == 0 && g.InitialCards > 0);
                        OnPropertyChanged(nameof(SortedGames));
                        UpdateTotalEstimatedValue();
                    });

                    var remainingToIdle = Games.Where(g => (g.RemainingCards > 0 || ShowZeroCardGames) && g.IsSelected && !g.IsBlacklisted).ToList();

                    if (!remainingToIdle.Any())
                    {
                        AddLog(Strings["AllCardsDropped"]);
                        _steamService.StopPlaying();
                        ShowTrayBalloon("🎉", Strings["TrayAllDone"], BalloonIcon.Info);
                        return true;
                    }

                    _steamService.PlayGames(remainingToIdle.Select(g => g.AppId).Take(32));
                }
            }
            catch (OperationCanceledException) { }

            _steamService.StopPlaying();
            return false;
        }

        private void UpdateSummary()
        {
            var elapsed = DateTime.Now - _idleStartTime;
            var cardsDropped = Math.Max(0, _cardsBeforeIdle - Games.Sum(g => g.RemainingCards));

            SummaryTime = elapsed.ToString(@"hh\:mm\:ss");
            SummaryCards = $"{cardsDropped} Kart Düşürüldü";
            SummaryGames = $"{_completedGamesCount} Oyun Tamamlandı";
            IsSummaryOpen = true;
        }

        private void UpdateTotalEstimatedValue()
        {
            var total = Games.Sum(g => g.EstimatedValue);
            TotalEstimatedValue = total > 0 ? $"Tahmini Kazanç: ~${total:F2}" : "";
        }

        private void TriggerAutoAction()
        {
            if (AutoShutdownPc)
            {
                AddLog(Strings["AutoShutdown"]);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 60") { CreateNoWindow = true, UseShellExecute = true });
            }
            else if (AutoCloseApp)
            {
                AddLog(Strings["AutoClose"]);
                MinimizeToTrayOnClose = false;
                _trayIcon?.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void AddLog(string msg)
        {
            var formattedMsg = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                LogEntries.Insert(0, formattedMsg);
                LastLogMessage = msg;
            });
            try { System.IO.File.AppendAllText("debug.log", formattedMsg + Environment.NewLine); } catch { }
        }

        [RelayCommand]
        public async Task RefreshGames()
        {
            if (IsRefreshingGames) return;
            IsRefreshingGames = true;
            _libraryLoaded = false;
            OnPropertyChanged(nameof(HasNoGames));

            // 1. Scan games with card drops
            AddLog(Strings["SvcScanning"]);
            var cardGames = await _steamService.GetGamesWithCardDrops(false);
            AddLog(Strings["VacChecking"]);
            await _steamService.PopulateVacStatus(cardGames);
            foreach (var g in cardGames)
                g.IsBlacklisted = BlacklistAppIds.Contains(g.AppId);
            _cardGames = cardGames;

            // 2. Fetch full library (includes card games)
            AddLog(Strings["SvcLibraryLoading"]);
            var allGames = await _steamService.GetGamesWithCardDrops(true);
            await _steamService.PopulateVacStatus(allGames);
            foreach (var g in allGames)
                g.IsBlacklisted = BlacklistAppIds.Contains(g.AppId);
            _allGames = allGames;
            _libraryLoaded = true;

            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                RebuildGamesFromCache();
                if (_cardGames.Count == 0)
                    AddLog(Strings["NoGamesFound"]);
                else
                    AddLog(string.Format(Strings["GamesListed"], _cardGames.Count));
            });

            OnPropertyChanged(nameof(HasNoGames));
            IsRefreshingGames = false;
            ScanProgressText = "";
        }

        private async Task LoadLibraryAsync()
        {
            IsRefreshingGames = true;
            AddLog(Strings["SvcLibraryLoading"]);
            var allGames = await _steamService.GetGamesWithCardDrops(true);
            await _steamService.PopulateVacStatus(allGames);
            foreach (var g in allGames)
                g.IsBlacklisted = BlacklistAppIds.Contains(g.AppId);
            _allGames = allGames;
            _libraryLoaded = true;
            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                RebuildGamesFromCache();
                AddLog(string.Format(Strings["SvcLibraryLoaded"], _allGames.Count));
            });
            IsRefreshingGames = false;
        }

        private void RebuildGamesFromCache()
        {
            var source = ShowZeroCardGames ? _allGames : _cardGames;
            Games.Clear();
            foreach (var g in source)
                Games.Add(g);
            OnPropertyChanged(nameof(SortedGames));
            OnPropertyChanged(nameof(HasNoGames));
            UpdateTotalEstimatedValue();
        }

        [RelayCommand]
        private void SelectAllBacklist()
        {
            var vacGames = Games.Where(g => g.IsVacProtected).ToList();
            foreach (var game in vacGames)
            {
                if (!BlacklistAppIds.Contains(game.AppId))
                    BlacklistAppIds.Add(game.AppId);
                game.IsBlacklisted = true;
                game.IsSelected = false;
            }
            AddLog(string.Format(Strings["VacBlacklisted"], vacGames.Count));
        }
    }
}
