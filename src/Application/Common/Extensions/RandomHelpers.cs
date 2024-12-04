namespace Application.Common.Extensions;

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
