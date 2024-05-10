using DisposableHelpers.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamLine.Common;

[Disposable]
public abstract partial class BaseStreamLine(int port)
{
    public int Port { get; } = port;
}
