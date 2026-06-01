namespace NetConduit.UnitTests;

using NetConduit.Internal;

public sealed class DocumentationContractTests
{
    [Fact]
    public void FramingProtocolDocumentsReconnectWirePayload()
    {
        string docs = File.ReadAllText(FindRepositoryFile("docs", "concepts", "framing-protocol.md"));

        Assert.Contains("Initial handshake payload", docs, StringComparison.Ordinal);
        Assert.Contains($"`{MuxHandshake.InitialPayloadLength}` bytes", docs, StringComparison.Ordinal);
        Assert.Contains("Guid.TryWriteBytes", docs, StringComparison.Ordinal);
        Assert.Contains("maxRecvPayload", docs, StringComparison.Ordinal);
        Assert.Contains($"`{MuxHandshake.ReconnectHeaderLength}`-byte", docs, StringComparison.Ordinal);
        Assert.Contains("channelCount", docs, StringComparison.Ordinal);
        Assert.Contains("channelIndex", docs, StringComparison.Ordinal);
        Assert.Contains("frameBytesReceived", docs, StringComparison.Ordinal);
        Assert.Contains($"`{MuxHandshake.ReconnectChannelEntrySize}`-byte", docs, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = directory.FullName;
            foreach (string segment in relativeSegments)
            {
                candidate = Path.Combine(candidate, segment);
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(relativeSegments));
    }
}