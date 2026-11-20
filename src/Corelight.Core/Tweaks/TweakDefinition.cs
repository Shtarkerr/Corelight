using System.Text.Json.Serialization;
using Corelight.Core.Actions;

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
    /// <summary>Может отключить функцию, которой кто-то пользуется. Последствия указаны в карточке.</summary>
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

    /// <summary>Официальная страница для ручного возврата приложения.</summary>
    public string? RestoreHelpUrl { get; init; }

    /// <summary>Как вернуть приложение, если автоматическая переустановка недоступна.</summary>
    public string? RestoreHelpText { get; init; }

    /// <summary>Ограничения доступности и возврата приложения, видимые до удаления и в истории.</summary>
    public string? AvailabilityNote { get; init; }

    public TweakRisk Risk { get; init; } = TweakRisk.Safe;

    public TweakImpact Impact { get; init; } = TweakImpact.Low;

    /// <summary>Попадает ли твик в результаты сканирования.</summary>
    public bool Recommended { get; init; }

    /// <summary>Галочка в результатах сканирования; null — по уровню риска. Advanced всегда выбирается вручную.</summary>
    public bool? Preselected { get; init; }

    public bool RequiresReboot { get; init; }
    /// <summary>Одного совпавшего действия достаточно для эффективного состояния настройки.</summary>
    public bool AppliedWhenAnyActionMatches { get; init; }

    /// <summary>Минимальная сборка Windows, на которой твик имеет смысл.</summary>
    public int MinBuild { get; init; }

    /// <summary>Условия по железу, при которых твик рекомендуется (все должны выполняться).</summary>
    public IReadOnlyList<string> RecommendWhen { get; init; } = [];

    /// <summary>Условия, без которых твик неприменим вовсе.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Твики одной группы меняют одно и то же, поэтому одновременно может быть применён только один.</summary>
    public string? ExclusiveGroup { get; init; }

    public IReadOnlyList<TweakAction> Actions { get; init; } = [];

    [JsonIgnore]
    public bool RequiresManualRestore => Actions.Any(a => a is CopilotDesktopAction or AppxPackageAction { StoreId: null });
}
