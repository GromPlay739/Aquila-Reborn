// Работа выполнена Claude (Anthropic).

using Content.Shared._Aquila.EventContent.Atlas739;
using Content.Shared.Actions;
using Content.Shared.Administration.Systems;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Content.Shared.Interaction;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Popups;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Timing;

namespace Content.Server._Aquila.EventContent.Atlas739;

/// <summary>
///     Платформы воскрешения. Привязка: клик по платформе (повторный клик отвязывает).
///
///     Два типа платформ (SotRespawnPad.PadType):
///       - Static: на базе, работает всегда, это точка воскрешения вне боёв;
///       - Ship: на шаттле, работает только пока идёт бой, в котором участвует этот шаттл (SotRuleSystem.IsGridInActiveBattle).
///         Когда бой заканчивается, платформа отключается, привязки к ней снимаются, а воскрешение идёт на статичных
///         платформах. Отключаются только платформы на шаттлах именно этого боя: активность считается по гриду платформы.
///     Куда воскрешать, решается в момент воскрешения: включённая судовая платформа игрока, иначе его статичная
///     платформа, иначе любая статичная платформа его фракции.
///
///     Смерть привязанного игрока запоминается (DiedAt). Дальше два режима (SotRespawnPad.AutoRespawn):
///       - false (по умолчанию): когда игрок становится призраком, он получает действие "Воскреснуть на платформе".
///         Оно становится доступным через ActionDelay (10 с) после смерти и воскрешает ЗА ОДИН ШАГ: тело переносится
///         на платформу, полностью лечится, душа возвращается в тело;
///       - true: воскрешение срабатывает само через Delay после смерти.
///     Если тело уничтожено (разорвано), воскрешать нечего: привязка пропадает вместе с ним.
///     Гиб игрокам с SotFaction запрещён отдельной системой SotFactionSystem.
/// </summary>
public sealed class SotRespawnSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly RejuvenateSystem _rejuvenate = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SotRuleSystem _rule = default!;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);
    private TimeSpan _nextCheck;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SotRespawnPadComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<SotRespawnPadComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<SotRespawnPadComponent, ComponentShutdown>(OnPadShutdown);
        SubscribeLocalEvent<SotRespawnBoundComponent, ComponentShutdown>(OnBoundShutdown);
        SubscribeLocalEvent<SotRespawnBoundComponent, MobStateChangedEvent>(OnMobStateChanged);

        // Действие призрака. Подписка на GhostComponent: событие приходит на призрака (исполнителя).
        SubscribeLocalEvent<GhostComponent, SotRespawnActionEvent>(OnRespawnAction);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextCheck)
            return;

        _nextCheck = now + CheckInterval;

        // Судовые платформы включаются и отключаются вместе с боями своих шаттлов.
        UpdatePads();

        var query = EntityQueryEnumerator<SotRespawnBoundComponent>();
        while (query.MoveNext(out var uid, out var bound))
        {
            // Автоматическое воскрешение.
            if (bound.RespawnAt is { } at && now >= at)
            {
                Respawn(uid, bound);
                continue;
            }

            // Выдача действия призраку: игрок мёртв, стал призраком-посетителем, действия у него ещё нет.
            if (bound.DiedAt != null && bound.GhostAction == null)
                TryGrantAction(uid, bound);
        }
    }

    #region Платформы

    /// <summary>Включена ли платформа: статичная всегда, судовая - пока идёт бой шаттла, на котором она стоит.</summary>
    private bool IsPadActive(Entity<SotRespawnPadComponent> pad)
    {
        if (pad.Comp.PadType == SotRespawnPadType.Static)
            return true;

        return Transform(pad).GridUid is { } grid && _rule.IsGridInActiveBattle(grid);
    }

    /// <summary>
    ///     Раз в полсекунды сверяет состояние платформ с боями. Платформа, чей шаттл больше не воюет, отключается:
    ///     меняется её внешний вид, привязанные игроки отвязываются. Платформы на других шаттлах не затрагиваются.
    /// </summary>
    private void UpdatePads()
    {
        var query = EntityQueryEnumerator<SotRespawnPadComponent>();
        while (query.MoveNext(out var uid, out var pad))
        {
            var ent = new Entity<SotRespawnPadComponent>(uid, pad);
            var active = IsPadActive(ent);
            if (pad.Active == active)
                continue;

            pad.Active = active;
            _appearance.SetData(uid, SotRespawnPadVisuals.Active, active);

            if (!active)
                DisableShipPad(ent);
        }
    }

    /// <summary>
    ///     Судовая платформа отключилась (бой закончился): снимаем привязки игроков к ней и предупреждаем их.
    ///     Игрок остаётся привязанным к своей статичной платформе, а если её нет, воскрешается на запасной.
    /// </summary>
    private void DisableShipPad(Entity<SotRespawnPadComponent> pad)
    {
        foreach (var user in new List<EntityUid>(pad.Comp.Bound))
        {
            pad.Comp.Bound.Remove(user);

            if (!TryComp<SotRespawnBoundComponent>(user, out var bound) || bound.ShipPad != pad.Owner)
                continue;

            bound.ShipPad = null;

            // Умершему игроку сообщение показываем на призраке: тело игрок уже не видит.
            var recipient = bound.Ghost is { } ghost && !TerminatingOrDeleted(ghost) ? ghost : user;
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-ship-disabled"), user, recipient, PopupType.Large);

            // Воскрешение теперь пойдёт на другую платформу: пересчитываем задержки и перевыдаём действие призраку.
            RemoveGhostAction(bound);
            ScheduleAutoRespawn(user, bound);
        }

        UpdateVisual(pad);
    }

    #endregion

    #region Привязка

    private void OnActivate(Entity<SotRespawnPadComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        var user = args.User;
        var pad = ent.Comp;

        // Платформа фракции: привязаться могут только её члены (компонент SotFaction).
        if (pad.Side is { } side
            && !(TryComp<SotFactionComponent>(user, out var faction) && faction.Side == side))
        {
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-wrong-faction"), ent, user);
            return;
        }

        // Судовая платформа работает только во время боя своего шаттла.
        if (!IsPadActive(ent))
        {
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-ship-inactive"), ent, user);
            return;
        }

        // Повторный клик отвязывает.
        if (pad.Bound.Contains(user))
        {
            Unbind(user, ent);
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-unbound"), ent, user);
            return;
        }

        if (pad.Bound.Count >= pad.MaxBound)
        {
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-full"), ent, user);
            return;
        }

        var bound = EnsureComp<SotRespawnBoundComponent>(user);

        // Привязан к другой платформе того же типа: перепривязываем сюда.
        var previous = pad.PadType == SotRespawnPadType.Ship ? bound.ShipPad : bound.Pad;
        if (previous is { } old && old != ent.Owner)
            RemoveFromPad(user, old);

        if (pad.PadType == SotRespawnPadType.Ship)
            bound.ShipPad = ent.Owner;
        else
            bound.Pad = ent.Owner;

        pad.Bound.Add(user);
        UpdateVisual(ent);

        _popup.PopupEntity(Loc.GetString("sot-respawn-popup-bound"), ent, user);
    }

    private void OnExamined(Entity<SotRespawnPadComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("sot-respawn-examine",
            ("count", ent.Comp.Bound.Count),
            ("max", ent.Comp.MaxBound)));

        var typeKey = ent.Comp.PadType == SotRespawnPadType.Static
            ? "sot-respawn-examine-static"
            : IsPadActive(ent) ? "sot-respawn-examine-ship-active" : "sot-respawn-examine-ship-inactive";
        args.PushMarkup(Loc.GetString(typeKey));

        foreach (var bound in ent.Comp.Bound)
        {
            args.PushMarkup(Loc.GetString("sot-respawn-examine-bound", ("name", Name(bound))));
        }
    }

    /// <summary>
    ///     Платформа удалена: привязки к ней снимаются. Игрок, у которого не осталось ни одной платформы, теряет
    ///     привязку целиком (если удалена статичная); при потере судовой он остаётся с запасной точкой воскрешения.
    /// </summary>
    private void OnPadShutdown(Entity<SotRespawnPadComponent> ent, ref ComponentShutdown args)
    {
        foreach (var user in new List<EntityUid>(ent.Comp.Bound))
        {
            if (!TryComp<SotRespawnBoundComponent>(user, out var bound))
                continue;

            if (bound.Pad == ent.Owner)
                bound.Pad = null;

            if (bound.ShipPad == ent.Owner)
                bound.ShipPad = null;

            if (bound.Pad == null && bound.ShipPad == null && ent.Comp.PadType == SotRespawnPadType.Static)
                RemCompDeferred<SotRespawnBoundComponent>(user);
        }

        ent.Comp.Bound.Clear();
    }

    /// <summary>Игрок отвязан или удалён: убираем его из списков платформ и забираем действие у призрака.</summary>
    private void OnBoundShutdown(Entity<SotRespawnBoundComponent> ent, ref ComponentShutdown args)
    {
        RemoveGhostAction(ent.Comp);
        RemoveFromPad(ent.Owner, ent.Comp.Pad);
        RemoveFromPad(ent.Owner, ent.Comp.ShipPad);
    }

    /// <summary>Игрок сам отвязывается от платформы (повторный клик).</summary>
    private void Unbind(EntityUid user, Entity<SotRespawnPadComponent> pad)
    {
        pad.Comp.Bound.Remove(user);
        UpdateVisual(pad);

        if (!TryComp<SotRespawnBoundComponent>(user, out var bound))
            return;

        if (bound.Pad == pad.Owner)
            bound.Pad = null;

        if (bound.ShipPad == pad.Owner)
            bound.ShipPad = null;

        // Последняя привязка снята вручную: воскрешение игроку больше не нужно.
        if (bound.Pad == null && bound.ShipPad == null)
            RemComp<SotRespawnBoundComponent>(user);
    }

    /// <summary>Убирает игрока из списка платформы (сам компонент игрока не трогает).</summary>
    private void RemoveFromPad(EntityUid user, EntityUid? padUid)
    {
        if (padUid is not { } uid || !TryComp<SotRespawnPadComponent>(uid, out var pad))
            return;

        pad.Bound.Remove(user);
        UpdateVisual(new Entity<SotRespawnPadComponent>(uid, pad));
    }

    private void UpdateVisual(Entity<SotRespawnPadComponent> pad)
    {
        if (TerminatingOrDeleted(pad))
            return;

        _appearance.SetData(pad, SotRespawnPadVisuals.Bound, pad.Comp.Bound.Count > 0);
    }

    #endregion

    #region Выбор платформы воскрешения

    private bool TryGetPad(EntityUid? uid, out Entity<SotRespawnPadComponent> pad)
    {
        if (uid is { } padUid && !TerminatingOrDeleted(padUid) && TryComp<SotRespawnPadComponent>(padUid, out var comp))
        {
            pad = new Entity<SotRespawnPadComponent>(padUid, comp);
            return true;
        }

        pad = default;
        return false;
    }

    /// <summary>
    ///     Куда воскрешать игрока прямо сейчас: включённая судовая платформа, иначе его статичная платформа,
    ///     иначе любая статичная платформа его фракции. Считается в момент использования, поэтому после конца боя
    ///     воскрешение сразу идёт на базу.
    /// </summary>
    private bool TryGetRespawnPad(EntityUid body, SotRespawnBoundComponent bound, out Entity<SotRespawnPadComponent> pad)
    {
        if (TryGetPad(bound.ShipPad, out var ship) && IsPadActive(ship))
        {
            pad = ship;
            return true;
        }

        if (TryGetPad(bound.Pad, out var home))
        {
            pad = home;
            return true;
        }

        return TryFindFallbackPad(body, out pad);
    }

    /// <summary>
    ///     Запасная точка: самая свободная статичная платформа фракции игрока (платформа без фракции подходит всем).
    ///     Нужна игроку, который привязывался только к судовой платформе, когда бой закончился.
    ///     Лимит MaxBound тут не учитывается: он ограничивает привязку, а не воскрешение.
    /// </summary>
    private bool TryFindFallbackPad(EntityUid body, out Entity<SotRespawnPadComponent> found)
    {
        found = default;
        var best = int.MaxValue;
        TryComp<SotFactionComponent>(body, out var faction);

        var query = EntityQueryEnumerator<SotRespawnPadComponent>();
        while (query.MoveNext(out var uid, out var pad))
        {
            if (pad.PadType != SotRespawnPadType.Static || TerminatingOrDeleted(uid))
                continue;

            if (pad.Side is { } side && faction?.Side != side)
                continue;

            if (pad.Bound.Count >= best)
                continue;

            best = pad.Bound.Count;
            found = new Entity<SotRespawnPadComponent>(uid, pad);
        }

        return best != int.MaxValue;
    }

    #endregion

    #region Смерть и воскрешение

    private void OnMobStateChanged(Entity<SotRespawnBoundComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
        {
            ent.Comp.DiedAt = _timing.CurTime;
            ScheduleAutoRespawn(ent.Owner, ent.Comp);
            return;
        }

        // Игрока вылечили раньше (дефибриллятор и т. п.): воскрешение платформой не нужно.
        ent.Comp.RespawnAt = null;
        ent.Comp.DiedAt = null;
        RemoveGhostAction(ent.Comp);
    }

    /// <summary>
    ///     Автоматический режим: воскрешение само через Delay платформы, на которую игрок воскреснет сейчас.
    ///     Считается от времени смерти и пересчитывается, если платформа сменилась (отключилась судовая).
    /// </summary>
    private void ScheduleAutoRespawn(EntityUid body, SotRespawnBoundComponent bound)
    {
        bound.RespawnAt = null;

        if (bound.DiedAt is not { } diedAt)
            return;

        if (TryGetRespawnPad(body, bound, out var pad) && pad.Comp.AutoRespawn)
            bound.RespawnAt = diedAt + pad.Comp.Delay;
    }

    /// <summary>
    ///     Выдаёт призраку привязанного игрока действие воскрешения. Игрок должен быть мёртв, а его душа - в роли
    ///     призрака-посетителя (может вернуться). Действие недоступно до DiedAt + ActionDelay (кулдаун).
    /// </summary>
    private void TryGrantAction(EntityUid body, SotRespawnBoundComponent bound)
    {
        if (!TryGetRespawnPad(body, bound, out var pad)
            || !_mind.TryGetMind(body, out _, out var mind)
            || mind.OwnedEntity != body
            || mind.VisitingEntity is not { } ghost
            || !HasComp<GhostComponent>(ghost))
        {
            return;
        }

        EntityUid? action = null;
        if (!_actions.AddAction(ghost, ref action, pad.Comp.ActionPrototype) || action == null)
            return;

        bound.Ghost = ghost;
        bound.GhostAction = action;

        // Задержка считается от смерти: если призрак появился позже, часть времени уже прошла.
        var now = _timing.CurTime;
        var availableAt = bound.DiedAt!.Value + pad.Comp.ActionDelay;
        if (availableAt > now)
            _actions.SetCooldown(action, now, availableAt);
    }

    private void RemoveGhostAction(SotRespawnBoundComponent bound)
    {
        if (bound.Ghost is { } ghost && bound.GhostAction is { } action && !TerminatingOrDeleted(ghost))
            _actions.RemoveAction(ghost, action);

        bound.Ghost = null;
        bound.GhostAction = null;
    }

    /// <summary>Нажатие действия "Воскреснуть на платформе" призраком.</summary>
    private void OnRespawnAction(EntityUid uid, GhostComponent component, SotRespawnActionEvent args)
    {
        if (args.Handled)
            return;

        // Тело игрока берём из его разума: призрак - лишь посетитель, владелец разума - тело.
        if (!_mind.TryGetMind(uid, out _, out var mind)
            || mind.OwnedEntity is not { } body
            || !TryComp<SotRespawnBoundComponent>(body, out var bound)
            || !TryGetRespawnPad(body, bound, out var pad))
        {
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-action-no-body"), uid, uid);
            return;
        }

        // Защита от обхода кулдауна: проверяем время ещё раз на сервере.
        if (bound.DiedAt is { } diedAt && _timing.CurTime < diedAt + pad.Comp.ActionDelay)
        {
            var left = (int) Math.Ceiling((diedAt + pad.Comp.ActionDelay - _timing.CurTime).TotalSeconds);
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-action-wait", ("seconds", left)), uid, uid);
            return;
        }

        args.Handled = true;
        Respawn(body, bound);
    }

    /// <summary>Телепортирует тело на платформу, лечит его и возвращает душу. Сразу, без задержек.</summary>
    private void Respawn(EntityUid uid, SotRespawnBoundComponent bound)
    {
        bound.RespawnAt = null;

        // Платформа выбирается в момент воскрешения: после конца боя это уже статичная платформа на базе.
        if (!TryGetRespawnPad(uid, bound, out var pad))
        {
            var recipient = bound.Ghost is { } ghost && !TerminatingOrDeleted(ghost) ? ghost : uid;
            _popup.PopupEntity(Loc.GetString("sot-respawn-popup-no-pad"), uid, recipient);
            return;
        }

        if (!_mobState.IsDead(uid))
            return;

        var origin = Transform(uid).Coordinates;
        var target = Transform(pad).Coordinates;

        // Освобождаем тело: буксировка и контейнеры (шкаф, мешок для трупов) телепорту мешают.
        if (TryComp<PullableComponent>(uid, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(uid, pullable, ignoreGrab: true);

        if (TryComp<PullerComponent>(uid, out var puller)
            && puller.Pulling is { } pulledUid
            && TryComp<PullableComponent>(pulledUid, out var pulled))
        {
            _pulling.TryStopPull(pulledUid, pulled, ignoreGrab: true);
        }

        if (_container.IsEntityInContainer(uid))
            _container.TryRemoveFromContainer(uid);

        _audio.PlayPvs(pad.Comp.DepartureSound, origin);
        _transform.SetCoordinates(uid, target);

        // Полное лечение: урон, состояние (Dead -> Alive), кровотечение и т. д.
        _rejuvenate.PerformRejuvenate(uid);

        // Возвращаем душу: призрак-посетитель исчезает, игрок снова в теле.
        ReturnMind(uid);

        // Действие призрака больше не нужно (сам призрак исчезнет вместе с возвратом души).
        RemoveGhostAction(bound);
        bound.DiedAt = null;

        _audio.PlayPvs(pad.Comp.ArrivalSound, uid);
        _popup.PopupEntity(Loc.GetString("sot-respawn-popup-revived"), uid, uid, PopupType.Large);
    }

    /// <summary>
    ///     Если игрок после смерти превратился в призрака-посетителя (может вернуться), возвращаем его в тело.
    ///     Тем, кто окончательно покинул тело, воскрешение платформой душу не вернёт.
    /// </summary>
    private void ReturnMind(EntityUid body)
    {
        if (!_mind.TryGetMind(body, out var mindId, out var mind))
            return;

        if (mind.OwnedEntity == body && mind.VisitingEntity != null)
            _mind.UnVisit(mindId, mind);
    }

    #endregion
}
