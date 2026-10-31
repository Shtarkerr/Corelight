using System.Text.Json.Serialization;

namespace Corelight.Core.Tweaks;

public enum TweakCategory
{
    Apps,
    Privacy,
    Interface,
    Gaming,
    Services,
    Network,
}

public enum TweakRisk
{
    /// <summary>Ничего заметного для пользователя не ломает.</summary>
    Safe,
    /// <summary>Может отключить функцию, которой кто-то пользуется. В сканировании не отмечается по умолчанию.</summary>
    Caution,
    /// <summary>Только для тех, кто понимает последствия. Перед применением показывается предупреждение.</summary>
    Advanced,
}

public enum TweakImpact
{
    Low,
    Medium,
    High,
}

public sealed class TweakDefinition
{
    public required string Id { get; init; }

    [JsonIgnore]
    public TweakCategory Category { get; set; }

    public required string Title { get; init; }

    /// <summary>1–2 предложения: что это за функция и что пользователь потеряет.</summary>
    public required string Description { get; init; }

    /// <summary>Дополнительное предупреждение, которое показывается перед применением.</summary>
    public string? Warning { get; init; }

    public TweakRisk Risk { get; init; } = TweakRisk.Safe;

    public TweakImpact Impact { get; init; } = TweakImpact.Low;

    /// <summary>Попадает ли твик в результаты сканирования.</summary>
    public bool Recommended { get; init; }

    public bool RequiresReboot { get; init; }

    /// <summary>Минимальная сборка Windows, на которой твик имеет смысл.</summary>
    public int MinBuild { get; init; }

    /// <summary>Условия по железу, при которых твик рекомендуется (все должны выполняться).</summary>
    public IReadOnlyList<string> RecommendWhen { get; init; } = [];

    /// <summary>Условия, без которых твик неприменим вовсе.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Твики одной группы меняют одно и то же, поэтому одновременно может быть применён только один.</summary>
    public string? ExclusiveGroup { get; init; }

    public IReadOnlyList<TweakAction> Actions { get; init; } = [];
}
