using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Systems;
using Content.Shared.Physics;
using Content.Shared.Theta.ShipEvent.CircularShield;
using Robust.Server.GameObjects;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Content.Server.Power.Components;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Power;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Theta.ShipEvent.Components;
using Content.Shared.Theta.ShipEvent.UI;
using Content.Shared.UserInterface;
using Robust.Server.GameStates;
using Robust.Shared.Physics.Events;
using Robust.Shared.Threading;
using Content.Shared._Mono.SpaceArtillery;

namespace Content.Server.Theta.ShipEvent.Systems;

public sealed class CircularShieldSystem : SharedCircularShieldSystem
{
    [Dependency] private readonly PhysicsSystem _physSys = default!;
    [Dependency] private readonly FixtureSystem _fixSys = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSys = default!;
    [Dependency] private readonly TransformSystem _formSys = default!;
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private readonly PvsOverrideSystem _pvsIgnoreSys = default!;
    [Dependency] private readonly IParallelManager _parallel = default!;

    private const string ShieldFixtureId = "ShieldFixture";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CircularShieldConsoleComponent, CircularShieldToggleMessage>(OnShieldToggle);
        SubscribeLocalEvent<CircularShieldConsoleComponent, CircularShieldChangeParametersMessage>(OnShieldChangeParams);
        SubscribeLocalEvent<CircularShieldConsoleComponent, AfterActivatableUIOpenEvent>(AfterUIOpen);

