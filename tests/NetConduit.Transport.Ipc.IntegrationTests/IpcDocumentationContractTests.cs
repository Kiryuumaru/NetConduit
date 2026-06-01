namespace NetConduit.Transport.Ipc.IntegrationTests;

public sealed class IpcDocumentationContractTests
{
    [Fact]
    public void WindowsEndpointDocs_DescribeRegistryFileAndOsAssignedPort()
    {
        var docs = File.ReadAllText(FindRepositoryFile("docs", "transports", "ipc.md"));

        Assert.Contains("OS-assigned ephemeral port", docs);
        Assert.Contains("%LOCALAPPDATA%", docs);
        Assert.Contains("NetConduit", docs);
        Assert.Contains("ipc-endpoints", docs);
        Assert.Contains("{SHA256(endpoint)}.port", docs);
        Assert.Contains("deleted when the server-side stream pair is disposed", docs);
        Assert.False(
            docs.Contains("deterministic port derived from the endpoint name", StringComparison.OrdinalIgnoreCase),
            "Windows IPC docs must not describe the removed deterministic-port endpoint model.");
        Assert.False(
            docs.Contains("No file is created", StringComparison.OrdinalIgnoreCase),
            "Windows IPC docs must not deny the endpoint registry file used by the implementation.");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(segments));
    }
}