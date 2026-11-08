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
        string profileName;
        CPH.TryGetArg("msgId", out messageId);
        CPH.TryGetArg("input0", out profileName);
        profileName = (profileName ?? "").Trim();
        if (string.IsNullOrEmpty(profileName))
        {
            return Fail("Usage: !best <osu! username>", messageId);
        }

        try
        {
            bool configured;
            string token = ApiToken(out configured);
            if (token == null)
            {
                return Fail(configured ? "Failed to fetch data from osu!." : "osu! API is not configured.", messageId);
            }
            return Run(profileName, token, messageId);
        }
        catch (Exception ex)
        {
            CPH.LogError("!best: " + ex);
            return Fail("Failed to fetch data from osu!.", messageId);
        }
    }

    // osu! API v2 resolves the player id; the peak is still read from osutrack.
    private bool Run(string profileName, string token, string messageId)
    {
        int status;
        string body = ApiGet("/users/@" + Uri.EscapeDataString(profileName) + "/osu", token, out status);
        JToken user = body == null ? null : JToken.Parse(body);
        string userId = user == null ? "" : Text(user["id"]);
        if (string.IsNullOrEmpty(userId))
        {
            return Fail("osu! player not found.", messageId);
        }
        return Peak(userId, profileName, messageId);
    }


    private bool Peak(string userId, string profileName, string messageId)
    {
        string peakUrl = "https://osutrack-api.ameo.dev/peak?user=" + Uri.EscapeDataString(userId) + "&mode=0";
        JArray peaks = JArray.Parse(Http.GetStringAsync(peakUrl).GetAwaiter().GetResult());
        if (peaks.Count == 0)
        {
            return Fail("osutrack data is not available for this player.", messageId);
        }

        JToken peak = peaks[0];
        string rank = Text(peak["best_global_rank"]);
        string accText = Text(peak["best_accuracy"]);
        double acc;
        if (string.IsNullOrEmpty(rank) || string.IsNullOrEmpty(accText) ||
            !double.TryParse(accText, NumberStyles.Float, CultureInfo.InvariantCulture, out acc))
        {
            return Fail("osutrack data is not available for this player.", messageId);
        }

        CPH.SetArgument("osuProfileName", profileName);
        CPH.SetArgument("rank", "#" + rank);
        CPH.SetArgument("acc", acc.ToString("F2", CultureInfo.InvariantCulture) + "%");
        return true;
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