        SubscribeLocalEvent<CircularShieldConsoleComponent, ComponentInit>(OnShieldConsoleInit);
        SubscribeLocalEvent<CircularShieldComponent, ComponentShutdown>(OnShieldRemoved);
        SubscribeLocalEvent<CircularShieldComponent, PowerChangedEvent>(OnShieldPowerChanged);
        SubscribeLocalEvent<CircularShieldComponent, StartCollideEvent>(OnShieldEnter);
        SubscribeLocalEvent<CircularShieldComponent, NewLinkEvent>(OnShieldLink);
    }

    public override void Update(float time)
    {
        base.Update(time);

        // Get all shields
        var shields = new List<(EntityUid Uid, CircularShieldComponent Shield)>();
        var query = EntityManager.EntityQueryEnumerator<CircularShieldComponent>();
        while (query.MoveNext(out var uid, out var shield))
        {
            shields.Add((uid, shield));
        }

        // If there are enough shields to benefit from parallelization
        if (shields.Count > 4)
        {
            // Create parallel job for updating shields
            var job = new ShieldUpdateJob
            {
                System = this,
                Time = time,
                Shields = shields
            };

            // Process shield updates in parallel
            _parallel.ProcessNow(job, shields.Count);
        }
        else
        {
            // Sequential update for small number of shields
            foreach (var (uid, shield) in shields)
            {
                // Update shield effects
                foreach (CircularShieldEffect effect in shield.Effects)
                {
                    effect.OnShieldUpdate(uid, shield, time);
                }

                // Update power surge dissipation
                UpdateDamageSurge(uid, shield, time);
            }
        }
    }

    /// <summary>
    /// Updates the shield's power surge, reducing it over time.
    /// </summary>
    private void UpdateDamageSurge(EntityUid uid, CircularShieldComponent shield, float deltaTime)
    {
        // If no surge, nothing to do
        if (shield.CurrentSurgePower <= 0f || shield.SurgeTimeRemaining <= 0f)
        {
            shield.CurrentSurgePower = 0f;
            shield.SurgeTimeRemaining = 0f;
            return;
        }

        // Decrease the surge time
        shield.SurgeTimeRemaining -= deltaTime;

        // If surge time has expired, reset the power surge
        if (shield.SurgeTimeRemaining <= 0f)
        {
            shield.CurrentSurgePower = 0f;
            shield.SurgeTimeRemaining = 0f;
            // Update power draw after surge dissipates
            UpdatePowerDraw(uid, shield);
        }
    }

    /// <summary>
    /// Helper method to apply a power surge to the shield based on projectile impact
    /// </summary>
    private void ApplyShieldPowerSurge(EntityUid uid, CircularShieldComponent shield)
    {
        // Add to current surge and reset timer
        shield.CurrentSurgePower += shield.ProjectileWattPerImpact;
        shield.SurgeTimeRemaining = shield.DamageSurgeDuration;

        // Update power draw immediately to reflect increased power usage
        UpdatePowerDraw(uid, shield);
    }

    /// <summary>
    /// Helper method to apply a power surge to the shield based on projectile damage
    /// </summary>
    private void ApplyShieldPowerSurge(EntityUid uid, CircularShieldComponent shield, ProjectileComponent projectile)
    {
        // Calculate power surge based on projectile damage
        float totalDamage = 0f;
        foreach (var (damageType, damageValue) in projectile.Damage.DamageDict)
        {
            totalDamage += damageValue.Float();
        }
        
        // Add to current surge based on damage and reset timer
        shield.CurrentSurgePower += totalDamage * shield.ProjectileWattPerImpact;
        shield.SurgeTimeRemaining = shield.DamageSurgeDuration;

        // Update power draw immediately to reflect increased power usage
        UpdatePowerDraw(uid, shield);
    }

    private void AfterUIOpen(EntityUid uid, CircularShieldConsoleComponent component, AfterActivatableUIOpenEvent args)
    {
        UpdateConsoleState(uid, component);
    }

    private void UpdateConsoleState(EntityUid uid, CircularShieldConsoleComponent? console = null, RadarConsoleComponent? radar = null)
    {
        if (!Resolve(uid, ref console, ref radar) || console.BoundShield == null)
            return;

        if (!TryComp<CircularShieldComponent>(console.BoundShield, out var shield) ||
            !TryComp<TransformComponent>(console.BoundShield, out var transform))
            return;

        var shieldState = new ShieldInterfaceState
        {
            Coordinates = GetNetCoordinates(_formSys.GetMoverCoordinates(console.BoundShield.Value, transform)),
            Powered = shield.Powered,
            Enabled = shield.Enabled,
            Angle = shield.Angle,
            Width = shield.Width,
            MaxWidth = shield.MaxWidth,
            Radius = shield.Radius,
            MaxRadius = shield.MaxRadius
        };

        _uiSys.SetUiState(uid, CircularShieldConsoleUiKey.Key, new ShieldConsoleBoundsUserInterfaceState(
            _shuttleConsole.GetNavState(uid, new Dictionary<NetEntity, List<DockingPortState>>()),
            shieldState
            ));
    }

    private void OnShieldToggle(EntityUid uid, CircularShieldConsoleComponent console, CircularShieldToggleMessage args)
    {
        if (console.BoundShield == null)
            return;

        if (!TryComp(console.BoundShield, out CircularShieldComponent? shield))
            return;

        shield.Enabled = !shield.Enabled;
        UpdatePowerDraw(console.BoundShield.Value, shield);
        UpdateConsoleState(uid, console);

        if (!shield.Enabled)
        {
            foreach (CircularShieldEffect effect in shield.Effects)
            {
                effect.OnShieldShutdown(uid, shield);
            }
        }

        // Radar blips are now handled by CircularShieldRadarSystem
        // which will react to changes in the shield component state

        Dirty(console.BoundShield.Value, shield);
    }

    private void OnShieldChangeParams(EntityUid uid, CircularShieldConsoleComponent console, CircularShieldChangeParametersMessage args)
    {
        if (console.BoundShield == null)
            return;

        if (!TryComp(console.BoundShield, out CircularShieldComponent? shield))
            return;

        if (args.Radius > shield.MaxRadius || args.Width?.Degrees > shield.MaxWidth)
            return;

        shield.Angle = args.Angle ?? shield.Angle;

        // Ensure width is always set to 360 degrees for a full circle
        shield.Width = Angle.FromDegrees(360);
        shield.Radius = args.Radius ?? shield.Radius;

        UpdateShieldFixture(console.BoundShield.Value, shield);
        UpdatePowerDraw(console.BoundShield.Value, shield);

        Dirty(console.BoundShield.Value, shield);
    }

    //this is silly, but apparently sink component on shields does not contain linked sources on startup
    //while source component on consoles always does right after init
    //so subscribing to it instead of sink
    private void OnShieldConsoleInit(EntityUid uid, CircularShieldConsoleComponent console, ComponentInit args)
    {
        _pvsIgnoreSys.AddGlobalOverride(uid);

        EntityUid shieldUid;
        CircularShieldComponent shield;

        if (!TryComp<DeviceLinkSourceComponent>(uid, out var source))
            return;

        if (source.LinkedPorts.Count == 0)
            return;

        shieldUid = source.LinkedPorts.First().Key;
        shield = Comp<CircularShieldComponent>(shieldUid);
        console.BoundShield = shieldUid;
        shield.BoundConsole = uid;

        // Always initialize with a full 360-degree circle
        shield.Width = Angle.FromDegrees(360);
        UpdateShieldFixture(shieldUid, shield);
        Dirty(shieldUid, shield);

        // Make sure the shield is visible from a distance by adding a PVS override
        _pvsIgnoreSys.AddGlobalOverride(shieldUid);

        foreach (CircularShieldEffect effect in shield.Effects)
        {
            effect.OnShieldInit(uid, shield);
        }
    }

    private void OnShieldRemoved(EntityUid uid, CircularShieldComponent shield, ComponentShutdown args)
    {
        foreach (CircularShieldEffect effect in shield.Effects)
        {
            effect.OnShieldShutdown(uid, shield);
        }
    }

    private void OnShieldPowerChanged(EntityUid uid, CircularShieldComponent shield, ref PowerChangedEvent args)
    {
        shield.Powered = args.Powered;

        if (shield.BoundConsole == null)
            return;
        UpdateConsoleState(shield.BoundConsole.Value);

        if (!shield.Powered)
        {
            foreach (CircularShieldEffect effect in shield.Effects)
            {
                effect.OnShieldShutdown(uid, shield);
            }
        }

        // Radar blips are now handled by CircularShieldRadarSystem
        // which will react to changes in the shield component state

        Dirty(uid, shield);
    }

    private void OnShieldEnter(EntityUid uid, CircularShieldComponent shield, ref StartCollideEvent args)
    {
        if (!shield.CanWork || args.OurFixtureId != ShieldFixtureId)
            return;

        if (!EntityInShield(uid, shield, args.OtherEntity, _formSys))
            return;

        // Check if the object colliding with the shield is a projectile from a different grid
        bool isProjectileFromDifferentGrid = false;

        // Only do grid check for projectiles
        if (TryComp<ProjectileComponent>(args.OtherEntity, out var projectile))
        {
            // Check if the projectile has the ShipWeaponProjectile component
            if (!HasComp<ShipWeaponProjectileComponent>(args.OtherEntity))
                return;
                
            // Get the shield's grid
            if (TryComp<TransformComponent>(uid, out var shieldTransform) && shieldTransform.GridUid != null)
            {
                var shieldGridUid = shieldTransform.GridUid;

                // Get the projectile's grid and the shooter's grid
                EntityUid? projectileGridUid = null;
                EntityUid? shooterGridUid = null;

                if (TryComp<TransformComponent>(args.OtherEntity, out var projectileTransform))
                    projectileGridUid = projectileTransform.GridUid;

                if (projectile.Shooter.HasValue &&
                    EntityManager.EntityExists(projectile.Shooter.Value) &&
                    TryComp<TransformComponent>(projectile.Shooter.Value, out var shooterTransform))
                {
                    shooterGridUid = shooterTransform.GridUid;
                }

                // Projectile is from a different grid if its grid or its shooter's grid differs from the shield's grid
                isProjectileFromDifferentGrid = (projectileGridUid != shieldGridUid) ||
                                               (shooterGridUid != null && shooterGridUid != shieldGridUid);
            }

            // If projectile is from a different grid, the shield absorbs its energy
            if (isProjectileFromDifferentGrid)
            {
                // Apply power surge from impact based on projectile damage
                ApplyShieldPowerSurge(uid, shield, projectile);
            }
        }

        // Process shield effects
        foreach (CircularShieldEffect effect in shield.Effects)
        {
            effect.OnShieldEnter(args.OtherEntity, shield);
        }
    }

    private void OnShieldLink(EntityUid uid, CircularShieldComponent shield, NewLinkEvent args)
    {
        if (!TryComp<CircularShieldConsoleComponent>(args.Source, out var console))
            return;

        shield.BoundConsole = args.Source;
        console.BoundShield = uid;

        Dirty(uid, shield);
        Dirty(shield.BoundConsole.Value, console);
    }

    private void UpdateShieldFixture(EntityUid uid, CircularShieldComponent shield)
    {
        shield.Radius = Math.Max(shield.Radius, 0);
        shield.Width = Math.Max(shield.Width, Angle.FromDegrees(10));

        // Get the shield's transform and grid
        var transform = Transform(uid);
        var gridUid = transform.GridUid;

        // Get the physics center of mass if available
        Vector2 centerOffset = Vector2.Zero;
        if (gridUid != null && TryComp<PhysicsComponent>(gridUid.Value, out var physics))
        {
            centerOffset = physics.LocalCenter;
        }

        // Get or create the shield fixture
        Fixture? shieldFix = _fixSys.GetFixtureOrNull(uid, ShieldFixtureId);
        if (shieldFix == null)
        {
            // Create a new circle shape at the center of mass
            PhysShapeCircle circle = new(shield.Radius, centerOffset);
            _fixSys.TryCreateFixture(uid, circle, ShieldFixtureId, hard: false, collisionLayer: (int) CollisionGroup.BulletImpassable);
        }
        else
        {
            // Update existing fixture with new radius and center offset
            _physSys.SetRadius(uid, ShieldFixtureId, shieldFix, shieldFix.Shape, shield.Radius);

            // Update the shape's position to the center of mass
            if (shieldFix.Shape is PhysShapeCircle circle)
            {
                _physSys.SetPosition(uid, ShieldFixtureId, shieldFix, circle, centerOffset);
            }
        }

        // The radar blip is now handled by CircularShieldRadarSystem
        // We don't need to do anything with the radar blip here

        Dirty(uid, shield);
    }

    private void UpdatePowerDraw(EntityUid uid, CircularShieldComponent shield)
    {
        if (TryComp<ApcPowerReceiverComponent>(uid, out var receiver))
        {
            receiver.Load = shield.DesiredDraw;
        }
        else if (shield.DesiredDraw > 0)
        {
            shield.Powered = false;
        }
    }

    private class ShieldUpdateJob : IParallelRobustJob
    {
        public CircularShieldSystem System = default!;
        public float Time;
        public List<(EntityUid Uid, CircularShieldComponent Shield)> Shields = default!;

        // Process shields in batches for better performance
        public int BatchSize => 4;
        public int MinimumBatchParallel => 2;

        public void Execute(int index)
        {
            if (index >= Shields.Count)
                return;

            var (uid, shield) = Shields[index];

            // Update shield effects
            foreach (CircularShieldEffect effect in shield.Effects)
            {
                effect.OnShieldUpdate(uid, shield, Time);
            }

            // Update power surge dissipation
            System.UpdateDamageSurge(uid, shield, Time);
        }
    }
}
