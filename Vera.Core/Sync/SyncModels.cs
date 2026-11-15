using System.Text.Json.Nodes;
using Vera.Core.Providers;

namespace Vera.Core.Sync;

/// <summary>Запись, которую можно синхронизировать: версия (гибридные часы), устройство, локальный номер изменения.</summary>
public interface ISyncable
{
    long Ver { get; set; }
    string? Dev { get; set; }
    long Seq { get; set; }
}

/// <summary>
/// Гибридные логические часы: версия = микросекунды UTC, но всегда больше всех виденных версий.
/// Так «последнее изменение побеждает» работает даже при разнице часов ПК и телефона.
/// </summary>
public static class SyncClock
{
    /// <summary>a новее b? При равных версиях решает id устройства (детерминированно на обоих концах).</summary>
    public static bool Newer(long aVer, string? aDev, long bVer, string? bDev) =>
        aVer > bVer || (aVer == bVer && string.CompareOrdinal(aDev ?? "", bDev ?? "") > 0);
}

/// <summary>Надгробие: запись удалена (чтобы удаление дошло до другого устройства и запись не «воскресла»).</summary>
public sealed class Tombstone
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public long Ver { get; set; }
    public string? Dev { get; set; }
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Привязанное устройство. Общий ключ хранится в защищённом хранилище ("sync.peer.&lt;id&gt;").</summary>
public sealed class SyncPeer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"desktop" | "mobile"</summary>
    public string Platform { get; set; } = "";
    /// <summary>До какого нашего номера изменения устройство уже получило данные.</summary>
    public long SentSeq { get; set; }
    public DateTimeOffset? LastSync { get; set; }
    /// <summary>Телефон: адреса ПК (из QR и последний удачный) и порт.</summary>
    public List<string> Hosts { get; set; } = new();
    public int Port { get; set; }
    /// <summary>Когда пользователь последний раз был активен на этом устройстве (для «звенеть там, где я»).</summary>
    public DateTimeOffset? LastActive { get; set; }
}

/// <summary>Общие настройки, которые синхронизируются (характер, имя, скин…).</summary>
public sealed class SharedSettings
{
    public string? UserName { get; set; }
    public string Persona { get; set; } = "";
    public string? CustomPrompt { get; set; }
    public string? Skin { get; set; }
    public string RingOn { get; set; } = "both";
    public bool Chat { get; set; } = true;

    public static SharedSettings From(VeraSettings s) => new()
    {
        UserName = s.UserName, Persona = s.Persona, CustomPrompt = s.CustomPrompt, Skin = s.Skin,
        RingOn = s.Sync.RingOn, Chat = s.Sync.Chat,
    };

    public void ApplyTo(VeraSettings s)
    {
        s.UserName = UserName ?? "";
        if (!string.IsNullOrWhiteSpace(Persona)) s.Persona = Persona;
        s.CustomPrompt = CustomPrompt ?? "";
        if (!string.IsNullOrWhiteSpace(Skin)) s.Skin = Skin;
        s.Sync.RingOn = RingOn;
        s.Sync.Chat = Chat;
    }

    public string Key() => System.Text.Json.JsonSerializer.Serialize(this, VeraJson.Options);
}

/// <summary>Состояние синхронизации этого устройства (sync.json).</summary>
public sealed class SyncState
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    /// <summary>Последняя выданная версия (гибридные часы).</summary>
    public long Clock { get; set; }
    /// <summary>Последний выданный локальный номер изменения.</summary>
    public long Seq { get; set; }
    public List<Tombstone> Tombstones { get; set; } = new();
    public List<SyncPeer> Peers { get; set; } = new();

    public string SharedKey { get; set; } = "";
    public long SharedVer { get; set; }
    public string? SharedDev { get; set; }
    public long SharedSeq { get; set; }

    /// <summary>Когда менялся набор ключей API (для передачи по желанию).</summary>
    public long KeysSeq { get; set; }
}

/// <summary>Пакет изменений от одного устройства другому.</summary>
public sealed class SyncBatch
{
    public string From { get; set; } = "";
    /// <summary>Наибольший номер изменения, вошедший в пакет (отправитель запомнит, что он доставлен).</summary>
    public long UpTo { get; set; }
    public List<Reminder> Reminders { get; set; } = new();
    public List<MemoryFact> Memory { get; set; } = new();
    public List<Tombstone> Deleted { get; set; } = new();
    public SharedSettings? Shared { get; set; }
    public long SharedVer { get; set; }
    public string? SharedDev { get; set; }
    public List<List<VeraMessage>>? Chat { get; set; }
    /// <summary>Ключи API (имя → значение) — только если на обоих устройствах включено «передавать ключи».</summary>
    public Dictionary<string, string>? Keys { get; set; }
    /// <summary>Пользователь был активен на устройстве-отправителе в этот момент (для «там, где я сейчас»).</summary>
    public DateTimeOffset? Active { get; set; }

    public bool IsEmpty => Reminders.Count == 0 && Memory.Count == 0 && Deleted.Count == 0 && Shared is null &&
                           Chat is not { Count: > 0 } && Keys is not { Count: > 0 };
}

/// <summary>Итог синхронизации для интерфейса.</summary>
public sealed record SyncResult(string PeerName, int Received, int Sent, DateTimeOffset At);

/// <summary>Что показать в QR-коде на ПК для привязки телефона.</summary>
public sealed record PairingOffer(string Uri, string Code, DateTimeOffset Expires);

internal static class JsonMsg
{
    public static JsonObject Obj(string type) => new() { ["t"] = type };
    public static string Str(this JsonObject o, string k) => o[k]?.GetValue<string>() ?? "";
}
