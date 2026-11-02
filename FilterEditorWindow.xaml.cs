using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Valve.Core;

namespace Valve;

/// <summary>
/// Editor row backing a single FilterFunction (criterion + operator + value).
/// </summary>
public sealed class FunctionRow
{
    public string Criterion { get; set; } = nameof(FilterFunctionType.ApplicationPathContains);
    public string Operator { get; set; } = nameof(FilterOperator.Contains);
    public string Value { get; set; } = string.Empty;
}

public partial class FilterEditorWindow : Window
{
    private readonly FilterRule _filter;
    private readonly bool _isNew;
    private readonly IReadOnlyList<FilterRule> _allFilters;
    private readonly List<FunctionRow> _rows = new();

    /// <summary>
    /// Filter Editor with full condition builder (Item 5.02 / 5.03 / 5.04).
    /// </summary>
    /// <param name="existing">Optional filter being edited.</param>
    /// <param name="allFilters">All filters (used for the Composite child picker).</param>
    public FilterEditorWindow(FilterRule? existing = null, IReadOnlyList<FilterRule>? allFilters = null)
    {
        InitializeComponent();
        _allFilters = allFilters ?? new List<FilterRule>();

        foreach (ComboBoxItem item in TypeBox.Items)
        {
            if (item.Tag?.ToString() == "Normal")
            {
                item.IsSelected = true;
            }
        }

        if (existing is not null)
        {
            _filter = existing;
            _isNew = false;
            NameBox.Text = existing.Name;
            SelectType(existing.FilterType.ToString());

            if (existing.Functions.Count > 0)
            {
                foreach (var func in existing.Functions)
                {
                    _rows.Add(new FunctionRow
                    {
                        Criterion = ((FilterFunctionType)func.Criterion).ToString(),
                        Operator = func.Operator.ToString(),
                        Value = func.Value,
                    });
                }
            }
            else
            {
                // Legacy single-condition filter -> one function row.
                _rows.Add(LegacyRow(existing.ConditionType, existing.Value));
            }

            foreach (FilterRule child in _allFilters.Where(f => f != existing))
            {
                var check = new CheckBox
                {
                    Content = child.Name,
                    Tag = child.Id,
                    IsChecked = existing.CompositeChildIds.Contains(child.Id),
                    Foreground = (System.Windows.Media.Brush)FindResource("Text"),
                };
                ChildFilterList.Items.Add(check);
            }
        }
        else
        {
            _filter = new FilterRule();
            _isNew = true;
            NameBox.Text = "Custom filter";
            _rows.Add(new FunctionRow { Criterion = nameof(FilterFunctionType.ApplicationPathContains), Operator = nameof(FilterOperator.Contains), Value = "firefox" });
        }

        RefreshFunctionRows();
        UpdateCompositeVisibility();
    }

    public FilterRule Result => _filter;
    public bool IsNew => _isNew;

    private static FunctionRow LegacyRow(FilterConditionType type, string value)
    {
        return new FunctionRow
        {
            Criterion = type switch
            {
                FilterConditionType.Application => nameof(FilterFunctionType.Application),
                FilterConditionType.ApplicationPath => nameof(FilterFunctionType.ApplicationPath),
                FilterConditionType.ApplicationPathContains => nameof(FilterFunctionType.ApplicationPathContains),
                FilterConditionType.Loopback => nameof(FilterFunctionType.Loopback),
                FilterConditionType.LocalAddress => nameof(FilterFunctionType.LocalAddress),
                FilterConditionType.RemoteAddress or FilterConditionType.IpRange => nameof(FilterFunctionType.RemoteAddress),
                FilterConditionType.LocalPort => nameof(FilterFunctionType.LocalPort),
                FilterConditionType.RemotePort or FilterConditionType.PortRange => nameof(FilterFunctionType.RemotePort),
                FilterConditionType.Network => nameof(FilterFunctionType.Network),
                FilterConditionType.RemoteHostInternet => nameof(FilterFunctionType.RemoteHostInternet),
                FilterConditionType.RemoteHostLocal or FilterConditionType.Zone => nameof(FilterFunctionType.RemoteHostLocal),
                FilterConditionType.Protocol => nameof(FilterFunctionType.Protocol),
                FilterConditionType.User => nameof(FilterFunctionType.User),
                FilterConditionType.Tag => nameof(FilterFunctionType.Tag),
                FilterConditionType.Forwarded => nameof(FilterFunctionType.Forwarded),
                FilterConditionType.DomainName => nameof(FilterFunctionType.DomainName),
                _ => nameof(FilterFunctionType.ApplicationPathContains),
            },
            Operator = type is FilterConditionType.ApplicationPath or FilterConditionType.ApplicationPathContains
                ? nameof(FilterOperator.Contains)
                : type is FilterConditionType.RemoteAddress or FilterConditionType.IpRange
                    or FilterConditionType.RemotePort or FilterConditionType.PortRange
                    ? nameof(FilterOperator.InRange)
                    : nameof(FilterOperator.Is),
            Value = value,
        };
    }

