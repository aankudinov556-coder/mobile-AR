using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vera.Core.Agent;

namespace Vera.Core.Sync;

/// <summary>
/// Синхронизация ПК ↔ телефон в домашней сети (PLAN.md, п. 9.4).
/// <list type="bullet">
/// <item>Каждое изменение напоминания, факта памяти, общих настроек и хода чата получает версию (гибридные часы)
/// и локальный номер; удаления запоминаются надгробиями.</item>
/// <item>Между устройствами ходят только изменения с прошлой встречи; конфликт — побеждает более позднее.</item>
/// <item>ПК — сервер (<see cref="SyncServer"/>), телефон — клиент (<see cref="SyncAsync"/>). Привязка — QR-код с ПК.</item>
/// </list>
/// </summary>
public sealed class SyncService
{
    private const string KeyPrefix = "sync.peer.";
    private static readonly TimeSpan OfferLife = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KeepTombstones = TimeSpan.FromDays(90);

    private readonly JsonFile<SyncState> _file;
    private readonly ISecretStore _store;
    private readonly SettingsService _settings;
    private readonly SecretsService _keys;
    private readonly ReminderService _reminders;
    private readonly MemoryService _memory;
    private readonly VeraAgent _agent;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private bool _applyingShared;

    private (byte[] Token, ECDiffieHellman Key, DateTimeOffset Expires)? _offer;

    public SyncService(IVeraStorage storage, ISecretStore store, SettingsService settings, SecretsService keys,
        ReminderService reminders, MemoryService memory, VeraAgent agent, string platform)
    {
        _store = store;
        _settings = settings;
        _keys = keys;
        _reminders = reminders;
        _memory = memory;
        _agent = agent;
        Platform = platform;
        _file = new JsonFile<SyncState>(storage, "sync.json", () => new SyncState());
        var st = _file.Data;
        var fresh = string.IsNullOrEmpty(st.DeviceId);
        if (fresh)
        {
            st.DeviceId = "d_" + Guid.NewGuid().ToString("N")[..12];
            st.DeviceName = platform == "desktop" ? Environment.MachineName : "Телефон";
        }

        _reminders.Stamp = Stamp;
        _reminders.Deleted = OnDeleted;
        _memory.Stamp = Stamp;
        _memory.Deleted = OnDeleted;
        _agent.SyncStamp = () => (NextSeq(), DeviceId);
        _settings.Changed += OnSettingsChanged;
        _keys.Changed += () =>
        {
            if (_settings.Current.Sync.Keys) lock (_gate) st.KeysSeq = NextSeqLocked();
        };

        if (fresh)
        {
            // данные, созданные до синхронизации, тоже должны попасть на телефон
            foreach (var r in _reminders.List()) _reminders.Update(r.Id);
            foreach (var m in _memory.List()) _memory.Update(m.Id, m.Text);
            st.SharedKey = SharedSettings.From(_settings.Current).Key();
        }
        _file.Save();
    }

    public string Platform { get; }
    public string DeviceId => _file.Data.DeviceId;

    /// <summary>Имя этого устройства, которое видит второе («ПК Андрея», «Телефон»).</summary>
    public string DeviceName
    {
        get => _file.Data.DeviceName;
        set { lock (_gate) _file.Data.DeviceName = (value ?? "").Trim(); _file.Save(); Changed?.Invoke(); }
    }

    public IReadOnlyList<SyncPeer> Peers
    {
        get { lock (_gate) return _file.Data.Peers.ToList(); }
    }

    /// <summary>Список устройств или их статус изменился.</summary>
    public event Action? Changed;

    /// <summary>Синхронизация прошла (в фоновом потоке).</summary>
    public event Action<SyncResult>? Synced;

    /// <summary>Когда пользователь последний раз был активен на этом устройстве (задаёт платформа).</summary>
    public Func<DateTimeOffset?>? LastActive { get; set; }

    // ================================================================== часы и номера

