using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Newtonsoft.Json.Linq;

// Timer "PP Tracker": reads the account's total PP from Tosu and announces it in chat
// only when it increases. If Tosu does not respond or PP drops, nothing is written.
public class CPHInline
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    private const double MinGain = 0.05;
    private const string BaseVar = "osuPpBase";
    private const string SessionVar = "osuPpSession";

    public bool Execute()
    {
        string tosuUrl = "http://127.0.0.1:24050/json/v2";
        string payload;
        try
        {
            payload = Http.GetStringAsync(tosuUrl).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
        return Evaluate(payload);
    }

    internal bool Evaluate(string payload)
    {
        double pp;
        if (!TryReadTotalPp(payload, out pp))
        {
            return false;
        }

        long session;
        using (Process process = Process.GetCurrentProcess())
        {
            session = process.StartTime.ToUniversalTime().Ticks;
        }

        double basePp = CPH.GetGlobalVar<double>(BaseVar, true);
        if (CPH.GetGlobalVar<long>(SessionVar, false) != session || basePp <= 0.0)
        {
            // First tick of this session: store the baseline without announcing anything.
            CPH.SetGlobalVar(SessionVar, session, false);
            CPH.SetGlobalVar(BaseVar, pp, true);
            return false;
        }

        double gain = pp - basePp;
        if (gain < MinGain)
        {
            if (gain < 0.0)
            {
                // PP decreased (rework, decay): realign the baseline silently.
                CPH.SetGlobalVar(BaseVar, pp, true);
            }
            return false;
        }

        CPH.SetGlobalVar(BaseVar, pp, true);
        CPH.SetArgument("ppGain", gain.ToString("0.##", CultureInfo.InvariantCulture) + "pp");
        CPH.SetArgument("ppTotal", Math.Round(pp).ToString("0", CultureInfo.InvariantCulture) + "pp");
        return true;
    }

    private static bool TryReadTotalPp(string payload, out double pp)
    {
        pp = 0.0;
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        JObject json;
        try
        {
            json = JObject.Parse(payload);
        }
        catch
        {
            return false;
        }

        string raw = Text(json.SelectToken("profile.pp"));
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out pp) && pp > 0.0;
    }

    // Under it-IT culture JValue.ToString() uses a comma: force the invariant format.
    private static string Text(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
        {
            return null;
        }
        if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
        {
            return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);
        }
        return token.ToString();
    }
}
