namespace Vera.Core.Sprites;

/// <summary>Режим питомца для выбора кадра.</summary>
public enum PetMode { Idle, Listening, Recording, Processing, Thinking, Talking, Sleeping }

/// <summary>Поза тела: стоит (ПК), сидит на панели чата (Android), сидит на полу (ПК, когда скучно).</summary>
public enum Posture { Stand, Bar, Floor }

/// <summary>Слои кадра (имена PNG без расширения из assets/vera), рисовать по порядку.</summary>
public sealed record SpriteFrame(IReadOnlyList<string> Layers)
{
    public string Key => string.Join("|", Layers);
}

/// <summary>
/// Общая логика мимики Веры для ПК и Android. Спрайт 64×72, пиксель-арт (масштабировать без сглаживания).
/// Стоя: base_&lt;pose&gt;, eyes_*, mouth_*, blush_*, front, hand_&lt;pose&gt;.
/// Сидя (мобильный чат): legs_sit_&lt;0..2&gt; ПОД sit_&lt;pose&gt;, затем те же слои.
/// </summary>
public static class VeraSprite
{
    public const int Width = 64;
    public const int Height = 72;
    /// <summary>Строка спрайта, совпадающая с верхним краем панели, на которой сидит Вера (низ юбки).</summary>
    public const int SeatLine = 62;

    private sealed record Emo(string Eyes, string Mouth, string Talk, int Blush, string Pose);

    private static readonly Dictionary<string, Emo> Map = new()
    {
        ["neutral"] = new("normal", "smirk", "open_fang", 0, "idle"),
        ["smug"] = new("smug", "smirk", "open_fang", 1, "giggle"),
        ["happy"] = new("happy", "cat", "laugh", 1, "wave"),
        ["laugh"] = new("happy", "laugh", "open", 1, "giggle"),
        ["think"] = new("up", "flat", "small_open", 0, "think"),
        ["surprised"] = new("surprised", "o", "small_open", 0, "idle"),
        ["angry"] = new("angry", "pout", "open_fang", 2, "idle"),
        ["shy"] = new("side", "wobbly", "small_open", 2, "idle"),
        ["sad"] = new("side", "pout", "small_open", 0, "idle"),
        ["sleepy"] = new("closed", "cat", "small_open", 0, "idle"),
    };

    private static readonly HashSet<string> Blinkable = new() { "normal", "smug", "side", "up", "angry" };

    public static IReadOnlyCollection<string> Emotions => Map.Keys;

    /// <summary>Все имена файлов слоёв (для предзагрузки).</summary>
    public static IEnumerable<string> AllLayerNames()
    {
        string[] poses = { "idle", "wave", "giggle", "think" };
        foreach (var p in poses) { yield return "base_" + p; yield return "sit_" + p; yield return "hand_" + p; yield return "floor_" + p; }
        foreach (var e in new[] { "normal", "smug", "happy", "closed", "surprised", "angry", "side", "up" }) yield return "eyes_" + e;
        foreach (var m in new[] { "smirk", "cat", "flat", "open", "open_fang", "laugh", "o", "pout", "wobbly", "small_open" }) yield return "mouth_" + m;
        for (var i = 0; i < 3; i++) { yield return "blush_" + i; yield return "legs_sit_" + i; }
        yield return "front";
    }

    /// <param name="emotion">одна из Emotions</param>
    /// <param name="mode">режим (думает/слушает/говорит/спит)</param>
    /// <param name="blink">моргание</param>
    /// <param name="mouthOpen">рот открыт (липсинк)</param>
    /// <param name="dragging">её тащат мышкой (только ПК)</param>
    /// <param name="sitting">сидячая поза на панели чата (Android)</param>
    /// <param name="legFrame">кадр болтания ногами 0..2 (только сидя)</param>
    public static SpriteFrame Compose(string emotion, PetMode mode, bool blink, bool mouthOpen,
        bool dragging = false, bool sitting = false, int legFrame = 0) =>
        Compose(emotion, mode, blink, mouthOpen, dragging, sitting ? Posture.Bar : Posture.Stand, legFrame);

    /// <summary>Кадр с явной позой тела. Пока Веру тащат, она всегда стоит.</summary>
    public static SpriteFrame Compose(string emotion, PetMode mode, bool blink, bool mouthOpen,
        bool dragging, Posture posture, int legFrame = 0)
    {
        if (dragging) posture = Posture.Stand;
        var e = Map.TryGetValue(emotion ?? "neutral", out var v) ? v : Map["neutral"];
        var eyes = e.Eyes; var mouth = e.Mouth; var blush = e.Blush; var pose = e.Pose;

        switch (mode)
        {
            case PetMode.Thinking: pose = "think"; eyes = "up"; mouth = "flat"; break;
            case PetMode.Listening: pose = "idle"; eyes = "normal"; mouth = "smirk"; break;
            case PetMode.Recording: pose = "idle"; eyes = "normal"; mouth = "cat"; break;
            case PetMode.Processing: pose = "think"; eyes = "up"; mouth = "cat"; break;
            case PetMode.Sleeping: pose = "idle"; eyes = "closed"; mouth = "cat"; blush = 0; break;
        }
        if (dragging) { pose = "wave"; eyes = "surprised"; mouth = "o"; }

        var talking = mode == PetMode.Talking;
        if (talking && pose == "giggle") pose = "idle"; // рука закрывает рот — во время речи убираем
        if (talking && mouthOpen) mouth = e.Talk;
        else if (talking && mouth == "laugh") mouth = "cat";
        if (blink && Blinkable.Contains(eyes)) eyes = "closed";

        var layers = new List<string>(7);
        switch (posture)
        {
            case Posture.Bar:
                layers.Add("legs_sit_" + Math.Clamp(legFrame, 0, 2));
                layers.Add("sit_" + pose);
                break;
            case Posture.Floor:
                layers.Add("floor_" + pose);
                break;
            default:
                layers.Add("base_" + pose);
                break;
        }
        layers.Add("eyes_" + eyes);
        layers.Add("mouth_" + mouth);
        layers.Add("blush_" + blush);
        layers.Add("front");
        if (pose is "giggle" or "think") layers.Add("hand_" + pose);
        return new SpriteFrame(layers);
    }
}
