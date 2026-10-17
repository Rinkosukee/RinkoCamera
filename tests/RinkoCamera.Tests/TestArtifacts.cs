namespace RinkoCamera.Tests;

internal static class TestArtifacts
{
    public static string CreateDirectory(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RinkoCamera.sln"))) root = root.Parent;
        var directory = Path.Combine(root?.FullName ?? AppContext.BaseDirectory,
            "artifacts", "regression", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
