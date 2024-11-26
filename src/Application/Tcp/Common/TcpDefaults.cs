using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Tcp.Common;

internal static class TcpDefaults
{
    public static readonly TimeSpan LivelinessSpan = TimeSpan.FromSeconds(1);
}
