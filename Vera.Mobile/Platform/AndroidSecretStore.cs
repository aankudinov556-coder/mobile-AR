using Vera.Core;

namespace Vera.Mobile.Platform;

/// <summary>
/// Ключи API в SecureStorage (Android Keystore). Интерфейс ядра синхронный, а SecureStorage — асинхронный,
/// поэтому значения кэшируются: читаются один раз (в фоновом потоке, без риска дедлока), пишутся в фоне.
/// </summary>
public sealed class AndroidSecretStore : ISecretStore
{
    private const string Prefix = "vera_key_";
    private readonly Dictionary<string, string?> _cache = new();
    private readonly object _gate = new();
    private Task _writes = Task.CompletedTask;

    public string? Get(string name)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(name, out var v)) return v;
        }
        string? value = null;
        try
        {
            value = Task.Run(async () => await SecureStorage.Default.GetAsync(Prefix + name).ConfigureAwait(false))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // хранилище повреждено (например, после восстановления из резервной копии) — начинаем заново
            try { SecureStorage.Default.Remove(Prefix + name); } catch { }
        }
        lock (_gate) _cache[name] = value;
        return value;
    }

    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) value = null;
        lock (_gate)
        {
            _cache[name] = value;
            var prev = _writes;
            _writes = Task.Run(async () =>
            {
                try { await prev.ConfigureAwait(false); } catch { }
                try
                {
                    if (value is null) SecureStorage.Default.Remove(Prefix + name);
                    else await SecureStorage.Default.SetAsync(Prefix + name, value).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Android.Util.Log.Warn("Vera", "SecureStorage: " + e.Message);
                }
            });
        }
    }

    /// <summary>Дождаться записи (перед выходом).</summary>
    public void Flush()
    {
        Task t;
        lock (_gate) t = _writes;
        try { t.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
