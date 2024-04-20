using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Client.StreamLine.Services;

public class StreamLineClientService(ILogger<StreamLineClientService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<StreamLineClientService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private void Start()
    {

    }
}
