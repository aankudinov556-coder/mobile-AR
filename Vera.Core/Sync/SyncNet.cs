using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Vera.Core.Sync;

/// <summary>Сеть: адреса этого ПК, поиск ПК телефоном (широковещательный UDP), TCP-сервер синхронизации.</summary>
public static class SyncNet
{
    /// <summary>UDP-порт поиска ПК в домашней сети.</summary>
    public const int DiscoveryPort = 47321;

    /// <summary>IPv4-адреса этого компьютера в локальной сети: сначала проводная и Wi-Fi, виртуальные — в конце.</summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<(string Ip, int Rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                var rank = name.Contains("virtual") || name.Contains("vethernet") || name.Contains("vmware") || name.Contains("hyper-v") ||
                           name.Contains("vbox") || name.Contains("docker") || name.Contains("wsl") ? 2
                    : ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                    var b = a.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // APIPA — сети нет
                    list.Add((a.Address.ToString(), rank));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // без списка адресов телефон найдёт ПК поиском
        }
        return list.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
    }

    /// <summary>Телефон: найти ПК с этим id в сети. null — не ответил.</summary>
    public static async Task<(string Host, int Port)?> DiscoverAsync(string pcId, TimeSpan timeout, CancellationToken ct, int port = DiscoveryPort)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var ask = Encoding.UTF8.GetBytes("VERA?" + pcId);
        foreach (var target in Broadcasts().Append(IPAddress.Loopback))
        {
            try { await udp.SendAsync(ask, new IPEndPoint(target, port), ct).ConfigureAwait(false); }
            catch (SocketException) { /* интерфейс без широковещания */ }
        }
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(t.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(r.Buffer);
                // VERA!<id>|<port>|<имя>
                if (!text.StartsWith("VERA!", StringComparison.Ordinal)) continue;
                var parts = text[5..].Split('|');
                if (parts.Length < 2 || parts[0] != pcId || !int.TryParse(parts[1], out var tcp)) continue;
                return (r.RemoteEndPoint.Address.ToString(), tcp);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static IEnumerable<IPAddress> Broadcasts()
    {
        yield return IPAddress.Broadcast;
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var ni in all)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask is null) continue;
                var ip = a.Address.GetAddressBytes();
                var mask = a.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bc);
            }
        }
    }
}

/// <summary>
/// ПК: сервер синхронизации в домашней сети. TCP — обмен (шифрованный), UDP — отвечает на поиск телефона.
/// Windows при первом запуске спросит разрешение брандмауэра — нужна «Частная сеть».
/// </summary>
public sealed class SyncServer : IDisposable
{
    private readonly SyncService _sync;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;

    public SyncServer(SyncService sync) => _sync = sync;

    public int Port { get; private set; }
    public bool Running => _tcp is not null;
    public string? Error { get; private set; }

    /// <summary>Запустить на порту (если занят — следующие 10).</summary>
    public void Start(int port, int discoveryPort = SyncNet.DiscoveryPort)
    {
        for (var p = port; p < port + 10 && _tcp is null; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, p);
                l.Start();
                _tcp = l;
                Port = p;
            }
            catch (SocketException e)
            {
                Error = e.Message;
            }
        }
        if (_tcp is null) throw new SyncException("Не удалось открыть порт синхронизации: " + Error);
        Error = null;
        _ = AcceptLoop(_tcp, _cts.Token);
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            _ = DiscoveryLoop(_udp, _cts.Token);
        }
        catch (SocketException e)
        {
            // без поиска телефон подключится по адресу из QR
            Error = "Поиск в сети недоступен: " + e.Message;
            _udp?.Dispose();
            _udp = null;
        }
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                try { await _sync.HandleConnectionAsync(c, ct).ConfigureAwait(false); }
                catch (Exception e) { LastError = e.Message; }
            }, ct);
        }
    }

    /// <summary>Ошибка последнего подключения (для журнала и статуса).</summary>
    public string? LastError { get; private set; }

    private async Task DiscoveryLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (!text.StartsWith("VERA?", StringComparison.Ordinal)) continue;
                var id = text[5..];
                if (id.Length > 0 && id != _sync.DeviceId) continue;
                var reply = Encoding.UTF8.GetBytes($"VERA!{_sync.DeviceId}|{Port}|{_sync.DeviceName}");
                await udp.SendAsync(reply, r.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { /* следующий запрос */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _tcp?.Stop(); } catch (SocketException) { }
        _udp?.Dispose();
        _tcp = null;
        _udp = null;
        _cts.Dispose();
    }
}
