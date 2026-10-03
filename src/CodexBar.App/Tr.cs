using System.Windows.Markup;
using CodexBar.Core;

namespace CodexBar.App;

/// <summary>
/// Inline bilingual text for XAML: <c>{local:Tr Bahasa, En=Language}</c>. Resolved when the
/// window loads, which is enough for windows that are created fresh each time they open.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class Tr : MarkupExtension
{
    public Tr()
    {
    }

    public Tr(string id) => Id = id;

    public string Id { get; set; } = string.Empty;

    public string En { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Text(Id, En);

    public static string Text(string id, string en) =>
        CodexBar.Core.Localization.Instance.Language == AppLanguage.En && en.Length > 0 ? en : id;
}
