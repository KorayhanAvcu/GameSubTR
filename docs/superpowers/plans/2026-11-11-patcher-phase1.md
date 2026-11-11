# GameSubTR.Patcher — 1. Aşama (Çekirdek + Ren'Py + RPG Maker) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Oyun klasörü seçildiğinde Ren'Py ve RPG Maker MV/MZ oyunlarına geri alınabilir Türkçe yama kuran `GameSubTR.Patcher.exe`'yi üretmek.

**Architecture:** UI'sız `GameSubTR.Patching` kütüphanesi (algılama → çıkarma → maskeleme → toplu çeviri → kurulum → yedek) `GameSubTR.Core`'daki `TranslationService` motor zincirini kullanır. `GameSubTR.Patcher` WPF uygulaması bu kütüphaneyi tek pencerelik sihirbazla sürer. Unity ve Unreal bu planın dışındadır (ayrı planlar); `IGamePatcher` arayüzü onların eklenmesine hazır bırakılır.

**Tech Stack:** C# / .NET 8, WPF, xUnit 2.9.2, System.Text.Json (ek NuGet paketi yok).

**Spec:** `docs/superpowers/specs/2026-11-11-patcher-design.md`

## Global Constraints

- Hedef: `net8.0` (kütüphane, test), `net8.0-windows10.0.19041.0` (WPF). Kütüphaneye NuGet bağımlılığı eklenmez.
- Yalnızca İngilizce → Türkçe. Arayüz metinleri ve log mesajları Türkçe.
- Değiştirilen/eklenen her oyun dosyası `BackupManager`'dan geçer; kaldırma sonrası oyun klasörü SHA256 düzeyinde eski haline döner ve `.gamesubtr-patch/` silinir.
- Anti-cheat algılanan oyuna kurulum yapılmaz.
- Çeviri ilerlemesi oyun klasörüne değil `%LocalAppData%\GameSubTR\patcher\<oyunYoluSHA1>\translations.jsonl`'e yazılır.
- Ayarlar ve API anahtarları GameSubTR ile ortak: `%LocalAppData%\GameSubTR\settings.json`; loglar `%LocalAppData%\GameSubTR\logs`.
- Metin dosyaları okunduğu kodlamayla (UTF-8, BOM varsa BOM'lu) ve satır sonuyla yazılır.
- Commit'ler `Korayhan Avcu <korayhanavcu@gmail.com>` kimliğiyle; `Co-Authored-By` satırı eklenmez. Dal: `patcher`. Push yapılmaz.
- Test komutu: `dotnet test tests/GameSubTR.Patching.Tests -c Release --nologo`

## Spec'ten bilinçli sapmalar (spec bu plana göre güncellendi)

- **Ren'Py:** `tl/turkish` çeviri blokları yerine tek eklenen betik + JSON sözlük kullanılır (`config.say_menu_text_filter` ve `config.replace_text`). Gerekçe: Ren'Py çeviri blokları her satır için motorun ürettiği özet kimliğini ister; bunu dışarıdan birebir üretmek kırılgandır.
- **Toplu çeviri:** Tüm motorlarda aynı yol: satırlar `[n] ` ön ekiyle tek metin olarak gönderilir, işaretler bozulursa birim birim çeviriye düşülür. `TranslationCache` kullanılmaz (anahtarı noktalama/büyük-küçük harf yok sayar; yama için birebir eşleşme gerekir).

## Review Focus

1. **Türkçe çeviri orijinalden uzun** → RPG Maker mesaj penceresinden taşmamalı: satırlar yeniden sarılır (Task 6 testi `Install_RewrapsLongTranslation`).
2. **Çeviri motoru satır işaretini düşürür / boş döner** → o grup birim birim çevrilir, yine olmazsa orijinal kalır ve rapora yazılır (Task 4 testi `Batch_FallsBackWhenMarkersLost`).
3. **Oyun yamadan sonra güncellenmiş** (dosya değişmiş) → kaldırma o dosyayı sessizce ezmez, çakışma olarak döndürür (Task 2 testi `Uninstall_ReportsConflict_WhenFileChangedAfterPatch`).
4. **BOM'lu `.rpy` / BOM'suz JSON, CRLF satır sonu** → ayrıştırma bozulmaz, yazılan dosya aynı kodlamada kalır (Task 5 `Extract_HandlesBomAndCrlf`, Task 6 `Install_PreservesEncodingWithoutBom`).
5. **Çevrilecek harf içermeyen birimler** (boş, yalnızca sayı/yer tutucu, dosya adı gibi `bgm_01.ogg`) → çeviri motoruna gönderilmez, aynen kalır (Task 3 testi `IsTranslatable_RejectsNonText`).

---

## Dosya yapısı

```
src/GameSubTR.Patching/
  GameSubTR.Patching.csproj
  Models.cs                 EngineKind, GameInfo, TextUnit, PatchReport, IGamePatcher
  EngineDetector.cs
  BackupManager.cs
  PlaceholderGuard.cs
  TranslationStore.cs       birebir anahtarlı jsonl ilerleme deposu
  BatchTranslator.cs
  PatchSession.cs           uçtan uca akış + geri alma
  SteamLibrary.cs
  RenPy/RenPyScriptParser.cs, RenPy/RenPyPatcher.cs
  RpgMaker/RpgMakerPatcher.cs
src/GameSubTR.Patcher/
  GameSubTR.Patcher.csproj, App.xaml(.cs), MainWindow.xaml(.cs), TranslationFactory.cs
tests/GameSubTR.Patching.Tests/
  GameSubTR.Patching.Tests.csproj, TestGames.cs (sahte oyun klasörü üreticileri), *Tests.cs
```

---

### Task 1: Proje iskeleti, modeller, EngineDetector

**Files:**
- Create: `src/GameSubTR.Patching/GameSubTR.Patching.csproj`, `Models.cs`, `EngineDetector.cs`
- Create: `tests/GameSubTR.Patching.Tests/GameSubTR.Patching.Tests.csproj`, `TestGames.cs`, `EngineDetectorTests.cs`
- Modify: `GameSubTR.sln` (iki projeyi ekle)

**Interfaces — Produces:**
```csharp
namespace GameSubTR.Patching;
public enum EngineKind { Unknown, RenPy, RpgMakerMV, RpgMakerMZ, Unity, Unreal }
public sealed record GameInfo(string RootDir, string Name, EngineKind Engine, string DataDir, bool AntiCheat, string? UnsupportedReason);
public sealed record TextUnit(string Id, string Source, string? Context);
public interface IGamePatcher
{
    bool CanHandle(EngineKind engine);
    Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct);
    /// translations: anahtar = TextUnit.Source, değer = Türkçe metin. Eksik anahtar = orijinal kalır.
    Task InstallAsync(GameInfo game, IReadOnlyDictionary<string, string> translations, BackupManager backup, CancellationToken ct);
}
public static class EngineDetector { public static GameInfo Detect(string rootDir); }
```
`TestGames` (test yardımcıları): `static string CreateRenPy(Action<string gameDir>? fill = null)`, `CreateRpgMv()`, `CreateRpgMz()`, `CreateEmpty()` → geçici klasör yolu döndürür.

Algılama kuralları (spec'ten):
- Ren'Py: `renpy/` klasörü var ve `game/` içinde `.rpy|.rpyc|.rpa` var. `DataDir = game`. Hiç `.rpy` yoksa `UnsupportedReason = "Betikler derlenmiş (.rpyc/.rpa); bu sürümde desteklenmiyor."`
- RPG Maker MV: `www/data/System.json` + `www/js/rpg_core.js` → `DataDir = www`. MZ: `data/System.json` + `js/rmmz_core.js` → `DataDir = kök`. (MV'nin `www`'suz dağıtımı: `data/System.json` + `js/rpg_core.js` → MV, `DataDir = kök`.)
- Unity (`*_Data` + `UnityPlayer.dll`) ve Unreal (`Engine/Binaries` + `*/Content/Paks/*.pak`) tanınır ama `UnsupportedReason = "Bu motor sonraki sürümde desteklenecek."`
- Hiçbiri: `Unknown`, `UnsupportedReason = "Oyun motoru tanınamadı. Canlı çeviri için GameSubTR'yi kullanabilirsin."`
- `AntiCheat = true`: kök veya bir alt düzeyde `EasyAntiCheat` klasörü, `BattlEye` klasörü, `*EAC*.exe`, `BEService*.exe`, `vgk.sys`.
- `Name`: `RootDir`'in klasör adı.

- [ ] **Step 1:** csproj'ları oluştur (Patching: `net8.0`, Nullable+ImplicitUsings, `ProjectReference` → Core; Tests: Core.Tests csproj'unun kopyası, referans → Patching), `dotnet sln add` ile ekle.
- [ ] **Step 2: Başarısız testleri yaz** (`EngineDetectorTests`):
  - `Detects_RenPy` → `Engine == RenPy`, `DataDir` `game` ile biter, `UnsupportedReason == null`
  - `RenPy_CompiledOnly_IsUnsupported` → yalnızca `script.rpyc` → `UnsupportedReason` "derlenmiş" içerir
  - `Detects_RpgMakerMV_WithWww`, `Detects_RpgMakerMV_WithoutWww`, `Detects_RpgMakerMZ`
  - `Unity_IsRecognizedButUnsupported`, `Unknown_ForEmptyFolder`
  - `Flags_AntiCheat` → `EasyAntiCheat/` klasörü eklenince `AntiCheat == true`
- [ ] **Step 3:** Testleri çalıştır, derleme hatasıyla başarısız olduğunu gör.
- [ ] **Step 4:** `Models.cs` ve `EngineDetector.Detect` uygula.
- [ ] **Step 5:** Testler geçer.
- [ ] **Step 6:** Commit: `Patching: proje iskeleti ve motor algılama`

### Task 2: BackupManager

**Files:** Create `src/GameSubTR.Patching/BackupManager.cs`, `tests/.../BackupManagerTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record UninstallResult(int Restored, int Removed, IReadOnlyList<string> Conflicts);
public sealed class BackupManager
{
    public const string DirName = ".gamesubtr-patch";
    public BackupManager(string gameRoot);
    public bool IsInstalled { get; }                       // manifest.json var mı
    public void WriteFile(string relativePath, byte[] content);   // var olanı önce yedekler (modified), yoksa added
    public void Commit();                                  // sha256After'ları hesaplar, manifest.json yazar
    public UninstallResult Uninstall(bool force = false);  // force=false: çakışan dosyaya dokunmaz
}
```
- Düzen: `<root>/.gamesubtr-patch/manifest.json` + `backup/<göreli yol>`. Manifest girdisi: `{ path, action: "modified"|"added", sha256Before, sha256After }`; yollar `/` ile.
- Aynı dosyaya ikinci `WriteFile` yedeği ezmez (ilk hali korunur).
- `WriteFile` her çağrıda manifest'i hemen diske yazar (yarıda kesilirse geri alınabilsin); `Commit` `sha256After`'ı doldurur.
- `Uninstall`: dosyanın şimdiki özeti `sha256After`'dan farklıysa (ve `sha256After` doluysa) çakışma → listeye ekle, `force` değilse atla. Çakışma yoksa veya `force` ise: `added` sil (boş kalan klasörleri de), `modified` yedekten geri kopyala; sonunda çakışma kalmadıysa `.gamesubtr-patch/` sil.
- `relativePath` kök dışına çıkıyorsa (`..`, mutlak yol) `ArgumentException`.

- [ ] **Step 1: Testler:**
  - `Roundtrip_RestoresOriginalBytes` → bir dosyayı değiştir + bir dosya ekle + `Commit` + `Uninstall` ⇒ değişen dosyanın SHA256'sı başlangıçla aynı, eklenen dosya ve `.gamesubtr-patch` yok, `Restored==1, Removed==1`
  - `SecondWrite_KeepsFirstBackup`
  - `Uninstall_WithoutCommit_StillRestores` (yarıda kalan kurulum)
  - `Uninstall_ReportsConflict_WhenFileChangedAfterPatch` → `Conflicts` o yolu içerir, dosya dokunulmamış, `.gamesubtr-patch` duruyor; `force:true` ile geri yüklenir
  - `WriteFile_RejectsPathOutsideRoot`
- [ ] **Step 2:** Başarısız gör → **Step 3:** uygula → **Step 4:** geçer → **Step 5:** Commit: `Patching: yedekleme ve geri alma`

### Task 3: PlaceholderGuard

**Files:** Create `src/GameSubTR.Patching/PlaceholderGuard.cs`, `tests/.../PlaceholderGuardTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record MaskedText(string Text, IReadOnlyList<string> Tokens);
public static class PlaceholderGuard
{
    public static MaskedText Mask(string source, EngineKind engine);      // her yer tutucu → <x0/>, <x1/>…
    public static bool TryRestore(string translated, MaskedText masked, out string result);
    public static bool IsTranslatable(string source, EngineKind engine);
}
```
- Maskelenen kalıplar — hepsinde: satır sonu (`\r\n`|`\n`), `%s %d %1 %2`, `{0}`.
  - Ren'Py: `[...]` (iç içe olmayan köşeli ifade, `[[` hariç), `{...}` metin etiketleri (`{b}`, `{/b}`, `{color=#fff}`, `{w=0.5}`), `%(name)s`.
  - RPG Maker: `\X[n]` (`\N[1]`, `\V[12]`, `\C[2]`, `\I[5]`, `\PX[..]` vb. — `\\[A-Za-z]+\[[^\]]*\]`), tek karakterli kodlar `\. \| \! \> \< \^ \$ \{ \} \\ \G`.
- `TryRestore`: çevirideki `<\s*x\s*(\d+)\s*/\s*>` eşleşmelerini token'larla değiştirir. Her token tam bir kez geçmiyorsa `false`. Sıra değişebilir (Türkçe söz dizimi).
- `IsTranslatable`: maskelendikten sonra en az 2 Latin harfi kalmalı; boşluk içermeyen ve `.ogg|.png|.mp3|.jpg|.webp|.m4a|.wav|.rpy|.json` ile biten metin reddedilir.

- [ ] **Step 1: Testler:**
  - `Mask_RenPy` → `"Hi [player], {b}run{/b}!\nNow"` ⇒ `"Hi <x0/>, <x1/>run<x2/>!<x3/>Now"`, 4 token
  - `Mask_RpgMaker` → `@"\N[1]: Take \C[2]this\C[0]\."` ⇒ 4 token, metinde ters eğik çizgi kalmaz
  - `Restore_AllowsReorderAndSpaces` → `"< x1 / >koş<x2/>, <x0/>!<x3/>Şimdi"` geri yüklenir
  - `Restore_FailsWhenTokenMissing`, `Restore_FailsWhenTokenDuplicated`
  - `IsTranslatable_RejectsNonText` → `""`, `"   "`, `"123"`, `"[name]"`, `"bgm_01.ogg"`, `"..."` ⇒ false; `"Go"`, `"Hello [name]"` ⇒ true
- [ ] **Step 2–4:** başarısız gör, uygula, geçer. **Step 5:** Commit: `Patching: yer tutucu koruma`

### Task 4: TranslationStore + BatchTranslator

**Files:** Create `TranslationStore.cs`, `BatchTranslator.cs`, `tests/.../BatchTranslatorTests.cs`

**Interfaces:**
- Consumes: `GameSubTR.Core.Translation.TranslationService.TranslateAsync(string text, string game, IReadOnlyDictionary<string,string> glossary, CancellationToken)` → `TranslationResult.Text`; `PlaceholderGuard` (Task 3).
- Produces:
```csharp
public sealed class TranslationStore   // birebir (ordinal) anahtar; her Put satır ekler: {"s":..,"t":..}
{
    public TranslationStore(string filePath);
    public static string PathFor(string gameRoot);   // %LocalAppData%\GameSubTR\patcher\<sha1(küçük harf tam yol)>\translations.jsonl
    public bool TryGet(string source, out string target);
    public void Put(string source, string target);
    public int Count { get; }
}
public sealed record BatchProgress(int Done, int Total, int Failed);
public sealed record BatchResult(IReadOnlyDictionary<string, string> Translations, IReadOnlyList<string> Failed, int Skipped);
public sealed class BatchTranslator
{
    public BatchTranslator(TranslationService service, TranslationStore store, int batchSize = 40, int maxBatchChars = 4000);
    public Task<BatchResult> TranslateAsync(IReadOnlyList<TextUnit> units, EngineKind engine, string gameName,
        IReadOnlyDictionary<string, string> glossary, IProgress<BatchProgress>? progress, CancellationToken ct);
}
```
Algoritma:
1. `Source`'a göre tekilleştir. `IsTranslatable` olmayanlar `Skipped` (sonuç sözlüğüne girmez). Depoda olanlar doğrudan sonuca.
2. Kalanları sırayla `batchSize` / `maxBatchChars` sınırına göre grupla. Her birimi `Mask` et; grup metni = `"[1] <maskeli>\n[2] <maskeli>…"`.
3. `service.TranslateAsync(grupMetni, gameName, glossary, ct)`. Yanıtı `^\s*\[(\d+)\]\s?(.*)$` (çok satırlı) ile ayrıştır. Numara kümesi `1..n` ile birebir değilse veya herhangi bir satırda `TryRestore` başarısızsa → o grup **birim birim** yeniden çevrilir (her biri tek maskeli metin olarak).
4. Birim birim çeviride de `TryRestore` başarısız, sonuç boş veya `TranslationException` ise → `Failed`'e ekle (orijinal kalır).
5. Başarılı her birim anında `store.Put`. İptal (`OperationCanceledException`) yukarı fırlatılır; o ana kadarkiler depoda kalır.
6. Her grup sonunda `progress.Report`.

Çağıran taraf `TranslationService`'i **`cache: null`** ve `Timeout = 60 sn` ile kurar (Task 9).

- [ ] **Step 1: Testler** (`FakeTranslator` deseni `tests/GameSubTR.Core.Tests/TranslationTests.cs:8`'deki gibi; servis `new TranslationService(new[]{fake}, () => new[]{"fake"}, () => 0, null)`):
  - `Batch_TranslatesAndRestoresPlaceholders` → sahte motor her satırda `[n] ` ön ekini koruyup metni `"TR:"+…` yapar; `"Hi [player]"` ⇒ `"TR:Hi [player]"`
  - `Batch_SendsOneRequestPerBatch` → 5 birim, `batchSize:5` ⇒ `fake.Calls == 1`
  - `Batch_FallsBackWhenMarkersLost` → sahte motor çok satırlı girdide işaretleri siler, tek satırlıda düzgün çevirir ⇒ tüm birimler çevrilmiş, `Calls == 1 + n`
  - `Unit_FailsWhenPlaceholderDropped` → motor `<x0/>`'ı hep siler ⇒ birim `Failed`'de, `Translations`'da yok
  - `Skips_NonTranslatable_And_Dedupes` → `["Hello","Hello","123"]` ⇒ motor 1 birim görür, `Skipped == 1`
  - `Resume_UsesStore` → aynı depo dosyasıyla ikinci çalıştırma ⇒ `fake.Calls` artmaz
  - `Store_IsExactMatch` → `"Hello"` kayıtlıyken `TryGet("hello!")` false
  - `Cancel_KeepsFinishedWork` → 2. grupta iptal ⇒ istisna fırlar, 1. grubun birimleri depoda
- [ ] **Step 2–4:** başarısız gör, uygula, geçer. **Step 5:** Commit: `Patching: toplu ve devam edilebilir çeviri`

### Task 5: Ren'Py — ayrıştırıcı ve yamacı

**Files:** Create `RenPy/RenPyScriptParser.cs`, `RenPy/RenPyPatcher.cs`, `tests/.../RenPyTests.cs`

**Interfaces:**
- Consumes: `IGamePatcher`, `TextUnit`, `GameInfo` (Task 1), `BackupManager.WriteFile` (Task 2).
- Produces: `public static class RenPyScriptParser { public static IEnumerable<TextUnit> Parse(string scriptText, string fileName); }`, `public sealed class RenPyPatcher : IGamePatcher { public bool ReplaceFonts { get; init; } = true; }`

Ayrıştırma kuralları (satır bazlı; `Id = "<dosya>:<satırNo>"`, `Context` = konuşan tanımlayıcı veya `"menu"`/`"string"`):
- Yorumlar (`#` ile başlayan satırlar) ve `python:` / `init python:` / `screen` / `style` / `transform` / `image` bloklarının gövdeleri (daha derin girintili satırlar) diyalog aramasında atlanır.
- **Diyalog:** girintiden sonra `^(?:(?<who>[A-Za-z_]\w*)(?:\s+[A-Za-z_]\w*)*\s+)?"(?<t>(?:[^"\\]|\\.)*)"(?:\s+(?:nointeract|with\s+\S.*|\(.*\)))?\s*$`. İlk sözcük şu listede ise diyalog değildir: `show scene hide play queue stop voice image define default jump call return if elif else while for label menu screen text textbutton style transform init python pause window with translate old new key add use has action $ renpy`. `who "..."` yerine yalnızca `"Ad" "metin"` (iki dizge) biçimi desteklenmez, atlanır.
- **Menü seçeneği:** `^"(?<t>(?:[^"\\]|\\.)*)"(?:\s+if\s+.+)?\s*:\s*$`
- **`_()` dizgeleri:** her yerde (atlanan bloklar dahil) `_\(\s*"((?:[^"\\]|\\.)*)"\s*\)`.
- Tek tırnaklı (`'...'`) eşdeğerleri de aynı kurallarla; üç tırnaklı (`"""`) dizgeler atlanır.
- `Source` çalışma zamanındaki metindir: `\"`→`"`, `\'`→`'`, `\n`→satır sonu, `\\`→`\`, `\ `→boşluk; ardışık boşluklar teke iner.

Kurulum (`InstallAsync`) — orijinal dosya değişmez, iki dosya **eklenir**:
- `game/gamesubtr_tr.json`: `{ "kaynak": "çeviri", … }` (UTF-8, BOM'suz).
- `game/zz_gamesubtr_tr.rpy` — içeriği birebir:
```renpy
# GameSubTR.Patcher tarafından üretildi. Kaldırmak için Patcher'da "Yamayı kaldır".
init offset = -1
__FONTS__
init 999 python:
    import json as _gstr_json
    _gstr_map = {}
    try:
        _gstr_f = renpy.file("gamesubtr_tr.json")
        _gstr_map = _gstr_json.loads(_gstr_f.read().decode("utf-8"))
        _gstr_f.close()
    except Exception as _gstr_e:
        renpy.log("GameSubTR: %r" % (_gstr_e,))
    _gstr_prev_filter = config.say_menu_text_filter
    def _gstr_filter(s):
        if _gstr_prev_filter is not None:
            s = _gstr_prev_filter(s)
        return _gstr_map.get(s, s)
    config.say_menu_text_filter = _gstr_filter
    _gstr_prev_replace = config.replace_text
    def _gstr_replace(s):
        if _gstr_prev_replace is not None:
            s = _gstr_prev_replace(s)
        return _gstr_map.get(s, s)
    config.replace_text = _gstr_replace
```
  `__FONTS__`: `ReplaceFonts` true ise şu beş satır, değilse boş:
  `define gui.text_font = "DejaVuSans.ttf"`, aynı biçimde `gui.name_text_font`, `gui.interface_text_font`, `gui.button_text_font`, `gui.choice_button_text_font`. (DejaVuSans Ren'Py ile birlikte gelir ve Türkçe karakterleri içerir.)
- `ExtractAsync`: `game/**/*.rpy` (kendi `zz_gamesubtr_tr.rpy` hariç), `tl/` altı hariç.

- [ ] **Step 1: Testler:**
  - `Parse_Dialogue_Narration_Menu_Strings` → girdi:
    ```
    label start:
        e "Hello, [player]!"
        "It was a dark night."
        e happy "Nice \"day\"." with dissolve
        menu:
            "Go left" if seen:
                jump left
        show eileen happy
        $ x = "not dialogue"
        textbutton _("Start") action Start()
    ```
    ⇒ Source'lar sırasıyla: `Hello, [player]!`, `It was a dark night.`, `Nice "day".`, `Go left`, `Start`; başka birim yok
  - `Parse_SkipsPythonBlocksAndComments`, `Parse_SkipsTripleQuoted`, `Parse_UnescapesNewline` (`"A\nB"` ⇒ gerçek satır sonu)
  - `Extract_HandlesBomAndCrlf` → BOM'lu, CRLF'li `.rpy` ⇒ ilk satırdaki diyalog dahil hepsi bulunur
  - `Extract_IgnoresTlFolderAndOwnFile`
  - `Install_AddsTwoFiles_AndUninstallRemovesThem` → `gamesubtr_tr.json` ayrıştırılınca `["Go left"] == "Sola git"`; `.rpy` `config.say_menu_text_filter` içerir; `Uninstall` sonrası oyun klasörünün dosya listesi + özetleri başlangıçla aynı
  - `Install_WithoutFonts_OmitsGuiDefines`
- [ ] **Step 2–4:** başarısız gör, uygula, geçer. **Step 5:** Commit: `Patching: Ren'Py desteği`

### Task 6: RPG Maker MV/MZ yamacı

**Files:** Create `RpgMaker/RpgMakerPatcher.cs`, `tests/.../RpgMakerTests.cs`

**Interfaces:** Consumes Task 1–2 tipleri. Produces `public sealed class RpgMakerPatcher : IGamePatcher { public bool ReplaceFont { get; init; } = true; public string FontSourcePath { get; init; } = @"C:\Windows\Fonts\arial.ttf"; }`

Çıkarma (`<DataDir>/data/*.json`, `System.Text.Json.Nodes` ile):
- **Olay komutları** — `Map*.json` (`events[].pages[].list`), `CommonEvents.json` (`[].list`), `Troops.json` (`[].pages[].list`):
  - Ardışık `code 401` komutları tek birim: `parameters[0]`'lar `\n` ile birleştirilir. Aynısı `code 405` (kayan metin) için.
  - `code 102`: `parameters[0]` dizisindeki her seçenek ayrı birim.
  - `code 101` (MZ): `parameters[4]` (konuşan adı) doluysa birim.
- **Veritabanı** — `Actors, Classes, Items, Weapons, Armors, Skills, States, Enemies`: dolu olan `name, nickname, profile, description, message1, message2, message3, message4`.
- **System.json:** `gameTitle`, `terms.basic[]`, `terms.commands[]`, `terms.params[]`, `terms.messages.*`, `armorTypes[]`, `weaponTypes[]`, `skillTypes[]`, `equipTypes[]`, `elements[]`.
- `Id = "<dosya>:<json yolu>"`, `Context = dosya adı`.

Kurulum:
- Her JSON yeniden yazılır (yalnızca en az bir değeri değişen dosyalar; `BackupManager.WriteFile`). BOM durumu korunur; çıktı sıkıştırılmış (girintisiz) JSON, ASCII dışı karakterler kaçışsız (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`).
- 401/405 grubu: çeviri satırlara bölünür ve **yeniden sarılır**: sınır = `max(40, o mesajın orijinal satırlarının en uzunu)` görünür karakter (kontrol kodları sayılmaz), sözcük sınırından. Eski komut grubu, aynı `indent` ve `code` ile yeni satır sayısı kadar komutla değiştirilir.
- Seçenekler (102) ve veritabanı adları sarılmaz.
- Font (`ReplaceFont` ve `FontSourcePath` mevcutsa): `FontSourcePath` → `<DataDir>/fonts/gamesubtr-tr.ttf`. MV: `fonts/gamefont.css` içindeki `url(...)` → `url("gamesubtr-tr.ttf")`. MZ: `System.json` → `advanced.mainFontFilename = "gamesubtr-tr.ttf"`. Kaynak font yoksa adım atlanır, `Log.Warn`.

- [ ] **Step 1: Testler** (`TestGames.CreateRpgMv/Mz` küçük ama gerçek biçimli JSON üretir):
  - `Extract_JoinsConsecutive401` → iki 401 (`"Hello \N[1]."`, `"How are you?"`) ⇒ tek birim `"Hello \\N[1].\nHow are you?"`
  - `Extract_Choices_Database_SystemTerms` → 102 seçenekleri, `Items[1].name/description`, `System.terms.commands` birimleri var; boş dizgeler ve `null`'lar yok
  - `Extract_Mz_SpeakerName`
  - `Install_ReplacesTextAndKeepsStructure` → çeviri sonrası `Map001.json` geçerli JSON, 401 `parameters[0]`'ları Türkçe, diğer komutlar (kod 0, 201…) birebir aynı
  - `Install_RewrapsLongTranslation` → 2 satırlık (en uzun 30 karakter) mesajın 130 karakterlik çevirisi ⇒ 401 komut sayısı ≥ 4, hiçbir satır 40 görünür karakteri aşmaz, hepsi aynı `indent`, satırlar birleştirilince sözcükler eksiksiz
  - `Install_PreservesEncodingWithoutBom` → BOM'suz girdi ⇒ çıktı `EF BB BF` ile başlamaz; `"ğüşıöçİ"` dosyada kaçışsız UTF-8
  - `Install_Mv_ReplacesFontCss`, `Install_Mz_SetsMainFontFilename` (test `FontSourcePath`'i geçici sahte dosyaya verir), `Install_SkipsFont_WhenSourceMissing`
  - `Uninstall_RestoresAllHashes`
- [ ] **Step 2–4:** başarısız gör, uygula, geçer. **Step 5:** Commit: `Patching: RPG Maker MV/MZ desteği`

### Task 7: PatchSession (uçtan uca akış)

**Files:** Create `PatchSession.cs`, `tests/.../PatchSessionTests.cs`

**Interfaces:**
- Consumes: `EngineDetector`, `IGamePatcher`, `BatchTranslator`, `BackupManager`.
- Produces:
```csharp
public sealed record PatchReport(int TotalUnits, int Translated, int Skipped, int Failed, IReadOnlyList<string> FailedSamples);
public sealed class PatchException(string message) : Exception(message);
public sealed class PatchSession
{
    public PatchSession(IEnumerable<IGamePatcher> patchers, Func<GameInfo, BatchTranslator> translatorFactory);
    public static IReadOnlyList<IGamePatcher> DefaultPatchers();           // RenPyPatcher, RpgMakerPatcher
    public Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct);
    public Task<PatchReport> InstallAsync(GameInfo game, IReadOnlyList<TextUnit> units,
        IReadOnlyDictionary<string, string> glossary, IProgress<BatchProgress>? progress, CancellationToken ct);
    public UninstallResult Uninstall(GameInfo game, bool force = false);
    public static string? PreflightError(GameInfo game);   // null = sorun yok
}
```
- `PreflightError` sırayla: `UnsupportedReason` → onu döndür; `AntiCheat` → `"Bu oyunda anti-cheat algılandı; yama kurulmaz."`; oyun klasöründen çalışan süreç varsa (`Process.MainModule.FileName` kök altında; erişim hataları yok sayılır) → `"Oyun çalışıyor. Kapatıp tekrar dene."`; köke deneme dosyası yazılamıyorsa → `"Oyun klasörüne yazılamıyor. Patcher'ı yönetici olarak çalıştır."`
- `InstallAsync`: `PreflightError` doluysa `PatchException`. Yama zaten kuruluysa önce `Uninstall(force:false)`; çakışma varsa `PatchException("Oyun dosyaları yamadan sonra değişmiş…")`. Çeviri → `patcher.InstallAsync` → `backup.Commit()`. Kurulum sırasında istisna/iptal olursa `backup.Uninstall(force:true)` ile geri alınır ve istisna yeniden fırlatılır. `FailedSamples` en fazla 20 kaynak metin.

- [ ] **Step 1: Testler** (sahte motorlu gerçek `BatchTranslator`, `TestGames` klasörleri):
  - `EndToEnd_RenPy_InstallThenUninstall_RestoresFolder`
  - `EndToEnd_RpgMaker_InstallThenUninstall_RestoresFolder`
  - `Install_Twice_DoesNotStackBackups` → ikinci kurulumdan sonra kaldırma yine orijinale döner
  - `Install_RollsBack_WhenPatcherThrows` → 2. dosyada istisna atan sahte `IGamePatcher` ⇒ klasör özetleri başlangıçla aynı, `.gamesubtr-patch` yok
  - `Preflight_BlocksAntiCheat_And_Unsupported`
  - `Report_CountsFailedAndSkipped`
- [ ] **Step 2–4:** başarısız gör, uygula, geçer. **Step 5:** Commit: `Patching: uçtan uca kurulum akışı`

### Task 8: SteamLibrary

**Files:** Create `SteamLibrary.cs`, `tests/.../SteamLibraryTests.cs`

**Interfaces — Produces:** `public sealed record InstalledGame(string Name, string Path);` `public static class SteamLibrary { public static IReadOnlyList<InstalledGame> Find(); public static IReadOnlyList<InstalledGame> FindIn(string steamRoot); }`

- `Find`: Steam kökü kayıt defterinden (`HKCU\Software\Valve\Steam` → `SteamPath`); yoksa boş liste. Kayıt defteri erişimi `OperatingSystem.IsWindows()` korumasıyla (`Microsoft.Win32.Registry` .NET 8 Windows'ta yerleşik).
- `FindIn`: `steamapps/libraryfolders.vdf` içindeki tüm `"path"` değerleri (`\\` → `\`) + kökün kendisi; her kütüphanede `steamapps/appmanifest_*.acf` → `"name"` ve `"installdir"` → `<kütüphane>/steamapps/common/<installdir>`; klasörü olmayanlar atlanır; ada göre sıralı. Bozuk dosyalar atlanır, istisna fırlatılmaz.

- [ ] **Step 1: Testler:** `FindIn_ReadsMultipleLibraries`, `FindIn_SkipsMissingInstallDir`, `FindIn_ReturnsEmpty_WhenNoSteamapps`, `FindIn_ToleratesGarbageFiles`
- [ ] **Step 2–4:** başarısız gör, uygula, geçer. **Step 5:** Commit: `Patching: Steam kütüphanesi taraması`

### Task 9: WPF uygulaması (GameSubTR.Patcher)

**Files:**
- Create: `src/GameSubTR.Patcher/GameSubTR.Patcher.csproj`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `TranslationFactory.cs`
- Modify: `GameSubTR.sln`

**Interfaces — Consumes:** `PatchSession`, `EngineDetector.Detect`, `SteamLibrary.Find`, `TranslationStore.PathFor`, `BackupManager.IsInstalled`; Core: `SettingsStore`, `AppSettings.Translation`, `GlossaryParser`, `Log.Init`, çevirmen sınıfları.

- csproj: `WinExe`, `net8.0-windows10.0.19041.0`, `UseWPF`, `AssemblyName=GameSubTR.Patcher`, `RuntimeIdentifier=win-x64`, referanslar → Core + Patching. WinForms kullanılmaz; klasör seçimi `Microsoft.Win32.OpenFolderDialog`.
- `TranslationFactory.Create(AppSettings s, HttpClient http)`: `src/GameSubTR.App/SubtitleController.cs:80-87`'deki çevirmen listesinin aynısı, ancak `cache: null`, `contextLines: () => 0`, `Timeout = 60 sn`.
- `App.xaml.cs`: `Log.Init(%LocalAppData%\GameSubTR\logs)`, ayarları `SettingsStore` ile yükler; yakalanmamış istisnalar loglanır ve `MessageBox` ile gösterilir.
- `MainWindow`: tek pencere (yaklaşık 720×520), kod-arkası; adımlar görünürlük değiştirerek ilerler:
  1. **Oyun seç:** "Klasör seç…" butonu, pencereye klasör sürükle-bırak, altında Steam oyun listesi (çift tıkla seç).
  2. **Algılama:** oyun adı, motor, `PreflightError` (varsa kırmızı ve "Devam" kapalı). Yama kuruluysa "Yamayı güncelle" + "Yamayı kaldır". Ren'Py/RPG Maker için "Türkçe karakterli font kullan" onay kutusu (varsayılan işaretli). "Metinleri çıkar" → birim sayısı, toplam karakter, depoda hazır çeviri sayısı ve kullanılacak motor (`ActiveChain()[0].DisplayName`) gösterilir; "Çeviriyi başlat".
  3. **Çeviri:** `ProgressBar` (`BatchProgress`), "x / y çevrildi, z hata", "İptal" (iptal sonrası 2. adıma döner; ilerleme depoda kalır).
  4. **Sonuç:** `PatchReport` özeti, hatalı örnekler listesi, "Log klasörünü aç", "Yamayı kaldır", "Başka oyun".
- Kaldırmada çakışma dönerse: dosya listesiyle "Yine de eski yedeği geri yükle?" sorusu → evet ise `force:true`.
- Tüm uzun işler `Task.Run` + `CancellationTokenSource`; UI iş parçacığı bloklanmaz. Hata mesajları `PatchException.Message` aynen gösterilir.
- Hiç yapılandırılmış motor yoksa (Google kapalı, anahtar yok) 2. adımda: "Çeviri motoru yapılandırılmamış. GameSubTR → Ayarlar → Çeviri."

- [ ] **Step 1:** Projeyi oluştur, çözüme ekle, `TranslationFactory` + `App` + `MainWindow` uygula.
- [ ] **Step 2:** `dotnet build src/GameSubTR.Patcher -c Release` → 0 hata, 0 uyarı (CA1416 hariç).
- [ ] **Step 3: Elle doğrulama** (sahte oyun klasörüyle — `TestGames` üreticilerini kullanan küçük bir konsol komutu yerine testlerdeki klasörü kopyala): exe'yi aç → klasörü seç → motor doğru görünür → çevir → dosyalar değişir → "Yamayı kaldır" → klasör eski halinde. Ekran görüntüsünü `docs/patcher.png` olarak kaydet.
- [ ] **Step 4:** Commit: `Patcher: WPF sihirbaz arayüzü`

### Task 10: Derleme, CI, README

**Files:** Modify `build.ps1`, `README.md`

- `build.ps1`: test adımına `tests\GameSubTR.Patching.Tests` eklenir; yayın adımında `src\GameSubTR.Patcher` aynı `dist\GameSubTR` klasörüne yayınlanır; zorunlu dosya listesine `GameSubTR.Patcher.exe` eklenir. Böylece mevcut CI ve Release iş akışları değişmeden iki exe'yi paketler.
- `README.md`: "Kalıcı Türkçe yama (GameSubTR.Patcher)" bölümü — desteklenen motorlar tablosu ve sınırlar (spec'ten), 4 adımlı kullanım, "Yamayı kaldır", yalnızca kendi oyunlarında ve tek oyunculu oyunlarda kullanım notu. Proje yapısı listesine iki yeni proje.

- [ ] **Step 1:** Değişiklikleri yap.
- [ ] **Step 2:** `.\build.ps1` → testler geçer, `dist\GameSubTR\GameSubTR.exe` ve `dist\GameSubTR\GameSubTR.Patcher.exe` oluşur.
- [ ] **Step 3:** Commit: `Patcher: derleme betiği ve README`

---

## Kullanıcının yapacağı son doğrulama

Bir gerçek Ren'Py ve bir gerçek RPG Maker MV/MZ oyunuyla: kur → oyunu aç (diyaloglar Türkçe, ğ/ş/ı düzgün) → kaldır → oyun eski halinde.
