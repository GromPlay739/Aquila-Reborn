// Работа выполнена Claude (Anthropic).
// SPDX-FileCopyrightText: 2026 GromPlay739
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._Aquila.EventContent.Atlas739;

/// <summary>Где должна находиться сторона, чтобы действие "Проклясть" сработало (см. SotCurseZone).</summary>
[Serializable, NetSerializable]
public enum SotCurseZoneRule : byte
{
    /// <summary>Без ограничений: проклинать можно где угодно.</summary>
    Anywhere,

    /// <summary>Проклинающий должен стоять в зоне.</summary>
    Caster,

    /// <summary>Цель должна стоять в зоне.</summary>
    Target,

    /// <summary>И проклинающий, и цель должны стоять в зоне.</summary>
    Both,
}
