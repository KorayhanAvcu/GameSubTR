using System.Net.Http;
using GameSubTR.Core.Models;
using GameSubTR.Core.Translation;

namespace GameSubTR.Patcher;

/// <summary>
/// GameSubTR ile aynı motor zinciri (Claude → OpenAI uyumlu → DeepL → Google), yama için ayarlanmış:
/// önbellek yok (Patcher birebir eşleşen kendi deposunu kullanır), bağlam yok, toplu istekler için uzun zaman aşımı.
/// </summary>
public static class TranslationFactory
{
    public static TranslationService Create(Func<AppSettings> s, HttpClient http)
    {
        var list = new ITranslator[]
        {
            new ClaudeTranslator(http, () => s().Translation.AnthropicApiKey, () => s().Translation.AnthropicModel),
            new OpenAiCompatibleTranslator(http, () => s().Translation.OpenAiBaseUrl, () => s().Translation.OpenAiApiKey, () => s().Translation.OpenAiModel),
            new DeepLTranslator(http, () => s().Translation.DeepLApiKey),
            new GoogleFreeTranslator(http, () => s().Translation.UseGoogleFree)
        };
        return new TranslationService(list, () => s().Translation.Order, () => 0, cache: null)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }
}
