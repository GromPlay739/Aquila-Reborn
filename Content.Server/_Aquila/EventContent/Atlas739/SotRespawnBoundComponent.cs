// Работа выполнена Claude (Anthropic).

namespace Content.Server._Aquila.EventContent.Atlas739;

/// <summary>
///     Висит на игроке, привязанном к платформе воскрешения (SotRespawnPad). Добавляется и снимается системой
///     SotRespawnSystem при взаимодействии с платформой. Игрок может быть привязан к одной статичной платформе
///     (на базе) и к одной судовой (на шаттле) одновременно.
/// </summary>
[RegisterComponent]
public sealed partial class SotRespawnBoundComponent : Component
{
    /// <summary>Статичная платформа на базе, к которой привязан игрок (null - не привязан).</summary>
    [ViewVariables]
    public EntityUid? Pad;

    /// <summary>
    ///     Судовая платформа, к которой привязан игрок (null - не привязан). Действует только во время боя
    ///     её шаттла: потом привязка сама снимается.
    /// </summary>
    [ViewVariables]
    public EntityUid? ShipPad;

    /// <summary>Когда сработает автоматическое воскрешение (только при AutoRespawn платформы). null - не запланировано.</summary>
    [ViewVariables]
    public TimeSpan? RespawnAt;

    /// <summary>Время смерти (CurTime сервера). null - игрок жив. От него считается задержка действия призрака.</summary>
    [ViewVariables]
    public TimeSpan? DiedAt;

    /// <summary>Призрак игрока, получивший действие воскрешения.</summary>
    [ViewVariables]
    public EntityUid? Ghost;

    /// <summary>Действие воскрешения, выданное призраку.</summary>
    [ViewVariables]
    public EntityUid? GhostAction;
}
