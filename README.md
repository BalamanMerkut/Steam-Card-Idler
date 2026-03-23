# 🎮 Steam Card Idler

A WPF desktop application for automating Steam trading card farming and playtime boosting. Built with C# / .NET 8 and SteamKit2.

![Platform](https://img.shields.io/badge/Platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Version](https://img.shields.io/badge/Version-1.0.0-green)
![License](https://img.shields.io/badge/License-MIT-yellow)

---

## ✨ Features

- **Card Farming** — Automatically idles games with remaining card drops
- **Playtime Boosting** — Run any game from your library to accumulate hours
- **VAC Detection** — Identifies VAC-protected games and supports blacklisting
- **Steam Market Pricing** — Estimates card value using Steam Market API
- **Progress Tracking** — Shows X/Y cards dropped per game with a progress bar
- **Multi-language** — English, Turkish, Spanish, Italian, German, French, Russian, Chinese
- **System Tray** — Minimizes to tray, runs in background with balloon notifications
- **Logout** — Full session reset without restarting the application
- **Auto Actions** — Optionally shut down PC or close app when farming completes

---

## 🏗️ Architecture

```
SteamCardIdler/
├── MainViewModel.cs          # Main application logic (MVVM)
├── MainWindow.xaml/.cs       # Shell window, tray icon
├── App.xaml/.cs              # App entry, global exception handler
├── Converters.cs             # WPF value converters
├── Services/
│   ├── SteamService.cs       # Steam connection, auth, game playback (SteamKit2)
│   ├── SteamWebService.cs    # Web session, badge scraping, Market API, VAC check
│   └── LocalizationService.cs# Multi-language string dictionary
├── Views/
│   ├── DashboardView.xaml    # Main dashboard (game grid, controls, log)
│   └── LoginView.xaml        # Login screen with language selector
├── Helpers/
│   └── PasswordBoxHelper.cs  # MVVM-safe PasswordBox binding
└── Resources/
    └── tray.ico              # System tray icon
```

### Key Design Decisions

- **MVVM** via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- **Two-cache system** — `_cardGames` (badge scrape) and `_allGames` (full library via API). Toggling the playtime mode switches the view without re-fetching.
- **VAC detection** — Each game queried individually against `appdetails` API (10 parallel requests), because batch requests only return the first result.
- **Localization** — `L` function is injected into `SteamService` and `SteamWebService` so log messages respect the selected language.

---

## 🚀 Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Windows 10/11 (WPF is Windows-only)
- A Steam account

### Running from source

```bash
git clone https://github.com/your-username/SteamCardIdler.git
cd SteamCardIdler
dotnet run
```

### Building a single-file EXE

Run `build_exe.bat` or:

```bash
dotnet publish SteamCardIdler.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./Publish
```

The EXE will be in `./Publish/SteamCardIdler.exe`.

---

## 📦 Dependencies

| Package | Version | Purpose |
|---|---|---|
| SteamKit2 | 3.4.0 | Steam network protocol |
| SteamAuth | 3.0.0 | Steam Guard / 2FA |
| HtmlAgilityPack | 1.12.4 | Badge page scraping |
| CommunityToolkit.Mvvm | 8.2.2 | MVVM helpers |
| WPF-UI | 3.0.0 | Fluent/Mica UI controls |
| Hardcodet.NotifyIcon.Wpf | 1.1.0 | System tray icon |
| Newtonsoft.Json | 13.0.3 | JSON parsing |

---

## 🔧 How It Works

1. **Login** — Authenticates via SteamKit2's modern auth flow (supports Steam Guard mobile and email). Access token is used for web API calls.
2. **Scanning** — Scrapes the user's Steam badge page to find games with remaining card drops. Also fetches full library via `IPlayerService/GetOwnedGames` for playtime mode.
3. **Farming** — Sends `ClientGamesPlayed` messages to Steam every 15 minutes. Steam registers the session as actively playing, which triggers card drops.
4. **VAC Check** — Queries `store.steampowered.com/api/appdetails` individually per game (10 in parallel) to detect VAC protection.
5. **Market Pricing** — Queries Steam Community Market to estimate card value per game.

---

## 🤝 Contributing

Pull requests are welcome. For major changes, please open an issue first to discuss what you'd like to change.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## ⚠️ Disclaimer

This tool is for educational purposes. Use it responsibly and in accordance with Steam's Terms of Service. The developers are not responsible for any account actions taken by Valve.

---

## 👨‍💻 Developer

Made by **Alejandev**

---

## ☕ Support

If you find this project useful, consider supporting it on [Patreon](https://www.patreon.com/posts/steam-card-idler-153691711). It helps keep the project maintained and new features coming!
