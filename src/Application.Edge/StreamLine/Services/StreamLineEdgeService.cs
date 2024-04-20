using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.StreamLine.Services;

public class StreamLineEdgeService(ILogger<StreamLineEdgeService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<StreamLineEdgeService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private void Start()
    {

    }
}
