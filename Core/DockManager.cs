using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Valve.Core;

public enum DockState
{
    Docked,
    Floating,
    Hidden
}

public enum DockPosition
{
    Left,
    Right,
    Bottom,
    Center
}

public enum HostParentType
{
    None,
    Panel,
    Border,
    ContentControl,
    ContentPresenter,
    TabControl,
    TabItem,
    ItemsControl
}

/// <summary>
/// Preserves the exact slot, index, and grid coordinates of a hosted WPF element
/// so it can be safely detached for floating and re-inserted on docking.
/// </summary>
public sealed class HostSlot
{
    public HostParentType ParentType { get; set; } = HostParentType.None;
    public object? Host { get; set; }
    public int Index { get; set; } = -1;
    public int GridRow { get; set; }
    public int GridColumn { get; set; }
    public int GridRowSpan { get; set; } = 1;
    public int GridColumnSpan { get; set; } = 1;
}

public sealed class DockPaneModel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DockState State { get; set; } = DockState.Docked;
    public DockPosition Position { get; set; } = DockPosition.Center;
    public double FloatingLeft { get; set; } = 150;
    public double FloatingTop { get; set; } = 150;
    public double FloatingWidth { get; set; } = 640;
    public double FloatingHeight { get; set; } = 420;

    public DockPaneModel() { }

    public DockPaneModel(string id, string title, DockPosition pos, DockState state = DockState.Docked)
    {
        Id = id;
        Title = title;
        Position = pos;
        State = state;
    }
}

public sealed class DockLayout
{
    public int Version { get; set; } = 1;
    public List<DockPaneModel> Panes { get; set; } = new();

    public static DockLayout GetDefaultLayout()
    {
        return new DockLayout
        {
            Version = 1,
            Panes = new List<DockPaneModel>
            {
                new("Activity", "Activity Grid", DockPosition.Center, DockState.Docked),
                new("TrafficChart", "Traffic Chart", DockPosition.Right, DockState.Docked),
                new("InfoPanel", "Info Panel", DockPosition.Right, DockState.Docked),
                new("ConnectionLog", "Connection Log", DockPosition.Bottom, DockState.Docked),
                new("TrafficStats", "Traffic Statistics", DockPosition.Center, DockState.Docked)
            }
        };
    }

    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            AtomicStorage.SaveAtomic(path, json, createBackup: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save layout: {ex.Message}");
        }
    }

    public static DockLayout Load(string path)
    {
        try
        {
            if (File.Exists(path) || File.Exists(path + ".bak"))
            {
                string? json = AtomicStorage.LoadAtomic(path, out _);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var layout = JsonSerializer.Deserialize<DockLayout>(json);
                    if (layout != null && layout.Panes != null && layout.Panes.Count > 0)
                    {
                        return layout;
                    }
                }
            }
        }
        catch
        {
            // fallback on corruption or read issue
        }

        return GetDefaultLayout();
    }
}

public sealed class DockPane
{
    public DockPaneModel Model { get; }
    public FrameworkElement Content { get; }
    public HostSlot Slot { get; }
    public Window? FloatingWindow { get; set; }
    public Border CaptionBar { get; }
    public ContentControl Container { get; }

    public event Action<DockPane>? StateChanged;

