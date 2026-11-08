using System;
using System.Globalization;
using System.Net.Http;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

    public bool Execute()
    {
        CPH.TryGetArg("msgId", out string messageId);
        CPH.TryGetArg("input0", out string input);
        input = (input ?? "").Trim();

        int desiredPP;
        if (string.IsNullOrEmpty(input) || !int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out desiredPP) || desiredPP <= 0)
        {
            return Fail("Usage: !pp <integer pp>", messageId);
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
            string modsName = Text(json.SelectToken("play.mods.name"));
            string modsNumber = Text(json.SelectToken("play.mods.number"));
            if (string.IsNullOrEmpty(modsNumber)) modsNumber = "0";

            float maxPP;
            if (!float.TryParse(Text(json.SelectToken("performance.accuracy.100")), NumberStyles.Float, CultureInfo.InvariantCulture, out maxPP))
            {
                return Fail("Beatmap PP data is not available.", messageId);
            }

            float basePP = GetPP(90.0f, modsNumber);
            if (basePP < 0.0f)
            {
                return Fail("Beatmap PP data is not available.", messageId);
            }
            if (desiredPP > maxPP)
            {
                return Fail("PP too high: maximum " + maxPP.ToString("F2", CultureInfo.InvariantCulture) + "pp.", messageId);
            }
            if (desiredPP < basePP)
            {
                return Fail("PP too low: minimum " + basePP.ToString("F2", CultureInfo.InvariantCulture) + "pp.", messageId);
            }

            float low = 90.0f;
            float high = 100.0f;
            float bestAcc = 100.0f;
            float bestPP = 0.0f;
            for (int i = 0; i < 12; i++)
            {
                float mid = (low + high) / 2.0f;
                float pp = GetPP(mid, modsNumber);
                if (pp < 0.0f)
                {
                    return Fail("PP calculation failed, try again.", messageId);
                }
                if (pp >= desiredPP)
                {
                    bestAcc = mid;
                    bestPP = pp;
                    high = mid;
                }
                else
                {
                    low = mid;
                }
            }

            if (bestPP <= 0.0f)
            {
                bestPP = GetPP(100.0f, modsNumber);
                bestAcc = 100.0f;
                if (bestPP < 0.0f)
                {
                    return Fail("PP calculation failed, try again.", messageId);
                }
            }

            CPH.SetArgument("artist", artist);
            CPH.SetArgument("title", title);
            CPH.SetArgument("difficulty", difficulty);
            CPH.SetArgument("mods", FormatMods(modsName));
            CPH.SetArgument("starRating", FormatStars(Text(json.SelectToken("beatmap.stats.stars.total")),
                Text(json.SelectToken("beatmap.stats.stars.live"))));
            CPH.SetArgument("desiredAcc", bestAcc.ToString("F2", CultureInfo.InvariantCulture) + "%");
            CPH.SetArgument("scoredPP", bestPP.ToString("F2", CultureInfo.InvariantCulture) + "pp");
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError("!pp: " + ex);
            return Fail("PP calculation failed, try again.", messageId);
        }
    }

    private float GetPP(float acc, string modsNumber)
    {
        try
        {
            string accText = acc.ToString("0.00", CultureInfo.InvariantCulture);
            string url = "http://127.0.0.1:24050/api/calculate/pp?mode=0&mods=" + Uri.EscapeDataString(modsNumber) + "&acc=" + Uri.EscapeDataString(accText);
            string content = Http.GetStringAsync(url).GetAwaiter().GetResult();
            JObject json = JObject.Parse(content);
            JToken ppToken = json["pp"];
            float pp;
            if (ppToken == null || !float.TryParse(Text(ppToken), NumberStyles.Float, CultureInfo.InvariantCulture, out pp))
            {
                return -1.0f;
            }
            return pp;
        }
        catch
        {
            return -1.0f;
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
