using System.Linq;
using Content.Server.Cargo.Components;
using Content.Server.GameTicking.Events;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Shared.Stacks;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Shuttles.Components;
using Content.Shared.Tiles;
using Content.Shared.Whitelist;
using Robust.Server.Maps;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Audio;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;

namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    /*
     * Handles cargo shuttle / trade mechanics.
     */

    public MapId? CargoMap { get; private set; }

    private static readonly SoundPathSpecifier ApproveSound = new("/Audio/Effects/Cargo/ping.ogg");

    private void InitializeShuttle()
    {
        SubscribeLocalEvent<TradeStationComponent, GridSplitEvent>(OnTradeSplit);

        SubscribeLocalEvent<CargoShuttleConsoleComponent, ComponentStartup>(OnCargoShuttleConsoleStartup);

        SubscribeLocalEvent<CargoPalletConsoleComponent, CargoPalletSellMessage>(OnPalletSale);
        SubscribeLocalEvent<CargoPalletConsoleComponent, CargoPalletAppraiseMessage>(OnPalletAppraise);
        SubscribeLocalEvent<CargoPalletConsoleComponent, BoundUIOpenedEvent>(OnPalletUIOpen);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<StationInitializedEvent>(OnStationInitialize);

        Subs.CVar(_cfgManager, CCVars.GridFill, SetGridFill);
    }

    private void SetGridFill(bool obj)
    {
        if (obj)
        {
            SetupTradePost();
        }
    }

    #region Console

    private void UpdateCargoShuttleConsoles(EntityUid shuttleUid, CargoShuttleComponent _)
    {
        // Update pilot consoles that are already open.
        _console.RefreshDroneConsoles();

        // Update order consoles.
        var shuttleConsoleQuery = AllEntityQuery<CargoShuttleConsoleComponent>();

        while (shuttleConsoleQuery.MoveNext(out var uid, out var _))
        {
            var stationUid = _station.GetOwningStation(uid);
            if (stationUid != shuttleUid)
                continue;

            UpdateShuttleState(uid, stationUid);
        }
    }

    private void UpdatePalletConsoleInterface(EntityUid uid)
    {
        var bui = _uiSystem.GetUi(uid, CargoPalletConsoleUiKey.Sale);
        if (Transform(uid).GridUid is not EntityUid gridUid)
        {
            _uiSystem.SetUiState(bui,
            new CargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }
        GetPalletGoods(gridUid, out var toSell, out var amount);
        _uiSystem.SetUiState(bui,
            new CargoPalletConsoleInterfaceState((int) amount, toSell.Count, true));
    }

    private void OnPalletUIOpen(EntityUid uid, CargoPalletConsoleComponent component, BoundUIOpenedEvent args)
    {
        var player = args.Session.AttachedEntity;

        if (player == null)
            return;

        UpdatePalletConsoleInterface(uid);
    }

    /// <summary>
    /// Ok so this is just the same thing as opening the UI, its a refresh button.
    /// I know this would probably feel better if it were like predicted and dynamic as pallet contents change
    /// However.
    /// I dont want it to explode if cargo uses a conveyor to move 8000 pineapple slices or whatever, they are
    /// known for their entity spam i wouldnt put it past them
    /// </summary>

    private void OnPalletAppraise(EntityUid uid, CargoPalletConsoleComponent component, CargoPalletAppraiseMessage args)
    {
        var player = args.Session.AttachedEntity;

        if (player == null)
            return;

        UpdatePalletConsoleInterface(uid);
    }

    private void OnCargoShuttleConsoleStartup(EntityUid uid, CargoShuttleConsoleComponent component, ComponentStartup args)
    {
        var station = _station.GetOwningStation(uid);
        UpdateShuttleState(uid, station);
    }

    private void UpdateShuttleState(EntityUid uid, EntityUid? station = null)
    {
        TryComp<StationCargoOrderDatabaseComponent>(station, out var orderDatabase);
        TryComp<CargoShuttleComponent>(orderDatabase?.Shuttle, out var shuttle);

        var orders = GetProjectedOrders(station ?? EntityUid.Invalid, orderDatabase, shuttle);
        var shuttleName = orderDatabase?.Shuttle != null ? MetaData(orderDatabase.Shuttle.Value).EntityName : string.Empty;

        if (_uiSystem.TryGetUi(uid, CargoConsoleUiKey.Shuttle, out var bui))
            _uiSystem.SetUiState(bui, new CargoShuttleConsoleBoundUserInterfaceState(
                station != null ? MetaData(station.Value).EntityName : Loc.GetString("cargo-shuttle-console-station-unknown"),
                string.IsNullOrEmpty(shuttleName) ? Loc.GetString("cargo-shuttle-console-shuttle-not-found") : shuttleName,
                orders
            ));
    }

    #endregion

    private void OnTradeSplit(EntityUid uid, TradeStationComponent component, ref GridSplitEvent args)
    {
        // If the trade station gets bombed it's still a trade station.
        foreach (var gridUid in args.NewGrids)
        {
            EnsureComp<TradeStationComponent>(gridUid);
        }
    }

    #region Shuttle

    /// <summary>
    /// Returns the orders that can fit on the cargo shuttle.
    /// </summary>
    private List<CargoOrderData> GetProjectedOrders(
        EntityUid shuttleUid,
        StationCargoOrderDatabaseComponent? component = null,
        CargoShuttleComponent? shuttle = null)
    {
        var orders = new List<CargoOrderData>();

        if (component == null || shuttle == null || component.Orders.Count == 0)
            return orders;

        var spaceRemaining = GetCargoSpace(shuttleUid);
        for (var i = 0; i < component.Orders.Count && spaceRemaining > 0; i++)
        {
            var order = component.Orders[i];
            if (order.Approved)
            {
                var numToShip = order.OrderQuantity - order.NumDispatched;
                if (numToShip > spaceRemaining)
                {
                    // We won't be able to fit the whole order on, so make one
                    // which represents the space we do have left:
                    var reducedOrder = new CargoOrderData(order.OrderId,
                            order.ProductId, order.Price, spaceRemaining, order.Requester, order.Reason);
                    orders.Add(reducedOrder);
                }
                else
                {
                    orders.Add(order);
                }
                spaceRemaining -= numToShip;
            }
        }

        return orders;
    }

    /// <summary>
    /// Get the amount of space the cargo shuttle can fit for orders.
    /// </summary>
    private int GetCargoSpace(EntityUid gridUid)
    {
        var space = GetCargoPallets(gridUid).Count;
        return space;
    }

    private List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent PalletXform)> GetCargoPallets(EntityUid gridUid)
    {
        _pads.Clear();
        var query = AllEntityQuery<CargoPalletComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var compXform))
        {
            if (compXform.ParentUid != gridUid ||
                !compXform.Anchored)
            {
                continue;
            }

            _pads.Add((uid, comp, compXform));
        }

        return _pads;
    }

    private IEnumerable<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)>
        GetFreeCargoPallets(EntityUid gridUid,
            List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)> pallets)
    {
        _setEnts.Clear();

        foreach (var pallet in pallets)
        {
            var aabb = _lookup.GetAABBNoContainer(pallet.Entity, pallet.Transform.LocalPosition, pallet.Transform.LocalRotation);

            if (_lookup.AnyLocalEntitiesIntersecting(gridUid, aabb, LookupFlags.Dynamic))
                continue;

            yield return pallet;
        }
    }

    #endregion

    #region Station

    private bool SellPallets(EntityUid gridUid, EntityUid? station, out double amount)
    {
        station ??= _station.GetOwningStation(gridUid);
        GetPalletGoods(gridUid, out var toSell, out amount);

        Log.Debug($"Cargo sold {toSell.Count} entities for {amount}");

        if (toSell.Count == 0)
            return false;

        if (station != null)
        {
            var ev = new EntitySoldEvent(station.Value, toSell);
            RaiseLocalEvent(ref ev);
        }

        foreach (var ent in toSell)
        {
            Del(ent);
        }

        return true;
    }

    private void GetPalletGoods(EntityUid gridUid, out HashSet<EntityUid> toSell, out double amount)
    {
        amount = 0;
        toSell = new HashSet<EntityUid>();

        foreach (var (palletUid, _, _) in GetCargoPallets(gridUid))
        {
            // Containers should already get the sell price of their children so can skip those.
            _setEnts.Clear();

            _lookup.GetEntitiesIntersecting(palletUid, _setEnts,
                LookupFlags.Dynamic | LookupFlags.Sundries);

            foreach (var ent in _setEnts)
            {
                // Dont sell:
                // - anything already being sold
                // - anything anchored (e.g. light fixtures)
                // - anything blacklisted (e.g. players).
                if (toSell.Contains(ent) ||
                    _xformQuery.TryGetComponent(ent, out var xform) &&
                    (xform.Anchored || !CanSell(ent, xform)))
                {
                    continue;
                }

                if (_blacklistQuery.HasComponent(ent))
                    continue;

                var price = _pricing.GetPrice(ent);
                if (price == 0)
                    continue;
                toSell.Add(ent);
                amount += price;
            }
        }
    }

    private bool CanSell(EntityUid uid, TransformComponent xform)
    {
        if (_mobQuery.HasComponent(uid))
        {
            return false;
        }

        var complete = IsBountyComplete(uid, (EntityUid?) null, out var bountyEntities);

        // Recursively check for mobs at any point.
        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (complete && bountyEntities.Contains(child))
                continue;

            if (!CanSell(child, _xformQuery.GetComponent(child)))
                return false;
        }

        return true;
    }

    private void OnPalletSale(EntityUid uid, CargoPalletConsoleComponent component, CargoPalletSellMessage args)
    {
        var player = args.Session.AttachedEntity;

        if (player == null)
            return;

        var bui = _uiSystem.GetUi(uid, CargoPalletConsoleUiKey.Sale);
        var xform = Transform(uid);

        if (xform.GridUid is not EntityUid gridUid)
        {
            _uiSystem.SetUiState(bui,
            new CargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        if (!SellPallets(gridUid, null, out var price))
            return;

        var stackPrototype = _protoMan.Index<StackPrototype>(component.CashType);
        _stack.Spawn((int) price, stackPrototype, xform.Coordinates);
        _audio.PlayPvs(ApproveSound, uid);
        UpdatePalletConsoleInterface(uid);
    }

    #endregion

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        CleanupTradeStation();
    }

    private void OnStationInitialize(StationInitializedEvent args)
    {
        if (!HasComp<StationCargoOrderDatabaseComponent>(args.Station)) // No cargo, L
            return;

        if (_cfgManager.GetCVar(CCVars.GridFill))
            SetupTradePost();
    }

    private void CleanupTradeStation()
    {
        if (CargoMap == null || !_mapManager.MapExists(CargoMap.Value))
        {
            CargoMap = null;
            DebugTools.Assert(!EntityQuery<CargoShuttleComponent>().Any());
            return;
        }

        _mapManager.DeleteMap(CargoMap.Value);
        CargoMap = null;

        // Shuttle may not have been in the cargo dimension (e.g. on the station map) so need to delete.
        var query = AllEntityQuery<CargoShuttleComponent>();

        while (query.MoveNext(out var uid, out var _))
        {
            if (TryComp<StationCargoOrderDatabaseComponent>(uid, out var station))
            {
                station.Shuttle = null;
            }

            QueueDel(uid);
        }
    }

    private void SetupTradePost()
    {
        if (CargoMap != null && _mapManager.MapExists(CargoMap.Value))
        {
            return;
        }

        // It gets mapinit which is okay... buuutt we still want it paused to avoid power draining.
        CargoMap = _mapManager.CreateMap();

        var options = new MapLoadOptions
        {
            LoadMap = true,
        };

        _mapLoader.TryLoad((MapId) CargoMap, "/Maps/Shuttles/trading_outpost.yml", out var rootUids, options); // Oh boy oh boy, hardcoded paths!

        // If this fails to load for whatever reason, cargo is fucked
        if (rootUids == null || !rootUids.Any())
            return;

        foreach (var grid in rootUids)
        {
            EnsureComp<ProtectedGridComponent>(grid);
            EnsureComp<TradeStationComponent>(grid);

            var shuttleComponent = EnsureComp<ShuttleComponent>(grid);
            shuttleComponent.AngularDamping = 10000;
            shuttleComponent.LinearDamping = 10000; // This shit ain't going nowhere
        }

        var mapUid = _mapManager.GetMapEntityId(CargoMap.Value);
        var ftl = EnsureComp<FTLDestinationComponent>(_mapManager.GetMapEntityId(CargoMap.Value));
        ftl.Whitelist = new EntityWhitelist()
        {
            Components =
            [
                _factory.GetComponentName(typeof(CargoShuttleComponent))
            ]
        };

        _metaSystem.SetEntityName(mapUid, $"Automated Trade Station {_random.Next(1000):000}");

        _console.RefreshShuttleConsoles();
    }
}

/// <summary>
/// Event broadcast raised by-ref before it is sold and
/// deleted but after the price has been calculated.
/// </summary>
[ByRefEvent]
public readonly record struct EntitySoldEvent(EntityUid Station, HashSet<EntityUid> Sold);
