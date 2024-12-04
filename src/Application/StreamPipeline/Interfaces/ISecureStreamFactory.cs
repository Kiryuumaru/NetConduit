using Application.StreamPipeline.Common;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Interfaces;

public interface ISecureStreamFactory
{
    TranceiverStream CreateSecureTranceiverStream(int capacity);
}