    public DockPane(DockPaneModel model, FrameworkElement content, HostSlot slot)
    {
        Model = model;
        Content = content;
        Slot = slot;

        CaptionBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 34, 37)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 50, 54)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 28,
            Padding = new Thickness(10, 2, 8, 2),
            Cursor = Cursors.Hand
        };

        var dock = new DockPanel();
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 22,
            Height = 22,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 175, 180)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Arrow,
            ToolTip = "Hide tool window"
        };
        System.Windows.Controls.DockPanel.SetDock(closeBtn, System.Windows.Controls.Dock.Right);
        closeBtn.Click += (_, _) => Hide();
        dock.Children.Add(closeBtn);

        var floatBtn = new Button
        {
            Content = "❐",
            Width = 22,
            Height = 22,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 175, 180)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Arrow,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Toggle floating / docked"
        };
        System.Windows.Controls.DockPanel.SetDock(floatBtn, System.Windows.Controls.Dock.Right);
        floatBtn.Click += (_, _) => ToggleFloat();
        dock.Children.Add(floatBtn);

        var titleBlock = new TextBlock
        {
            Text = model.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 222, 225)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        dock.Children.Add(titleBlock);
        CaptionBar.Child = dock;

        CaptionBar.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2)
            {
                ToggleFloat();
            }
            else if (FloatingWindow != null && e.LeftButton == MouseButtonState.Pressed)
            {
                FloatingWindow.DragMove();
            }
        };

        // Container starts with null content to avoid logical-parent conflicts
        Container = new ContentControl();
    }

    public void Float(Window? owner = null)
    {
        Window? winOwner = owner ?? Application.Current?.MainWindow;
        Model.State = DockState.Floating;

        // Ensure Content is detached from any host or parent
        DockManager.Detach(Content);
        if (Container.Content != null) Container.Content = null;

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (CaptionBar.Parent is Panel bp) bp.Children.Remove(CaptionBar);
        rootGrid.Children.Add(CaptionBar);
        Grid.SetRow(CaptionBar, 0);

        Container.Content = Content;
        rootGrid.Children.Add(Container);
        Grid.SetRow(Container, 1);

        if (FloatingWindow == null)
        {
            FloatingWindow = new Window
            {
                Title = Model.Title,
                Width = Model.FloatingWidth > 200 ? Model.FloatingWidth : 640,
                Height = Model.FloatingHeight > 150 ? Model.FloatingHeight : 420,
                Left = Model.FloatingLeft,
                Top = Model.FloatingTop,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(24, 25, 28))
            };

            if (winOwner != null && winOwner != FloatingWindow)
            {
                FloatingWindow.Owner = winOwner;
            }

            FloatingWindow.LocationChanged += (_, _) =>
            {
                Model.FloatingLeft = FloatingWindow.Left;
                Model.FloatingTop = FloatingWindow.Top;
            };

            FloatingWindow.SizeChanged += (_, _) =>
            {
                Model.FloatingWidth = FloatingWindow.ActualWidth;
                Model.FloatingHeight = FloatingWindow.ActualHeight;
            };

            FloatingWindow.Closing += (s, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }

        FloatingWindow.Content = rootGrid;
        FloatingWindow.Show();
        StateChanged?.Invoke(this);
    }

    public void Dock()
    {
        Model.State = DockState.Docked;
        if (FloatingWindow != null)
        {
            FloatingWindow.Content = null;
            FloatingWindow.Hide();
        }

        if (Container.Content != null)
        {
            Container.Content = null;
        }

        // Reattach content back to original host slot in the main window
        DockManager.Reattach(Slot, Content);

        StateChanged?.Invoke(this);
    }

    public void Hide()
    {
        Model.State = DockState.Hidden;
        if (FloatingWindow != null)
        {
            FloatingWindow.Hide();
        }

        if (Container.Content != null)
        {
            Container.Content = null;
        }

        DockManager.Detach(Content);
        StateChanged?.Invoke(this);
    }

    public void Show(Window? owner = null)
    {
        if (Model.State == DockState.Floating)
        {
            Float(owner);
        }
        else
        {
            Dock();
        }
    }

    public void ToggleFloat(Window? owner = null)
    {
        if (Model.State == DockState.Floating)
        {
            Dock();
        }
        else
        {
            Float(owner);
        }
    }
}

public sealed class DockManager
{
    private readonly Window _mainWindow;
    private readonly string _layoutPath;
    private readonly Dictionary<string, DockPane> _panes = new();

    public DockLayout Layout { get; private set; }
    public IReadOnlyDictionary<string, DockPane> Panes => _panes;

    public DockManager(Window mainWindow, string? customPath = null)
    {
        _mainWindow = mainWindow;
        _layoutPath = customPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Valve", "layout.json");
        Layout = DockLayout.Load(_layoutPath);
    }

    /// <summary>
    /// Safely detaches a FrameworkElement from its current logical/visual parent,
    /// recording all parent, slot, and grid properties into a HostSlot.
    /// </summary>
    public static HostSlot Detach(FrameworkElement el)
    {
        if (el == null) return new HostSlot();
        var slot = new HostSlot
        {
            GridRow = Grid.GetRow(el),
            GridColumn = Grid.GetColumn(el),
            GridRowSpan = Grid.GetRowSpan(el),
            GridColumnSpan = Grid.GetColumnSpan(el)
        };

        var parent = el.Parent ?? VisualTreeHelper.GetParent(el);

        if (parent is TabControl tc)
        {
            slot.ParentType = HostParentType.TabControl;
            slot.Host = tc;
            slot.Index = tc.Items.IndexOf(el);
            if (slot.Index >= 0) tc.Items.RemoveAt(slot.Index);
            return slot;
        }
        if (parent is TabItem ti)
        {
            slot.ParentType = HostParentType.TabItem;
            slot.Host = ti;
            slot.Index = 0;
            ti.Content = null;
            return slot;
        }
        if (parent is Panel panel)
        {
            slot.ParentType = HostParentType.Panel;
            slot.Host = panel;
            slot.Index = panel.Children.IndexOf(el);
            if (slot.Index >= 0) panel.Children.RemoveAt(slot.Index);
            return slot;
        }
        if (parent is Border border)
        {
            slot.ParentType = HostParentType.Border;
            slot.Host = border;
            border.Child = null;
            return slot;
        }
        if (parent is ContentControl cc)
        {
            slot.ParentType = HostParentType.ContentControl;
            slot.Host = cc;
            cc.Content = null;
            return slot;
        }
        if (parent is ContentPresenter cp)
        {
            slot.ParentType = HostParentType.ContentPresenter;
            slot.Host = cp;
            cp.Content = null;
            return slot;
        }
        if (parent is ItemsControl ic)
        {
            slot.ParentType = HostParentType.ItemsControl;
            slot.Host = ic;
            slot.Index = ic.Items.IndexOf(el);
            if (slot.Index >= 0) ic.Items.RemoveAt(slot.Index);
            return slot;
        }

        return slot;
    }

