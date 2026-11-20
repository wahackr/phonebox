using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PhoneBox.Agent;

public sealed record SteamFriend(string Id, string Name, int? State, string? Game,
    [property: JsonIgnore] string? GameId, [property: JsonIgnore] string? Avatar, long? LastLogoff)
{
    public bool HasAvatar => !string.IsNullOrWhiteSpace(Avatar);
}
public sealed record SteamActivityEvent(string Id, string Name, [property: JsonIgnore] string? Avatar, string Kind, string? Game, long At)
{
    public bool HasAvatar => !string.IsNullOrWhiteSpace(Avatar);
}
public sealed record SteamSnapshot(bool Connected, string? Error, int TotalFriends, int OnlineCount,
    long? Updated, IReadOnlyList<SteamFriend> Friends, IReadOnlyList<SteamActivityEvent> Activity);

internal readonly record struct SteamProfileReference(string? SteamId, string? VanityName);

internal sealed class SteamErrorException : Exception
{
    public SteamErrorException(string message, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}
/// <summary>Reads a user's friend list and presence from Steam's Web API while an enabled page contains the widget.</summary>
public sealed class SteamMonitor(ConfigStore config, ILogger<SteamMonitor> log) : BackgroundService
{
    private sealed record PublishedSnapshot(SteamSnapshot Value, string? SteamId, string? ApiKey);

    private static readonly HttpClient Http = CreateApiHttp();
    private static readonly HttpClient AvatarHttp = CreateAvatarHttp();
    private static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FriendsEvery = TimeSpan.FromMinutes(15);
    private const int SummaryBatchSize = 100;
    internal const int MaxOnlineFriends = 100;
    internal const int MaxOfflineFriends = 50;
    internal const int ApiMaxResponseBytes = 2 * 1024 * 1024;
    private const int AvatarMaxBytes = 512 * 1024;
    private const long AvatarCacheLimit = 8 * 1024 * 1024;
    private static readonly TimeSpan MaxRateLimitDelay = TimeSpan.FromMinutes(15);

    private volatile PublishedSnapshot _published = new(Empty(null), null, null);
    private string? _steamId;
    private string? _apiKey;
    private string? _resolvedAccountId;
    private string[] _friendIds = [];
    private DateTime _friendsAt = DateTime.MinValue;
    private DateTime _nextPoll = DateTime.MinValue;
    private readonly Dictionary<string, (int State, string? Game, string? GameId)> _lastPresence = new(StringComparer.Ordinal);
    private readonly List<SteamActivityEvent> _activity = [];
    private bool _hasPresenceBaseline;
    private int _rateLimitFailures;
    private readonly object _avatarLock = new();
    private readonly Dictionary<string, (byte[] Data, string Type)> _avatars = new(StringComparer.Ordinal);
    private long _avatarBytes;
    private long _avatarGeneration;

    private static HttpClient CreateApiHttp()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        client.MaxResponseContentBufferSize = ApiMaxResponseBytes;
        return client;
    }

