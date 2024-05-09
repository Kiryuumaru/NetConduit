using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamLine.Common;

public abstract class BaseStreamLine(int port, CancellationToken stoppingToken)
{
    public int Port { get; } = port;

    public CancellationToken StoppingToken { get; } = stoppingToken;
}
