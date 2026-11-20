using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhoneBox.Agent;
using Xunit;

public class SteamMonitorTests
{
    [Theory]
    [InlineData("https://avatars.steamstatic.com/a.jpg", true)]
    [InlineData("https://steamcdn-a.akamaihd.net/a.jpg", true)]
    [InlineData("https://steamstatic.com.evil.com/a.jpg", false)]
    [InlineData("https://avatars.steamstatic.com.evil.com/a.jpg", false)]
    [InlineData("https://user@avatars.steamstatic.com/a.jpg", false)]
    [InlineData("https://avatars.steamstatic.com:444/a.jpg", false)]
    [InlineData("http://avatars.steamstatic.com/a.jpg", false)]
    public void AvatarUrlsRequireAnApprovedHttpsHostAndDefaultPort(string url, bool expected) =>
        Assert.Equal(expected, SteamMonitor.IsSafeAvatarUrl(url));

    [Theory]
    [InlineData("76561198000000001", "76561198000000001")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001", "76561198000000001")]
    [InlineData("http://www.steamcommunity.com/profiles/76561198000000001/", "76561198000000001")]
    public async Task ResolveAccountIdParsesNumericProfileReferencesWithoutNetwork(string input, string expected) =>
        Assert.Equal(expected, await SteamMonitor.ResolveAccountId(input, "unused-test-key", CancellationToken.None));

    [Fact]
    public void ResolveAccountIdParsesCustomProfileLinksWithoutResolvingThem()
    {
        var profile = SteamMonitor.ParseProfileReference("https://steamcommunity.com/id/a-custom-name/");
        Assert.Null(profile.SteamId);
        Assert.Equal("a-custom-name", profile.VanityName);
    }

    [Fact]
    public void RedactRemovesTheApiKeyFromMessages()
    {
        Assert.Equal("key [redacted]", SteamMonitor.Redact("key secret-key", "secret-key"));
        Assert.Equal("message", SteamMonitor.Redact("message", ""));
    }

    [Fact]
    public void SnapshotCapsVisibleFriendsWhileKeepingExactTotals()
    {
        var friends = Enumerable.Range(1, 160).Select(i => Friend(i, i <= 105 ? 1 : 0)).ToArray();
        var source = new SteamSnapshot(true, null, 160, 105, 123, friends, []);

        var onlineOnly = SteamMonitor.ProjectSnapshot(source, includeFriends: true, includeOffline: false, includeActivity: false);
        Assert.Equal(105, onlineOnly.OnlineCount);
        Assert.Equal(160, onlineOnly.TotalFriends);
        Assert.Equal(100, onlineOnly.Friends.Count);

        var withOffline = SteamMonitor.ProjectSnapshot(source, includeFriends: true, includeOffline: true, includeActivity: false);
        Assert.Equal(100, withOffline.Friends.Count(f => f.State is > 0));
        Assert.Equal(50, withOffline.Friends.Count(f => f.State is not > 0));
        Assert.Equal(160, withOffline.TotalFriends);
        Assert.Equal(105, withOffline.OnlineCount);
    }

    [Fact]
    public void PhonePayloadIncludesAvatarFlagButNotAvatarUrlOrGameId()
    {
        var friend = Friend(1, 1) with { Avatar = "https://avatars.steamstatic.com/a.jpg", GameId = "42" };
        var json = JsonSerializer.SerializeToElement(friend, ConfigStore.WriteOpts);
        Assert.True(json.GetProperty("hasAvatar").GetBoolean());
        Assert.False(json.TryGetProperty("avatar", out _));
        Assert.False(json.TryGetProperty("gameId", out _));

        var activity = new SteamActivityEvent(friend.Id, friend.Name, friend.Avatar, "playing", "Game", 1);
        var eventJson = JsonSerializer.SerializeToElement(activity, ConfigStore.WriteOpts);
        Assert.True(eventJson.GetProperty("hasAvatar").GetBoolean());
        Assert.False(eventJson.TryGetProperty("avatar", out _));
    }

    [Fact]
    public void RecordActivityBuildsAChangeFeedAndCapsItInMemory()
    {
        var presence = new Dictionary<string, (int State, string? Game, string? GameId)>(StringComparer.Ordinal);
        var events = new List<SteamActivityEvent>();
        var first = Friend(1, 0);
        Assert.True(SteamMonitor.RecordActivity([first], presence, events, hasPresenceBaseline: false, now: 1));
        Assert.Empty(events);

        var state = 0;
        for (var i = 2; i < 32; i++)
        {
            state = state == 0 ? 1 : 0;
            SteamMonitor.RecordActivity([Friend(1, state)], presence, events, hasPresenceBaseline: true, now: i);
        }
        Assert.Equal(20, events.Count);
        Assert.Contains(events, e => e.Kind == "online");
        Assert.Contains(events, e => e.Kind == "offline");
    }

    [Fact]
    public void MissingPresenceDoesNotCarryForwardLastKnownOnlineState()
    {
        var knownState = SteamMonitor.PresenceState(JsonNode.Parse("""{"personastate":1}"""));
        var missingState = SteamMonitor.PresenceState(JsonNode.Parse("{}"));
        Assert.Equal(1, knownState);
        Assert.Null(missingState);

        var knownFriend = Friend(1, knownState);
        var currentFriend = knownFriend with { State = missingState };
        var presence = new Dictionary<string, (int State, string? Game, string? GameId)>(StringComparer.Ordinal);
        var events = new List<SteamActivityEvent>();
        SteamMonitor.RecordActivity([knownFriend], presence, events, hasPresenceBaseline: false, now: 1);
        SteamMonitor.RecordActivity([currentFriend], presence, events, hasPresenceBaseline: true, now: 2);

        Assert.Equal(0, SteamMonitor.CountOnline([currentFriend]));
        Assert.Empty(events);
        Assert.Equal(1, presence[knownFriend.Id].State);
    }

    [Fact]
    public void ForbiddenAndRateLimitResponsesHaveSafeMessagesAndBoundedBackoff()
    {
        var forbidden = SteamMonitor.ApiStatusError(HttpStatusCode.Forbidden, "ISteamUser/GetFriendList/v1/");
        Assert.Contains("rejected", forbidden.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTP 403", forbidden.Message);

        Assert.Equal(TimeSpan.FromMinutes(8), SteamMonitor.RateLimitDelay(4, null));
        Assert.Equal(TimeSpan.FromMinutes(15), SteamMonitor.RateLimitDelay(5, null));
        Assert.Equal(TimeSpan.FromMinutes(15), SteamMonitor.RateLimitDelay(1, TimeSpan.FromHours(1)));
        // a Retry-After shorter than the back-off (0, or a date already past) never makes it ask sooner
        Assert.Equal(TimeSpan.FromMinutes(1), SteamMonitor.RateLimitDelay(1, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(4), SteamMonitor.RateLimitDelay(3, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMinutes(10), SteamMonitor.RateLimitDelay(1, TimeSpan.FromMinutes(10)));
        var headerDelay = SteamMonitor.RetryAfterDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(45)), DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.FromSeconds(45), headerDelay);
        Assert.Equal("Steam returned an unreadable response.", SteamMonitor.UserError(new JsonException("parser internals")));
        Assert.Equal("Steam did not respond in time. Try again later.", SteamMonitor.UserError(new TaskCanceledException("client timeout internals")));
    }

    private static SteamFriend Friend(int id, int? state) => new(
        (76561198000000000L + id).ToString(), $"Friend {id}", state, null, null, null, null);
}
