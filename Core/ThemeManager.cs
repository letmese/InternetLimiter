using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Valve.Interop;

namespace Valve.Core;

public static class ThemeManager
{
    public static string CurrentTheme { get; private set; } = "Dark";

    public static event Action<string>? ThemeChanged;

    public static void ApplyTheme(string themeName, Window? targetWindow = null)
    {
        if (string.IsNullOrWhiteSpace(themeName))
        {
            themeName = "Dark";
        }

        string normalized = themeName.Trim().ToLowerInvariant() switch
        {
            "cyberpunk" => "Cyberpunk",
            "light" => "Light",
            _ => "Dark"
        };

        try
        {
            // Standard WPF Pack URI format: pack://application:,,,/AssemblyName;component/Path
            var dictUri = new Uri($"pack://application:,,,/Valve;component/Themes/{normalized}.xaml", UriKind.Absolute);
            var newDict = new ResourceDictionary { Source = dictUri };

            var merged = Application.Current.Resources.MergedDictionaries;
            int replaceIdx = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.Contains("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                    src.Contains("Cyberpunk.xaml", StringComparison.OrdinalIgnoreCase) ||
                    src.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    replaceIdx = i;
                    break;
                }
            }

            if (replaceIdx >= 0)
            {
                merged.RemoveAt(replaceIdx);
                merged.Insert(replaceIdx, newDict);
            }
            else
            {
                merged.Insert(0, newDict);
            }

            // Directly copy all theme brushes into Application.Current.Resources and all window resources
            // This guarantees instant dynamic resource invalidation on all active controls and templates
            foreach (var key in newDict.Keys)
            {
                Application.Current.Resources[key] = newDict[key];
            }

            if (targetWindow != null)
            {
                try
                {
                    foreach (var key in newDict.Keys)
                    {
                        targetWindow.Resources[key] = newDict[key];
                    }
                }
                catch { }
            }

            foreach (Window win in Application.Current.Windows)
            {
                try
                {
                    foreach (var key in newDict.Keys)
                    {
                        win.Resources[key] = newDict[key];
                    }
                }
                catch { }
            }

            CurrentTheme = normalized;

            // Apply DWM frame theme (dark mode / light mode) to all open windows
            bool isDark = normalized != "Light";
            foreach (Window win in Application.Current.Windows)
            {
                try
                {
                    var helper = new WindowInteropHelper(win);
                    IntPtr hwnd = helper.Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        Native.SetWindowFrameTheme(hwnd, isDark);
                    }
                }
                catch
                {
                    // Ignore window interop errors for unopened windows
                }
            }

            ThemeChanged?.Invoke(normalized);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to apply theme '{themeName}': {ex}");
        }
    }

    public static double CurrentFontScale { get; private set; } = 1.0;

    /// <summary>Applies dynamic font scale factor across application resource definitions (22.05).</summary>
    public static void ApplyFontScale(double scale)
    {
        if (scale < 0.5) scale = 0.5;
        if (scale > 2.5) scale = 2.5;
        CurrentFontScale = scale;

        if (Application.Current != null)
        {
            Application.Current.Resources["BaseFontSize"] = 12.0 * scale;
            Application.Current.Resources["SmallFontSize"] = 10.0 * scale;
            Application.Current.Resources["HeaderFontSize"] = 14.0 * scale;
            Application.Current.Resources["TitleFontSize"] = 16.0 * scale;

            foreach (Window win in Application.Current.Windows)
            {
                try
                {
                    win.FontSize = 12.0 * scale;
                }
                catch { }
            }
        }
    }
}
