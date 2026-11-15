using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vera.Core.Sync;

/// <summary>Ошибка синхронизации с понятным пользователю текстом.</summary>
public sealed class SyncException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Канал поверх TCP: кадры «4 байта длины + данные». После приветствия всё шифруется:
/// AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC) со счётчиком кадров — подделку, повтор и перестановку видно.
/// Ключи сеанса выводятся из общего ключа пары и двух случайных чисел (HKDF), отдельно для каждого направления.
/// Выбраны алгоритмы, которые есть и в Windows, и в Android.
/// </summary>
public sealed class SyncChannel : IDisposable
{
    private const int MaxFrame = 32 * 1024 * 1024;
    private readonly NetworkStream _stream;
    private readonly TcpClient? _owner;
    private byte[]? _sendEnc, _sendMac, _recvEnc, _recvMac;
    private long _sendN, _recvN;

    public SyncChannel(TcpClient client)
    {
        _owner = client;
        _stream = client.GetStream();
    }

    public bool Secure => _sendEnc is not null;

    // ---------------------------------------------------------------- кадры

    private async Task WriteFrame(byte[] data, CancellationToken ct)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        await _stream.WriteAsync(len, ct).ConfigureAwait(false);
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadFrame(CancellationToken ct)
    {
        var len = new byte[4];
        await _stream.ReadExactlyAsync(len, ct).ConfigureAwait(false);
        var n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n <= 0 || n > MaxFrame) throw new SyncException("Неверный ответ устройства (размер кадра).");
        var buf = new byte[n];
        await _stream.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    public async Task SendAsync(JsonObject msg, CancellationToken ct)
    {
        var plain = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await WriteFrame(Secure ? Seal(plain) : plain, ct).ConfigureAwait(false);
    }

    public async Task<JsonObject> ReceiveAsync(CancellationToken ct)
    {
        var frame = await ReadFrame(ct).ConfigureAwait(false);
        var plain = Secure ? Open(frame) : frame;
        try
        {
            return JsonNode.Parse(plain) as JsonObject ?? throw new SyncException("Неверный ответ устройства.");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new SyncException("Неверный ответ устройства.", e);
        }
    }

    // ---------------------------------------------------------------- шифрование

    /// <summary>Включить шифрование. isClient — направление ключей (у клиента «отправка» = c2s).</summary>
    public void StartSecure(byte[] pairKey, byte[] clientNonce, byte[] serverNonce, bool isClient)
    {
        var salt = clientNonce.Concat(serverNonce).ToArray();
        byte[] K(string info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, pairKey, 32, salt, Encoding.ASCII.GetBytes(info));
        var c2sE = K("vera-sync c2s enc"); var c2sM = K("vera-sync c2s mac");
        var s2cE = K("vera-sync s2c enc"); var s2cM = K("vera-sync s2c mac");
        (_sendEnc, _sendMac, _recvEnc, _recvMac) = isClient ? (c2sE, c2sM, s2cE, s2cM) : (s2cE, s2cM, c2sE, c2sM);
        _sendN = _recvN = 0;
    }

    private byte[] Seal(byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = _sendEnc!;
        var iv = RandomNumberGenerator.GetBytes(16);
        var ct = aes.EncryptCbc(plain, iv);
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, _sendN++);
        var mac = HMACSHA256.HashData(_sendMac!, counter.Concat(iv).Concat(ct).ToArray());
        return iv.Concat(ct).Concat(mac).ToArray();
    }

    private byte[] Open(byte[] frame)
    {
        if (frame.Length < 16 + 16 + 32) throw new SyncException("Повреждённые данные синхронизации.");
        var iv = frame[..16];
        var ct = frame[16..^32];
        var mac = frame[^32..];
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, _recvN);
        var expect = HMACSHA256.HashData(_recvMac!, counter.Concat(iv).Concat(ct).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(mac, expect))
            throw new SyncException("Ключ устройства не подошёл — привяжите телефон заново.");
        _recvN++;
        using var aes = Aes.Create();
        aes.Key = _recvEnc!;
        return aes.DecryptCbc(ct, iv);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>Привязка устройств: эфемерные ключи ECDH (P-256) и одноразовый код из QR.</summary>
public static class SyncPairing
{
    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] Public(ECDiffieHellman k) => k.PublicKey.ExportSubjectPublicKeyInfo();

    /// <summary>Отпечаток ключа ПК в QR: телефон проверит, что говорит именно с этим ПК.</summary>
    public static string Fingerprint(byte[] publicKey) => B64Url(SHA256.HashData(publicKey)[..12]);

    public static byte[] Mac(byte[] token, string label, params byte[][] parts) =>
        HMACSHA256.HashData(token, Encoding.UTF8.GetBytes(label).Concat(parts.SelectMany(p => p)).ToArray());

    /// <summary>Общий ключ пары: ECDH + одноразовый код (без кода из QR ключ не получить).</summary>
    public static byte[] SharedKey(ECDiffieHellman mine, byte[] otherPublic, byte[] token)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublic, out _);
        return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, token, Encoding.ASCII.GetBytes("vera-sync-v1"));
    }

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
