using SteamKit2;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace SteamCardIdler.Services
{
    /// <summary>
    /// Manages web sessions, badge/card scraping, Market pricing, and VAC detection.
    /// </summary>
    public class SteamWebService
    {
        private readonly SteamClient _client;
        private string _sessionID = "";
        private string _steamLoginSecure = "";
        private string _accessToken = ""; // Used for Steam Web API calls

        public event Action<string>? OnLog;

        /// <summary>Localization lookup — set by SteamService.</summary>
        public Func<string, string> L { get; set; } = key => key;

        private string Fmt(string key, params object[] args)
        {
            var template = L(key);
            return args.Length > 0 ? string.Format(template, args) : template;
        }
        public event Action<int, int, int>? OnScanProgress;

        public SteamWebService(SteamClient client)
        {
            _client = client;
        }

        public async Task<(string Avatar, string Name)> FetchProfileData(SteamID steamID)
        {
            string avatar = "https://avatars.akamai.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb_full.jpg";
            string name = "";
            try
            {
                using var http = new System.Net.Http.HttpClient();
                var xml = await http.GetStringAsync($"https://steamcommunity.com/profiles/{steamID.ConvertToUInt64()}?xml=1");
                
                var matchAvatar = Regex.Match(xml, @"<avatarFull><!\[CDATA\[(.*?)\]\]></avatarFull>");
                if (matchAvatar.Success) avatar = matchAvatar.Groups[1].Value;

                var matchName = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>");
                if (matchName.Success) name = matchName.Groups[1].Value;
            }
            catch { }
            return (avatar, name);
        }

        public void ClearSession()
        {
            _sessionID = "";
            _steamLoginSecure = "";
            _accessToken = "";
        }

        public async Task FetchWebCookies(SteamID steamID, string refreshToken, string accessToken = "")
        {
            _accessToken = accessToken; // Stored for subsequent Web API calls
            try
            {
                OnLog?.Invoke(Fmt("SvcWebSession"));

                _sessionID = Convert.ToHexString(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)
                ).ToLower();

                using var handler = new System.Net.Http.HttpClientHandler();
                handler.CookieContainer = new CookieContainer();
                using var http = new System.Net.Http.HttpClient(handler);
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                var form = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("nonce", refreshToken),
                    new KeyValuePair<string, string>("sessionid", _sessionID),
                    new KeyValuePair<string, string>("redir", "https://steamcommunity.com/login/home/?goto=")
                });

                var resp = await http.PostAsync("https://login.steampowered.com/jwt/finalizelogin", form);
                var json = await resp.Content.ReadAsStringAsync();

                // raw response log removed
                try { System.IO.File.WriteAllText("finalize_response.json", json); } catch { }

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("transfer_info", out var transfers))
                {
                    OnLog?.Invoke(Fmt("SvcWebSessionFail"));
                    return;
                }

                foreach (var transfer in transfers.EnumerateArray())
                {
                    var url = transfer.GetProperty("url").GetString();
                    if (url == null || !url.Contains("steamcommunity.com")) continue;

                    var p = transfer.GetProperty("params");
                    string nonce = p.TryGetProperty("nonce", out var nonceEl) ? nonceEl.GetString() ?? "" : "";
                    string auth = p.TryGetProperty("auth", out var authEl) ? authEl.GetString() ?? "" : "";

                    using var tHandler = new System.Net.Http.HttpClientHandler();
                    tHandler.CookieContainer = new CookieContainer();
                    tHandler.AllowAutoRedirect = true;
                    using var tHttp = new System.Net.Http.HttpClient(tHandler);
                    tHttp.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    tHttp.DefaultRequestHeaders.Add("Referer", "https://steamcommunity.com/");

                    var tForm = new System.Net.Http.FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("nonce", nonce),
                        new KeyValuePair<string, string>("auth", auth),
                        new KeyValuePair<string, string>("steamID", steamID.ConvertToUInt64().ToString())
                    });

                    var tResp = await tHttp.PostAsync(url, tForm);
                    var cookies = tHandler.CookieContainer.GetCookies(new Uri("https://steamcommunity.com"));
                    var secure = cookies["steamLoginSecure"];
                    if (secure != null)
                    {
                        _steamLoginSecure = secure.Value;
                        OnLog?.Invoke(Fmt("SvcWebSessionOk"));
                        break;
                    }
                }

                if (string.IsNullOrEmpty(_steamLoginSecure))
                    OnLog?.Invoke(Fmt("SvcWebSessionFail"));
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(Fmt("SvcApiError", ex.Message));
            }
        }

        /// <summary>
        /// Scrapes the user's badge page to find games with remaining card drops and fetches Market prices.
        /// </summary>
        public async Task<List<SteamGame>> GetGamesWithCardDrops(bool showZeroCardGames = false)
        {
            var games = new List<SteamGame>();

            if (string.IsNullOrEmpty(_steamLoginSecure))
            {
                OnLog?.Invoke(Fmt("SvcWebSessionFail"));
                return games;
            }

            try
            {
                OnLog?.Invoke(Fmt("SvcScanning"));

                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                http.DefaultRequestHeaders.Add("Cookie", $"sessionid={_sessionID}; steamLoginSecure={_steamLoginSecure}");

                var steamID = _client.SteamID.ConvertToUInt64();
                var html = await http.GetStringAsync(
                    $"https://steamcommunity.com/profiles/{steamID}/badges?l=english");

                System.IO.File.WriteAllText("last_scan_debug.html", html);

                if (html.Contains("g_steamID = false"))
                {
                    OnLog?.Invoke(Fmt("SvcWebSessionFail"));
                    return games;
                }

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var rows = doc.DocumentNode.SelectNodes("//div[contains(@class,'badge_row')]");
                if (rows == null)
                {
                    // silent - no badge rows
                    return games;
                }

                int totalRows = rows.Count;
                int scannedCount = 0;

                foreach (var row in rows)
                {
                    scannedCount++;
                    var progress = row.SelectSingleNode(".//span[contains(@class,'progress_info_bold')]");
                    if (progress == null) continue;

                    var text = progress.InnerText.ToLower();
                    if (!text.Contains("card drop")) continue;

                    var match = Regex.Match(text, @"(\d+)");
                    int cards = match.Success ? int.Parse(match.Groups[1].Value) : 0;
                    if (cards == 0 && !showZeroCardGames) continue;

                    var linkNode = row.SelectSingleNode(".//a[@class='badge_row_overlay']");
                    var href = linkNode?.GetAttributeValue("href", "") ?? "";
                    var appMatch = Regex.Match(href, @"gamecards/(\d+)");
                    if (!appMatch.Success) continue;

                    int appId = int.Parse(appMatch.Groups[1].Value);
                    string name = await FetchGameName((uint)appId);

                    // Fetch card price from Steam Market
                    double estimatedValue = await FetchCardPrice(appId, name);

                    games.Add(new SteamGame {
                        AppId = appId,
                        Name = name,
                        RemainingCards = cards,
                        InitialCards = cards, // On first scan, initial = current
                        PlaytimeHours = 0,
                        IsVacProtected = false,
                        EstimatedValue = estimatedValue * cards
                    });

                    OnScanProgress?.Invoke(scannedCount, totalRows, games.Count);
                }

                OnLog?.Invoke(Fmt("SvcScanDone", games.Count, games.Sum(g => g.RemainingCards)));

                if (showZeroCardGames)
                {
                    OnLog?.Invoke(Fmt("SvcLibraryLoading"));
                    var libraryGames = await FetchOwnedGames(steamID);
                    int added = 0;
                    foreach (var (appId, name) in libraryGames)
                    {
                        if (games.Any(g => g.AppId == appId)) continue;
                        games.Add(new SteamGame {
                            AppId = appId,
                            Name = name,
                            RemainingCards = 0,
                            InitialCards = 0,
                            PlaytimeHours = 0,
                            IsVacProtected = false,
                            EstimatedValue = 0
                        });
                        added++;
                    }
                    OnLog?.Invoke(Fmt("SvcLibraryLoaded", added));
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(Fmt("SvcScanFail", ex.Message));
            }

            return games;
        }

        public async Task PopulateVacStatus(List<SteamGame> games)
        {
            if (games.Count == 0) return;

            // appdetails API does not support multiple appids: only the first game's data is returned.
            // Solution: individual requests per game, max 10 in parallel.
            OnLog?.Invoke(Fmt("SvcVacDone", 0).Replace("0", $"0/{games.Count}"));

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            http.Timeout = TimeSpan.FromSeconds(10);

            int parallelBatch = 10;

            for (int i = 0; i < games.Count; i += parallelBatch)
            {
                var batch = games.Skip(i).Take(parallelBatch).ToList();

                var tasks = batch.Select(async game =>
                {
                    try
                    {
                        var json = await http.GetStringAsync(
                            $"https://store.steampowered.com/api/appdetails" +
                            $"?appids={game.AppId}&filters=categories");

                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty(game.AppId.ToString(), out var appData) &&
                            appData.TryGetProperty("success", out var successEl) && successEl.GetBoolean() &&
                            appData.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("categories", out var categories))
                        {
                            foreach (var cat in categories.EnumerateArray())
                            {
                                if (cat.TryGetProperty("id", out var idEl) && idEl.GetInt32() == 8)
                                {
                                    game.IsVacProtected = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                });

                await Task.WhenAll(tasks);

                if (i + parallelBatch < games.Count)
                    await Task.Delay(300);
            }

            int vacFound = games.Count(g => g.IsVacProtected);
            OnLog?.Invoke(Fmt("SvcVacDone", vacFound));
        }

        /// <summary>
        /// Fetches the user's full library via IPlayerService/GetOwnedGames API.
        /// Uses access token if available; falls back to steamLoginSecure cookie.
        /// </summary>
        private async Task<List<(int AppId, string Name)>> FetchOwnedGames(ulong steamID)
        {
            var result = new List<(int, string)>();
            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                http.Timeout = TimeSpan.FromSeconds(30);

                string url;

                if (!string.IsNullOrEmpty(_accessToken))
                {
                    // Use access token directly — most reliable method
                    url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                          $"?access_token={Uri.EscapeDataString(_accessToken)}" +
                          $"&steamid={steamID}" +
                          $"&include_appinfo=true" +
                          $"&include_played_free_games=true";
                }
                else
                {
                    // Fallback: use cookie-based auth
                    http.DefaultRequestHeaders.Add("Cookie",
                        $"sessionid={_sessionID}; steamLoginSecure={_steamLoginSecure}");
                    url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                          $"?steamid={steamID}" +
                          $"&include_appinfo=true" +
                          $"&include_played_free_games=true";
                }

                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("response", out var response)) return result;
                if (!response.TryGetProperty("games", out var gamesArr)) return result;

                foreach (var g in gamesArr.EnumerateArray())
                {
                    int appId = g.TryGetProperty("appid", out var aid) ? aid.GetInt32() : 0;
                    if (appId == 0) continue;
                    string name = g.TryGetProperty("name", out var nm) ? nm.GetString() ?? $"App {appId}" : $"App {appId}";
                    result.Add((appId, name));
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(Fmt("SvcApiError", ex.Message));
            }
            return result;
        }

        /// <summary>
        /// Fetches the average Trading Card price for a game from Steam Market (USD).
        /// </summary>
        private async Task<double> FetchCardPrice(int appId, string gameName)
        {
            try
            {
                // Steam Market search: appid=753 (Steam items), query card by game appid
                var query = Uri.EscapeDataString($"{appId} Trading Card");
                var url = $"https://steamcommunity.com/market/search/render/" +
                          $"?appid=753&q={query}&norender=1&count=5&sort_column=price&sort_dir=asc";

                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                http.Timeout = TimeSpan.FromSeconds(10);

                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("results", out var results)) return 0;

                double total = 0;
                int count = 0;

                foreach (var item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("sell_price", out var priceEl)) continue;

                    // sell_price comes in cents from Steam (e.g. 15 = $0.15)
                    double price = priceEl.GetDouble() / 100.0;
                    if (price > 0)
                    {
                        total += price;
                        count++;
                    }
                }

                return count > 0 ? Math.Round(total / count, 2) : 0;
            }
            catch
            {
                return 0; // Silently return 0 if price cannot be fetched
            }
        }

        private async Task<string> FetchGameName(uint appId)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient();
                var json = await http.GetStringAsync(
                    $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(appId.ToString(), out var appData) &&
                    appData.GetProperty("success").GetBoolean() &&
                    appData.TryGetProperty("data", out var data))
                {
                    return data.GetProperty("name").GetString() ?? $"App {appId}";
                }
            }
            catch { }
            return $"App {appId}";
        }
    }
}
