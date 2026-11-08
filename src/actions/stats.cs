using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
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
        string userName;
        CPH.TryGetArg("msgId", out messageId);
        CPH.TryGetArg("input0", out userName);
        userName = (userName ?? "").Trim();
        if (string.IsNullOrEmpty(userName))
        {
            return Fail("Usage: !stats <osu! username>", messageId);
        }

        try
        {
            bool configured;
            string token = ApiToken(out configured);
            if (token == null)
            {
                return Fail(configured ? "Failed to fetch data from osu!." : "osu! API is not configured.", messageId);
            }
            return Run(userName, token, messageId);
        }
        catch (Exception ex)
        {
            CPH.LogError("!stats: " + ex);
            return Fail("Failed to fetch data from osu!.", messageId);
        }
    }

    // osu! API v2: one request returns the profile and the statistics.
    private bool Run(string userName, string token, string messageId)
    {
        JToken user = FetchUser(userName, token);
        if (user == null)
        {
            return Fail("osu! player not found.", messageId);
        }

        JToken statistics = user["statistics"];
        string userId = Text(user["id"]);
        if (statistics == null || string.IsNullOrEmpty(userId))
        {
            return Fail("osu! player not found.", messageId);
        }

        double accuracy;
        double ppRaw;
        int ppRank;
        if (!double.TryParse(Text(statistics["hit_accuracy"]), NumberStyles.Float, CultureInfo.InvariantCulture, out accuracy) ||
            !double.TryParse(Text(statistics["pp"]), NumberStyles.Float, CultureInfo.InvariantCulture, out ppRaw) ||
            !int.TryParse(Text(statistics["global_rank"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out ppRank))
        {
            return Fail("osu! profile data is not available.", messageId);
        }

        double level;
        JToken levelToken = statistics["level"];
        if (!double.TryParse(Text(levelToken == null ? null : levelToken["current"]), NumberStyles.Float, CultureInfo.InvariantCulture, out level))
        {
            level = 0;
        }

        double secondsPlayed;
        if (!double.TryParse(Text(statistics["play_time"]), NumberStyles.Float, CultureInfo.InvariantCulture, out secondsPlayed))
        {
            secondsPlayed = 0;
        }

        SetArguments(Text(user["username"]), Text(user["country_code"]), userId, accuracy, ppRaw, level, secondsPlayed, ppRank,
            FormatIsoDate(user["join_date"]));
        return true;
    }
    private void SetArguments(string userName, string country, string userId, double accuracy, double ppRaw, double level,
        double secondsPlayed, int ppRank, string joinDate)
    {
        country = (country ?? "").Trim().ToUpperInvariant();

        CPH.SetArgument("osuName", userName);
        CPH.SetArgument("country", country);
        CPH.SetArgument("countryFlag", CountryFlag(country));
        CPH.SetArgument("acc", accuracy.ToString("F2", CultureInfo.InvariantCulture) + "%");
        CPH.SetArgument("pp", Math.Round(ppRaw, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "pp");
        CPH.SetArgument("rank", "#" + ppRank.ToString(CultureInfo.InvariantCulture));
        CPH.SetArgument("lvl", Math.Floor(level).ToString("0", CultureInfo.InvariantCulture));
        CPH.SetArgument("timePlayed", (secondsPlayed / 3600.0).ToString("F2", CultureInfo.InvariantCulture) + "h");
        CPH.SetArgument("joinDate", joinDate);
        CPH.SetArgument("profileLink", "osu.ppy.sh/u/" + userId);
    }

    private static string CountryFlag(string country)
    {
        if (country.Length < 2)
        {
            return "";
        }
        char first = country[0];
        char second = country[1];
        if (first < 'A' || first > 'Z' || second < 'A' || second > 'Z')
        {
            return "";
        }
        return char.ConvertFromUtf32(0x1F1E6 + (first - 'A')) + char.ConvertFromUtf32(0x1F1E6 + (second - 'A'));
    }

    // osu! API v2 returns ISO-8601 UTC timestamps; parsing is explicit because the
    // framework's culture-aware parser swaps day and month on this format.
    private static readonly string[] IsoDateFormats = new[]
    {
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    };

    // osu! API v2 returns ISO-8601 UTC timestamps; Newtonsoft turns them into Date tokens
    // (with the current culture applied by ToString), so both token and raw string are handled.
    private static string FormatIsoDate(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return "";
        }

        if (token.Type == JTokenType.Date)
        {
            object raw = ((JValue)token).Value;
            DateTime utc;
            if (raw is DateTimeOffset)
            {
                utc = ((DateTimeOffset)raw).UtcDateTime;
            }
            else
            {
                utc = ((DateTime)raw).ToUniversalTime();
            }
            return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local)
                .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }

        string value = token.ToString();
        DateTime parsed;
        if (DateTime.TryParseExact(value, IsoDateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
        {
            return TimeZoneInfo.ConvertTimeFromUtc(parsed, TimeZoneInfo.Local).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return value;
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
