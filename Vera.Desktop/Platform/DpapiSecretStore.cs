using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vera.Core;

namespace Vera.Desktop.Platform;

/// <summary>Ключи API, зашифрованные Windows DPAPI (CurrentUser) — расшифровать может только этот пользователь Windows.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const string FileName = "secrets.json";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VeraAssistant.v2");

    private readonly IVeraStorage _storage;
    private readonly object _gate = new();
    private Dictionary<string, string>? _data;

    public DpapiSecretStore(IVeraStorage storage) => _storage = storage;

    private Dictionary<string, string> Data
    {
        get
        {
            if (_data is not null) return _data;
            try
            {
                var text = _storage.ReadText(FileName);
                _data = string.IsNullOrWhiteSpace(text)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new Dictionary<string, string>();
            }
            catch (Exception)
            {
                _data = new Dictionary<string, string>();
            }
            return _data;
        }
    }

    public string? Get(string name)
    {
        lock (_gate)
        {
            if (!Data.TryGetValue(name, out var b64) || string.IsNullOrEmpty(b64)) return null;
            try
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {
                // файл скопирован с другого ПК / другого пользователя — ключ недоступен
                return null;
            }
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(value)) Data.Remove(name);
            else
            {
                var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                Data[name] = Convert.ToBase64String(enc);
            }
            _storage.WriteText(FileName, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