    /// <summary>
    /// Re-inserts a previously detached element back into its original slot and position.
    /// </summary>
    public static bool Reattach(HostSlot slot, FrameworkElement el)
    {
        if (slot == null || el == null || slot.Host == null) return false;

        // Ensure element is free from any temporary parent before re-inserting
        if (el.Parent is Panel p) p.Children.Remove(el);
        else if (el.Parent is Border b) b.Child = null;
        else if (el.Parent is ContentControl cc) cc.Content = null;
        else if (el.Parent is ContentPresenter cp) cp.Content = null;
        else if (el.Parent is TabItem ti) ti.Content = null;
        else if (el.Parent is TabControl tcParent) tcParent.Items.Remove(el);

        Grid.SetRow(el, slot.GridRow);
        Grid.SetColumn(el, slot.GridColumn);
        Grid.SetRowSpan(el, slot.GridRowSpan);
        Grid.SetColumnSpan(el, slot.GridColumnSpan);

        switch (slot.ParentType)
        {
            case HostParentType.Panel:
                if (slot.Host is Panel panel)
                {
                    if (slot.Index >= 0 && slot.Index <= panel.Children.Count)
                        panel.Children.Insert(slot.Index, el);
                    else
                        panel.Children.Add(el);
                    return true;
                }
                break;

            case HostParentType.Border:
                if (slot.Host is Border border)
                {
                    border.Child = el;
                    return true;
                }
                break;

            case HostParentType.TabControl:
                if (slot.Host is TabControl tc)
                {
                    if (slot.Index >= 0 && slot.Index <= tc.Items.Count)
                        tc.Items.Insert(slot.Index, el);
                    else
                        tc.Items.Add(el);
                    return true;
                }
                break;

            case HostParentType.TabItem:
                if (slot.Host is TabItem tiHost)
                {
                    tiHost.Content = el;
                    return true;
                }
                break;

            case HostParentType.ContentControl:
                if (slot.Host is ContentControl ccHost)
                {
                    ccHost.Content = el;
                    return true;
                }
                break;

            case HostParentType.ContentPresenter:
                if (slot.Host is ContentPresenter cpHost)
                {
                    cpHost.Content = el;
                    return true;
                }
                break;

            case HostParentType.ItemsControl:
                if (slot.Host is ItemsControl ic)
                {
                    if (slot.Index >= 0 && slot.Index <= ic.Items.Count)
                        ic.Items.Insert(slot.Index, el);
                    else
                        ic.Items.Add(el);
                    return true;
                }
                break;
        }

        return false;
    }

    public void RegisterPane(string id, string title, DockPosition pos, FrameworkElement content)
    {
        var model = Layout.Panes.FirstOrDefault(p => p.Id == id);
        if (model == null)
        {
            model = new DockPaneModel(id, title, pos);
            Layout.Panes.Add(model);
        }

        // 1. Detach from main window to capture slot and safely eliminate logical parent conflict
        var slot = Detach(content);

        // 2. Build DockPane with captured slot
        var pane = new DockPane(model, content, slot);
        pane.StateChanged += _ => SaveLayout();
        _panes[id] = pane;

        // 3. Apply state
        if (model.State == DockState.Floating)
        {
            pane.Float(_mainWindow);
        }
        else if (model.State == DockState.Hidden)
        {
            pane.Hide();
        }
        else
        {
            // Reattach to window for Docked state
            pane.Dock();
        }
    }

    public void SaveLayout()
    {
        Layout.Save(_layoutPath);
    }

    public void ResetLayout()
    {
        Layout = DockLayout.GetDefaultLayout();
        foreach (var p in Layout.Panes)
        {
            if (_panes.TryGetValue(p.Id, out var pane))
            {
                pane.Model.State = p.State;
                pane.Model.Position = p.Position;
                pane.Dock();
            }
        }
        SaveLayout();
    }
}
