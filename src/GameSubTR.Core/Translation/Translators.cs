using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameSubTR.Core.Translation;

public sealed record TranslationRequest(
    string Text,
    IReadOnlyList<string> Context,
    IReadOnlyDictionary<string, string> Glossary,
    string GameName);

public interface ITranslator
{
    string Id { get; }
    string DisplayName { get; }
    bool IsConfigured { get; }
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct);
}

public sealed class TranslationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>LLM motorları için ortak istem (prompt) üretimi.</summary>
public static class PromptBuilder
{
    public const string System =
        "You are a professional video game localizer translating English in-game subtitles into natural, fluent Turkish. " +
        "Rules: (1) Output ONLY the Turkish translation, no quotes, notes or explanations. " +
        "(2) Keep the speaker's tone (casual, formal, angry, joking) and keep it short enough to read quickly. " +
        "(3) Do not translate character names, place names or proper nouns unless the glossary says otherwise. " +
        "(4) Keep line breaks: if the input has speaker lines starting with '-' or 'NAME:', keep the same structure. " +
        "(5) The text may come from OCR or speech recognition and contain small errors; silently fix obvious ones. " +
        "(6) Use previous lines only as context; translate only the line marked TRANSLATE.";

    public static string User(TranslationRequest r)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.GameName)) sb.Append("Game: ").AppendLine(r.GameName);
        if (r.Glossary.Count > 0)
        {
            sb.AppendLine("Glossary (source => Turkish):");
            foreach (var (k, v) in r.Glossary) sb.Append("- ").Append(k).Append(" => ").AppendLine(v);
        }
        if (r.Context.Count > 0)
        {
            sb.AppendLine("Previous lines (context only):");
            foreach (var c in r.Context) sb.Append("> ").AppendLine(c);
        }
        sb.AppendLine("TRANSLATE:");
        sb.Append(r.Text);
        return sb.ToString();
    }

    /// <summary>Modelin yanlışlıkla eklediği tırnak/önek gibi kalıntıları temizler.</summary>
    public static string CleanOutput(string s)
    {
        s = s.Trim();
        foreach (var prefix in new[] { "Turkish:", "Türkçe:", "Translation:", "Çeviri:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..].Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '“' && s[^1] == '”'))) s = s[1..^1].Trim();
        return s;
    }
}

/// <summary>Anthropic Claude Messages API.</summary>
public sealed class ClaudeTranslator(HttpClient http, Func<string?> apiKey, Func<string> model) : ITranslator
{
    public string Id => "claude";
    public string DisplayName => "Claude (LLM)";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey());

    public async Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model(),
            ["max_tokens"] = 600,
            ["temperature"] = 0.2,
            ["system"] = PromptBuilder.System,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = PromptBuilder.User(r) })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", apiKey());
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new TranslationException($"Claude HTTP {(int)resp.StatusCode}: {Trunc(json)}");
        var node = JsonNode.Parse(json);
        var sb = new StringBuilder();
        foreach (var part in node?["content"]?.AsArray() ?? new JsonArray())
            if (part?["type"]?.GetValue<string>() == "text") sb.Append(part["text"]?.GetValue<string>());
        var text = PromptBuilder.CleanOutput(sb.ToString());
        if (text.Length == 0) throw new TranslationException("Claude boş yanıt döndü");
        return text;
    }

    internal static string Trunc(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>OpenAI uyumlu /chat/completions (OpenAI, OpenRouter, LM Studio, Ollama...).</summary>
public sealed class OpenAiCompatibleTranslator(HttpClient http, Func<string?> baseUrl, Func<string?> apiKey, Func<string?> model) : ITranslator
{
    public string Id => "openai";
    public string DisplayName => "OpenAI uyumlu LLM";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(baseUrl()) && !string.IsNullOrWhiteSpace(model());

    public async Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct)
    {
        var url = baseUrl()!.TrimEnd('/') + "/chat/completions";
        var body = new JsonObject
        {
            ["model"] = model(),
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = PromptBuilder.System },
                new JsonObject { ["role"] = "user", ["content"] = PromptBuilder.User(r) })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        var key = apiKey();
        if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new TranslationException($"LLM HTTP {(int)resp.StatusCode}: {ClaudeTranslator.Trunc(json)}");
        var text = JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        // Bazı yerel modeller düşünme bloğu döndürür.
        int think = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (think >= 0) text = text[(think + 8)..];
        text = PromptBuilder.CleanOutput(text);
        if (text.Length == 0) throw new TranslationException("LLM boş yanıt döndü");
        return text;
    }
}

