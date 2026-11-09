# GameSubTR — Tasarım Özeti

**Amaç:** Tek oyunculu, hikâye odaklı oyunlarda İngilizce altyazı/konuşmayı oyun sırasında Türkçeye çevirmek. Her oyunda çalışmalı, tam ekranda görünmeli, en iyi kalite/gecikme hedeflenir. Hedef donanım: Windows 10/11 + RTX 3060.

## Bileşenler

```
ForegroundWatcher ──► SubtitleController ──► ISubtitlePresenter
   (oyun algılama)        │    ▲                ├─ WindowPresenter (WPF overlay, WDA_EXCLUDEFROMCAPTURE)
                          │    │                └─ HookSession (paylaşımlı bellek → hook DLL → oyun ekranı)
                          ▼    │
        ┌── OcrPipeline ──┤    └── TranslationService (Claude → OpenAI-uyumlu → DeepL → Google, önbellek, bağlam)
        │  IFrameSource   │
        │  ├─ DesktopFrameSource (DXGI Desktop Duplication)
        │  └─ HookSession (arka tampondan bölge okuma)
        └── AudioPipeline: ProcessLoopbackCapture → StreamingResampler → SileroVad/SpeechSegmenter → Whisper
```

## Kararlar

| Konu | Karar | Gerekçe |
|---|---|---|
| Dil | C# .NET 8 (WPF) + C++ hook | Düşük gecikme, tek paket, yerel Windows API'leri |
| OCR | Windows.Media.Ocr + ön işleme | Hızlı (~20–50 ms), ücretsiz, çevrimdışı |
| Kararlılık | 2 ardışık benzer okuma → yayınla | Typewriter efektinde yarım cümleyi çevirmemek |
| Ses | Process loopback | Sadece oyun sesi; Discord/müzik karışmaz |
| ASR | Whisper large-v3-turbo q5_0, Vulkan | 3060'ta ~1.5 GB VRAM, iyi doğruluk, CUDA kurulumu gerektirmez |
| Çeviri | LLM öncelikli zincir | Bağlam + isim koruma; hata/zaman aşımında otomatik yedek |
| Overlay | Gizlenebilir şeffaf pencere | Gösterilecek bir şey yokken gizlenir → oyunun independent flip'i bozulmaz |
| Exclusive fullscreen | DX9/DX11 Present hook | DX12/Vulkan'da gerçek exclusive yok; pencere yeterli |
| Paylaşımlı bellek | `Local\GameSubTR_{pid}`, sabit düzen | `SharedLayout.cs` ↔ `shared.h` (static_assert ile korunur) |

## Hata yönetimi
- Çeviri motoru hatası → 60 sn devre dışı, sıradaki motor.
- Hook kurulamazsa/heartbeat yoksa → pencere moduna düş.
- Ekran yakalama hatası → 1 sn bekle, yeniden başlat.
- Ses yakalama/Whisper hatası → durum mesajı + log, OCR etkilenmez.
