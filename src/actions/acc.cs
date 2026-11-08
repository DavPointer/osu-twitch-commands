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
        CPH.TryGetArg("input0", out string input);
        input = (input ?? "").Trim();
        if (string.IsNullOrEmpty(input))
        {
            return Fail("Usage: !acc <0-100>", messageId);
        }

        double value;
        if (!double.TryParse(input.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0.0 || value > 100.0)
        {
            return Fail("Invalid percentage: use a value between 0 and 100.", messageId);
        }

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
            string modsNumber = Text(json.SelectToken("play.mods.number"));
            if (string.IsNullOrEmpty(modsNumber)) modsNumber = "0";
            string modsName = Text(json.SelectToken("play.mods.name"));
            string acc = value.ToString("0.##", CultureInfo.InvariantCulture);

            string url = "http://127.0.0.1:24050/api/calculate/pp?mode=0&mods=" + Uri.EscapeDataString(modsNumber) + "&acc=" + Uri.EscapeDataString(acc);
            string calculation = Http.GetStringAsync(url).GetAwaiter().GetResult();
            JObject result = JObject.Parse(calculation);
            JToken ppToken = result["pp"];
            float ppValue;
            if (ppToken == null || !float.TryParse(Text(ppToken), NumberStyles.Float, CultureInfo.InvariantCulture, out ppValue))
            {
                return Fail("Could not calculate PP.", messageId);
            }

            CPH.SetArgument("artist", artist);
            CPH.SetArgument("title", title);
            CPH.SetArgument("difficulty", difficulty);
            CPH.SetArgument("mods", FormatMods(modsName));
            CPH.SetArgument("starRating", FormatStars(Text(json.SelectToken("beatmap.stats.stars.total")),
                Text(json.SelectToken("beatmap.stats.stars.live"))));
            CPH.SetArgument("acc", acc + "%");
            CPH.SetArgument("ppWithAcc", ppValue.ToString("F2", CultureInfo.InvariantCulture) + "pp");
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError("!acc: " + ex);
            return Fail("Could not calculate PP.", messageId);
        }
    }

    private bool Fail(string message, string messageId)
    {
        CPH.TwitchReplyToMessage(message, messageId, true);
        return false;
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
