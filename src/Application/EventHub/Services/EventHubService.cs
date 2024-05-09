using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.EventHub.Services;

public class EventHubService(ILogger<EventHubService> logger)
{
    private readonly ILogger<EventHubService> _logger = logger;


}
