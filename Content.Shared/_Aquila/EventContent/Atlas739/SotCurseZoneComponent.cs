// Работа выполнена Claude (Anthropic).
// SPDX-FileCopyrightText: 2026 GromPlay739
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._Aquila.EventContent.Atlas739;

/// <summary>
///     Маркер зоны проклятия SoT. Ставится в маппинге на плитку (как AtmosFix-маркеры) и отмечает её как часть зоны:
///     действие "Проклясть" (SotCurseCaster) срабатывает только когда нужная сторона стоит на плитке с таким маркером
///     (см. SotCurseCaster.ZoneRule). Зону образуют все плитки, на которых стоят маркеры. В игре маркер не виден.
///     Маркер привязан к сетке (кораблю), поэтому зона двигается и поворачивается вместе с ней.
/// </summary>
[RegisterComponent]
[Access(typeof(SharedSotCurseSystem))]
public sealed partial class SotCurseZoneComponent : Component;
