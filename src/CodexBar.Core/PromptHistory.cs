namespace CodexBar.Core;

public sealed class PromptHistory(int capacity = 100)
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<string> _items = [];
    private int _index;
    private string _draft = string.Empty;

    public IReadOnlyList<string> Items => _items;

    public void Add(string prompt)
    {
        var value = prompt?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            ResetNavigation();
            return;
        }

        if (_items.Count == 0 || !string.Equals(_items[^1], value, StringComparison.Ordinal))
        {
            _items.Add(value);
            if (_items.Count > _capacity)
            {
                _items.RemoveAt(0);
            }
        }

        ResetNavigation();
    }

    public string Previous(string currentText)
    {
        if (_items.Count == 0)
        {
            return currentText;
        }

        if (_index == _items.Count)
        {
            _draft = currentText;
        }

        _index = Math.Max(0, _index - 1);
        return _items[_index];
    }

    public string Next()
    {
        if (_items.Count == 0 || _index >= _items.Count)
        {
            return _draft;
        }

        _index++;
        return _index == _items.Count ? _draft : _items[_index];
    }

    public void ResetNavigation()
    {
        _index = _items.Count;
        _draft = string.Empty;
    }
}
