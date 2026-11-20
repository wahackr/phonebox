# Steam friends widgets

The **Steam friends** widget shows friend avatars, online state, last-seen time, and current games. The **Steam friend
activity** widget keeps a short feed of friends coming online, starting a game, or going offline. The PhoneBox agent
fetches Steam data and avatar images; the phone never receives your API key or contacts Steam's API or image CDN directly.

## Set it up

1. Create a standard user Web API key at the [Steam API registration page](https://steamcommunity.com/dev/apikey).
   If Steam asks for a domain, enter `localhost` for this local integration.
2. Add a **Steam friends** widget to an enabled page. Add **Steam friend activity** too if you want a feed of presence
   changes. A profile URL can only be saved after Steam is enabled and one of these widgets is on an enabled page.
3. In PhoneBox settings, open **General › Steam**. Turn on **Connect Steam**, enter the key, and enter either your
   SteamID64 or the URL of your Steam profile. Steam profile links using a custom name (for example
   `https://steamcommunity.com/id/your-name`) are resolved when you save; only the SteamID64 is stored.
4. Set your Steam **Friend List** privacy to **Public**. Set **Game details** to **Public** if you want game titles to
   appear. Steam does not return a private friend list through its `GetFriendList` API.
5. In the Steam friends widget, online friends are shown by default; turn on **Show offline friends** in the widget
   options to include offline friends too (at most 50 offline friends are sent to the phone).

The agent refreshes presence about once a minute and refreshes the friend list every 15 minutes, only while an enabled
page contains either widget. It uses Steam's `GetFriendList`, `GetPlayerSummaries` (in batches of up to 100), and
`ResolveVanityURL` endpoints. Vanity names are resolved once when you save the setting. The activity feed is built from
successive polls, stays in memory only, is never written to disk or logged, and starts empty after an agent restart; it
does not backfill older events. Removing both widgets from enabled pages or clearing the API key stops Steam requests.

These widgets use the public Steam Web API, which provides profile avatars, names, and public presence/game details
([Steam user Web API](https://partner.steamgames.com/doc/webapi/ISteamUser)). It does not provide private Steam chat
messages. Steam's documented friend-message API is a callback in the Steamworks client API, intended for a running
Steamworks game/client integration ([Steam friends API](https://partner.steamgames.com/doc/api/ISteamFriends)). PhoneBox
therefore shows online/game activity alerts, not message contents or Steam's unread-message notifications.

If Steam reports an error, check that the API key is valid, the correct Steam account ID or profile link is configured,
and the friend list is public. A friend's privacy settings can hide their presence or current game.
