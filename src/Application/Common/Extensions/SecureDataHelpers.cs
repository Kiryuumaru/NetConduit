using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Application.Common.Extensions;

public static class SecureDataHelpers
{
    public static byte[] Encrypt(byte[] data, RSA rsa)
    {
        using var aes = Aes.Create();
        aes.GenerateKey();
        aes.GenerateIV();

        using var aesEncryptor = aes.CreateEncryptor();
        byte[] encryptedData = aesEncryptor.TransformFinalBlock(data, 0, data.Length);

        byte[] encryptedKey = rsa.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA256);

        Span<byte> encrypted = stackalloc byte[12 + encryptedData.Length + encryptedKey.Length + aes.IV.Length];
        Span<byte> encryptedDataHead = encrypted[..4];
        Span<byte> encryptedKeyHead = encrypted.Slice(4, 4);
        Span<byte> aesIVHead = encrypted.Slice(8, 4);
        Span<byte> dataSpan = encrypted.Slice(12, encryptedData.Length);
        Span<byte> keySpan = encrypted.Slice(12 + encryptedData.Length, encryptedKey.Length);
        Span<byte> aesIVSpan = encrypted.Slice(12 + encryptedData.Length + encryptedKey.Length, aes.IV.Length);

        BinaryPrimitives.WriteInt32LittleEndian(encryptedDataHead, encryptedData.Length);
        BinaryPrimitives.WriteInt32LittleEndian(encryptedKeyHead, encryptedKey.Length);
        BinaryPrimitives.WriteInt32LittleEndian(aesIVHead, aes.IV.Length);

        encryptedData.AsSpan().CopyTo(dataSpan);
        encryptedKey.AsSpan().CopyTo(keySpan);
        aes.IV.AsSpan().CopyTo(aesIVSpan);

        return encrypted.ToArray();
    }

    public static byte[] Decrypt(byte[] encryptedBytes, RSA rsa)
    {
        Span<byte> encrypted = encryptedBytes.AsSpan();
        Span<byte> encryptedDataHead = encrypted[..4];
        Span<byte> encryptedKeyHead = encrypted.Slice(4, 4);
        Span<byte> aesIVHead = encrypted.Slice(8, 4);

        int encryptedDataLength = BinaryPrimitives.ReadInt32LittleEndian(encryptedDataHead);
        int encryptedKeyLength = BinaryPrimitives.ReadInt32LittleEndian(encryptedKeyHead);
        int aesIVLength = BinaryPrimitives.ReadInt32LittleEndian(aesIVHead);

        byte[] encryptedData = new byte[encryptedDataLength];
        byte[] encryptedKey = new byte[encryptedKeyLength];
        byte[] aesIV = new byte[aesIVLength];

        encrypted.Slice(12, encryptedDataLength).CopyTo(encryptedData);
        encrypted.Slice(12 + encryptedDataLength, encryptedKeyLength).CopyTo(encryptedKey);
        encrypted.Slice(12 + encryptedDataLength + encryptedKeyLength, aesIVLength).CopyTo(aesIV);

        byte[] aesKey = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = aesIV;

        using var aesDecryptor = aes.CreateDecryptor();
        byte[] decryptedData = aesDecryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);

        return decryptedData;
    }

    public static byte[] Encrypt(byte[] data, byte[] publicKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportRSAPublicKey(publicKey, out _);
        return Encrypt(data, rsa);
    }

    public static byte[] Decrypt(byte[] encryptedBytes, byte[] privateKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(privateKey, out _);
        return Decrypt(encryptedBytes, rsa);
    }

    public static byte[] Encrypt<T>(T obj, RSA rsa, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return Encrypt(Encoding.Unicode.GetBytes(JsonSerializer.Serialize(obj, jsonSerializerOptions)), rsa);
    }

    public static T? Decrypt<T>(byte[] encryptedBytes, RSA rsa, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return JsonSerializer.Deserialize<T>(Encoding.Unicode.GetString(Decrypt(encryptedBytes, rsa)), jsonSerializerOptions);
    }
}
