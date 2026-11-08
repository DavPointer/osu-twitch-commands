using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    private const string TokenUrl = "https://osu.ppy.sh/oauth/token";
    private const string ApiBase = "https://osu.ppy.sh/api/v2";
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    private static string _token;
    private static DateTime _tokenExpiry;

    public bool Execute()
    {
        string messageId;
        string playerName;
        CPH.TryGetArg("msgId", out messageId);
        CPH.TryGetArg("input0", out playerName);
        playerName = (playerName ?? "").Trim();
        if (string.IsNullOrEmpty(playerName))
        {
            return Fail("Usage: !recent <osu! username>", messageId);
        }

        try
        {
            bool configured;
            string token = ApiToken(out configured);
            if (token == null)
            {
                return Fail(configured ? "Failed to fetch data from osu!." : "osu! API is not configured.", messageId);
            }
            return Run(playerName, token, messageId);
        }
        catch (Exception ex)
        {
            CPH.LogError("!recent: " + ex);
            return Fail("Failed to fetch data from osu!.", messageId);
        }
    }

    // osu! API v2: the latest score with the beatmap embedded, including lazer plays.
    private bool Run(string playerName, string token, string messageId)
    {
        JToken user = FetchUser(playerName, token);
        if (user == null)
        {
            return Fail("osu! player not found.", messageId);
        }

        string userId = Text(user["id"]);
        if (string.IsNullOrEmpty(userId))
        {
            return Fail("osu! player not found.", messageId);
        }

        int status;
        string body = ApiGet("/users/" + Uri.EscapeDataString(userId) + "/scores/recent?mode=osu&limit=1", token, out status);
        if (body == null)
        {
            return Fail("No recent play found.", messageId);
        }

        JArray recent = JArray.Parse(body);
        if (recent.Count == 0)
        {
            return Fail("No recent play found.", messageId);
        }

        JToken beatmap;
        JToken beatmapset;
        if (!ReadScore(recent[0], token, out beatmap, out beatmapset))
        {
            return Fail("No recent play found.", messageId);
        }

        if (!SetBeatmapArguments(beatmap, beatmapset, messageId))
        {
            return false;
        }

        List<string> mods = ModsOf(recent[0]);
        // The API reports the star rating of the base beatmap: the difficulty attributes
        // endpoint returns it with the played mods applied, which is what chat must show.
        JToken attributes = FetchAttributes(Text(beatmap["id"]), mods, token);
        CPH.SetArgument("mods", mods.Count == 0 ? "" : " +" + string.Join("", mods.ToArray()));
        CPH.SetArgument("starRating", FormatStars(attributes == null ? null : Text(attributes["star_rating"]),
            Text(beatmap["difficulty_rating"])));
        return true;
    }


    // Reads the beatmap and its set from a score, fetching the beatmap when it is not embedded.
    private bool ReadScore(JToken score, string token, out JToken beatmap, out JToken beatmapset)
    {
        beatmap = score["beatmap"];
        beatmapset = score["beatmapset"];
        string beatmapId = Text(score["beatmap_id"]);
        if (beatmap == null && !string.IsNullOrEmpty(beatmapId))
        {
            int status;
            string body = ApiGet("/beatmaps/" + Uri.EscapeDataString(beatmapId), token, out status);
            if (body != null)
            {
                beatmap = JToken.Parse(body);
            }
        }
        if (beatmapset == null && beatmap != null)
        {
            beatmapset = beatmap["beatmapset"];
        }
        return beatmap != null && beatmapset != null;
    }

    private bool SetBeatmapArguments(JToken beatmap, JToken beatmapset, string messageId)
    {
        string artist = Text(beatmapset["artist"]);
        string title = Text(beatmapset["title"]);
        string difficulty = Text(beatmap["version"]);
        string beatmapId = Text(beatmap["id"]);
        if (string.IsNullOrEmpty(beatmapId) || string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(difficulty))
        {
            return Fail("Beatmap not found.", messageId);
        }

        CPH.SetArgument("artist", artist);
        CPH.SetArgument("title", title);
        CPH.SetArgument("difficulty", difficulty);
        CPH.SetArgument("link", "osu.ppy.sh/b/" + beatmapId);
        return true;
    }

    // Difficulty attributes of a beatmap with the played mods (star rating, max combo).
    private JToken FetchAttributes(string beatmapId, List<string> mods, string token)
    {
        if (string.IsNullOrEmpty(beatmapId))
        {
            return null;
        }

        try
        {
            StringBuilder payload = new StringBuilder("{\"mods\":[");
            for (int i = 0; i < mods.Count; i++)
            {
                if (i > 0)
                {
                    payload.Append(',');
                }
                payload.Append('"').Append(mods[i]).Append('"');
            }
            payload.Append("],\"ruleset\":\"osu\"}");

            int status;
            string body = ApiPost("/beatmaps/" + Uri.EscapeDataString(beatmapId) + "/attributes", payload.ToString(), token, out status);
            if (body == null)
            {
                return null;
            }
            JToken result = JToken.Parse(body);
            return result["attributes"] ?? result;
        }
        catch (Exception ex)
        {
            CPH.LogWarn("!recent: beatmap attributes unavailable: " + ex.Message);
            return null;
        }
    }

    private static List<string> ModsOf(JToken score)
    {
        List<string> mods = new List<string>();
        JToken list = score["mods"];
        if (list == null)
        {
            return mods;
        }
        foreach (JToken mod in list)
        {
            // Lazer scores expose mods as plain acronym strings, legacy scores as objects.
            string acronym = mod.Type == JTokenType.String ? Text(mod) : Text(mod["acronym"]);
            // CL marks a classic score, it is not a playable mod.
            if (!string.IsNullOrEmpty(acronym) && acronym != "CL")
            {
                mods.Add(acronym);
            }
        }
        return mods;
    }

    private static string FormatStars(string raw, string fallback)
    {
        string value = string.IsNullOrEmpty(raw) ? fallback : raw;
        double stars;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out stars))
        {
            return stars.ToString("0.00", CultureInfo.InvariantCulture) + "\u2605";
        }
        return "";
    }

    private JToken FetchUser(string userName, string token)
    {
        int status;
        string body = ApiGet("/users/@" + Uri.EscapeDataString(userName) + "/osu", token, out status);
        if (body == null)
        {
            return null;
        }
        JToken user = JToken.Parse(body);
        return user["id"] == null ? null : user;
    }

    // Returns an API v2 access token; configured is false when the OAuth client globals are empty.
    private string ApiToken(out bool configured)
    {
        configured = false;
        if (!string.IsNullOrEmpty(_token) && DateTime.UtcNow < _tokenExpiry)
        {
            configured = true;
            return _token;
        }

        string clientId = (CPH.GetGlobalVar<string>("osuClientId", true) ?? "").Trim();
        string clientSecret = (CPH.GetGlobalVar<string>("osuClientSecret", true) ?? "").Trim();
        if (clientId.Length == 0 || clientSecret.Length == 0)
        {
            CPH.LogError("osu! OAuth client is not configured: set the osuClientId and osuClientSecret globals.");
            return null;
        }
        configured = true;

        try
        {
            using (FormUrlEncodedContent form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "public")
            }))
            using (HttpResponseMessage response = Http.PostAsync(TokenUrl, form).GetAwaiter().GetResult())
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    CPH.LogError("osu! OAuth token request failed: " + (int)response.StatusCode + " " + body);
                    return null;
                }

                JObject json = JObject.Parse(body);
                string token = Text(json["access_token"]);
                int expiresIn;
                if (token.Length == 0 ||
                    !int.TryParse(Text(json["expires_in"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out expiresIn))
                {
                    CPH.LogError("osu! OAuth token response has no access_token/expires_in.");
                    return null;
                }

                _token = token;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn > 300 ? expiresIn - 300 : expiresIn);
                return token;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError("osu! OAuth token request failed: " + ex.Message);
            return null;
        }
    }

    // GETs an API v2 path. Returns the body, or null when the resource does not exist.
    private string ApiGet(string pathAndQuery, string token, out int status)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ApiBase + pathAndQuery))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using (HttpResponseMessage response = Http.SendAsync(request).GetAwaiter().GetResult())
                {
                    status = (int)response.StatusCode;
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (status == 404)
                    {
                        return null;
                    }
                    if (status == 401 && attempt == 0)
                    {
                        _token = null;
                        bool ignored;
                        string renewed = ApiToken(out ignored);
                        if (renewed == null)
                        {
                            throw new InvalidOperationException("osu! API token refresh failed.");
                        }
                        token = renewed;
                        continue;
                    }
                    if (status < 200 || status >= 300)
                    {
                        throw new InvalidOperationException("osu! API " + pathAndQuery + " returned " + status + ": " + body);
                    }
                    return body;
                }
            }
        }
        status = 0;
        return null;
    }

    // POSTs a JSON body to an API v2 path. Returns the body, or null when the resource does not exist.
    private string ApiPost(string pathAndQuery, string jsonBody, string token, out int status)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ApiBase + pathAndQuery))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = Http.SendAsync(request).GetAwaiter().GetResult())
                {
                    status = (int)response.StatusCode;
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (status == 404)
                    {
                        return null;
                    }
                    if (status == 401 && attempt == 0)
                    {
                        _token = null;
                        bool ignored;
                        string renewed = ApiToken(out ignored);
                        if (renewed == null)
                        {
                            throw new InvalidOperationException("osu! API token refresh failed.");
                        }
                        token = renewed;
                        continue;
                    }
                    if (status < 200 || status >= 300)
                    {
                        throw new InvalidOperationException("osu! API " + pathAndQuery + " returned " + status + ": " + body);
                    }
                    return body;
                }
            }
        }
        status = 0;
        return null;
    }

    private bool Fail(string message, string messageId)
    {
        CPH.TwitchReplyToMessage(message, messageId, true);
        return false;
    }

    private static string Text(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return "";
        JValue value = token as JValue;
        if (value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer))
        {
            return Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return token.ToString();
    }
}
