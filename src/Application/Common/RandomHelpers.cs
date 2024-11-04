using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

public static class RandomHelpers
{
    public static byte[] ByteArray(int size)
    {
        Random random = new();
        byte[] bytes = new byte[size];
        random.NextBytes(bytes);
        return bytes;
    }
}
