using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FastTranslation.App;

/// <summary>
/// ComboBox with template-driven popup: template uses NO Bindings (InvariantGlobalization
/// makes WPF binding culture resolution throw, which silently kills Popup.IsOpen bindings —
/// v15 root cause of the dead dropdown). PART_Toggle clicks toggle IsDropDownOpen in code;
/// the ControlTemplate opens PART_Popup via an IsDropDownOpen trigger.
/// </summary>
public class FtComboBox : ComboBox
{
    private ToggleButton? _toggle;

    public FtComboBox()
    {
        PreviewMouseLeftButtonDown += (_, _) => UiTestLog("combo PreviewMouseDown");
        MouseLeftButtonDown += (_, _) => UiTestLog("combo MouseDown");
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_toggle is not null) _toggle.Click -= OnToggleClick;
        _toggle = GetTemplateChild("PART_Toggle") as ToggleButton;
        UiTestLog($"OnApplyTemplate: toggle found={_toggle is not null}");
        if (_toggle is not null)
        {
            _toggle.Click += OnToggleClick;
            _toggle.PreviewMouseLeftButtonDown += (_, _) => UiTestLog("toggle PreviewMouseDown");
        }
    }

    private static void UiTestLog(string what)
    {
        if (Environment.GetEnvironmentVariable("FT_UITEST") is null) return;
        try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log", $"{DateTime.Now:HH:mm:ss.fff} {what}\n"); } catch { }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("FT_UITEST") is not null)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log", $"{DateTime.Now:HH:mm:ss.fff} OnToggleClick fired\n"); } catch { }
        }
        IsDropDownOpen = !IsDropDownOpen;
    }
}
