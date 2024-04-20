using Microsoft.Extensions.Logging;
using System;
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

}
