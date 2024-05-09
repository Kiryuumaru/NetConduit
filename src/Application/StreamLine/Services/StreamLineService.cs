using Application.StreamLine.Common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.StreamLine.Services;

public class StreamLineService(ILogger<StreamLineService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<StreamLineService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ConcurrentDictionary<int, OutgoingStreamLine> _outgoingStreams = [];
    private readonly ConcurrentDictionary<int, IncomingStreamLine> _incomingStreams = [];
    private readonly SemaphoreSlim _locker = new(1);

    public void Add(int port)
    {

    }
}
