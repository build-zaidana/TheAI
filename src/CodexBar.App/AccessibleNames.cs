using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RadioButton = System.Windows.Controls.RadioButton;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexBar.App;

/// <summary>
/// Gives each unnamed switch, drop-down, field and slider the label of its row, so a screen reader
/// says "Sound when done" and not just "check box".
/// </summary>
internal static class AccessibleNames
{
    public static void FromRows(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToArray())
        {
            if (child is ToggleButton and not RadioButton or Selector and not TabControl or TextBox or PasswordBox or Slider &&
                string.IsNullOrEmpty(AutomationProperties.GetName(child)) &&
                RowLabel(child) is { } label)
            {
                AutomationProperties.SetName(child, label);
            }

            FromRows(child);
        }
    }

    // The nearest label before the control, so a row of several fields names each one; up to the row's grid.
    private static string? RowLabel(DependencyObject control)
    {
        var current = control;
        for (var parent = LogicalTreeHelper.GetParent(current); parent is not null; parent = LogicalTreeHelper.GetParent(current))
        {
            var before = LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().TakeWhile(child => child != current).Reverse();
            foreach (var sibling in before)
            {
                if (sibling is TextBlock { Text.Length: > 0 } text)
                {
                    return text.Text;
                }

                if (sibling is not (ToggleButton or Selector or TextBox or PasswordBox or Slider) && FirstText(sibling) is { } found)
                {
                    return found;
                }
            }

            if (parent is Grid)
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static string? FirstText(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is TextBlock { Text.Length: > 0 } text)
            {
                return text.Text;
            }

            if (child is not (ToggleButton or Selector or TextBox or PasswordBox or Slider) && FirstText(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
