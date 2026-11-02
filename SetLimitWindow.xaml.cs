using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Valve.Core;

namespace Valve;

public partial class SetLimitWindow : Window
{
    private readonly ProcessRow _row;
    private readonly MainViewModel _vm;
    private bool _isDownload;
    private bool _coupleBoth;

    public SetLimitWindow(ProcessRow row, MainViewModel vm, bool isDownload = true, bool coupleBoth = false)
    {
        InitializeComponent();
        _row = row;
        _vm = vm;
        _isDownload = isDownload;
        _coupleBoth = coupleBoth;

        AppNameText.Text = string.IsNullOrEmpty(row.DisplayName) ? row.Name : row.DisplayName;
        PidText.Text = $"PID: {row.Pid}";
        AppPathText.Text = string.IsNullOrEmpty(row.Path) ? "No file path" : row.Path;
        if (row.Icon != null) AppIconImg.Source = row.Icon;

        if (_coupleBoth)
        {
            RadioBoth.IsChecked = true;
        }
        else if (!_isDownload)
        {
            RadioUpload.IsChecked = true;
        }
        else
        {
            RadioDownload.IsChecked = true;
        }

        PopulateInitialValue();
        ValueBox.Focus();
        ValueBox.SelectAll();
    }

    private void PopulateInitialValue()
    {
        int currentKbps = _isDownload ? _row.DownloadLimitKbps : _row.LimitKbps;
        if (_coupleBoth && currentKbps <= 0)
        {
            currentKbps = Math.Max(_row.DownloadLimitKbps, _row.LimitKbps);
        }

        if (currentKbps > 0)
        {
            long bytesPerSec = (long)currentKbps * 1024 / 8;
            var (val, unit) = UnitConverter.FormatOptimal(bytesPerSec, useBits: false);
            ValueBox.Text = val.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SelectUnit(unit);
            EnabledCheck.IsChecked = _isDownload ? _row.IsDownloadLimitEnabled : _row.IsUploadLimitEnabled;
            RemoveBtn.IsEnabled = true;
        }
        else
        {
            ValueBox.Text = "1";
            SelectUnit("MB/s");
            EnabledCheck.IsChecked = true;
            RemoveBtn.IsEnabled = false;
        }
    }

    private void SelectUnit(string unit)
    {
        foreach (ComboBoxItem item in UnitBox.Items)
        {
            if (string.Equals(item.Tag as string, unit, StringComparison.OrdinalIgnoreCase))
            {
                UnitBox.SelectedItem = item;
                break;
            }
        }
    }

    private void Direction_Changed(object sender, RoutedEventArgs e)
    {
        if (RadioBoth?.IsChecked == true)
        {
            _coupleBoth = true;
            _isDownload = true;
        }
        else if (RadioUpload?.IsChecked == true)
        {
            _coupleBoth = false;
            _isDownload = false;
        }
        else
        {
            _coupleBoth = false;
            _isDownload = true;
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var parts = tag.Split('|');
            if (parts.Length == 2)
            {
                ValueBox.Text = parts[0];
                SelectUnit(parts[1]);
                ValueBox.Focus();
                ValueBox.SelectAll();
            }
        }
    }

    private void ValueBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Apply_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ValueBox.Text.Trim(), out double val) || val < 0)
        {
            MessageBox.Show(this, "Please enter a valid positive number for the limit.", "Invalid Limit", MessageBoxButton.OK, MessageBoxImage.Warning);
            ValueBox.Focus();
            ValueBox.SelectAll();
            return;
        }

        string unit = (UnitBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "KB/s";
        int kbps = UnitConverter.ParseToKbps(val, unit);
        bool enabled = EnabledCheck.IsChecked == true;

        if (_coupleBoth)
        {
            await _vm.SetDownloadCapAsync(_row, enabled ? kbps : 0);
            await _vm.SetCapAsync(_row, enabled ? kbps : 0);
        }
        else if (_isDownload)
        {
            await _vm.SetDownloadCapAsync(_row, enabled ? kbps : 0);
        }
        else
        {
            await _vm.SetCapAsync(_row, enabled ? kbps : 0);
        }

        _vm.RefreshTree();
        DialogResult = true;
        Close();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_coupleBoth)
        {
            await _vm.SetDownloadCapAsync(_row, 0);
            await _vm.SetCapAsync(_row, 0);
        }
        else if (_isDownload)
        {
            await _vm.SetDownloadCapAsync(_row, 0);
        }
        else
        {
            await _vm.SetCapAsync(_row, 0);
        }

        _vm.RefreshTree();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
