namespace FolderPeek.App.Tests;

public sealed class SingleInstanceServiceTests
{
    [Theory]
    [InlineData("--open-folder", "C:\\Users\\Example\\Desktop")]
    [InlineData("--OPEN-FOLDER", "D:\\Work")]
    public void TryParseOpenFolderArgument_RecognizesShellCommand(string option, string expectedPath)
    {
        Assert.Equal(expectedPath, SingleInstanceService.TryParseOpenFolderArgument(new[] { option, expectedPath }));
    }

    [Fact]
    public void TryParseOpenFolderArgument_RejectsUnexpectedArguments()
    {
        Assert.Null(SingleInstanceService.TryParseOpenFolderArgument(Array.Empty<string>()));
        Assert.Null(SingleInstanceService.TryParseOpenFolderArgument(new[] { "--open-folder" }));
        Assert.Null(SingleInstanceService.TryParseOpenFolderArgument(new[] { "--other", "C:\\Folder" }));
    }
}
