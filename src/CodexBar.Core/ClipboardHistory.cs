using System.Security.Cryptography;
using System.Text.Json;

namespace CodexBar.Core;

/// <summary>One thing that was copied: text, or an image kept as PNG bytes.</summary>
public sealed class ClipItem
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public byte[]? Png { get; init; }
    public DateTimeOffset At { get; set; }
    public bool Pinned { get; set; }

    /// <summary>What makes two copies "the same" (moved to the top instead of kept twice).</summary>
    public required string Key { get; init; }

    public bool IsImage => Png is not null;
}

/// <summary>
/// What was copied lately, newest first: <see cref="MaxItems"/> at most, of them
/// <see cref="MaxImages"/> images (they are big). Copying the same thing again moves it to the
/// top. Pinned texts stay (and are the only thing written to disk); everything else lives in
/// memory and is gone when TheAI quits.
/// </summary>
public sealed class ClipboardHistory
{
    public const int MaxItems = 30;
    public const int MaxImages = 5;
    public const int MaxText = 20_000;

    private readonly List<ClipItem> _items = [];

    public IReadOnlyList<ClipItem> Items => _items;

    public ClipItem? AddText(string text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length > MaxText)
        {
            text = text[..MaxText];
        }

        return Add(new ClipItem { Id = NewId(), Text = text, At = now, Key = "t:" + text });
    }

    public ClipItem? AddImage(byte[] png, DateTimeOffset now) =>
        png.Length == 0 ? null : Add(new ClipItem { Id = NewId(), Png = png, At = now, Key = "i:" + Convert.ToHexString(SHA256.HashData(png)) });

    private ClipItem Add(ClipItem item)
    {
        var existing = _items.FirstOrDefault(other => other.Key == item.Key);
        if (existing is not null)
        {
            _items.Remove(existing);
            existing.At = item.At;
            _items.Insert(0, existing);
            return existing;
        }

        _items.Insert(0, item);
        Trim();
        return item;
    }

    public void TogglePin(string id)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is { } item)
        {
            item.Pinned = !item.Pinned;
        }
    }

    public void Remove(string id) => _items.RemoveAll(item => item.Id == id);

    /// <summary>Everything that isn't pinned goes.</summary>
    public void Clear() => _items.RemoveAll(item => !item.Pinned);

    // The oldest unpinned ones go first; pins are never pushed out.
    private void Trim()
    {
        while (_items.Count(item => item.IsImage && !item.Pinned) > MaxImages)
        {
            _items.Remove(_items.Last(item => item.IsImage && !item.Pinned));
        }

        while (_items.Count > MaxItems && _items.LastOrDefault(item => !item.Pinned) is { } oldest)
        {
            _items.Remove(oldest);
        }
    }

    // ---- Pins on disk ----------------------------------------------------------------------

    private sealed record PinnedText(string Text, DateTimeOffset At);

    public void LoadPins(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<List<PinnedText>>(File.ReadAllText(path)) is not { } pins)
            {
                return;
            }

            foreach (var pin in pins.AsEnumerable().Reverse())
            {
                if (AddText(pin.Text, pin.At) is { } item)
                {
                    item.Pinned = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Clipboard", exception.Message);
        }
    }

    public void SavePins(string path)
    {
        try
        {
            var pins = _items.Where(item => item.Pinned && item.Text is not null).Select(item => new PinnedText(item.Text!, item.At)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(pins));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Clipboard", exception.Message);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
