namespace Vera.Core.Sprites;

/// <summary>Послойный скин (мимика собирается из слоёв) или покадровый (каждая эмоция — готовая поза).</summary>
public enum SkinKind { Layered, Frames }

/// <summary>Скин маскота. PixelArt — масштабировать без сглаживания.</summary>
public sealed record SkinInfo(string Id, string Title, SkinKind Kind, bool PixelArt, string Folder);

/// <summary>
/// Готовая поза покадрового скина. Height — высота относительно стоящей позы (1.0).
/// Folder — папка, если поза взята у другого скина (у «чиби» нет своих поз «думает» и «спит»).
/// </summary>
public sealed record FramePose(string Name, double Height, string? Folder = null);

/// <summary>Скины маскота и выбор позы для покадровых скинов.</summary>
public static class Skins
{
    public static readonly IReadOnlyList<SkinInfo> All = new[]
    {
        new SkinInfo("vera", "Пиксельная Вера", SkinKind.Layered, true, "assets/vera"),
        new SkinInfo("halo", "Halo (твой рисунок)", SkinKind.Frames, false, "assets/skins/halo"),
        new SkinInfo("chibi", "Halo — чиби-стикеры", SkinKind.Frames, false, "assets/skins/chibi"),
    };

    /// <summary>Позы скина «чиби» (все сидячие, одной высоты).</summary>
    public static readonly IReadOnlyList<string> ChibiPoses = new[] { "talk", "laugh", "surprised", "angry", "wave" };

    /// <summary>Тёплая тема «золото нимба» для скинов halo и чиби, розовая — для пиксельной Веры.</summary>
    public static string AccentFor(string? skin) => skin is "halo" or "chibi" ? "gold" : "pink";

    /// <summary>Поза покадрового скина по эмоции и состоянию.</summary>
    public static FramePose Pose(string skin, string emotion, PetMode mode, Posture posture, bool dragging = false, bool mouthOpen = false) =>
        skin == "chibi" ? ChibiPose(emotion, mode, posture, dragging, mouthOpen) : HaloPose(emotion, mode, posture, dragging);

    /// <summary>
    /// «Чиби»: говорит — рот открыт (talk) / закрыт (поза эмоции), смеётся и смущается — laugh, удивлена — surprised,
    /// злится — angry, спокойна и дразнится — wave. «Думает», «спит», «грустит» берутся у скина halo.
    /// </summary>
    public static FramePose ChibiPose(string emotion, PetMode mode, Posture posture, bool dragging = false, bool mouthOpen = false)
    {
        const string halo = "assets/skins/halo";
        const double h = 0.9;
        if (dragging) return new FramePose("surprised", h);
        if (mode is PetMode.Thinking or PetMode.Processing || (emotion == "think" && mode != PetMode.Talking))
            return new FramePose("think_sit", HaloPoses["think_sit"], halo);
        if (mode == PetMode.Sleeping || (emotion == "sleepy" && mode != PetMode.Talking))
            return new FramePose("lie", HaloPoses["lie"], halo);
        if (mode is PetMode.Listening or PetMode.Recording) return new FramePose("surprised", h);
        if (mode == PetMode.Talking && mouthOpen) return new FramePose("talk", h);
        var name = emotion switch
        {
            "happy" or "laugh" or "shy" => "laugh",
            "surprised" => "surprised",
            "angry" => "angry",
            "sad" => "",
            _ => "wave",
        };
        if (name.Length == 0) return new FramePose("sit", HaloPoses["sit"], halo);
        return new FramePose(name, h);
    }

    public static SkinInfo Get(string? id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];

    /// <summary>Позы скина «halo» (имена файлов) с относительной высотой персонажа.</summary>
    public static readonly IReadOnlyDictionary<string, double> HaloPoses = new Dictionary<string, double>
    {
        ["shrug"] = 1.0,
        ["laugh"] = 0.96,
        ["giggle"] = 0.86,
        ["think_sit"] = 0.9,
        ["sit"] = 0.72,
        ["lie"] = 0.52,
    };

    /// <summary>
    /// Поза «halo» по эмоции и состоянию:
    /// думает → think_sit; спит/сонная → lie; на полу или на панели → sit; радость/смех → laugh;
    /// ухмылка/смущение (дразнится) → giggle; остальное (спокойно, говорит, слушает, удивлена…) → shrug.
    /// </summary>
    public static FramePose HaloPose(string emotion, PetMode mode, Posture posture, bool dragging = false)
    {
        string name;
        if (dragging) name = "shrug";
        else if (mode is PetMode.Thinking or PetMode.Processing) name = "think_sit";
        else if (mode == PetMode.Sleeping || emotion == "sleepy") name = "lie";
        else if (mode is PetMode.Listening or PetMode.Recording) name = "shrug";
        else if (emotion is "happy" or "laugh") name = "laugh";
        else if (emotion is "smug" or "shy") name = "giggle";
        else if (emotion == "think") name = "think_sit";
        else if (posture is Posture.Floor or Posture.Bar && mode != PetMode.Talking) name = "sit";
        else name = "shrug";
        return new FramePose(name, HaloPoses[name]);
    }
}
