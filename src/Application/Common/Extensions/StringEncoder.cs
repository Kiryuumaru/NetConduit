using System.Text;

namespace Application.Common.Extensions;

public static class StringEncoder
{
    private const string AlphanumericCaseSensitiveChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string AlphanumericCaseInsensitiveChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string Encode(this string value)
    {
        string enc = Convert.ToBase64String(Encoding.ASCII.GetBytes(value));
        enc = enc.Replace("/", "_");
        enc = enc.Replace("+", "-");
        return enc;
    }

    public static string Decode(this string encoded)
    {
        encoded = encoded.Replace("_", "/");
        encoded = encoded.Replace("-", "+");
        byte[] buffer = Convert.FromBase64String(encoded);
        return Encoding.ASCII.GetString(buffer);
    }

    public static string Random(int length, bool caseSensitive = true)
    {
        StringBuilder sb = new();
        Random random = new();

        string chars = caseSensitive ? AlphanumericCaseSensitiveChars : AlphanumericCaseInsensitiveChars;

        for (int i = 0; i < length; i++)
        {
            int index = random.Next(chars.Length);
            sb.Append(chars[index]);
        }

        return sb.ToString();
    }
}