/// <summary>DeepL API (ücretsiz anahtarlar ":fx" ile biter).</summary>
public sealed class DeepLTranslator(HttpClient http, Func<string?> apiKey) : ITranslator
{
    public string Id => "deepl";
    public string DisplayName => "DeepL";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey());

    public async Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct)
    {
        var key = apiKey()!.Trim();
        var host = key.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com" : "https://api.deepl.com";
        var text = GlossaryProtector.Protect(r.Text, r.Glossary, out var map);
        var body = new JsonObject
        {
            ["text"] = new JsonArray(text),
            ["source_lang"] = "EN",
            ["target_lang"] = "TR",
            ["preserve_formatting"] = true
        };
        if (r.Context.Count > 0) body["context"] = string.Join("\n", r.Context);
        using var req = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new TranslationException($"DeepL HTTP {(int)resp.StatusCode}: {ClaudeTranslator.Trunc(json)}");
        var outText = JsonNode.Parse(json)?["translations"]?[0]?["text"]?.GetValue<string>() ?? "";
        if (outText.Length == 0) throw new TranslationException("DeepL boş yanıt döndü");
        return GlossaryProtector.Restore(outText, map);
    }
}

/// <summary>Anahtarsız Google uç noktası (translate.googleapis.com, client=gtx).</summary>
public sealed class GoogleFreeTranslator(HttpClient http, Func<bool> enabled) : ITranslator
{
    public string Id => "google";
    public string DisplayName => "Google (ücretsiz)";
    public bool IsConfigured => enabled();

    public async Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct)
    {
        var text = GlossaryProtector.Protect(r.Text, r.Glossary, out var map);
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=tr&dt=t&q=" + Uri.EscapeDataString(text);
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new TranslationException($"Google HTTP {(int)resp.StatusCode}");
        var outText = ParseResponse(json);
        if (outText.Length == 0) throw new TranslationException("Google boş yanıt döndü");
        return GlossaryProtector.Restore(outText, map);
    }

    public static string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
            foreach (var seg in root[0].EnumerateArray())
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                    sb.Append(seg[0].GetString());
        return sb.ToString().Trim();
    }
}

/// <summary>LLM olmayan motorlarda sözlük terimlerini yer tutucularla korur.</summary>
public static class GlossaryProtector
{
    public static string Protect(string text, IReadOnlyDictionary<string, string> glossary, out Dictionary<string, string> map)
    {
        map = new Dictionary<string, string>();
        if (glossary.Count == 0) return text;
        int i = 0;
        foreach (var (src, dst) in glossary.OrderByDescending(k => k.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(src)}\b";
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            var token = $"X{i++}Q";
            map[token] = string.IsNullOrWhiteSpace(dst) ? src : dst;
            text = System.Text.RegularExpressions.Regex.Replace(text, pattern, token, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return text;
    }

    public static string Restore(string text, Dictionary<string, string> map)
    {
        foreach (var (token, value) in map)
            text = System.Text.RegularExpressions.Regex.Replace(text, Regex(token), value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    // Motor yer tutucuya ek getirebilir (X0Q'nun → X0Q'nun); kelime sınırı olmadan değiştir.
    private static string Regex(string token) => System.Text.RegularExpressions.Regex.Escape(token);
}