    private static HttpClient CreateAvatarHttp()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        client.MaxResponseContentBufferSize = AvatarMaxBytes;
        return client;
    }

    private void ResetAvatarCache()
    {
        lock (_avatarLock)
        {
            _avatars.Clear();
            _avatarBytes = 0;
            _avatarGeneration++;
        }
    }

    private static SteamSnapshot Empty(string? error) => new(false, error, 0, 0, null, [], []);

    internal static bool WidgetWanted(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type is "steam" or "steam-activity");

    internal static SteamSnapshot ProjectSnapshot(SteamSnapshot value, bool includeFriends, bool includeOffline, bool includeActivity)
    {
        SteamFriend[] friends;
        if (!includeFriends) friends = [];
        else
        {
            var online = value.Friends.Where(f => f.State is > 0).Take(MaxOnlineFriends);
            friends = includeOffline
                ? online.Concat(value.Friends.Where(f => f.State is not > 0).Take(MaxOfflineFriends)).ToArray()
                : online.ToArray();
        }
        return value with
        {
            TotalFriends = includeFriends ? value.TotalFriends : 0,
            OnlineCount = includeFriends ? value.OnlineCount : 0,
            Updated = null,
            Friends = friends,
            Activity = includeActivity ? value.Activity : [],
        };
    }

    public SteamSnapshot Snapshot(bool includeFriends, bool includeOffline, bool includeActivity)
    {
        var cfg = config.Get();
        var published = _published;
        if (!cfg.Steam.Enabled || !WidgetWanted(cfg)
            || published.SteamId != (cfg.Steam.SteamId?.Trim() ?? "")
            || published.ApiKey != (cfg.Steam.ApiKey?.Trim() ?? "")) return Empty(null);
        return ProjectSnapshot(published.Value, includeFriends, includeOffline, includeActivity);
    }

    private bool IsActive(string steamId, string apiKey)
    {
        var cfg = config.Get();
        return cfg.Steam.Enabled && WidgetWanted(cfg)
            && (cfg.Steam.SteamId?.Trim() ?? "") == steamId
            && (cfg.Steam.ApiKey?.Trim() ?? "") == apiKey;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = config.Get();
            var steam = cfg.Steam;
            if (!steam.Enabled || !WidgetWanted(cfg))
            {
                if (_steamId is not null || _apiKey is not null) ResetAvatarCache();
                _published = new(Empty(null), null, null);
                _steamId = _apiKey = null;
                _resolvedAccountId = null;
                _friendIds = [];
                _friendsAt = DateTime.MinValue;
                _nextPoll = DateTime.MinValue;
                _rateLimitFailures = 0;
                _lastPresence.Clear();
                _activity.Clear();
                _hasPresenceBaseline = false;
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            var id = steam.SteamId?.Trim() ?? "";
            var key = steam.ApiKey?.Trim() ?? "";
            if (id != _steamId || key != _apiKey)
            {
                _steamId = id;
                _apiKey = key;
                _resolvedAccountId = null;
                _friendIds = [];
                _friendsAt = DateTime.MinValue;
                _nextPoll = DateTime.MinValue;
                _rateLimitFailures = 0;
                _lastPresence.Clear();
                _activity.Clear();
                _hasPresenceBaseline = false;
                ResetAvatarCache();
                _published = new(Empty(null), null, null);
            }

            if (id.Length == 0 || key.Length == 0)
            {
                _published = new(Empty("Add your SteamID64 and Web API key in Settings › General › Steam"), id, key);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            if (DateTime.UtcNow >= _nextPoll)
            {
                try
                {
                    if (IsActive(id, key))
                    {
                        if (id.Length != 17 || !id.All(char.IsAsciiDigit))
                            throw new SteamErrorException("Save the Steam profile URL in Settings to resolve it to a SteamID64");
                        _resolvedAccountId ??= id;
                        await Refresh(_resolvedAccountId, key, id, ct);
                    }
                    _rateLimitFailures = 0;
                    _nextPoll = DateTime.UtcNow + PollEvery;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var message = Redact(UserError(ex), key);
                    var previous = _published;
                    var snapshot = previous.SteamId == id && previous.ApiKey == key ? previous.Value : Empty(null);
                    _published = new(snapshot with { Connected = false, Error = message }, id, key);
                    if (ex is SteamErrorException { StatusCode: HttpStatusCode.TooManyRequests } limited)
                    {
                        _rateLimitFailures++;
                        _nextPoll = DateTime.UtcNow + RateLimitDelay(_rateLimitFailures, limited.RetryAfter);
                    }
                    else
                    {
                        _rateLimitFailures = 0;
                        _nextPoll = DateTime.UtcNow.AddMinutes(2);
                    }
                    log.LogInformation("steam: {Msg}", message);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task Refresh(string steamId, string apiKey, string settingsSteamId, CancellationToken ct)
    {
        if (!IsActive(settingsSteamId, apiKey)) return;
        if (_friendsAt == DateTime.MinValue || DateTime.UtcNow - _friendsAt >= FriendsEvery)
        {
            if (!IsActive(settingsSteamId, apiKey)) return;
            var list = await Request("ISteamUser/GetFriendList/v1/", $"steamid={Uri.EscapeDataString(steamId)}&relationship=friend", apiKey, ct);
            if (!IsActive(settingsSteamId, apiKey)) return;
            var ids = list["friendslist"]?["friends"]?.AsArray()
                .Select(f => Text(f?["steamid"]))
                .OfType<string>()
                .Where(s => s.Length == 17 && s.All(char.IsAsciiDigit))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            _friendIds = ids;
            _friendsAt = DateTime.UtcNow;
            var currentIds = ids.ToHashSet(StringComparer.Ordinal);
            foreach (var previousId in _lastPresence.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                _lastPresence.Remove(previousId);
        }

        var friends = new List<SteamFriend>();
        for (var i = 0; i < _friendIds.Length; i += SummaryBatchSize)
        {
            if (!IsActive(settingsSteamId, apiKey)) return;
            var batch = _friendIds.Skip(i).Take(SummaryBatchSize);
            var ids = string.Join(',', batch.Select(Uri.EscapeDataString));
            var summaries = await Request("ISteamUser/GetPlayerSummaries/v2/", $"steamids={ids}", apiKey, ct);
            if (!IsActive(settingsSteamId, apiKey)) return;
            foreach (var p in summaries["response"]?["players"]?.AsArray() ?? [])
            {
                var id = Text(p?["steamid"]);
                if (id is null) continue;
                var state = PresenceState(p);
                var lastLogoff = Number(p?["lastlogoff"]);
                friends.Add(new(id,
                    Text(p?["personaname"]) ?? "",
                    state,
                    Text(p?["gameextrainfo"]),
                    Text(p?["gameid"]),
                    AvatarUrl(p),
                    lastLogoff > 0 ? lastLogoff : null));
            }
        }

        var ordered = friends
            .OrderByDescending(f => f.State is > 0)
            .ThenByDescending(f => !string.IsNullOrWhiteSpace(f.Game))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _hasPresenceBaseline = RecordActivity(ordered, _lastPresence, _activity, _hasPresenceBaseline,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!IsActive(settingsSteamId, apiKey)) return;
        var online = CountOnline(ordered);
        _published = new(new(true, null, _friendIds.Length, online,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ordered, _activity.ToArray()), settingsSteamId, apiKey);
    }

    internal static bool RecordActivity(IReadOnlyList<SteamFriend> friends,
        IDictionary<string, (int State, string? Game, string? GameId)> lastPresence,
        IList<SteamActivityEvent> activity, bool hasPresenceBaseline, long now)
    {
        foreach (var friend in friends)
        {
            if (friend.State is not int state) continue;
            var current = (state, friend.Game, friend.GameId);
            if (hasPresenceBaseline && lastPresence.TryGetValue(friend.Id, out var previous))
            {
                var changedGame = !string.IsNullOrWhiteSpace(friend.Game)
                    && (friend.Game != previous.Game || friend.GameId != previous.GameId);
                var kind = changedGame
                    ? "playing"
                    : previous.State == 0 && state > 0 ? "online"
                    : previous.State > 0 && state == 0 ? "offline"
                    : null;
                if (kind is not null)
                {
                    activity.Insert(0, new(friend.Id, friend.Name, friend.Avatar, kind, friend.Game, now));
                    while (activity.Count > 20) activity.RemoveAt(activity.Count - 1);
                }
            }
            lastPresence[friend.Id] = current;
        }
        return true;
    }

    internal static int? PresenceState(JsonNode? player) =>
        player?["personastate"] is { } value ? Number(value) : null;

    internal static int CountOnline(IReadOnlyList<SteamFriend> friends) =>
        friends.Count(friend => friend.State is > 0);

    /// <summary>Fetch only an avatar URL reported by the current Steam snapshot; the phone never contacts the image CDN.</summary>
    public async Task<(byte[] Data, string Type)?> AvatarAsync(string id, CancellationToken ct)
    {
        var published = _published;
        if (published.SteamId is null || published.ApiKey is null || !IsActive(published.SteamId, published.ApiKey)
            || id.Length != 17 || !id.All(char.IsAsciiDigit)) return null;
        var url = published.Value.Friends.FirstOrDefault(f => f.Id == id)?.Avatar
            ?? published.Value.Activity.FirstOrDefault(e => e.Id == id)?.Avatar;
        if (url is null || !IsSafeAvatarUrl(url)) return null;
        long generation;
        lock (_avatarLock)
        {
            generation = _avatarGeneration;
            if (_avatars.TryGetValue(url, out var hit)) return IsActive(published.SteamId, published.ApiKey) ? hit : null;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await AvatarHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > AvatarMaxBytes) return null;
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")) return null;
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffered = new MemoryStream(response.Content.Headers.ContentLength is long length ? (int)length : 0);
            var chunk = new byte[81920];
            while (true)
            {
                var remaining = AvatarMaxBytes + 1L - buffered.Length;
                var read = await source.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), timeout.Token);
                if (read == 0) break;
                if (buffered.Length + read > AvatarMaxBytes) return null;
                await buffered.WriteAsync(chunk.AsMemory(0, read), timeout.Token);
            }
            var data = buffered.ToArray();
            if (data.Length is 0 or > AvatarMaxBytes || !IsActive(published.SteamId, published.ApiKey)) return null;
            lock (_avatarLock)
            {
                if (generation != _avatarGeneration || !IsActive(published.SteamId, published.ApiKey)) return null;
                if (_avatars.TryGetValue(url, out var existing)) return existing;
                if (_avatarBytes + data.Length > AvatarCacheLimit)
                {
                    _avatars.Clear();
                    _avatarBytes = 0;
                }
                var avatar = (data, type);
                _avatars[url] = avatar;
                _avatarBytes += data.Length;
                return avatar;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    private static string? SafeAvatarUrl(string? value) => IsSafeAvatarUrl(value) ? value : null;

    internal static string Redact(string message, string apiKey) => string.IsNullOrEmpty(apiKey)
        ? message : message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);

    private static string? AvatarUrl(JsonNode? player)
    {
        foreach (var key in new[] { "avatarfull", "avatarmedium", "avatar" })
            if (SafeAvatarUrl(Text(player?[key])) is { } url) return url;
        return null;
    }

    internal static bool IsSafeAvatarUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase));

    internal static SteamProfileReference ParseProfileReference(string input)
    {
        var value = input.Trim();
        if (value.Length == 17 && value.All(char.IsAsciiDigit)) return new(value, null);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                || !(uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase)))
                throw new SteamErrorException("Enter a SteamID64 or a steamcommunity.com profile link");
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("profiles", StringComparison.OrdinalIgnoreCase))
            {
                if (parts[1].Length == 17 && parts[1].All(char.IsAsciiDigit)) return new(parts[1], null);
                throw new SteamErrorException("That Steam profile link does not contain a public profile ID");
            }
            if (parts.Length >= 2 && parts[0].Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                try { value = Uri.UnescapeDataString(parts[1]); }
                catch (UriFormatException) { throw new SteamErrorException("Enter a valid Steam custom profile link"); }
            }
            else throw new SteamErrorException("That Steam profile link does not contain a public profile ID");
        }
        if (value.Length == 0 || value.Contains('/') || value.Any(char.IsWhiteSpace))
            throw new SteamErrorException("Enter a SteamID64 or a Steam custom profile link");
        return new(null, value);
    }

    internal static async Task<string> ResolveAccountId(string input, string apiKey, CancellationToken ct)
    {
        var profile = ParseProfileReference(input);
        if (profile.SteamId is { } id) return id;
        var vanityName = profile.VanityName!;
        var resolved = await Request("ISteamUser/ResolveVanityURL/v1/", $"vanityurl={Uri.EscapeDataString(vanityName)}", apiKey, ct);
        var result = resolved["response"];
        var resolvedId = Text(result?["steamid"]);
        if (Number(result?["success"]) != 1 || resolvedId is not { Length: 17 } || !resolvedId.All(char.IsAsciiDigit))
            throw new SteamErrorException("Steam could not find that custom profile URL");
        return resolvedId;
    }

    private static async Task<JsonNode> Request(string method, string query, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steampowered.com/{method}?{query}");
        request.Headers.TryAddWithoutValidation("x-webapi-key", apiKey);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw ApiStatusError(response.StatusCode, method, RetryAfterDelay(response.Headers.RetryAfter, DateTimeOffset.UtcNow));
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
                ?? throw new SteamErrorException("Steam returned an empty response.");
        }
        catch (JsonException ex) { throw new SteamErrorException("Steam returned an unreadable response.", innerException: ex); }
    }

    internal static SteamErrorException ApiStatusError(HttpStatusCode statusCode, string method, TimeSpan? retryAfter = null) => statusCode switch
    {
        HttpStatusCode.Forbidden => new("Steam rejected the Web API key. Check it in Settings › General › Steam.", statusCode),
        HttpStatusCode.Unauthorized when method.Contains("GetFriendList", StringComparison.Ordinal) =>
            new("Steam could not read the friend list. Check the API key and profile, and set your Steam friend list to Public.", statusCode),
        HttpStatusCode.Unauthorized => new("Steam rejected the Web API key. Check it in Settings › General › Steam.", statusCode),
        HttpStatusCode.TooManyRequests => new("Steam is rate limiting requests. The agent will retry automatically.", statusCode, retryAfter),
        _ => new($"Steam Web API is temporarily unavailable (HTTP {(int)statusCode}).", statusCode),
    };

    internal static TimeSpan? RetryAfterDelay(RetryConditionHeaderValue? value, DateTimeOffset now)
    {
        if (value is null) return null;
        var delay = value.Delta ?? (value.Date is { } date ? date - now : null);
        return delay is null ? null : ClampRetryDelay(delay.Value);
    }

    /// <summary>
    /// The wait after a 429: the exponential back-off, or Steam's Retry-After when that is longer (up to 15 minutes). A
    /// Retry-After of 0, or a date already past on a clock that runs ahead, never makes the agent ask sooner.
    /// </summary>
    internal static TimeSpan RateLimitDelay(int failures, TimeSpan? retryAfter)
    {
        var exponent = Math.Clamp(failures - 1, 0, 4);
        var backoff = TimeSpan.FromMinutes(Math.Min(1 << exponent, 15));
        return retryAfter is { } requested && ClampRetryDelay(requested) > backoff ? ClampRetryDelay(requested) : backoff;
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay) =>
        TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, MaxRateLimitDelay.TotalSeconds));

    internal static string UserError(Exception ex) => ex switch
    {
        SteamErrorException safe => safe.Message,
        HttpRequestException => "Could not reach Steam. Check the internet connection and try again.",
        OperationCanceledException => "Steam did not respond in time. Try again later.",
        JsonException or InvalidDataException => "Steam returned an unreadable response.",
        _ => "Could not refresh Steam data. Check Steam settings and try again.",
    };

    private static string? Text(JsonNode? value)
    {
        try { return value?.GetValue<string>(); }
        catch { return value?.ToString(); }
    }

    private static int Number(JsonNode? value)
    {
        try { return value?.GetValue<int>() ?? 0; }
        catch { return int.TryParse(value?.ToString(), out var n) ? n : 0; }
    }
}
