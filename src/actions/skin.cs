using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    private const string TosuUrl = "http://127.0.0.1:24050/json/v2";
    private const string CatboxUrl = "https://catbox.moe/user/api.php";
    private const string X0Url = "https://x0.at/";
    private const int LinkCheckHours = 6;

    private static readonly HttpClient Http = CreateHttpClient();

    public bool Execute()
    {
        string messageId;
        CPH.TryGetArg("msgId", out messageId);
        try
        {
            string content = Http.GetStringAsync(TosuUrl).GetAwaiter().GetResult();
            JObject json = JObject.Parse(content);

            string gamePath = Text(json.SelectToken("folders.game"));
            string skinName = Text(json.SelectToken("settings.skin.name"));
            string skinDir = Text(json.SelectToken("folders.skin"));
            string songsPath = Text(json.SelectToken("folders.songs"));

            if (string.IsNullOrEmpty(skinName) || string.IsNullOrEmpty(gamePath))
            {
                return Fail("Skin not found.", messageId);
            }

            // osu!stable exposes the current skin folder (absolute, or just the folder name
            // under <game>\Skins); osu!lazer only reports ".".
            string stableDir = StableSkinFolder(gamePath, skinDir);
            bool stable = stableDir != null;
            string linksRoot = stable ? gamePath : LazerRoot(songsPath);
            if (string.IsNullOrEmpty(linksRoot))
            {
                return Fail("Skin not found.", messageId);
            }

            string safeName = SafeFileName(skinName);
            string archiveName = safeName + ".osk";
            string linkFile = Path.Combine(linksRoot, "Links", safeName + ".txt");
            string link = ReadLink(linkFile);
            if (!string.IsNullOrEmpty(link) && IsLinkCheckDue(linkFile) && !LinkAlive(link, linkFile))
            {
                link = null;
            }

            if (string.IsNullOrEmpty(link))
            {
                string archive = Path.Combine(TempFolder(), archiveName);
                DeleteFile(archive);

                if (stable)
                {
                    ZipFile.CreateFromDirectory(stableDir, archive, CompressionLevel.Optimal, false);
                }
                else
                {
                    string error;
                    if (!ExportLazerSkin(linksRoot, skinName, archive, out error))
                    {
                        CPH.LogError("!skin: lazer export failed: " + error);
                        return Fail("Could not create the .osk file.", messageId);
                    }
                }

                link = Upload(archive, archiveName);
                if (string.IsNullOrEmpty(link))
                {
                    DeleteFile(archive);
                    return Fail("Skin upload failed.", messageId);
                }

                DeleteFile(archive);
                SaveLink(linkFile, link);
            }

            CPH.SetArgument("skinName", skinName);
            CPH.SetArgument("link0", link);
            return true;
        }
        catch (Exception ex)
        {
            CPH.LogError("!skin: " + ex);
            return Fail("Skin not found.", messageId);
        }
    }

    // Returns the osu!lazer data folder (the one holding client.realm)
    private static string LazerRoot(string songsPath)
    {
        // tosu points at the beatmap store: <data>\files
        if (!string.IsNullOrEmpty(songsPath) && IsDirectory(songsPath))
        {
            DirectoryInfo parent = Directory.GetParent(songsPath.TrimEnd('\\', '/'));
            if (parent != null && File.Exists(Path.Combine(parent.FullName, "client.realm")))
            {
                return parent.FullName;
            }
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string storageIni = Path.Combine(appData, "osu", "storage.ini");
        if (File.Exists(storageIni))
        {
            string fullPath = ReadIniValue(storageIni, "FullPath");
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(Path.Combine(fullPath, "client.realm")))
            {
                return fullPath;
            }
        }

        string fallback = Path.Combine(appData, "osu");
        return File.Exists(Path.Combine(fallback, "client.realm")) ? fallback : null;
    }

    private bool ExportLazerSkin(string root, string skinName, string archive, out string error)
    {
        error = null;
        string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "osu-skin", "osu-skin-export.exe");
        if (!File.Exists(helper))
        {
            error = "helper not found: " + helper;
            return false;
        }

        ProcessStartInfo start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            Arguments = "--root " + Quote(root) + " --skin " + Quote(skinName) + " --out " + Quote(archive)
        };

        using (Process process = Process.Start(start))
        {
            if (process == null)
            {
                error = "helper did not start";
                return false;
            }

            System.Threading.Tasks.Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            System.Threading.Tasks.Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000))
            {
                try { process.Kill(); } catch { }
                error = "helper timed out";
                return false;
            }
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();

            JObject result;
            try
            {
                result = JObject.Parse(stdout.Trim());
            }
            catch
            {
                error = "invalid helper output: " + stdout + " " + stderr;
                return false;
            }

            if (result.Value<bool?>("ok") != true)
            {
                error = result.Value<string>("error") ?? "unknown helper error";
                return false;
            }
            return true;
        }
    }

    // Uploads the archive and returns a direct URL whose download keeps the skin file name.
    // x0.at serves it via Content-Disposition (the URL itself is a short random id);
    // catbox is the fallback but it renames the file to a random one.
    private string Upload(string archive, string fileName)
    {
        string url = UploadToX0(archive, fileName);
        if (!string.IsNullOrEmpty(url))
        {
            CPH.LogInfo("!skin: uploaded " + fileName + " -> " + url);
            return url;
        }

        url = UploadToCatbox(archive);
        if (!string.IsNullOrEmpty(url))
        {
            CPH.LogInfo("!skin: uploaded " + fileName + " (catbox fallback) -> " + url);
            return url;
        }
        return null;
    }

    // x0.at (0x0.st instance): anonymous, keeps the upload file name in
    // `Content-Disposition: attachment; filename="<name>"`, no account needed.
    private string UploadToX0(string archive, string fileName)
    {
        try
        {
            using (FileStream file = File.OpenRead(archive))
            using (MultipartFormDataContent form = new MultipartFormDataContent())
            {
                StreamContent body = new StreamContent(file);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(body, "file", fileName);

                using (HttpResponseMessage response = Http.PostAsync(X0Url, form).GetAwaiter().GetResult())
                {
                    string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                    if (response.IsSuccessStatusCode && text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return text;
                    }
                    CPH.LogWarn("!skin: x0.at replied " + (int)response.StatusCode + ": " + text);
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn("!skin: x0.at upload failed: " + ex.Message);
        }
        return null;
    }

    // Anonymous catbox upload (permanent, but the file name is randomised).
    private string UploadToCatbox(string archive)
    {
        try
        {
            using (FileStream file = File.OpenRead(archive))
            using (MultipartFormDataContent form = new MultipartFormDataContent())
            {
                form.Add(new StringContent("fileupload"), "reqtype");

                StreamContent body = new StreamContent(file);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(body, "fileToUpload", Path.GetFileName(archive));

                using (HttpResponseMessage response = Http.PostAsync(CatboxUrl, form).GetAwaiter().GetResult())
                {
                    string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                    if (response.IsSuccessStatusCode && text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return text;
                    }
                    CPH.LogWarn("!skin: catbox replied " + (int)response.StatusCode + ": " + text);
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn("!skin: catbox upload failed: " + ex);
        }
        return null;
    }

    // Checks that a cached link still serves the file. A 404/410 means the host dropped it
    // (x0.at expires files), so the skin is re-exported and re-uploaded. HEAD is not
    // available on x0.at, a one byte range request works everywhere.
    private bool LinkAlive(string link, string linkFile)
    {
        try
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using (HttpResponseMessage response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                int status = (int)response.StatusCode;
                bool alive = status != 404 && status != 410;
                if (alive)
                {
                    Touch(linkFile);
                }
                return alive;
            }
        }
        catch (Exception ex)
        {
            // Network hiccup: keep the cached link and do not retry on every command.
            CPH.LogWarn("!skin: link check failed: " + ex.Message);
            Touch(linkFile);
            return true;
        }
    }

    private static bool IsLinkCheckDue(string linkFile)
    {
        return (DateTime.UtcNow - File.GetLastWriteTimeUtc(linkFile)).TotalHours >= LinkCheckHours;
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // ignored
        }
    }

    private static string ReadLink(string linkFile)
    {
        if (!File.Exists(linkFile))
        {
            return null;
        }
        foreach (string line in File.ReadAllLines(linkFile))
        {
            string value = line.Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }
        return null;
    }

    private void SaveLink(string linkFile, string link)
    {
        try
        {
            string folder = Path.GetDirectoryName(linkFile);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            File.WriteAllText(linkFile, link + Environment.NewLine, new UTF8Encoding(false));
            CPH.LogInfo("!skin: wrote link cache " + linkFile);
        }
        catch (Exception ex)
        {
            CPH.LogWarn("!skin: cannot write link cache: " + ex.Message);
        }
    }

    private static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "osu-skin");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        string value = builder.ToString().Trim().TrimEnd('.');
        if (value.Length == 0)
        {
            value = "skin";
        }
        if (value.Length > 100)
        {
            value = value.Substring(0, 100).TrimEnd();
        }
        return value;
    }

    private static string ReadIniValue(string path, string key)
    {
        foreach (string line in File.ReadAllLines(path))
        {
            int separator = line.IndexOf('=');
            if (separator > 0 && line.Substring(0, separator).Trim() == key)
            {
                return line.Substring(separator + 1).Trim();
            }
        }
        return null;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "") + "\"";
    }

    private static bool IsDirectory(string path)
    {
        return !string.IsNullOrEmpty(path) && path != "." && Directory.Exists(path);
    }

    private static string StableSkinFolder(string gamePath, string skinDir)
    {
        if (IsDirectory(skinDir))
        {
            return skinDir;
        }
        if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(skinDir) || skinDir == ".")
        {
            return null;
        }
        string candidate = Path.Combine(gamePath, "Skins", skinDir.Trim().TrimEnd('\\', '/'));
        return IsDirectory(candidate) ? candidate : null;
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static HttpClient CreateHttpClient()
    {
        // Local tosu calls are instant, the catbox upload of a 10+ MB skin is not.
        // .NET Framework still needs TLS 1.2 enabled explicitly for catbox.moe.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamerBot-osu-skin/1.0");
        return client;
    }

    private bool Fail(string message, string messageId)
    {
        CPH.TwitchReplyToMessage(message, messageId, true);
        return false;
    }

    private static string Text(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return "";
        JValue value = token as JValue;
        if (value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer))
        {
            return Convert.ToString(value.Value, CultureInfo.InvariantCulture);
        }
        return token.ToString();
    }
}
