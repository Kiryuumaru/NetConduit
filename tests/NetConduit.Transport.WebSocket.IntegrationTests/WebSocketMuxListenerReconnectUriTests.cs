using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

public class WebSocketMuxListenerReconnectUriTests
{
    [Fact]
    public void BuildReconnectUri_NoExistingQuery_AddsSession()
    {
        var sid = Guid.NewGuid();
        var uri = WebSocketMuxListener.BuildReconnectUri(new Uri("wss://host/mux"), sid);
        Assert.Equal($"wss://host/mux?session={sid}", uri.ToString());
    }

    [Fact]
    public void BuildReconnectUri_PreservesExistingQueryParameters()
    {
        var sid = Guid.NewGuid();
        var uri = WebSocketMuxListener.BuildReconnectUri(
            new Uri("wss://host/mux?token=abc&tenant=42"),
            sid);

        var query = uri.Query.TrimStart('?').Split('&');
        Assert.Contains("token=abc", query);
        Assert.Contains("tenant=42", query);
        Assert.Contains($"session={sid}", query);
    }

    [Fact]
    public void BuildReconnectUri_SingleExistingParameter_PreservedAndSessionAppended()
    {
        var sid = Guid.NewGuid();
        var uri = WebSocketMuxListener.BuildReconnectUri(
            new Uri("wss://host/mux?access_token=jwt.value.here"),
            sid);

        Assert.Equal($"wss://host/mux?access_token=jwt.value.here&session={sid}", uri.ToString());
    }
}