    private void SelectType(string typeName)
    {
        foreach (ComboBoxItem item in TypeBox.Items)
        {
            if (item.Tag?.ToString() == typeName)
            {
                item.IsSelected = true;
                break;
            }
        }
    }

    private void RefreshFunctionRows()
    {
        FunctionsList.ItemsSource = null;
        FunctionsList.Items.Clear();
        foreach (var row in _rows)
        {
            var container = new Border
            {
                BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Background = (System.Windows.Media.Brush)FindResource("Bg"),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var panel = new StackPanel();

            var critGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            critGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            critGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var critLabel = new TextBlock
            {
                Text = "Criterion:",
                Foreground = (System.Windows.Media.Brush)FindResource("TextDim"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(critLabel, 0);
            var critCombo = new ComboBox
            {
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                DataContext = row,
            };
            foreach (FilterFunctionType t in Enum.GetValues<FilterFunctionType>())
            {
                critCombo.Items.Add(new ComboBoxItem { Content = t, Tag = t.ToString() });
                if (t.ToString() == row.Criterion)
                {
                    critCombo.SelectedIndex = critCombo.Items.Count - 1;
                }
            }
            critCombo.SelectionChanged += (_, __) => row.Criterion = (critCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? row.Criterion;
            Grid.SetColumn(critCombo, 1);
            critGrid.Children.Add(critLabel);
            critGrid.Children.Add(critCombo);
            panel.Children.Add(critGrid);

            var opGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            opGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            opGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var opLabel = new TextBlock
            {
                Text = "Operator:",
                Foreground = (System.Windows.Media.Brush)FindResource("TextDim"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(opLabel, 0);
            var opCombo = new ComboBox
            {
                Width = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            foreach (FilterOperator op in Enum.GetValues<FilterOperator>())
            {
                opCombo.Items.Add(new ComboBoxItem { Content = op, Tag = op.ToString() });
                if (op.ToString() == row.Operator)
                {
                    opCombo.SelectedIndex = opCombo.Items.Count - 1;
                }
            }
            opCombo.SelectionChanged += (_, __) => row.Operator = (opCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? row.Operator;
            Grid.SetColumn(opCombo, 1);
            opGrid.Children.Add(opLabel);
            opGrid.Children.Add(opCombo);
            panel.Children.Add(opGrid);

            var valGrid = new Grid();
            valGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            valGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var valueBox = new TextBox
            {
                Text = row.Value,
                Style = (Style)FindResource("DarkTextBox"),
                Padding = new Thickness(8, 5, 8, 5),
            };
            valueBox.TextChanged += (_, __) => row.Value = valueBox.Text;
            Grid.SetColumn(valueBox, 0);
            var removeBtn = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("Btn"),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(8, 0, 0, 0),
            };
            removeBtn.Click += (_, __) =>
            {
                _rows.Remove(row);
                RefreshFunctionRows();
            };
            Grid.SetColumn(removeBtn, 1);
            valGrid.Children.Add(valueBox);
            valGrid.Children.Add(removeBtn);
            panel.Children.Add(valGrid);

            container.Child = panel;
            FunctionsList.Items.Add(container);
        }
    }

    private void AddFunction_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new FunctionRow());
        RefreshFunctionRows();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        RefreshFunctionRows();
    }

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCompositeVisibility();
    }

    private void UpdateCompositeVisibility()
    {
        if (CompositePanel is null) return;
        string? tag = (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        CompositePanel.Visibility = tag == "Composite" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "Please enter a filter name.", "Filter Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string typeTag = (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Normal";
        bool isComposite = typeTag == "Composite";

        if (isComposite)
        {
            var ids = ChildFilterList.Items.Cast<CheckBox>()
                .Where(c => c.IsChecked == true && c.Tag is string id)
                .Select(c => (string)c.Tag!)
                .ToList();
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Composite filter needs at least one child filter selected.", "Filter Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _filter.FilterType = FilterType.Composite;
            _filter.CompositeChildIds.Clear();
            foreach (var id in ids)
            {
                _filter.CompositeChildIds.Add(id);
            }
            _filter.Functions.Clear();
            _filter.ConditionType = FilterConditionType.ApplicationPathContains;
            _filter.Value = string.Empty;
        }
        else
        {
            var functions = new List<FilterFunction>();
            foreach (var row in _rows)
            {
                string value = row.Value.Trim();
                if (value.Length == 0) continue;
                if (!Enum.TryParse<FilterFunctionType>(row.Criterion, out var criterion)) continue;
                if (!Enum.TryParse<FilterOperator>(row.Operator, out var op)) continue;

                functions.Add(new FilterFunction(criterion, op, value));
            }

            if (functions.Count == 0)
            {
                MessageBox.Show(this, "Add at least one condition with a non-empty value.", "Filter Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _filter.FilterType = typeTag == "Zone" ? FilterType.Zone : FilterType.Normal;
            _filter.CompositeChildIds.Clear();
            _filter.Functions.Clear();
            foreach (var f in functions)
            {
                _filter.Functions.Add(f);
            }

            // Keep legacy fields in sync for backward compatibility.
            var first = functions[0];
            _filter.ConditionType = (FilterConditionType)first.Criterion;
            _filter.Value = first.Value;
        }

        _filter.Name = name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
