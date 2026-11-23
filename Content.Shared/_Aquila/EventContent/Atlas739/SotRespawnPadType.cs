// Работа выполнена Claude (Anthropic).

using Robust.Shared.Serialization;

namespace Content.Shared._Aquila.EventContent.Atlas739;

/// <summary>Тип платформы воскрешения SoT (см. SotRespawnPad).</summary>
[Serializable, NetSerializable]
public enum SotRespawnPadType : byte
{
    /// <summary>
    ///     Статичная платформа на базе: работает всегда и является точкой воскрешения вне боёв.
    /// </summary>
    Static,

    /// <summary>
    ///     Судовая платформа на шаттле: работает только пока идёт бой, в котором участвует её шаттл.
    ///     Когда этот бой заканчивается, платформа отключается, а воскрешение идёт на статичных платформах.
    /// </summary>
    Ship,
}
