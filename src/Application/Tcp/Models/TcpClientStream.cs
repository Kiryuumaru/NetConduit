using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Application.Tcp.Models;

[Disposable]
public partial class TcpClientStream
{
    public required Stream ReceiverStream { get; init; }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReceiverStream.Close();
            ReceiverStream.Dispose();
        }
    }
}
