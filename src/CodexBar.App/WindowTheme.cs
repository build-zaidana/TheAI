using System.Linq;
using System.Windows;

namespace CodexBar.App;

/// <summary>
/// Light or dark for the Settings, Welcome and setup windows: swaps the colour dictionary they read
/// through DynamicResource, so open windows follow too. The island stays black either way.
/// </summary>
internal static class WindowTheme
{
    public static void Apply(bool dark)
    {
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var wanted = dark ? "FluentDark.xaml" : "FluentLight.xaml";
        var current = dictionaries.FirstOrDefault(d => IsColours(d, "FluentLight.xaml") || IsColours(d, "FluentDark.xaml"));
        if (current is not null && IsColours(current, wanted))
        {
            return;
        }

        if (current is not null)
        {
            dictionaries.Remove(current);
        }

        dictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/" + wanted) });
    }

    private static bool IsColours(ResourceDictionary dictionary, string file) =>
        dictionary.Source?.OriginalString.EndsWith(file, StringComparison.OrdinalIgnoreCase) == true;
}
