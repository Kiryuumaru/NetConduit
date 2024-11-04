using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Configuration.Exceptions;

public class NoConfigValueException(string configName) : Exception($"{configName} config is empty")
{
}
