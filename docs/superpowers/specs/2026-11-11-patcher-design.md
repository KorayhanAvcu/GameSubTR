# GameSubTR.Patcher — Otomatik Türkçe Yama Programı (Tasarım)

Tarih: 2026-11-11 · Durum: onay bekliyor

## Amaç

Kullanıcı bir oyun klasörü seçer; program oyunun motorunu tanır, İngilizce metinleri
Türkçeye çevirir, yamayı oyuna kurar ve istenince tamamen geri alır. GameSubTR canlı
(OCR/ses) çeviri yapar; Patcher bir kere kurulan kalıcı yama üretir.

**Başarı ölçütü:** Desteklenen dört motorun her birinden bir gerçek oyunda, kurulumdan
sonra oyun Türkçe metinle açılır, Türkçe karakterler doğru görünür ve "Yamayı kaldır"
oyunu bayt bayt eski haline döndürür.

## Kapsam

Kapsamda:
- İngilizce → Türkçe, Windows, kullanıcının kendi bilgisayarındaki tek oyunculu oyunlar.
- Motorlar: Ren'Py, RPG Maker MV/MZ, Unity, Unreal Engine 4/5.

Kapsam dışı:
- Özel motorlu oyunlar (tanınmazsa açıkça "desteklenmiyor" denir; kullanıcı canlı
  çeviri için GameSubTR'ye yönlendirilir).
- Görsellere gömülü yazılar, seslendirme.
- Çevrim içi / anti-cheat'li oyunlar (algılanırsa uyarı gösterilir, kurulum yapılmaz).
- Şifreli (AES anahtarı gereken) veya IoStore (`.utoc/.ucas`) paketli Unreal oyunları.
- Yamaların başkalarıyla paylaşılması / dağıtımı.

## Mimari

Aynı repo ve çözüm içinde iki yeni proje:

```
src/GameSubTR.Patching    Sınıf kütüphanesi (net8.0, UI yok): algılama, çıkarma, toplu çeviri, kurulum, yedek
src/GameSubTR.Patcher     WPF uygulaması (GameSubTR.Patcher.exe): tek pencere, sihirbaz akışı
tests/GameSubTR.Patching.Tests   xUnit
```

`GameSubTR.Patching` → `GameSubTR.Core`'a bağlıdır ve oradaki `TranslationService`,
`TranslationCache`, sözlük ve `SettingsStore`'u kullanır. Çeviri motoru ayarları
(API anahtarları) GameSubTR ile ortak `settings.json`'dan okunur.

### Birimler

| Birim | Sorumluluk |
|---|---|
| `EngineDetector` | Klasöre bakıp motoru ve sürüm ayrıntısını döndürür |
| `IGamePatcher` | Motor başına uygulama: `Extract`, `Install` |
| `PlaceholderGuard` | Çeviri öncesi kod/etiketleri maskeler, sonra geri koyar ve doğrular |
| `BatchTranslator` | Metin birimlerini toplu, devam edilebilir biçimde çevirir |
| `BackupManager` | Dosya yedekleme, manifest, geri alma |
| `ToolDownloader` | Dış araçları (BepInEx, XUnity, repak) sabit sürüm + SHA256 ile indirir |

```csharp
public sealed record TextUnit(string Id, string Source, string? Context);

public interface IGamePatcher
{
    EngineKind Engine { get; }
    Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct);
    Task InstallAsync(GameInfo game, IReadOnlyDictionary<string, string> translations,
                      BackupManager backup, CancellationToken ct);
}
```

### Ortak akış

1. **Algıla** — `EngineDetector`. Tanınmazsa dur.
2. **Çıkar** — `ExtractAsync` → `TextUnit` listesi. Aynı kaynak metinler tekilleştirilir.
3. **Çevir** — `PlaceholderGuard` ile maskele → `BatchTranslator` → maskeyi geri koy.
   Yer tutucu sayısı/sırası bozulduysa o birim bir kez yeniden denenir, yine bozuksa
   orijinal metin bırakılır ve rapora yazılır.
4. **Kur** — `InstallAsync`; değiştirilen/eklenen her dosya `BackupManager`'dan geçer.
5. **Rapor** — çevrilen / atlanan / hatalı birim sayısı, log dosyası yolu.

### Toplu çeviri

- Birimler 30–50'lik gruplar halinde, numaralı satırlar (`[n] metin`) olarak tek istekte
  gönderilir. Yanıtta numaralar bozulursa o grup birim birim yeniden çevrilir.
- Her çeviri anında birebir anahtarlı ilerleme deposuna yazılır
  (`%LocalAppData%\GameSubTR\patcher\<oyun>\translations.jsonl`) → işlem kesilirse kaldığı
  yerden devam. Core'daki `TranslationCache` kullanılmaz; anahtarı noktalama ve büyük-küçük
  harfi yok saydığı için yamada yanlış eşleşme üretir.
- Motor hatasında Core'daki mevcut yedek zinciri (Claude → OpenAI uyumlu → DeepL → Google) geçerlidir.
- Başlamadan önce birim ve karakter sayısı gösterilir; kullanıcı onaylar.

### Yedekleme ve geri alma

- Oyun klasöründe `.gamesubtr-patch/` dizini: `manifest.json` + `backup/`.
- Manifest her dosya için `{ path, action: modified|added, sha256Before, sha256After }` tutar.
- **Kaldır:** `added` dosyalar silinir, `modified` dosyalar yedekten geri kopyalanır.
  Dosya yamadan sonra değişmişse (oyun güncellemesi) kullanıcıya sorulur.
- Manifest varken yeniden kurulum: önce kaldır, sonra kur.

## Motor başına tasarım

### Ren'Py
- **Algılama:** `renpy/` klasörü + `game/*.rpy|*.rpyc|*.rpa`.
- **Çıkarma:** `game/**/*.rpy` içinden diyalog satırları, `menu` seçenekleri ve `_("...")`
  dizgeleri ayrıştırılır. Yalnızca `.rpyc`/`.rpa` varsa ilk sürümde "betikler derlenmiş,
  desteklenmiyor" denir.
- **Kurulum:** İki dosya eklenir: `game/gamesubtr_tr.json` (kaynak → çeviri sözlüğü) ve
  `game/zz_gamesubtr_tr.rpy` (sözlüğü `config.say_menu_text_filter` ve `config.replace_text`
  üzerinden uygular; isteğe bağlı olarak arayüz fontlarını Ren'Py ile gelen DejaVuSans yapar).
  `tl/turkish` çeviri blokları kullanılmaz: her satır için motorun ürettiği özet kimliğini
  dışarıdan birebir üretmek kırılgandır.
- **Sonuç:** Hiçbir orijinal dosya değişmez; yalnızca dosya eklenir.

### RPG Maker MV/MZ
- **Algılama:** `www/data/System.json` (MV) veya `data/System.json` (MZ) + `js/rpg_core.js` / `js/rmmz_core.js`.
- **Çıkarma:** `Map*.json` ve `CommonEvents.json`, `Troops.json` içindeki olay komutları
  (kod 401/405 metin, 102 seçenek, 101 konuşan adı); `Actors`, `Classes`, `Items`, `Weapons`,
  `Armors`, `Skills`, `States`, `Enemies` içinde `name`, `description`, `message1-4`,
  `nickname`, `profile`; `System.json` terimleri.
- **Korunanlar:** `\N[1]`, `\V[1]`, `\C[2]`, `\I[5]`, `\.` `\|` `\!` vb. kontrol kodları.
- **Kurulum:** JSON dosyaları yerinde yazılır (yedekli). Türkçe karakter için
  `fonts/` içine yedek font ve `gamefont.css` güncellemesi.
- **Sınır:** Eklenti (`js/plugins`) içindeki metinler çevrilmez; şifreli (`.rpgmvp` vb.) varlıklar etkilenmez, JSON şifreli değildir.

### Unity
- **Algılama:** `*_Data/` klasörü + `UnityPlayer.dll`. Mono (`*_Data/Managed/`) /
  IL2CPP (`GameAssembly.dll`) ve 32/64-bit ayrımı yapılır.
- **Yöntem:** Statik varlık düzenleme yerine çalışma zamanı: BepInEx (Mono için 5.x,
  IL2CPP için 6.x) + XUnity.AutoTranslator kurulur. `AutoTranslatorConfig.ini`
  `Language=tr`, `FromLanguage=en`, Türkçe destekli yedek TextMeshPro fontu ile yazılır.
- **Çeviri kaynağı:** XUnity kendi uç noktasıyla (varsayılan Google) oyun içinde çevirir ve
  `BepInEx/Translation/tr/Text/` altına önbellekler. Patcher ayrıca bu önbellek dosyasını
  okuyup "Çevirileri iyileştir" ile GameSubTR çeviri zincirinden (ör. Claude) geçirerek
  yeniden yazabilir.
- **Sonuç:** Orijinal dosya değişmez; eklenen dosyalar manifest'e yazılır.
- **Sınır:** İlk görülen metin kısa süre İngilizce kalabilir; görsele gömülü yazı çevrilmez.

### Unreal Engine 4/5
- **Algılama:** `Engine/Binaries/` + `<Proje>/Content/Paks/*.pak`.
- **Çıkarma:** `repak` ile pak listelenir, `Content/Localization/**/en/*.locres` çıkarılır.
  `.locres` okuma/yazma C# ile kendi kodumuzda (sürüm 0–3 biçimleri).
  `.locres` yoksa (metin doğrudan varlıklara gömülü) "desteklenmiyor" denir.
- **Kurulum:** Çevrilmiş `.locres`, `en` kültürünün üstüne yazacak şekilde
  `<Proje>-GameSubTR_P.pak` olarak paketlenip `Paks/` içine eklenir (oyun dili İngilizce
  kalır, metin Türkçe görünür).
- **Korunanlar:** `{0}`, `{Name}`, `<RichText>` etiketleri, `%s/%d`.
- **Sınır:** Şifreli pak veya IoStore → açık hata mesajı. Font Türkçe karakter içermiyorsa
  eksik harfler kutu görünebilir; ilk sürümde rapor edilir, font değişimi yapılmaz.

## Arayüz

Tek pencere, dört adım: **Oyun seç** (klasör seç veya sürükle-bırak; Steam kütüphanelerinden
kurulu oyun listesi) → **Algılama sonucu** (motor, tahmini metin sayısı, sınırlar) →
**Çeviri** (ilerleme çubuğu, duraklat/devam) → **Sonuç** (rapor, "Oyunu aç", "Yamayı kaldır").
Yaması kurulu oyun seçilirse doğrudan "Güncelle / Kaldır" ekranı açılır.

## Hata yönetimi

- Oyun çalışıyorsa (dosya kilitli) kurulum başlamaz; kapatılması istenir.
- Yazma izni yoksa (Program Files) yönetici olarak yeniden başlatma önerilir.
- Kurulum yarıda hata verirse o ana kadar yapılanlar manifest'ten otomatik geri alınır.
- İndirilen araçların SHA256'sı doğrulanır; uyuşmazsa çalıştırılmaz.
- Tüm adımlar `%LocalAppData%\GameSubTR\logs` altına loglanır.

## Test

- **Birim (xUnit, sahte dosyalarla):** her motor için algılama; çıkarma → sahte çeviri →
  kurulum → kaldır döngüsü ve kaldırma sonrası SHA256 eşitliği; `PlaceholderGuard`
  (her motorun kodları); `.locres` oku-yaz gidiş-dönüş; `BatchTranslator` devam etme;
  Ren'Py ve RPG Maker ayrıştırıcıları.
- **Elle (kullanıcı):** her motordan bir gerçek oyunla kur → oyna → kaldır.

## Yapım aşamaları

Her aşama kendi başına çalışır ve ayrı sürüm olarak yayınlanabilir:

1. **Çekirdek + Ren'Py + RPG Maker** — `Patching` kütüphanesi, yedekleme, toplu çeviri, WPF sihirbazı.
2. **Unity** — `ToolDownloader`, BepInEx/XUnity kurulumu, önbellek iyileştirme.
3. **Unreal** — `.locres` kodlayıcı, repak entegrasyonu.

CI ve Release iş akışları ikinci exe'yi de paketleyecek şekilde 1. aşamada güncellenir.
