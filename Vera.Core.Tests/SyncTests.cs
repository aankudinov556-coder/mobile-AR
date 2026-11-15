using Vera.Core.Providers;
using Vera.Core.Sync;
using Xunit;

namespace Vera.Core.Tests;

public class SyncTests : IDisposable
{
    private static readonly Random Rnd = new();
    private readonly VeraCore _pc = new(new MemStorage(), new MemSecrets(), null, "desktop");
    private readonly VeraCore _phone = new(new MemStorage(), new MemSecrets(), null, "mobile");
    private readonly SyncServer _server;
    private readonly int _discovery = 52000 + Rnd.Next(5000);

    public SyncTests()
    {
        _server = new SyncServer(_pc.Sync);
        _server.Start(45000 + Rnd.Next(5000), _discovery);
    }

    public void Dispose() => _server.Dispose();

    private string Qr(params string[] hosts) => _pc.Sync.StartPairing(hosts.Length > 0 ? hosts : new[] { "127.0.0.1" }, _server.Port).Uri;

    private async Task<SyncPeer> Pair() => await _phone.Sync.PairAsync(Qr());

    [Fact]
    public async Task PairBringsExistingDataAndSettingsFromPc()
    {
        _pc.Reminders.Add("Выпить воды", DateTimeOffset.Now.AddHours(1));
        _pc.Memory.Add("Любит чай");
        _pc.Settings.Update(s => { s.Persona = "kind"; s.UserName = "Андрей"; });
        _phone.Settings.Update(s => s.UserName = "кто-то");

        var peer = await Pair();

        Assert.Equal(_pc.Sync.DeviceId, peer.Id);
        Assert.Contains(_phone.Reminders.List(), r => r.Text == "Выпить воды");
        Assert.Contains(_phone.Memory.List(), m => m.Text == "Любит чай");
        Assert.Equal("kind", _phone.Settings.Current.Persona);
        Assert.Equal("Андрей", _phone.Settings.Current.UserName);
        Assert.Single(_pc.Sync.Peers);
        Assert.Equal("mobile", _pc.Sync.Peers[0].Platform);
    }

    [Fact]
    public async Task ChangesAndDeletesFlowBothWays()
    {
        var a = _pc.Reminders.Add("Позвонить", DateTimeOffset.Now.AddHours(2));
        var m = _pc.Memory.Add("Кот Барсик");
        var peer = await Pair();

        _phone.Reminders.Add("Купить хлеб", DateTimeOffset.Now.AddHours(3));
        _phone.Memory.Remove(m.Id);
        _pc.Reminders.Update(a.Id, text: "Позвонить маме");
        await _phone.Sync.SyncAsync(peer);

        Assert.Contains(_pc.Reminders.List(), r => r.Text == "Купить хлеб");
        Assert.DoesNotContain(_pc.Memory.List(), x => x.Id == m.Id);
        Assert.Contains(_phone.Reminders.List(), r => r.Id == a.Id && r.Text == "Позвонить маме");

        // удалённое не воскресает при следующих встречах
        await _phone.Sync.SyncAsync(peer);
        Assert.DoesNotContain(_pc.Memory.List(), x => x.Id == m.Id);
        Assert.DoesNotContain(_phone.Memory.List(), x => x.Id == m.Id);
        Assert.Equal(_pc.Reminders.List().Count, _phone.Reminders.List().Count);
    }

    [Fact]
    public async Task LaterEditWins()
    {
        var a = _pc.Reminders.Add("Тренировка", DateTimeOffset.Now.AddHours(2));
        var peer = await Pair();
        _pc.Reminders.Update(a.Id, text: "Тренировка (ПК)");
        await Task.Delay(5);
        _phone.Reminders.Update(a.Id, text: "Тренировка (телефон)");
        await _phone.Sync.SyncAsync(peer);
        Assert.Equal("Тренировка (телефон)", _pc.Reminders.Get(a.Id)!.Text);
        Assert.Equal("Тренировка (телефон)", _phone.Reminders.Get(a.Id)!.Text);
    }

    [Fact]
    public async Task CompleteOnPhoneCancelsOnPc()
    {
        var a = _pc.Reminders.Add("Таблетка", DateTimeOffset.Now.AddMinutes(30));
        var peer = await Pair();
        _phone.Reminders.Complete(a.Id);
        await _phone.Sync.SyncAsync(peer);
        Assert.True(_pc.Reminders.Get(a.Id)!.Done);
    }

