using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamLine.Common;

public class OutgoingStreamLine(int port, CancellationToken stoppingToken) : BaseStreamLine(port, stoppingToken)
{

}