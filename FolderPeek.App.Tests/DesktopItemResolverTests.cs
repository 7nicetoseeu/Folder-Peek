using System.Windows;

namespace FolderPeek.App.Tests;

public sealed class DesktopItemResolverTests
{
    [Fact]
    public void ResolveHitFromSnapshot_ReturnsFolderContainingPoint()
    {
        var items = new[]
        {
            new DesktopFolderHit("alpha", @"C:\alpha", "test", new Rect(10, 10, 40, 30)),
            new DesktopFolderHit("beta", @"C:\beta", "test", new Rect(80, 20, 50, 50))
        };

        var hit = DesktopItemResolver.ResolveHitFromSnapshot(items, 95, 30);

        Assert.NotNull(hit);
        Assert.Equal("beta", hit!.DisplayName);
    }

    [Fact]
    public void ResolveHitFromSnapshot_ReturnsNullWhenPointIsOutsideAllFolders()
    {
        var items = new[]
        {
            new DesktopFolderHit("alpha", @"C:\alpha", "test", new Rect(10, 10, 40, 30))
        };

        var hit = DesktopItemResolver.ResolveHitFromSnapshot(items, 200, 200);

        Assert.Null(hit);
    }
}
