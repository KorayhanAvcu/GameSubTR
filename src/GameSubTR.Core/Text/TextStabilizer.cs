namespace GameSubTR.Core.Text;

public enum StabilizerEventKind { None, NewText, Cleared }

public readonly record struct StabilizerEvent(StabilizerEventKind Kind, string Text);

/// <summary>
/// OCR sonuçlarını kararlı hale getirir:
/// - Harf harf beliren (typewriter) altyazılarda yarım cümleyi çevirmemek için metin
///   art arda <see cref="RequiredRepeats"/> kez (benzer) okunduğunda yayınlanır.
/// - Zaten yayınlanmış metin tekrar yayınlanmaz.
/// - Metin kaybolunca (art arda boş) Cleared yayınlanır.
/// </summary>
public sealed class TextStabilizer
{
    public int RequiredRepeats { get; init; } = 2;
    public int RequiredEmpty { get; init; } = 2;
    public double SameThreshold { get; init; } = 0.88;
    public int MinLetters { get; init; } = 2;

    private string _candidate = "";
    private int _candidateCount;
    private int _emptyCount;
    private string _published = "";

    public string Published => _published;

    public StabilizerEvent Push(string text)
    {
        text = text?.Trim() ?? "";
        if (TextUtil.LetterCount(text) < MinLetters)
        {
            _candidate = "";
            _candidateCount = 0;
            _emptyCount++;
            if (_published.Length > 0 && _emptyCount >= RequiredEmpty)
            {
                _published = "";
                return new(StabilizerEventKind.Cleared, "");
            }
            return default;
        }

        _emptyCount = 0;
        if (_candidate.Length > 0 && TextUtil.Similarity(_candidate, text) >= SameThreshold)
        {
            _candidateCount++;
            // Daha uzun okuma genelde daha eksiksizdir.
            if (text.Length >= _candidate.Length) _candidate = text;
        }
        else
        {
            _candidate = text;
            _candidateCount = 1;
        }

        if (_candidateCount >= RequiredRepeats &&
            (_published.Length == 0 || TextUtil.Similarity(_published, _candidate) < SameThreshold))
        {
            _published = _candidate;
            return new(StabilizerEventKind.NewText, _published);
        }
        return default;
    }

    public void Reset()
    {
        _candidate = ""; _candidateCount = 0; _emptyCount = 0; _published = "";
    }
}
