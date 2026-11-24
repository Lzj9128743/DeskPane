using DeskPane.Models;
using DeskPane.Services;

namespace DeskPane.Tests;

public sealed class VirtualDesktopGroupPolicyTests
{
    [Fact]
    public void ShouldIncludeItem_ExplicitAssignmentWinsAndPreventsDuplicates()
    {
        var groupA = new WidgetConfig
        {
            Id = "group-a",
            IsVirtualDesktopGroup = true,
            VirtualDesktopCategoryId = DesktopOrganizationCategoryIds.Shortcuts,
            Items = [new WidgetItemConfig { Path = @"C:\Users\Test\Desktop\App.lnk" }]
        };
        var groupB = new WidgetConfig
        {
            Id = "group-b",
            IsVirtualDesktopGroup = true,
            VirtualDesktopCategoryId = DesktopOrganizationCategoryIds.Shortcuts
        };
        var all = new[] { groupA, groupB };

        Assert.True(VirtualDesktopGroupPolicy.ShouldIncludeItem(groupA, all, @"C:\Users\Test\Desktop\App.lnk"));
        Assert.False(VirtualDesktopGroupPolicy.ShouldIncludeItem(groupB, all, @"C:\Users\Test\Desktop\App.lnk"));
    }

    [Fact]
    public void ShouldIncludeItem_AutoRoutesUnassignedItemsByCategory()
    {
        var docs = new WidgetConfig
        {
            Id = "docs",
            IsVirtualDesktopGroup = true,
            VirtualDesktopCategoryId = DesktopOrganizationCategoryIds.Documents
        };
        var images = new WidgetConfig
        {
            Id = "images",
            IsVirtualDesktopGroup = true,
            VirtualDesktopCategoryId = DesktopOrganizationCategoryIds.Images
        };
        var all = new[] { docs, images };

        Assert.True(VirtualDesktopGroupPolicy.ShouldIncludeItem(docs, all, @"C:\Users\Test\Desktop\report.pdf"));
        Assert.False(VirtualDesktopGroupPolicy.ShouldIncludeItem(images, all, @"C:\Users\Test\Desktop\report.pdf"));
        Assert.True(VirtualDesktopGroupPolicy.ShouldIncludeItem(images, all, @"C:\Users\Test\Desktop\photo.png"));
    }

    [Fact]
    public void AssignPathsToVirtualGroup_ReassignsLogicallyWithoutDuplication()
    {
        var groupA = new WidgetConfig
        {
            Id = "group-a",
            IsVirtualDesktopGroup = true,
            Items = [new WidgetItemConfig { Path = @"C:\Users\Test\Desktop\notes.txt" }]
        };
        var groupB = new WidgetConfig
        {
            Id = "group-b",
            IsVirtualDesktopGroup = true
        };
        var all = new[] { groupA, groupB };

        IReadOnlyList<string> affected = VirtualDesktopGroupPolicy.AssignPathsToVirtualGroup(
            groupB,
            all,
            [@"C:\Users\Test\Desktop\notes.txt"]);

        Assert.Contains("group-a", affected);
        Assert.Contains("group-b", affected);
        Assert.Empty(groupA.Items);
        Assert.Single(groupB.Items);
        Assert.False(VirtualDesktopGroupPolicy.ShouldIncludeItem(groupA, all, @"C:\Users\Test\Desktop\notes.txt"));
        Assert.True(VirtualDesktopGroupPolicy.ShouldIncludeItem(groupB, all, @"C:\Users\Test\Desktop\notes.txt"));
    }
}
