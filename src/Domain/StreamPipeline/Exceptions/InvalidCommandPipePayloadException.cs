using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.StreamPipeline.Exceptions;

public class InvalidCommandPipePayloadException(string nameOfType) : Exception($"Command is not {nameOfType}")
{
}