    private long NextVer()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            _file.Data.Clock = Math.Max(now, _file.Data.Clock + 1);
            return _file.Data.Clock;
        }
    }

    private void Observe(long ver)
    {
        lock (_gate) _file.Data.Clock = Math.Max(_file.Data.Clock, ver);
    }

    private long NextSeqLocked() => ++_file.Data.Seq;

    private long NextSeq()
    {
        long s;
        lock (_gate) s = NextSeqLocked();
        _file.Save();
        return s;
    }

    private void Stamp(ISyncable x)
    {
        x.Ver = NextVer();
        x.Dev = DeviceId;
        x.Seq = NextSeq();
    }

    private void OnDeleted(string kind, string id)
    {
        var t = new Tombstone { Kind = kind, Id = id, Ver = NextVer(), Dev = DeviceId, At = DateTimeOffset.UtcNow };
        lock (_gate)
        {
            t.Seq = NextSeqLocked();
            _file.Data.Tombstones.RemoveAll(x => x.Kind == kind && x.Id == id);
            _file.Data.Tombstones.Add(t);
            _file.Data.Tombstones.RemoveAll(x => DateTimeOffset.UtcNow - x.At > KeepTombstones);
        }
        _file.Save();
    }

    private long? DeletedVer(string kind, string id)
    {
        lock (_gate) return _file.Data.Tombstones.FirstOrDefault(t => t.Kind == kind && t.Id == id)?.Ver;
    }

    private void OnSettingsChanged(VeraSettings s)
    {
        if (_applyingShared) return;
        var key = SharedSettings.From(s).Key();
        lock (_gate)
        {
            if (key == _file.Data.SharedKey) return;
            _file.Data.SharedKey = key;
        }
        StampShared();
    }

    private void StampShared()
    {
        var ver = NextVer();
        lock (_gate)
        {
            _file.Data.SharedVer = ver;
            _file.Data.SharedDev = DeviceId;
            _file.Data.SharedSeq = NextSeqLocked();
        }
        _file.Save();
    }

    // ================================================================== пакеты

    internal SyncBatch BuildBatch(SyncPeer peer)
    {
        long upTo;
        SyncState st = _file.Data;
        lock (_gate) upTo = st.Seq;
        var since = peer.SentSeq;
        var b = new SyncBatch { From = DeviceId, UpTo = upTo, Active = LastActive?.Invoke() };
        b.Reminders = _reminders.ChangedSince(since, peer.Id).Where(r => r.Seq <= upTo).ToList();
        b.Memory = _memory.ChangedSince(since, peer.Id).Where(m => m.Seq <= upTo).ToList();
        lock (_gate)
        {
            b.Deleted = st.Tombstones.Where(t => t.Seq > since && t.Seq <= upTo && t.Dev != peer.Id).ToList();
            if (st.SharedVer > 0 && st.SharedSeq > since && st.SharedSeq <= upTo && st.SharedDev != peer.Id)
            {
                b.Shared = JsonSerializer.Deserialize<SharedSettings>(st.SharedKey, VeraJson.Options);
                b.SharedVer = st.SharedVer;
                b.SharedDev = st.SharedDev;
            }
        }
        var s = _settings.Current;
        if (s.Sync.Chat)
            b.Chat = _agent.ExportTurns(since, peer.Id).Where(t => t[0].Seq <= upTo).ToList();
        if (s.Sync.Keys && st.KeysSeq > since)
        {
            b.Keys = SecretsService.Known.Where(k => _keys.Has(k.Name)).ToDictionary(k => k.Name, k => _keys.Get(k.Name));
        }
        return b;
    }

    /// <summary>Применить пакет с другого устройства. Возвращает число принятых изменений.</summary>
    internal int Apply(SyncBatch b, SyncPeer from)
    {
        var n = 0;
        foreach (var v in b.Reminders.Select(r => r.Ver).Concat(b.Memory.Select(m => m.Ver)).Concat(b.Deleted.Select(t => t.Ver)).Append(b.SharedVer))
            Observe(v);

        foreach (var t in b.Deleted)
        {
            var applied = t.Kind switch
            {
                "reminder" => _reminders.ApplyDelete(t.Id, t.Ver, t.Dev),
                "memory" => _memory.ApplyDelete(t.Id, t.Ver, t.Dev),
                _ => false,
            };
            if (applied) n++;
            lock (_gate)
            {
                var cur = _file.Data.Tombstones.FirstOrDefault(x => x.Kind == t.Kind && x.Id == t.Id);
                if (cur is not null && cur.Ver >= t.Ver) continue;
                _file.Data.Tombstones.Remove(cur!);
                _file.Data.Tombstones.Add(new Tombstone { Kind = t.Kind, Id = t.Id, Ver = t.Ver, Dev = t.Dev, At = t.At, Seq = NextSeqLocked() });
            }
        }
        n += _reminders.ApplyRemote(b.Reminders, NextSeq, id => DeletedVer("reminder", id));
        n += _memory.ApplyRemote(b.Memory, NextSeq, id => DeletedVer("memory", id));

        if (b.Shared is { } shared && b.SharedVer > 0)
        {
            bool newer;
            lock (_gate) newer = SyncClock.Newer(b.SharedVer, b.SharedDev, _file.Data.SharedVer, _file.Data.SharedDev);
            if (newer)
            {
                _applyingShared = true;
                try { _settings.Update(shared.ApplyTo); }
                finally { _applyingShared = false; }
                lock (_gate)
                {
                    _file.Data.SharedKey = SharedSettings.From(_settings.Current).Key();
                    _file.Data.SharedVer = b.SharedVer;
                    _file.Data.SharedDev = b.SharedDev;
                    _file.Data.SharedSeq = NextSeqLocked();
                }
                n++;
            }
        }

        if (b.Chat is { Count: > 0 } && _settings.Current.Sync.Chat) n += _agent.ImportTurns(b.Chat, NextSeq);

        if (b.Keys is { Count: > 0 } && _settings.Current.Sync.Keys)
        {
            foreach (var (name, value) in b.Keys)
            {
                if (!SecretsService.Known.Any(k => k.Name == name) || _keys.Has(name) || string.IsNullOrWhiteSpace(value)) continue;
                _keys.Set(name, value);
                n++;
            }
        }

        lock (_gate)
        {
            from.LastActive = b.Active ?? from.LastActive;
            from.LastSync = DateTimeOffset.Now;
        }
        _file.Save();
        return n;
    }

    private void Delivered(SyncPeer peer, long upTo)
    {
        lock (_gate) peer.SentSeq = Math.Max(peer.SentSeq, upTo);
        _file.Save();
    }

    private SyncPeer? FindPeer(string id)
    {
        lock (_gate) return _file.Data.Peers.FirstOrDefault(p => p.Id == id);
    }

    private byte[]? PeerKey(string id) =>
        _store.Get(KeyPrefix + id) is { Length: > 0 } k ? Convert.FromBase64String(k) : null;

    public void Flush() => _file.SaveNow();

    public void Unpair(string id)
    {
        lock (_gate) _file.Data.Peers.RemoveAll(p => p.Id == id);
        _store.Set(KeyPrefix + id, null);
        _file.Save();
        Changed?.Invoke();
    }

    /// <summary>Нужно ли звенеть напоминанию на этом устройстве (настройка «где звенеть»).</summary>
    public bool ShouldRingHere(DateTimeOffset now)
    {
        var where = _settings.Current.Sync.RingOn;
        List<SyncPeer> peers;
        lock (_gate) peers = _file.Data.Peers.ToList();
        if (peers.Count == 0 || where == "both") return true;
        if (where == Platform switch { "desktop" => "pc", _ => "phone" }) return true;
        if (where != "active") return false;
        // «там, где я сейчас»: здесь — если активен последние 5 минут или второе устройство давно не видело пользователя
        var here = LastActive?.Invoke();
        if (here is { } h && now - h < TimeSpan.FromMinutes(5)) return true;
        var other = peers.Max(p => p.LastActive);
        return other is null || now - other.Value > TimeSpan.FromMinutes(5);
    }

    private static JsonNode? ToNode(SyncBatch b) => JsonSerializer.SerializeToNode(b, VeraJson.Options);
    private static SyncBatch FromNode(JsonNode? n) =>
        n.Deserialize<SyncBatch>(VeraJson.Options) ?? throw new SyncException("Пустой пакет синхронизации.");

    // ================================================================== привязка: ПК

    /// <summary>ПК: начать привязку телефона — вернуть данные для QR-кода (действуют 10 минут, один раз).</summary>
    public PairingOffer StartPairing(IEnumerable<string> hosts, int port)
    {
        var token = RandomNumberGenerator.GetBytes(16);
        var key = SyncPairing.NewKey();
        var expires = DateTimeOffset.Now + OfferLife;
        lock (_gate)
        {
            _offer?.Key.Dispose();
            _offer = (token, key, expires);
        }
        var fp = SyncPairing.Fingerprint(SyncPairing.Public(key));
        var uri = "vera-sync://pair?d=" + DeviceId + "&n=" + Uri.EscapeDataString(DeviceName) +
                  "&h=" + string.Join(",", hosts) + "&p=" + port + "&t=" + SyncPairing.B64Url(token) + "&f=" + fp;
        return new PairingOffer(uri, fp, expires);
    }

    public void CancelPairing()
    {
        lock (_gate) { _offer?.Key.Dispose(); _offer = null; }
    }

    private JsonObject AcceptPair(JsonObject req)
    {
        (byte[] Token, ECDiffieHellman Key, DateTimeOffset Expires) offer;
        lock (_gate)
        {
            if (_offer is not { } o || o.Expires < DateTimeOffset.Now)
                return new JsonObject { ["t"] = "error", ["msg"] = "Код привязки устарел — на ПК нажмите «Привязать телефон» ещё раз." };
            offer = o;
        }
        var dev = req.Str("dev");
        var pub = SyncPairing.FromB64Url(req.Str("pub"));
        var mac = SyncPairing.FromB64Url(req.Str("mac"));
        if (!CryptographicOperations.FixedTimeEquals(mac, SyncPairing.Mac(offer.Token, "pair|", pub, Encoding.UTF8.GetBytes(dev))))
            return new JsonObject { ["t"] = "error", ["msg"] = "Неверный код привязки." };
        var shared = SyncPairing.SharedKey(offer.Key, pub, offer.Token);
        var myPub = SyncPairing.Public(offer.Key);
        _store.Set(KeyPrefix + dev, Convert.ToBase64String(shared));
        lock (_gate)
        {
            _file.Data.Peers.RemoveAll(p => p.Id == dev);
            _file.Data.Peers.Add(new SyncPeer { Id = dev, Name = req.Str("name"), Platform = req.Str("platform") });
            offer.Key.Dispose();
            _offer = null;
        }
        // при привязке главный — ПК: его общие настройки новее только что установленного телефона
        StampShared();
        _file.Save();
        Changed?.Invoke();
        return new JsonObject
        {
            ["t"] = "paired", ["dev"] = DeviceId, ["name"] = DeviceName, ["platform"] = Platform,
            ["pub"] = SyncPairing.B64Url(myPub),
            ["mac"] = SyncPairing.B64Url(SyncPairing.Mac(offer.Token, "ok|", myPub, pub)),
        };
    }

    // ================================================================== сервер (ПК)

    /// <summary>ПК: обслужить одно подключение телефона (привязка или обмен изменениями).</summary>
    public async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using var ch = new SyncChannel(client);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var first = await ch.ReceiveAsync(token).ConfigureAwait(false);
        switch (first.Str("t"))
        {
            case "pair":
                await ch.SendAsync(AcceptPair(first), token).ConfigureAwait(false);
                return;
            case "hello":
                break;
            default:
                return;
        }
        var peer = FindPeer(first.Str("dev"));
        var key = peer is null ? null : PeerKey(peer.Id);
        if (peer is null || key is null)
        {
            await ch.SendAsync(new JsonObject { ["t"] = "error", ["msg"] = "Этот телефон не привязан к ПК — привяжите заново." }, token).ConfigureAwait(false);
            return;
        }
        var nc = SyncPairing.FromB64Url(first.Str("nonce"));
        var ns = RandomNumberGenerator.GetBytes(16);
        await ch.SendAsync(new JsonObject { ["t"] = "hello", ["nonce"] = SyncPairing.B64Url(ns), ["name"] = DeviceName }, token).ConfigureAwait(false);
        ch.StartSecure(key, nc, ns, isClient: false);

        var msg = await ch.ReceiveAsync(token).ConfigureAwait(false);
        if (msg.Str("t") != "sync") return;
        await _exchange.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var incoming = FromNode(msg["batch"]);
            var received = Apply(incoming, peer);
            var mine = BuildBatch(peer);
            await ch.SendAsync(new JsonObject { ["t"] = "sync", ["batch"] = ToNode(mine) }, token).ConfigureAwait(false);
            var ack = await ch.ReceiveAsync(token).ConfigureAwait(false);
            if (ack.Str("t") == "ack") Delivered(peer, mine.UpTo);
            var sent = mine.Reminders.Count + mine.Memory.Count + mine.Deleted.Count + (mine.Chat?.Count ?? 0) + (mine.Shared is null ? 0 : 1);
            Synced?.Invoke(new SyncResult(peer.Name, received, sent, DateTimeOffset.Now));
            Changed?.Invoke();
        }
        finally { _exchange.Release(); }
    }

    // ================================================================== клиент (телефон)

    /// <summary>Телефон: привязаться к ПК по содержимому QR-кода и сразу синхронизироваться.</summary>
    public async Task<SyncPeer> PairAsync(string qr, CancellationToken ct = default)
    {
        var q = ParseQr(qr);
        using var key = SyncPairing.NewKey();
        var myPub = SyncPairing.Public(key);
        var (ch, host) = await ConnectAsync(q.Hosts, q.Port, q.Id, ct).ConfigureAwait(false);
        using (ch)
        {
            await ch.SendAsync(new JsonObject
            {
                ["t"] = "pair", ["dev"] = DeviceId, ["name"] = DeviceName, ["platform"] = Platform,
                ["pub"] = SyncPairing.B64Url(myPub),
                ["mac"] = SyncPairing.B64Url(SyncPairing.Mac(q.Token, "pair|", myPub, Encoding.UTF8.GetBytes(DeviceId))),
            }, ct).ConfigureAwait(false);
            var resp = await ch.ReceiveAsync(ct).ConfigureAwait(false);
            if (resp.Str("t") == "error") throw new SyncException(resp.Str("msg"));
            var pcPub = SyncPairing.FromB64Url(resp.Str("pub"));
            if (SyncPairing.Fingerprint(pcPub) != q.Fingerprint || resp.Str("dev") != q.Id)
                throw new SyncException("Ответил не тот компьютер — привязка отменена.");
            var mac = SyncPairing.FromB64Url(resp.Str("mac"));
            if (!CryptographicOperations.FixedTimeEquals(mac, SyncPairing.Mac(q.Token, "ok|", pcPub, myPub)))
                throw new SyncException("ПК не подтвердил код привязки.");
            _store.Set(KeyPrefix + q.Id, Convert.ToBase64String(SyncPairing.SharedKey(key, pcPub, q.Token)));
        }
        var peer = new SyncPeer
        {
            Id = q.Id, Name = string.IsNullOrWhiteSpace(q.Name) ? "ПК" : q.Name, Platform = "desktop",
            Hosts = q.Hosts.OrderBy(h => h == host ? 0 : 1).ToList(), Port = q.Port,
        };
        lock (_gate)
        {
            _file.Data.Peers.RemoveAll(p => p.Id == peer.Id);
            _file.Data.Peers.Add(peer);
        }
        _file.Save();
        Changed?.Invoke();
        await SyncAsync(peer, ct).ConfigureAwait(false);
        return peer;
    }

    /// <summary>Телефон: обменяться изменениями с ПК.</summary>
    public async Task<SyncResult> SyncAsync(SyncPeer pc, CancellationToken ct = default)
    {
        var key = PeerKey(pc.Id) ?? throw new SyncException("Нет ключа этого ПК — привяжите заново.");
        await _exchange.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (ch, host) = await ConnectAsync(pc.Hosts, pc.Port, pc.Id, ct).ConfigureAwait(false);
            using (ch)
            {
                if (pc.Hosts.FirstOrDefault() != host)
                    lock (_gate) { pc.Hosts.Remove(host); pc.Hosts.Insert(0, host); }
                var nc = RandomNumberGenerator.GetBytes(16);
                await ch.SendAsync(new JsonObject { ["t"] = "hello", ["dev"] = DeviceId, ["nonce"] = SyncPairing.B64Url(nc) }, ct).ConfigureAwait(false);
                var hello = await ch.ReceiveAsync(ct).ConfigureAwait(false);
                if (hello.Str("t") == "error") throw new SyncException(hello.Str("msg"));
                ch.StartSecure(key, nc, SyncPairing.FromB64Url(hello.Str("nonce")), isClient: true);

                var mine = BuildBatch(pc);
                await ch.SendAsync(new JsonObject { ["t"] = "sync", ["batch"] = ToNode(mine) }, ct).ConfigureAwait(false);
                var resp = await ch.ReceiveAsync(ct).ConfigureAwait(false);
                if (resp.Str("t") != "sync") throw new SyncException("ПК не принял синхронизацию.");
                Delivered(pc, mine.UpTo);
                var theirs = FromNode(resp["batch"]);
                var received = Apply(theirs, pc);
                await ch.SendAsync(new JsonObject { ["t"] = "ack", ["upTo"] = theirs.UpTo }, ct).ConfigureAwait(false);
                var sent = mine.Reminders.Count + mine.Memory.Count + mine.Deleted.Count + (mine.Chat?.Count ?? 0) + (mine.Shared is null ? 0 : 1);
                var result = new SyncResult(pc.Name, received, sent, DateTimeOffset.Now);
                Synced?.Invoke(result);
                Changed?.Invoke();
                return result;
            }
        }
        finally { _exchange.Release(); }
    }

    /// <summary>Подключиться к ПК: адреса из QR / последний удачный, иначе поиск в сети.</summary>
    private static async Task<(SyncChannel Channel, string Host)> ConnectAsync(IReadOnlyList<string> hosts, int port, string pcId, CancellationToken ct)
    {
        foreach (var h in hosts)
        {
            if (await TryConnect(h, port, ct).ConfigureAwait(false) is { } c) return (new SyncChannel(c), h);
        }
        var found = await SyncNet.DiscoverAsync(pcId, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (found is { } f && await TryConnect(f.Host, f.Port, ct).ConfigureAwait(false) is { } c2) return (new SyncChannel(c2), f.Host);
        throw new SyncException("ПК не найден в сети. Он включён, Вера запущена, телефон в той же Wi-Fi сети?");
    }

    private static async Task<TcpClient?> TryConnect(string host, int port, CancellationToken ct)
    {
        var c = new TcpClient();
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(3));
            await c.ConnectAsync(host, port, t.Token).ConfigureAwait(false);
            return c;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            c.Dispose();
            return null;
        }
    }

    internal sealed record Qr(string Id, string Name, List<string> Hosts, int Port, byte[] Token, string Fingerprint);

    internal static Qr ParseQr(string text)
    {
        var t = (text ?? "").Trim();
        var i = t.IndexOf('?');
        if (!t.StartsWith("vera-sync://pair", StringComparison.OrdinalIgnoreCase) || i < 0)
            throw new SyncException("Это не QR-код привязки Веры.");
        var q = t[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        try
        {
            return new Qr(q["d"], q.GetValueOrDefault("n", "ПК"),
                q["h"].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(), int.Parse(q["p"]),
                SyncPairing.FromB64Url(q["t"]), q["f"]);
        }
        catch (Exception e) when (e is KeyNotFoundException or FormatException)
        {
            throw new SyncException("QR-код привязки повреждён.", e);
        }
    }
}
