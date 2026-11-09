using System.Reflection;
using System.Text.Json;
using GameSubTR.Core.Logging;

namespace GameSubTR.App.Services;

/// <summary>GitHub Releases'ta daha yeni bir sürüm var mı kontrol eder.</summary>
public static class UpdateChecker
{
    public const string Repo = "KorayhanAvcu/GameSubTR";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Yeni sürüm varsa etiketini (ör. "v1.1.0") döndürür, yoksa null.</summary>
    public static async Task<string?> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (tag == null) return null;
            var core = tag.TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(core, out var latest)) return null;
            var cur = CurrentVersion;
            // CI derlemeleri 0.0.x olarak işaretlenir; onlarda bildirim gösterme.
            if (cur.Major == 0 && cur.Minor == 0) return null;
            return Normalize(latest) > Normalize(cur) ? tag : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Güncelleme kontrolü başarısız: " + ex.Message);
            return null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
