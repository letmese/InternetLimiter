using System;
using System.Windows;
using Valve.Core;

namespace Valve;

/// <summary>
/// NetLimiter 4 compatible Options / Column Customization dialog (Item 4.01).
/// </summary>
public partial class OptionsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action? _onApply;

    public OptionsWindow(Settings settings, Action? onApply = null)
    {
        InitializeComponent();
        _settings = settings;
        _onApply = onApply;

        LoadSettings();
    }

    private void LoadSettings()
    {
        ChkAutoRefresh.IsChecked = _settings.AutoRefreshEnabled;
        ChkColName.IsChecked = _settings.ShowColName;
        ChkColDlRate.IsChecked = _settings.ShowColDlRate;
        ChkColUlRate.IsChecked = _settings.ShowColUlRate;
        ChkColDlLimit.IsChecked = _settings.ShowColDlLimit;
        ChkColUlLimit.IsChecked = _settings.ShowColUlLimit;
        ChkColDlTotal.IsChecked = _settings.ShowColDlTotal;
        ChkColUlTotal.IsChecked = _settings.ShowColUlTotal;
        ChkColInFw.IsChecked = _settings.ShowColInFw;
        ChkColOutFw.IsChecked = _settings.ShowColOutFw;
        ChkColStatus.IsChecked = _settings.ShowColStatus;
    }

    private void CommitSettings()
    {
        _settings.AutoRefreshEnabled = ChkAutoRefresh.IsChecked == true;
        _settings.ShowColName = ChkColName.IsChecked == true;
        _settings.ShowColDlRate = ChkColDlRate.IsChecked == true;
        _settings.ShowColUlRate = ChkColUlRate.IsChecked == true;
        _settings.ShowColDlLimit = ChkColDlLimit.IsChecked == true;
        _settings.ShowColUlLimit = ChkColUlLimit.IsChecked == true;
        _settings.ShowColDlTotal = ChkColDlTotal.IsChecked == true;
        _settings.ShowColUlTotal = ChkColUlTotal.IsChecked == true;
        _settings.ShowColInFw = ChkColInFw.IsChecked == true;
        _settings.ShowColOutFw = ChkColOutFw.IsChecked == true;
        _settings.ShowColStatus = ChkColStatus.IsChecked == true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitSettings();
        _onApply?.Invoke();
        DialogResult = true;
        Close();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        CommitSettings();
        _onApply?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
