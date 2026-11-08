using System;
using System.Globalization;
using System.Net.Http;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    public bool Execute()
    {
        CPH.TryGetArg("msgId", out string messageId);
        try
        {
            string tosuUrl = "http://127.0.0.1:24050/json/v2";
            string content = Http.GetStringAsync(tosuUrl).GetAwaiter().GetResult();
            JObject json = JObject.Parse(content);
            string artist = Text(json.SelectToken("beatmap.artist"));
            if (string.IsNullOrEmpty(artist))
            {
                return Fail("No beatmap is currently playing.", messageId);
            }

            string title = Text(json.SelectToken("beatmap.title"));
            string difficulty = Text(json.SelectToken("beatmap.version"));
            string mods = Text(json.SelectToken("play.mods.name"));
            string link = "osu.ppy.sh/b/" + Text(json.SelectToken("beatmap.id"));
            string acc98 = Text(json.SelectToken("performance.accuracy.98"));
            string acc99 = Text(json.SelectToken("performance.accuracy.99"));
            string acc100 = Text(json.SelectToken("performance.accuracy.100"));

            CPH.SetArgument("artist", artist);
            CPH.SetArgument("title", title);
            CPH.SetArgument("difficulty", difficulty);
            CPH.SetArgument("mods", FormatMods(mods));
            CPH.SetArgument("starRating", FormatStars(Text(json.SelectToken("beatmap.stats.stars.total")),
                Text(json.SelectToken("beatmap.stats.stars.live"))));
            CPH.SetArgument("maxCombo", FormatCount(Text(json.SelectToken("beatmap.stats.maxCombo")), "x"));
            CPH.SetArgument("bpm", FormatBpm(Text(json.SelectToken("beatmap.stats.bpm.common"))));
            CPH.SetArgument("link", link);
            string accSummary = "98%: " + acc98 + "pp, 99%: " + acc99 + "pp, 100%: " + acc100 + "pp";
            CPH.SetArgument("accSummary", accSummary);
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError("!np: " + ex);
            return Fail("Failed to fetch data from tosu.", messageId);
        }
    }

    private bool Fail(string message, string messageId)
    {
        CPH.TwitchReplyToMessage(message, messageId, true);
        return false;
    }

    private static string FormatBpm(string raw)
    {
        double value;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture) + " bpm";
        }
        return string.IsNullOrEmpty(raw) ? "" : raw + " bpm";
    }

    // Active mods only: empty when there are none (NM must not appear in chat).
    private static string FormatMods(string mods)
    {
        string value = (mods ?? "").Trim();
        if (value.Length == 0 || string.Equals(value, "NM", StringComparison.OrdinalIgnoreCase)) return "";
        return " +" + value;
    }

    private static string FormatStars(string total, string live)
    {
        string raw = string.IsNullOrEmpty(total) ? live : total;
        double value;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + "\u2605";
        }
        return "";
    }

    private static string FormatCount(string raw, string suffix)
    {
        double value;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value.ToString("0", CultureInfo.InvariantCulture) + suffix;
        }
        return "";
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
