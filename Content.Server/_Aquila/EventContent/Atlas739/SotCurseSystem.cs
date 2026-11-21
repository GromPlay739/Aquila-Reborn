// Работа выполнена Claude (Anthropic).

using Content.Shared._Aquila.EventContent.Atlas739;
using Content.Shared.Actions;
using Content.Shared.Popups;

namespace Content.Server._Aquila.EventContent.Atlas739;

/// <summary>
///     Проклятие SoT (серверная часть).
///     - SotCurseCaster: при появлении выдаёт действие "Проклясть".
///     - Действие "Проклясть": цель должна иметь SotFaction нужной стороны (Афина), на неё накладывается SotCurse.
///     - SotCurse: при появлении выдаёт проклятому действие "Снять проклятие", при исчезновении забирает его.
///     - Действие "Снять проклятие": убирает SotCurse с исполнителя.
///     Действия выдаются двумя путями: при MapInit (компонент задан в прототипе) и при ComponentStartup,
///     если сущность уже инициализирована (компонент добавлен позже: AddComponentSpecial, addcomp, наше же проклятие).
/// </summary>
public sealed class SotCurseSystem : SharedSotCurseSystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SotCurseCasterComponent, MapInitEvent>(OnCasterMapInit);
        SubscribeLocalEvent<SotCurseCasterComponent, ComponentStartup>(OnCasterStartup);
        SubscribeLocalEvent<SotCurseCasterComponent, ComponentShutdown>(OnCasterShutdown);
        SubscribeLocalEvent<SotCurseCasterComponent, SotCurseActionEvent>(OnCurseAction);

        SubscribeLocalEvent<SotCurseComponent, MapInitEvent>(OnCurseMapInit);
        SubscribeLocalEvent<SotCurseComponent, ComponentStartup>(OnCurseStartup);
        SubscribeLocalEvent<SotCurseComponent, ComponentShutdown>(OnCurseShutdown);
        SubscribeLocalEvent<SotCurseComponent, SotUncurseActionEvent>(OnUncurseAction);
    }

    #region Тот, кто проклинает

    private void OnCasterMapInit(Entity<SotCurseCasterComponent> ent, ref MapInitEvent args)
    {
        GrantCasterAction(ent, ent.Comp);
    }

    private void OnCasterStartup(Entity<SotCurseCasterComponent> ent, ref ComponentStartup args)
    {
        if (LifeStage(ent) >= EntityLifeStage.MapInitialized)
            GrantCasterAction(ent, ent.Comp);
    }

    private void GrantCasterAction(EntityUid uid, SotCurseCasterComponent comp)
    {
        if (comp.ActionEntity != null)
            return;

        _actions.AddAction(uid, ref comp.ActionEntity, comp.Action);
    }

    private void OnCasterShutdown(Entity<SotCurseCasterComponent> ent, ref ComponentShutdown args)
    {
        RemoveAndDelete(ent, ref ent.Comp.ActionEntity);
    }

    private void OnCurseAction(EntityUid uid, SotCurseCasterComponent comp, SotCurseActionEvent args)
    {
        if (args.Handled)
            return;

        var target = args.Target;

        // Зона проклятия: проверяем раньше всего, чтобы игрок сразу понял, что дело в месте. Не handled: кулдаун не тратится.
        if ((comp.ZoneRule is SotCurseZoneRule.Caster or SotCurseZoneRule.Both) && !InZone(uid))
        {
            _popup.PopupEntity(Loc.GetString("sot-curse-no-zone-caster"), uid, uid);
            return;
        }

        if ((comp.ZoneRule is SotCurseZoneRule.Target or SotCurseZoneRule.Both) && !InZone(target))
        {
            _popup.PopupEntity(Loc.GetString("sot-curse-no-zone-target"), uid, uid);
            return;
        }

        // Проклясть можно только члена нужной фракции: явно заданной (TargetSide) или, по умолчанию, своей
        // собственной (SotFaction.Side проклинающего). Не handled: кулдаун не запускается.
        SotSide? allowedSide = comp.TargetSide;
        if (allowedSide == null && TryComp<SotFactionComponent>(uid, out var ownFaction))
            allowedSide = ownFaction.Side;

        if (allowedSide == null
            || !TryComp<SotFactionComponent>(target, out var faction)
            || faction.Side != allowedSide)
        {
            _popup.PopupEntity(Loc.GetString("sot-curse-bad-target"), uid, uid);
            return;
        }

        if (HasComp<SotCurseComponent>(target))
        {
            _popup.PopupEntity(Loc.GetString("sot-curse-already", ("target", target)), uid, uid);
            return;
        }

        var curse = EnsureComp<SotCurseComponent>(target);
        curse.Color = comp.CurseColor;
        Dirty(target, curse);

        _popup.PopupEntity(Loc.GetString("sot-curse-cast", ("target", target)), uid, uid);
        _popup.PopupEntity(Loc.GetString("sot-curse-received"), target, target, PopupType.LargeCaution);

        args.Handled = true;
    }

    /// <summary>
    ///     Стоит ли сущность на плитке с маркером зоны проклятия. Сравнивается сетка и индексы плитки
    ///     (так же, как у AtmosFix-маркеров), поэтому зона не зависит от поворота и движения корабля.
    /// </summary>
    private bool InZone(EntityUid subject)
    {
        var subjectXform = Transform(subject);
        var subjectTile = _transform.GetGridOrMapTilePosition(subject, subjectXform);

        var query = EntityQueryEnumerator<SotCurseZoneComponent, TransformComponent>();
        while (query.MoveNext(out var zoneUid, out _, out var zoneXform))
        {
            if (zoneXform.MapID != subjectXform.MapID || zoneXform.GridUid != subjectXform.GridUid)
                continue;

            if (_transform.GetGridOrMapTilePosition(zoneUid, zoneXform) == subjectTile)
                return true;
        }

        return false;
    }

    #endregion

    #region Проклятый

    private void OnCurseMapInit(Entity<SotCurseComponent> ent, ref MapInitEvent args)
    {
        GrantUncurseAction(ent, ent.Comp);
    }

    private void OnCurseStartup(Entity<SotCurseComponent> ent, ref ComponentStartup args)
    {
        if (LifeStage(ent) >= EntityLifeStage.MapInitialized)
            GrantUncurseAction(ent, ent.Comp);
    }

    private void GrantUncurseAction(EntityUid uid, SotCurseComponent comp)
    {
        if (comp.UncurseActionEntity != null)
            return;

        _actions.AddAction(uid, ref comp.UncurseActionEntity, comp.UncurseAction);
    }

    private void OnCurseShutdown(Entity<SotCurseComponent> ent, ref ComponentShutdown args)
    {
        RemoveAndDelete(ent, ref ent.Comp.UncurseActionEntity);
    }

    private void OnUncurseAction(EntityUid uid, SotCurseComponent comp, SotUncurseActionEvent args)
    {
        if (args.Handled)
            return;

        // Действие удаляется через QueueDel (отложенно), поэтому безопасно внутри обработки самого действия.
        RemComp<SotCurseComponent>(uid);

        _popup.PopupEntity(Loc.GetString("sot-curse-removed"), uid, uid, PopupType.Large);
        args.Handled = true;
    }

    #endregion

    /// <summary>
    ///     RemoveAction только отсоединяет действие от владельца и не удаляет его, поэтому удаляем сами.
    /// </summary>
    private void RemoveAndDelete(EntityUid owner, ref EntityUid? actionEntity)
    {
        if (actionEntity is not { } action)
            return;

        actionEntity = null;

        if (TerminatingOrDeleted(action))
            return;

        if (!TerminatingOrDeleted(owner))
            _actions.RemoveAction(owner, action);

        QueueDel(action);
    }
}
