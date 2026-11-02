using System;
using System.IO;
using System.Linq;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class DockLayoutTests
{
    [TestCase(TestTier.Tier1, "Verifies dockable tool window layout model persistence, serialization, and round-trip recovery (Item 22.x)")]
    public void DockLayout_PersistAndRestore_RoundTrips()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"valve_dock_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string layoutPath = Path.Combine(tempDir, "layout.json");

        try
        {
            // 1. Initial Default Layout
            var defaultLayout = DockLayout.GetDefaultLayout();
            TestAssert.AreEqual(5, defaultLayout.Panes.Count, "Default layout should contain 5 primary tool window panes");

            var activityPane = defaultLayout.Panes.FirstOrDefault(p => p.Id == "Activity");
            TestAssert.IsNotNull(activityPane, "Activity pane should be present");
            TestAssert.AreEqual(DockState.Docked, activityPane!.State);

            // 2. Customise pane states and floating coordinates
            var chartPane = defaultLayout.Panes.FirstOrDefault(p => p.Id == "TrafficChart");
            TestAssert.IsNotNull(chartPane, "TrafficChart pane should be present");
            chartPane!.State = DockState.Floating;
            chartPane.FloatingLeft = 250;
            chartPane.FloatingTop = 180;
            chartPane.FloatingWidth = 800;
            chartPane.FloatingHeight = 500;

            var logPane = defaultLayout.Panes.FirstOrDefault(p => p.Id == "ConnectionLog");
            TestAssert.IsNotNull(logPane, "ConnectionLog pane should be present");
            logPane!.State = DockState.Hidden;

            var infoPane = defaultLayout.Panes.FirstOrDefault(p => p.Id == "InfoPanel");
            TestAssert.IsNotNull(infoPane, "InfoPanel pane should be present");
            infoPane!.Position = DockPosition.Left;

            // 3. Save layout atomically to disk
            defaultLayout.Save(layoutPath);
            TestAssert.IsTrue(File.Exists(layoutPath), "Layout file should exist on disk");

            // 4. Restore layout from disk
            var loadedLayout = DockLayout.Load(layoutPath);
            TestAssert.IsNotNull(loadedLayout, "Loaded layout should not be null");
            TestAssert.AreEqual(5, loadedLayout.Panes.Count, "Loaded layout should have 5 panes");

            var loadedChart = loadedLayout.Panes.FirstOrDefault(p => p.Id == "TrafficChart");
            TestAssert.IsNotNull(loadedChart);
            TestAssert.AreEqual(DockState.Floating, loadedChart!.State, "TrafficChart should be Floating");
            TestAssert.AreEqual(250, loadedChart.FloatingLeft, "FloatingLeft should round-trip");
            TestAssert.AreEqual(180, loadedChart.FloatingTop, "FloatingTop should round-trip");
            TestAssert.AreEqual(800, loadedChart.FloatingWidth, "FloatingWidth should round-trip");
            TestAssert.AreEqual(500, loadedChart.FloatingHeight, "FloatingHeight should round-trip");

            var loadedLog = loadedLayout.Panes.FirstOrDefault(p => p.Id == "ConnectionLog");
            TestAssert.IsNotNull(loadedLog);
            TestAssert.AreEqual(DockState.Hidden, loadedLog!.State, "ConnectionLog should be Hidden");

            var loadedInfo = loadedLayout.Panes.FirstOrDefault(p => p.Id == "InfoPanel");
            TestAssert.IsNotNull(loadedInfo);
            TestAssert.AreEqual(DockPosition.Left, loadedInfo!.Position, "InfoPanel position should be Left");

            // 5. Test resilience: corrupted file falls back to default layout safely
            File.WriteAllText(layoutPath, "{ invalid json data !!! corrupt }");
            var fallbackLayout = DockLayout.Load(layoutPath);
            TestAssert.IsNotNull(fallbackLayout, "Corrupted layout should gracefully fallback to default");
            TestAssert.AreEqual(5, fallbackLayout.Panes.Count, "Fallback layout should provide default 5 panes");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
