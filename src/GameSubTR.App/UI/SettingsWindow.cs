using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameSubTR.Core.Models;
using GameSubTR.Core.Translation;

namespace GameSubTR.App.UI;

/// <summary>Ayarlar penceresi (kod ile oluşturulmuş WPF).</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly Action<string> _clearCache;
    private readonly Func<AppSettings, string, Task<string>> _testTranslate;
    public AppSettings? Saved { get; private set; }

    // Çeviri
    private readonly TextBox _claudeKey = new(), _claudeModel = new(), _oaUrl = new(), _oaKey = new(), _oaModel = new(), _deepl = new(), _order = new(), _ctx = new();
    private readonly CheckBox _google = new() { Content = "Anahtarsız Google'ı son yedek olarak kullan" };
    private readonly TextBox _testInput = new() { Text = "We have to get out of here before they find us." };
    private readonly TextBlock _testOutput = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    // Ses
    private readonly ComboBox _whisper = new() { IsEditable = true };
    private readonly TextBox _vad = new(), _silence = new();
    private readonly CheckBox _gameOnly = new() { Content = "Sadece oyunun sesini dinle (Discord / müzik karışmaz)" };
    // Kısayollar
    private readonly TextBox _hkToggle = new(), _hkRegion = new(), _hkMode = new(), _hkOrig = new();
    // Oyunlar
    private readonly ListBox _games = new() { MinWidth = 180 };
    private readonly ComboBox _mode = new(), _presenter = new(), _placement = new(), _filter = new();
    private readonly TextBox _font = new(), _color = new(), _bg = new(), _fixedBottom = new(), _interval = new(), _glossary = new()
    { AcceptsReturn = true, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
    private readonly CheckBox _showOrig = new() { Content = "Çevirinin altında orijinal metni de göster" };
    private readonly StackPanel _gameEditor = new();
    private GameProfile? _editing;

    public SettingsWindow(AppSettings settings, Action<string> clearCache, Func<AppSettings, string, Task<string>> testTranslate)
    {
        _s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!; // derin kopya
        _s.Profiles = new Dictionary<string, GameProfile>(_s.Profiles, StringComparer.OrdinalIgnoreCase);
        _clearCache = clearCache;
        _testTranslate = testTranslate;

        Title = "GameSubTR — Ayarlar";
        Width = 760; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var tabs = new TabControl { Margin = new Thickness(10) };
        tabs.Items.Add(new TabItem { Header = "Çeviri", Content = Scroll(BuildTranslationTab()) });
        tabs.Items.Add(new TabItem { Header = "Oyunlar", Content = BuildGamesTab() });
        tabs.Items.Add(new TabItem { Header = "Ses (Whisper)", Content = Scroll(BuildAudioTab()) });
        tabs.Items.Add(new TabItem { Header = "Kısayollar", Content = Scroll(BuildHotkeysTab()) });

        var save = new Button { Content = "Kaydet", Width = 110, Margin = new Thickness(6), IsDefault = true, Padding = new Thickness(6, 3, 6, 3) };
        var cancel = new Button { Content = "İptal", Width = 110, Margin = new Thickness(6), IsCancel = true, Padding = new Thickness(6, 3, 6, 3) };
        save.Click += (_, _) => { if (Collect()) { Saved = _s; DialogResult = true; } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 10, 10) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);

        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(tabs);
        Content = dock;

        Load();
    }

    private static ScrollViewer Scroll(UIElement e) => new() { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };

    private static TextBlock Note(string t) => new() { Text = t, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };

    private static TextBlock Header(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 6) };

    private static FrameworkElement Row(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        g.Children.Add(l); g.Children.Add(control);
        return g;
    }

    private UIElement BuildTranslationTab()
    {
        var p = new StackPanel();
        p.Children.Add(Note("Motorlar sırayla denenir; biri hata verirse otomatik olarak sonrakine geçilir. En iyi kalite: Claude (karakter isimlerini ve konuşma bağlamını korur). Hiç anahtar girmezseniz ücretsiz Google kullanılır."));
        p.Children.Add(Header("Claude (önerilen)"));
        p.Children.Add(Row("API anahtarı", _claudeKey));
        p.Children.Add(Row("Model", _claudeModel));
        p.Children.Add(Header("OpenAI uyumlu LLM (OpenAI, OpenRouter, Ollama, LM Studio)"));
        p.Children.Add(Note("Offline için: Ollama kurup ör. 'ollama pull qwen2.5:7b' → Adres: http://localhost:11434/v1, Model: qwen2.5:7b (ekran kartını oyunla paylaşır)."));
        p.Children.Add(Row("Adres (base URL)", _oaUrl));
        p.Children.Add(Row("API anahtarı", _oaKey));
        p.Children.Add(Row("Model", _oaModel));
        p.Children.Add(Header("DeepL"));
        p.Children.Add(Row("API anahtarı", _deepl));
        p.Children.Add(Header("Genel"));
        p.Children.Add(_google);
        p.Children.Add(Row("Motor sırası", _order));
        p.Children.Add(Note("Virgülle: claude, openai, deepl, google"));
        p.Children.Add(Row("Bağlam (önceki replik)", _ctx));

        p.Children.Add(Header("Test"));
        var testBtn = new Button { Content = "Çeviriyi dene", Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        testBtn.Click += async (_, _) =>
        {
            if (!Collect()) return;
            testBtn.IsEnabled = false; _testOutput.Text = "Çevriliyor...";
            try { _testOutput.Text = await _testTranslate(_s, _testInput.Text); }
            catch (Exception ex) { _testOutput.Text = "Hata: " + ex.Message; }
            finally { testBtn.IsEnabled = true; }
        };
        p.Children.Add(_testInput);
        p.Children.Add(testBtn);
        p.Children.Add(_testOutput);
        return p;
    }

    private UIElement BuildAudioTab()
    {
        var p = new StackPanel();
        p.Children.Add(Note("Altyazısı olmayan oyunlarda oyunun sesi yerel Whisper modeliyle yazıya dökülür. Model ilk kullanımda indirilir. RTX 3060 için önerilen: large-v3-turbo-q5_0 (~550 MB, ~1.5 GB VRAM)."));
        foreach (var m in new[] { "ggml-large-v3-turbo-q5_0.bin", "ggml-large-v3-turbo.bin", "ggml-medium.en-q5_0.bin", "ggml-small.en.bin", "ggml-base.en.bin" })
            _whisper.Items.Add(m);
        p.Children.Add(Row("Whisper modeli", _whisper));
        p.Children.Add(Row("Konuşma eşiği (0.1–0.9)", _vad));
        p.Children.Add(Note("Müzik/efekt konuşma sanılıyorsa yükseltin (ör. 0.6); konuşma kaçıyorsa düşürün (ör. 0.4)."));
        p.Children.Add(Row("Cümle sonu sessizliği (ms)", _silence));
        p.Children.Add(_gameOnly);
        return p;
    }

    private UIElement BuildHotkeysTab()
    {
        var p = new StackPanel();
        p.Children.Add(Note("Biçim: Ctrl+Shift+T, Alt+F9 gibi. Tam ekran oyunlarda da çalışır."));
        p.Children.Add(Row("Çeviriyi aç/kapat", _hkToggle));
        p.Children.Add(Row("Altyazı bölgesi seç", _hkRegion));
        p.Children.Add(Row("Mod değiştir (OCR/Ses/İkisi)", _hkMode));
        p.Children.Add(Row("Orijinal metni göster/gizle", _hkOrig));
        return p;
    }

    private UIElement BuildGamesTab()
    {
        foreach (var v in Enum.GetValues<SourceMode>()) _mode.Items.Add(v);
        foreach (var v in Enum.GetValues<PresenterMode>()) _presenter.Items.Add(v);
        foreach (var v in Enum.GetValues<OverlayPlacement>()) _placement.Items.Add(v);
        foreach (var v in Enum.GetValues<OcrColorFilter>()) _filter.Items.Add(v);

        _gameEditor.Children.Add(Row("Kaynak", _mode));
        _gameEditor.Children.Add(Note("Ocr: ekrandaki altyazı · Audio: oyunun sesi · Both: ikisi (altyazı varsa o öncelikli)"));
        _gameEditor.Children.Add(Row("Gösterim", _presenter));
        _gameEditor.Children.Add(Note("Auto: exclusive fullscreen'de oyun içi hook, diğerlerinde pencere · Hook: sadece tek oyunculu oyunlarda"));
        _gameEditor.Children.Add(Row("Konum", _placement));
        _gameEditor.Children.Add(Row("Sabit konum alt sınırı (0–1)", _fixedBottom));
        _gameEditor.Children.Add(Row("Yazı boyutu (1080p px)", _font));
        _gameEditor.Children.Add(Row("Yazı rengi", _color));
        _gameEditor.Children.Add(Row("Arka plan opaklığı (0–1)", _bg));
        _gameEditor.Children.Add(Row("Altyazı rengi filtresi", _filter));
        _gameEditor.Children.Add(Row("OCR aralığı (ms)", _interval));
        _gameEditor.Children.Add(_showOrig);
        _gameEditor.Children.Add(Header("Sözlük (çevrilmeyecek isimler)"));
        _gameEditor.Children.Add(Note("Her satıra bir terim. 'Kaynak = Hedef' ile sabit çeviri verilebilir. Ör: Geralt  /  Wild Hunt = Yabani Av"));
        _gameEditor.Children.Add(_glossary);

        var del = new Button { Content = "Profili sil", Margin = new Thickness(0, 10, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        var clear = new Button { Content = "Çeviri önbelleğini temizle", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        del.Click += (_, _) =>
        {
            if (_editing == null) return;
            _s.Profiles.Remove(_editing.ExeName);
            _editing = null;
            RefreshGames();
        };
        clear.Click += (_, _) => { if (_editing != null) { _clearCache(_editing.ExeName); MessageBox.Show(this, "Önbellek temizlendi."); } };
        var bp = new StackPanel { Orientation = Orientation.Horizontal };
        bp.Children.Add(del); bp.Children.Add(clear);
        _gameEditor.Children.Add(bp);

        _games.SelectionChanged += (_, _) =>
        {
            SaveEditing();
            _editing = _games.SelectedItem is ListBoxItem { Tag: GameProfile gp } ? gp : null;
            LoadEditing();
        };

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var lt = new TextBlock { Text = "Oyunlar", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(lt, Dock.Top);
        left.Children.Add(lt);
        left.Children.Add(_games);
        var right = new ScrollViewer { Content = _gameEditor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left); grid.Children.Add(right);
        return grid;
    }

    private void RefreshGames()
    {
        _games.Items.Clear();
        foreach (var p in _s.Profiles.Values.OrderBy(p => p.DisplayName))
            _games.Items.Add(new ListBoxItem { Content = $"{p.DisplayName}  ({p.ExeName})", Tag = p });
        if (_games.Items.Count > 0) _games.SelectedIndex = 0;
        else { _editing = null; LoadEditing(); }
    }

    private static string D(double v) => v.ToString(CultureInfo.InvariantCulture);

    private void LoadEditing()
    {
        _gameEditor.IsEnabled = _editing != null;
        if (_editing == null) return;
        var p = _editing;
        _mode.SelectedItem = p.Mode; _presenter.SelectedItem = p.Presenter; _placement.SelectedItem = p.Placement; _filter.SelectedItem = p.ColorFilter;
        _font.Text = D(p.FontSize); _color.Text = p.TextColor; _bg.Text = D(p.BackgroundOpacity);
        _fixedBottom.Text = D(p.FixedBottom); _interval.Text = p.OcrIntervalMs.ToString(); _showOrig.IsChecked = p.ShowOriginal;
        _glossary.Text = GlossaryParser.Format(p.Glossary);
    }

    private bool SaveEditing()
    {
        if (_editing == null) return true;
        var p = _editing;
        if (_mode.SelectedItem is SourceMode m) p.Mode = m;
        if (_presenter.SelectedItem is PresenterMode pm) p.Presenter = pm;
        if (_placement.SelectedItem is OverlayPlacement pl) p.Placement = pl;
        if (_filter.SelectedItem is OcrColorFilter f) p.ColorFilter = f;
        if (!TryD(_font, 8, 200, out var fs) || !TryD(_bg, 0, 1, out var bg) || !TryD(_fixedBottom, 0.2, 1, out var fb) ||
            !int.TryParse(_interval.Text, out var iv) || iv < 50 || iv > 5000)
        {
            MessageBox.Show(this, "Oyun ayarlarında geçersiz sayı var.", "GameSubTR");
            return false;
        }
        p.FontSize = fs; p.BackgroundOpacity = bg; p.FixedBottom = fb; p.OcrIntervalMs = iv;
        p.TextColor = string.IsNullOrWhiteSpace(_color.Text) ? "#FFFFFF" : _color.Text.Trim();
        p.ShowOriginal = _showOrig.IsChecked == true;
        p.Glossary = GlossaryParser.Parse(_glossary.Text);
        return true;
    }

    private static bool TryD(TextBox t, double min, double max, out double v) =>
        double.TryParse(t.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    private void Load()
    {
        var t = _s.Translation;
        _claudeKey.Text = t.AnthropicApiKey ?? ""; _claudeModel.Text = t.AnthropicModel;
        _oaUrl.Text = t.OpenAiBaseUrl ?? ""; _oaKey.Text = t.OpenAiApiKey ?? ""; _oaModel.Text = t.OpenAiModel ?? "";
        _deepl.Text = t.DeepLApiKey ?? ""; _google.IsChecked = t.UseGoogleFree;
        _order.Text = string.Join(", ", t.Order); _ctx.Text = t.ContextLines.ToString();
        var a = _s.Audio;
        _whisper.Text = a.WhisperModel; _vad.Text = D(a.VadThreshold); _silence.Text = a.MinSilenceMs.ToString(); _gameOnly.IsChecked = a.CaptureGameOnly;
        var h = _s.Hotkeys;
        _hkToggle.Text = h.Toggle; _hkRegion.Text = h.SelectRegion; _hkMode.Text = h.CycleMode; _hkOrig.Text = h.ToggleOriginal;
        RefreshGames();
    }

    private bool Collect()
    {
        if (!SaveEditing()) return false;
        var t = _s.Translation;
        t.AnthropicApiKey = Null(_claudeKey.Text); t.AnthropicModel = Null(_claudeModel.Text) ?? "claude-haiku-5-5";
        t.OpenAiBaseUrl = Null(_oaUrl.Text); t.OpenAiApiKey = Null(_oaKey.Text); t.OpenAiModel = Null(_oaModel.Text);
        t.DeepLApiKey = Null(_deepl.Text); t.UseGoogleFree = _google.IsChecked == true;
        t.Order = _order.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList();
        if (int.TryParse(_ctx.Text, out var c)) t.ContextLines = Math.Clamp(c, 0, 30);
        var a = _s.Audio;
        a.WhisperModel = Null(_whisper.Text) ?? "ggml-large-v3-turbo-q5_0.bin";
        if (TryD(_vad, 0.1, 0.95, out var vad)) a.VadThreshold = (float)vad;
        if (int.TryParse(_silence.Text, out var sil)) a.MinSilenceMs = Math.Clamp(sil, 150, 3000);
        a.CaptureGameOnly = _gameOnly.IsChecked == true;
        var h = _s.Hotkeys;
        h.Toggle = _hkToggle.Text.Trim(); h.SelectRegion = _hkRegion.Text.Trim(); h.CycleMode = _hkMode.Text.Trim(); h.ToggleOriginal = _hkOrig.Text.Trim();
        return true;
    }

    private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
