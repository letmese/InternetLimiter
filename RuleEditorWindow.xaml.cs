
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Valve.Core;

namespace Valve;

/// <summary>
/// Editable wrapper for one scheduler row. Keeps time-of-day and day-of-week
/// parsing out of the model and inside the editor where it belongs.
/// </summary>
public sealed class RuleScheduleRow : Observable
{
    private bool _enabled = true;
    private string _action = "Start";
    private string _time = "09:00";
    private string _daysText = "Every Day";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Action { get => _action; set => Set(ref _action, value); }
    public string Time { get => _time; set => Set(ref _time, value); }
    public string DaysText { get => _daysText; set => Set(ref _daysText, value); }
}

public partial class RuleEditorWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly UnifiedRule _rule;
    private readonly bool _isNew;
    private readonly ObservableCollection<RuleScheduleRow> _schedule = new();

    public RuleEditorWindow(MainViewModel vm, ProcessRow? process, UnifiedRule? existing = null)
    {
        InitializeComponent();
        _vm = vm;

        if (existing is not null)
        {
            _rule = existing;
            _isNew = false;
        }
        else if (process is not null)
        {
            _rule = new UnifiedRule
            {
                Name = $"{process.Name} rule",
                TargetType = RuleTargetType.Application,
                TargetValue = process.Name,
            };
            _isNew = true;
        }
        else
        {
            _rule = new UnifiedRule
            {
                Name = "Whole computer rule",
                TargetType = RuleTargetType.WholeComputer,
            };
            _isNew = true;
        }

        PopulateFields();
        ScheduleGrid.ItemsSource = _schedule;
        UpdatePanelVisibility();
        UpdateTargetControls();
    }

    private void PopulateFields()
    {
        NameBox.Text = _rule.Name;
        TargetValueBox.Text = _rule.TargetValue ?? string.Empty;
        TargetFilterBox.ItemsSource = _vm.FilterRules.Select(f => f.Name).ToList();

        SelectByTag(TargetTypeBox, _rule.TargetType.ToString());
        SelectByTag(ZoneBox, string.IsNullOrWhiteSpace(_rule.TargetZone) ? "All" : _rule.TargetZone);
        SelectByTag(KindBox, _rule.Type.ToString());
        SelectByTag(DirectionBox, _rule.Direction.ToString());

        LimitBox.Text = _rule.LimitKbps > 0 ? _rule.LimitKbps.ToString() : "500";
        SelectByTag(LimitTypeBox, _rule.LimitType.ToString());

        SelectByTag(BlockerActionBox, _rule.BlockerAction.ToString());
        WeightBox.Text = _rule.Weight.ToString();

        QuotaBox.Text = _rule.QuotaBytes > 0 ? (_rule.QuotaBytes / 1048576).ToString() : "1024";
        QuotaDaysBox.Text = _rule.QuotaPeriodDays.ToString();
        SelectByTag(QuotaExceedBox, _rule.QuotaExceededAction);

        SelectByTag(PriorityLevelBox, _rule.PriorityLevel.ToString());
        SelectByTag(IgnoreModeBox, _rule.IgnoreMode.ToString());

        HotKeyBox.Text = _rule.HotKey ?? string.Empty;
        EnabledBox.IsChecked = _rule.IsEnabled;

        foreach (RuleScheduleEntry entry in _rule.Schedules)
        {
            _schedule.Add(new RuleScheduleRow
            {
                Enabled = entry.Enabled,
                Action = entry.Action,
                Time = entry.TimeText,
                DaysText = DaysToText(entry.Days),
            });
        }
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    private static string? SelectedTag(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void TargetType_Changed(object sender, SelectionChangedEventArgs e) => UpdateTargetControls();

    private void UpdateTargetControls()
    {
        if (TargetFilterBox == null || TargetValueBox == null || TargetTypeBox == null) return;
        bool isFilter = SelectedTag(TargetTypeBox) == "Filter";
        TargetFilterBox.Visibility = isFilter ? Visibility.Visible : Visibility.Collapsed;
        TargetValueBox.Visibility = isFilter ? Visibility.Collapsed : Visibility.Visible;

        if (isFilter && TargetFilterBox.Items.Count > 0 && TargetFilterBox.SelectedIndex < 0)
        {
            TargetFilterBox.SelectedIndex = 0;
        }
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e) => UpdatePanelVisibility();

    private void UpdatePanelVisibility()
    {
        if (KindBox == null || LimitPanel == null || BlockerPanel == null || QuotaPanel == null || PriorityPanel == null || IgnorePanel == null || DirectionBox == null) return;
        string? kind = SelectedTag(KindBox);

        LimitPanel.Visibility = kind == "Limit" ? Visibility.Visible : Visibility.Collapsed;
        BlockerPanel.Visibility = kind == "Blocker" ? Visibility.Visible : Visibility.Collapsed;
        QuotaPanel.Visibility = kind == "Quota" ? Visibility.Visible : Visibility.Collapsed;
        PriorityPanel.Visibility = kind == "Priority" ? Visibility.Visible : Visibility.Collapsed;
        IgnorePanel.Visibility = kind == "Ignore" ? Visibility.Visible : Visibility.Collapsed;

        // Direction does not apply to Ignore or Priority rules.
        DirectionBox.IsEnabled = kind is "Limit" or "Blocker" or "Quota";
    }

    private void AddScheduleEntry_Click(object sender, RoutedEventArgs e)
        => _schedule.Add(new RuleScheduleRow());

    private void RemoveScheduleEntry_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleGrid.SelectedItem is RuleScheduleRow row)
        {
            _schedule.Remove(row);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Enter a rule name.", "Rule Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? targetType = SelectedTag(TargetTypeBox) ?? "Application";
        string targetValue = targetType == "Filter"
            ? (TargetFilterBox.SelectedItem?.ToString() ?? string.Empty)
            : TargetValueBox.Text.Trim();

        if (targetType != "WholeComputer" && targetValue.Length == 0)
        {
            MessageBox.Show(this, "Enter a target for this rule.", "Rule Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _rule.Name = name;
        _rule.TargetType = Enum.Parse<RuleTargetType>(targetType);
        _rule.TargetValue = targetValue;
        _rule.TargetZone = string.Equals(SelectedTag(ZoneBox), "All", StringComparison.OrdinalIgnoreCase) ? "" : (SelectedTag(ZoneBox) ?? "");

        _rule.Type = Enum.Parse<RuleType>(SelectedTag(KindBox) ?? "Limit");
        _rule.Direction = Enum.Parse<RuleDirection>(SelectedTag(DirectionBox) ?? "Both");
        _rule.IsEnabled = EnabledBox.IsChecked == true;

        _rule.LimitKbps = int.TryParse(LimitBox.Text, out int kbps) ? Math.Max(0, kbps) : 0;
        _rule.LimitType = Enum.Parse<LimitApplicationType>(SelectedTag(LimitTypeBox) ?? "PerApplication");

        _rule.BlockerAction = Enum.Parse<BlockerAction>(SelectedTag(BlockerActionBox) ?? "Deny");
        _rule.Weight = int.TryParse(WeightBox.Text, out int weight) ? Math.Clamp(weight, 1, 10) : 3;

        _rule.QuotaBytes = (long)(double.TryParse(QuotaBox.Text, out double mb) ? Math.Max(0, mb) : 0) * 1048576;
        _rule.QuotaPeriodDays = int.TryParse(QuotaDaysBox.Text, out int days) ? Math.Max(1, days) : 1;
        _rule.QuotaExceededAction = SelectedTag(QuotaExceedBox) ?? "Block";

        _rule.PriorityLevel = Enum.Parse<TrafficPriority>(SelectedTag(PriorityLevelBox) ?? "Normal");
        _rule.IgnoreMode = Enum.Parse<IgnoreType>(SelectedTag(IgnoreModeBox) ?? "IgnoreData");
        _rule.HotKey = HotKeyBox.Text.Trim();

        _rule.Schedules = ParseSchedule();

        if (_isNew)
        {
            _vm.AddUnifiedRule(_rule);
        }
        else
        {
            _vm.UpdateUnifiedRule(_rule);
        }

        DialogResult = true;
        Close();
    }

    private List<RuleScheduleEntry> ParseSchedule()
    {
        var result = new List<RuleScheduleEntry>();

        foreach (RuleScheduleRow row in _schedule)
        {
            string[] parts = (row.Time ?? "09:00").Split(':');
            int hour = parts.Length == 2 && int.TryParse(parts[0], out int h) ? Math.Clamp(h, 0, 23) : 9;
            int minute = parts.Length == 2 && int.TryParse(parts[1], out int m) ? Math.Clamp(m, 0, 59) : 0;

            string action = row.Action is "Start" or "Stop" or "Reset" ? row.Action : "Start";

            result.Add(new RuleScheduleEntry
            {
                Enabled = row.Enabled,
                Action = action,
                MinuteOfDay = (hour * 60) + minute,
                Days = TextToDays(row.DaysText),
            });
        }

        return result;
    }

    private static string DaysToText(bool[] days)
    {
        if (days.All(d => d))
        {
            return "Every Day";
        }

        if (days.Length == 7 && !days[0] && !days[6] && days.Skip(1).Take(5).All(d => d))
        {
            return "Weekdays";
        }

        string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        return string.Join(",", names.Where((_, i) => i < days.Length && days[i]));
    }

    private static bool[] TextToDays(string? text)
    {
        string value = (text ?? "Every Day").Trim();
        if (value.Length == 0 || value.Equals("Every Day", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Repeat(true, 7).ToArray();
        }

        var days = new bool[7];

        if (value.Equals("Weekdays", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i <= 5; i++)
            {
                days[i] = true;
            }

            return days;
        }

        string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int index = Array.FindIndex(names, n => n.Equals(token.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                days[index] = true;
            }
        }

        return days.Any(d => d) ? days : Enumerable.Repeat(true, 7).ToArray();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
