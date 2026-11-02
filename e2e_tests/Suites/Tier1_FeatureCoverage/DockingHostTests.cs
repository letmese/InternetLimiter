using System;
using System.Windows.Controls;
using Valve.Core;
using Valve.E2ETests.Framework;

namespace Valve.E2ETests.Suites.Tier1_FeatureCoverage;

public sealed class DockingHostTests
{
    [TestCase(TestTier.Tier1, "Verifies HostSlot captures parent slot and re-hosts elements cleanly for StackPanel and TabControl (B1)")]
    public void DetachAndRehost_PaneReturnsToOriginalSlot()
    {
        // 1. StackPanel test
        var panel = new StackPanel();
        var item1 = new Button { Content = "1" };
        var item2 = new Button { Content = "2" };
        var item3 = new Button { Content = "3" };
        panel.Children.Add(item1);
        panel.Children.Add(item2);
        panel.Children.Add(item3);

        TestAssert.AreEqual(3, panel.Children.Count, "StackPanel starts with 3 children");

        // Detach item 2 (index 1)
        var slot = DockManager.Detach(item2);
        TestAssert.AreEqual(HostParentType.Panel, slot.ParentType, "Slot parent type should be Panel");
        TestAssert.AreEqual(1, slot.Index, "Slot index should be 1");
        TestAssert.IsNull(item2.Parent, "Item2 parent must be null after detach");
        TestAssert.AreEqual(2, panel.Children.Count, "Panel should have 2 children left");

        // Reattach item 2
        bool reattached = DockManager.Reattach(slot, item2);
        TestAssert.IsTrue(reattached, "Reattach should succeed");
        TestAssert.AreEqual(3, panel.Children.Count, "Panel should have 3 children again");
        TestAssert.AreEqual(item2, panel.Children[1], "Item2 must be restored to original index 1");

        // 2. TabControl test
        var tabControl = new TabControl();
        var tab1 = new TabItem { Header = "Tab 1" };
        var tab2 = new TabItem { Header = "Tab 2" };
        tabControl.Items.Add(tab1);
        tabControl.Items.Add(tab2);

        var tabSlot = DockManager.Detach(tab2);
        TestAssert.AreEqual(HostParentType.TabControl, tabSlot.ParentType, "Slot parent type should be TabControl");
        TestAssert.AreEqual(1, tabSlot.Index, "Tab slot index should be 1");
        TestAssert.AreEqual(1, tabControl.Items.Count, "TabControl should have 1 item left");

        bool tabReattached = DockManager.Reattach(tabSlot, tab2);
        TestAssert.IsTrue(tabReattached, "Tab reattach should succeed");
        TestAssert.AreEqual(2, tabControl.Items.Count, "TabControl should have 2 items again");
        TestAssert.AreEqual(tab2, tabControl.Items[1], "Tab2 must be restored to original index 1");

        // 3. Border test
        var border = new Border();
        var borderContent = new TextBlock { Text = "Border content" };
        border.Child = borderContent;

        var borderSlot = DockManager.Detach(borderContent);
        TestAssert.AreEqual(HostParentType.Border, borderSlot.ParentType, "Slot parent type should be Border");
        TestAssert.IsNull(border.Child, "Border child must be null after detach");

        bool borderReattached = DockManager.Reattach(borderSlot, borderContent);
        TestAssert.IsTrue(borderReattached, "Border reattach should succeed");
        TestAssert.AreEqual(borderContent, border.Child, "Border content must be restored");
    }
}
