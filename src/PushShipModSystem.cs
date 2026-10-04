using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace PushShip
{
    /// <summary>Pushes nearby boats forward, from Ctrl+right-click or /pushship.</summary>
    public class PushShipModSystem : ModSystem
    {
        private ICoreServerAPI sapi;
        private readonly Dictionary<long, long> lastPushMs = new Dictionary<long, long>();
        private readonly HashSet<long> pendingMoves = new HashSet<long>();
        private readonly Dictionary<long, PushTask> activePushes = new Dictionary<long, PushTask>();
        private readonly Dictionary<string, long> lastClickMs = new Dictionary<string, long>();

        private const long PushCooldownMs = 1200;
        private const long ClickCooldownMs = 700;
        private const int PushTickMs = 50;
        private const int PushDurationMs = 400;
        private const float PushImpulse = 0.14f;
        private const float InteractionRange = 4f;
        private long pushTickListenerId;

        private sealed class PushTask
        {
            public Entity Boat;
            public Vec3d Direction;
            public int TicksRemaining;
        }

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            pushTickListenerId = api.Event.RegisterGameTickListener(OnServerTick, PushTickMs, PushTickMs);
            api.ChatCommands.Create("pushship")
                .WithDescription("Push a nearby boat forward, or use 'fix' to resync a stuck boat")
                .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("action", "fix"))
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(OnPushShipCommand);

            api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
        }

        private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
        {
            // Interact is the right mouse button. Ctrl distinguishes pushing from normal boarding.
            if (mode == (int)EnumInteractMode.Interact &&
                byPlayer?.Entity?.Controls?.CtrlKey == true &&
                IsBoat(entity) &&
                entity.Pos.DistanceTo(byPlayer.Entity.Pos.XYZ) <= InteractionRange)
            {
                // Ctrl+right-click is reserved for pushing, so do not also board the boat.
                handling = EnumHandling.PreventDefault;
                long nowMs = sapi.World.ElapsedMilliseconds;
                string playerUid = byPlayer.PlayerUID;
                if (lastClickMs.TryGetValue(playerUid, out long lastClick) && nowMs - lastClick < ClickCooldownMs)
                    return;

                lastClickMs[playerUid] = nowMs;
                TryPushShip(entity, byPlayer.Entity.Pos.XYZ);
            }
        }

        private TextCommandResult OnPushShipCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player?.Entity == null) return TextCommandResult.Error("This command can only be used by a player.");

            Entity nearbyBoat = FindNearestBoat(player);

            if (nearbyBoat == null)
                return TextCommandResult.Error("No boat found within 4 blocks.");

            if (string.Equals(args[0] as string, "fix", System.StringComparison.OrdinalIgnoreCase))
            {
                return ResyncBoat(nearbyBoat)
                    ? TextCommandResult.Success("Boat position resynced and motion reset.")
                    : TextCommandResult.Error("This boat is already being updated; try again shortly.");
            }

            return TryPushShip(nearbyBoat, player.Entity.Pos.XYZ)
                ? TextCommandResult.Success("Pushed the boat forward.")
                : TextCommandResult.Error("The boat was pushed just now or is still updating.");
        }

        private Entity FindNearestBoat(IServerPlayer player)
        {
            return sapi.World.GetEntitiesAround(
                    player.Entity.Pos.XYZ, InteractionRange, InteractionRange, IsBoat)
                .OrderBy(entity => entity.Pos.DistanceTo(player.Entity.Pos.XYZ))
                .FirstOrDefault();
        }

        private bool TryPushShip(Entity boat, Vec3d pusherPosition)
        {
            if (boat == null || sapi == null) return false;

            long nowMs = sapi.World.ElapsedMilliseconds;
            if (pendingMoves.Contains(boat.EntityId) || activePushes.ContainsKey(boat.EntityId) ||
                (lastPushMs.TryGetValue(boat.EntityId, out long last) && nowMs - last < PushCooldownMs))
                return false;

            // Push away from the player's position. This lets a player push from the bow,
            // stern, or either side instead of forcing every push along the boat's heading.
            Vec3d direction = new Vec3d(boat.Pos.X - pusherPosition.X, 0, boat.Pos.Z - pusherPosition.Z);
            if (direction.Length() < 0.05)
            {
                var ahead = boat.Pos.HorizontalAheadCopy(1);
                direction.Set(ahead.X - boat.Pos.X, 0, ahead.Z - boat.Pos.Z);
            }
            if (direction.Length() < 0.001) return false;
            direction.Normalize();

            // Spread the same short impulse across several physics ticks for natural movement.
            activePushes[boat.EntityId] = new PushTask
            {
                Boat = boat,
                Direction = direction,
                TicksRemaining = PushDurationMs / PushTickMs
            };
            lastPushMs[boat.EntityId] = nowMs;
            return true;
        }

        private bool ResyncBoat(Entity boat)
        {
            if (boat == null || sapi == null || pendingMoves.Contains(boat.EntityId)) return false;
            activePushes.Remove(boat.EntityId);
            boat.Pos.Motion.Set(0, 0, 0);
            return MoveBoatTo(boat, boat.Pos.X, boat.Pos.Y, boat.Pos.Z, sapi.World.ElapsedMilliseconds);
        }

        private void OnServerTick(float dt)
        {
            if (activePushes.Count == 0) return;

            float impulsePerTick = PushImpulse / (PushDurationMs / (float)PushTickMs);
            foreach (var pair in activePushes.ToArray())
            {
                PushTask task = pair.Value;
                if (task.TicksRemaining <= 0 || task.Boat == null)
                {
                    activePushes.Remove(pair.Key);
                    if (task.Boat != null && !pendingMoves.Contains(pair.Key))
                    {
                        // Reconcile the final authoritative position once after smooth physics
                        // movement. This clears stale client interpolation without warping each step.
                        MoveBoatTo(task.Boat, task.Boat.Pos.X, task.Boat.Pos.Y, task.Boat.Pos.Z,
                            sapi.World.ElapsedMilliseconds);
                    }
                    continue;
                }

                task.Boat.Pos.Motion.Add(task.Direction.X * impulsePerTick, 0, task.Direction.Z * impulsePerTick);
                task.TicksRemaining--;
            }
        }

        private bool MoveBoatTo(Entity boat, double x, double y, double z, long nowMs)
        {
            long entityId = boat.EntityId;
            if (!pendingMoves.Add(entityId)) return false;

            boat.Pos.Motion.Set(0, 0, 0);
            lastPushMs[entityId] = nowMs;
            boat.TeleportToDouble(x, y, z, () => pendingMoves.Remove(entityId));
            return true;
        }

        private static bool IsBoat(Entity entity)
        {
            if (entity?.Code?.Path == null) return false;

            // Entity codes are supplied by each mod, so match common vehicle names rather than
            // depending on a particular boat mod or namespace.
            string identity = (entity.Code.Path + " " + entity.GetType().Name).ToLowerInvariant();
            return identity.Contains("boat") || identity.Contains("ship") ||
                   identity.Contains("raft") || identity.Contains("vessel");
        }

        public override void Dispose()
        {
            if (sapi != null)
            {
                sapi.Event.OnPlayerInteractEntity -= OnPlayerInteractEntity;
                sapi.Event.UnregisterGameTickListener(pushTickListenerId);
            }
            base.Dispose();
        }
    }
}
