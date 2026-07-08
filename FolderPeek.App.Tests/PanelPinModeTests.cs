namespace FolderPeek.App.Tests;

public sealed class PanelPinModeTests
{
    [Fact]
    public void GetNextPinMode_CyclesThroughAllThreeStates()
    {
        Assert.Equal(PanelPinMode.PinnedToDesktop, PanelPinMode.None.GetNextPinMode());
        Assert.Equal(PanelPinMode.PinnedTopmost, PanelPinMode.PinnedToDesktop.GetNextPinMode());
        Assert.Equal(PanelPinMode.None, PanelPinMode.PinnedTopmost.GetNextPinMode());
    }
}