    private sealed class EchoProvider : ILlmProvider
    {
        public string Id => "openrouter";
        public string Title => "Эхо";
        public ProviderConversation Begin(IReadOnlyList<VeraMessage> history) => new C();
        public Task<StepResult> StepAsync(StepRequest request, ProviderConversation conv, Action<string> onText, CancellationToken ct)
        {
            onText("Ответ");
            return Task.FromResult(new StepResult { Text = "Ответ" });
        }
        public void AppendToolResults(ProviderConversation conv, IReadOnlyList<ToolExecution> results) { }
        public void AppendUserText(ProviderConversation conv, string text, IReadOnlyList<ImageData>? images = null) { }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());
        public Task<string> TestAsync(CancellationToken ct = default) => Task.FromResult("ok");
        private sealed class C : ProviderConversation { }
    }

    [Fact]
    public async Task ChatTurnsAreMerged()
    {
        foreach (var c in new[] { _pc, _phone })
        {
            c.Providers.Register(new EchoProvider());
            c.Settings.Update(s => s.Provider = "openrouter");
        }
        var peer = await Pair();
        await _pc.Agent.SendAsync("Вопрос с ПК");
        await Task.Delay(5);
        await _phone.Agent.SendAsync("Вопрос с телефона");
        var changed = 0;
        _phone.Agent.HistoryChanged += () => changed++;
        await _phone.Sync.SyncAsync(peer);

        var onPhone = _phone.Agent.GetVisibleHistory().Where(m => m.Role == "user").Select(m => m.Text).ToList();
        var onPc = _pc.Agent.GetVisibleHistory().Where(m => m.Role == "user").Select(m => m.Text).ToList();
        Assert.Equal(new[] { "Вопрос с ПК", "Вопрос с телефона" }, onPhone); // по времени
        Assert.Equal(onPhone, onPc);
        Assert.Equal(1, changed);
        Assert.Equal(4, _pc.Agent.GetVisibleHistory().Count);

        // повторная синхронизация ничего не дублирует
        await _phone.Sync.SyncAsync(peer);
        Assert.Equal(4, _phone.Agent.GetVisibleHistory().Count);
    }

    [Fact]
    public async Task KeysOnlyWhenBothAllow()
    {
        _pc.Secrets.Set("openrouter", "sk-or-test");
        var peer = await Pair();
        Assert.False(_phone.Secrets.Has("openrouter"));

        _pc.Settings.Update(s => s.Sync.Keys = true);
        _phone.Settings.Update(s => s.Sync.Keys = true);
        _pc.Secrets.Set("groq", "gsk-test");
        await _phone.Sync.SyncAsync(peer);
        Assert.Equal("sk-or-test", _phone.Secrets.Get("openrouter"));
        Assert.Equal("gsk-test", _phone.Secrets.Get("groq"));
    }

    [Fact]
    public async Task KeysGoWhenEnabledAfterTheyWereSet()
    {
        _pc.Secrets.Set("openrouter", "sk-or-before");
        _pc.Secrets.Set("openai", "sk-oa-before");
        var peer = await Pair();
        // галочку включили уже после того, как ключи были на ПК, — ключи больше не меняются
        _pc.Settings.Update(s => s.Sync.Keys = true);
        _phone.Settings.Update(s => s.Sync.Keys = true);
        await _phone.Sync.SyncAsync(peer);
        Assert.Equal("sk-or-before", _phone.Secrets.Get("openrouter"));
        Assert.Equal("sk-oa-before", _phone.Secrets.Get("openai"));

        // и в обратную сторону: ключ, который есть только на телефоне
        _phone.Secrets.Set("groq", "gsk-phone");
        await _phone.Sync.SyncAsync(peer);
        Assert.Equal("gsk-phone", _pc.Secrets.Get("groq"));
        // свой ключ не перезаписывается чужим
        _phone.Secrets.Set("openrouter", "sk-or-phone");
        await _phone.Sync.SyncAsync(peer);
        Assert.Equal("sk-or-before", _pc.Secrets.Get("openrouter"));
    }

    [Fact]
    public async Task WrongOrExpiredCodeIsRejected()
    {
        var qr = Qr();
        var bad = qr.Replace("&t=", "&t=AAAA");
        await Assert.ThrowsAsync<SyncException>(() => _phone.Sync.PairAsync(bad));
        await _phone.Sync.PairAsync(qr);
        // код одноразовый
        var other = new VeraCore(new MemStorage(), new MemSecrets(), null, "mobile");
        var e = await Assert.ThrowsAsync<SyncException>(() => other.Sync.PairAsync(qr));
        Assert.Contains("устарел", e.Message);
    }

    [Fact]
    public async Task UnpairedPhoneIsRefused()
    {
        var peer = await Pair();
        _pc.Sync.Unpair(_phone.Sync.DeviceId);
        var e = await Assert.ThrowsAsync<SyncException>(() => _phone.Sync.SyncAsync(peer));
        Assert.Contains("не привязан", e.Message);
    }

    [Fact]
    public async Task FindsPcByDiscoveryWhenAddressChanged()
    {
        var peer = await Pair();
        peer.Hosts.Clear();
        peer.Hosts.Add("10.255.255.1"); // старый адрес — недоступен
        var found = await SyncNet.DiscoverAsync(_pc.Sync.DeviceId, TimeSpan.FromSeconds(2), default, _discovery);
        Assert.NotNull(found);
        Assert.Equal(_server.Port, found!.Value.Port);
    }

    [Fact]
    public void QrIsParsed()
    {
        var qr = Qr("192.168.1.5", "10.0.0.2");
        var q = SyncService.ParseQr(qr);
        Assert.Equal(new[] { "192.168.1.5", "10.0.0.2" }, q.Hosts);
        Assert.Equal(_server.Port, q.Port);
        Assert.Equal(16, q.Token.Length);
        Assert.Throws<SyncException>(() => SyncService.ParseQr("https://example.com"));
    }

    [Fact]
    public async Task RingPolicy()
    {
        Assert.True(_pc.Sync.ShouldRingHere(DateTimeOffset.Now)); // без телефона звенит всегда
        await Pair();
        _pc.Settings.Update(s => s.Sync.RingOn = "phone");
        Assert.False(_pc.Sync.ShouldRingHere(DateTimeOffset.Now));
        _pc.Settings.Update(s => s.Sync.RingOn = "active");
        _pc.Sync.LastActive = () => DateTimeOffset.Now.AddMinutes(-1);
        Assert.True(_pc.Sync.ShouldRingHere(DateTimeOffset.Now));
    }
}
