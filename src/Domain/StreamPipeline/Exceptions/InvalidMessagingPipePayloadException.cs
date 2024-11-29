using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.StreamPipeline.Exceptions;

public class InvalidMessagingPipePayloadException(string nameOfType) : Exception($"Message is not {nameOfType}")
{
}
