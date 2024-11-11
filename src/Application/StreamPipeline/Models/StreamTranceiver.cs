using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Models;

[Disposable]
public partial class StreamTranceiver
{
    public required Stream ReceiverStream { get; init; }

    public required Stream SenderStream { get; init; }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReceiverStream.Close();
            SenderStream.Close();

            ReceiverStream.Dispose();
            SenderStream.Dispose();
        }
    }
}
