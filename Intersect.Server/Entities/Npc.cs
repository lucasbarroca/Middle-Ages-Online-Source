﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web.UI;
using Intersect.Enums;
using Intersect.GameObjects;
using Intersect.GameObjects.Events;
using Intersect.GameObjects.Maps;
using Intersect.Logging;
using Intersect.Network.Packets.Server;
using Intersect.Server.Core;
using Intersect.Server.Database;
using Intersect.Server.Database.PlayerData.Players;
using Intersect.Server.Entities.Combat;
using Intersect.Server.Entities.Events;
using Intersect.Server.Entities.Pathfinding;
using Intersect.Server.Entities.PlayerData;
using Intersect.Server.General;
using Intersect.Server.Localization;
using Intersect.Server.Maps;
using Intersect.Server.Networking;
using Intersect.Server.Utilities;
using Intersect.Utilities;
using Org.BouncyCastle.Asn1.Mozilla;
using static Intersect.GameObjects.Maps.MapBase;

namespace Intersect.Server.Entities
{

    public partial class Npc : AttackingEntity
    {

        //Spell casting
        public long CastFreq;

        /// <summary>
        /// Damage Map - Keep track of who is doing the most damage to this npc and focus accordingly
        /// </summary>
        public ConcurrentDictionary<Entity, long> DamageMap = new ConcurrentDictionary<Entity, long>();

        public ConcurrentDictionary<Guid, bool> LootMap = new ConcurrentDictionary<Guid, bool>();

        public Guid[] LootMapCache = Array.Empty<Guid>();

        /// <summary>
        /// Returns the entity that ranks the highest on this NPC's damage map.
        /// </summary>
        public Entity DamageMapHighest { 
            get {
                long damage = 0;
                Entity top = null;
                foreach (var pair in DamageMap)
                {
                    // Only include players on the current instance
                    if (pair.Value > damage && pair.Key.MapInstanceId == MapInstanceId)
                    {
                        top = pair.Key;
                        damage = pair.Value;
                    }
                }
                return top;
            } 
        }

        public bool Despawnable;

        //Moving
        public long LastRandomMove;

        //Pathfinding
        private Pathfinder mPathFinder;

        private Task mPathfindingTask;

        //Respawn/Despawn
        public long RespawnTime;

        public long FindTargetWaitTime;
        public int FindTargetDelay = 500;

        private int mTargetFailCounter = 0;
        private int mTargetFailMax = 10;

        private int mResetDistance = 0;
        private int mResetCounter = 0;
        private int mResetMax = 100;
        private bool mResetting = false;

        private int mLastTargetDir = -1;
        private long mLastOverrideAttack = 0L;

        private Dictionary<Guid, ThreatLevel> PlayerThreatLevels = new Dictionary<Guid, ThreatLevel>();
        private long LastThreatLevelReset = 0L;
        private long DirChangeTime = 0L;

        private int AggressorCount { get; set; }
        private int LastAggressorCount { get; set; }

        public Guid SpawnMapId { get; set; }

        public MapController SpawnMap { get; set; }

        public int SpawnX { get; set; }
        
        public int SpawnY { get; set; }

        public long SpawnedAt { get; set; }

        /// <summary>
        /// The map on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        public MapController AggroCenterMap;

        /// <summary>
        /// The X value on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        public int AggroCenterX;

        /// <summary>
        /// The Y value on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        public int AggroCenterY;

        /// <summary>
        /// The Z value on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        public int AggroCenterZ;

        public string PermadeathKey = string.Empty; // A key that we can use to reference if this entity is permadead in an instance

        public Npc(NpcBase myBase, bool despawnable = false) : base()
        {
            SetToBase(myBase);
            Despawnable = despawnable;
        }

        public void InitializeToMap(Guid mapId, NpcSpawn spawnSettings, MapNpcSpawn mapSpawner, string npcKey)
        {
            SpawnMapId = mapId;
            SpawnMap = Map;
            SpawnX = X;
            SpawnY = Y;
            if (spawnSettings.PreventRespawn)
            {
                PermadeathKey = npcKey;
            }

            OverrideMovement = spawnSettings.OverrideMovement;
            OverriddenMovement = spawnSettings.OverriddenMovement;
            OverriddeRange = spawnSettings.OverrideRange;
            OverriddenRange = spawnSettings.OverriddenRange;

            mapSpawner.Entity = this;
        }

        private void SetToBase(NpcBase myBase)
        {
            if (myBase == default)
            {
                return;
            }

            Name = myBase.Name;
            Sprite = myBase.Sprite;
            Color = myBase.Color;
            Level = myBase.Level;
            ImmuneTo = myBase.Immunities;
            Base = myBase;

            for (var i = 0; i < (int)Stats.StatCount; i++)
            {
                BaseStats[i] = myBase.Stats[i];
                Stat[i] = new Stat((Stats)i, this);
            }

            var spellSlot = 0;
            for (var I = 0; I < Base.Spells.Count; I++)
            {
                var slot = new SpellSlot(spellSlot);
                slot.Set(new Spell(Base.Spells[I]));
                Spells.Add(slot);
                spellSlot++;
            }

            for (var i = 0; i < (int)Vitals.VitalCount; i++)
            {
                SetMaxVital(i, myBase.MaxVital[i]);
                SetVital(i, myBase.MaxVital[i]);
            }
            
            CastIndex = 0;

            mPathFinder = new Pathfinder(this);
            if (myBase.DeathAnimation != null)
            {
                DeathAnimation = myBase.DeathAnimation.Id;
            }

            SpawnedAt = Timing.Global.MillisecondsUtc;
        }

        public NpcBase Base { get; private set; }

        private bool IsStunnedOrSleeping => CachedStatuses.Any(PredicateStunnedOrSleeping);
        
        private bool IsStatusMovementBlocked => CachedStatuses.Any(PredicateStatusMovementBlocked);

        private bool IsUnableToCastSpells => CachedStatuses.Any(PredicateUnableToCastSpells);

        public override EntityTypes GetEntityType()
        {
            return EntityTypes.GlobalEntity;
        }

        public void TransformNpc(NpcBase newBase)
        {
            if (newBase == null)
            {
                return;
            }
            SetToBase(newBase);
            PacketSender.SendEntityDataToProximity(this);
        }

        public void RemoveFromMap(Entity killer)
        {
            if (MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var instance))
            {
                instance.RemoveEntity(this);
            }

            PacketSender.SendEntityDie(this);
            PacketSender.SendEntityLeave(this);
            // Do not process permadeaths on the overworld or if the entity was not killed by some other entity
            if (MapInstanceId != Guid.Empty && killer != null)
            {
                if (!string.IsNullOrEmpty(PermadeathKey) && InstanceProcessor.TryGetInstanceController(MapInstanceId, out var instanceController))
                {
                    instanceController.PermadeadNpcs.Add(PermadeathKey);
                }
            }
        }

        public override void Die(bool generateLoot = true, Entity killer = null, bool transform = false)
        {
            lock (EntityLock) {

                var validTransform = Base.DeathTransform != default && killer != null;

                if (validTransform)
                {
                    // Set some vars for the parent Die() method to transform properly
                    transform = true;
                }
                else
                {
                    RemoveFromMap(killer);
                }

                base.Die(generateLoot, killer, transform);


                var killedByPlayer = false;
                if (killer is Player playerKiller)
                {
                    killedByPlayer = true;
                    long recordKilled = playerKiller.IncrementRecord(RecordType.NpcKilled, Base.Id);

                    // If we've just unlocked some bestiary item, send a KC update, which will force a bestiary update on the client
                    var bestiaryThresholds = Base.BestiaryUnlocks.Values.Where(val => val > 0).ToList();
                    bestiaryThresholds.Sort();
                    var lastUnlock = bestiaryThresholds.LastOrDefault();

                    if (!Base.NotInBestiary && bestiaryThresholds.Contains((int)recordKilled))
                    {
                        PacketSender.SendKillCount(playerKiller, Base.Id);
                        // Did we just finish the bestiary entry for this mob?
                        if (lastUnlock != default && lastUnlock == (int)recordKilled)
                        {
                            PacketSender.SendChatMsg(playerKiller, $"You've completed the bestiary entry for {Base.Name}!", ChatMessageType.Experience, CustomColors.General.GeneralCompleted, sendToast: true);
                            // Give experience based on completing a bestiary entry
                            var bestiaryCompletionExp = Base.Experience * Options.Combat.BestiaryCompletionExpMultiplier;
                            playerKiller.GiveExperience(bestiaryCompletionExp);
                            PacketSender.SendExpToast(playerKiller, $"BESTIARY COMPLETE! {bestiaryCompletionExp} EXP", false, true, false);
                            if (NpcBase.Get(Base.ChampionId) != default)
                            {
                                PacketSender.SendChatMsg(playerKiller, $"Killing {Base.Name} will now have a chance to spawn their champion.", ChatMessageType.Experience, CustomColors.General.GeneralCompleted, sendToast: true);
                            }
                        }

                    }
                    else if (Options.SendNpcRecordUpdates && recordKilled % Options.NpcRecordUpdateInterval == 0)
                    {
                        playerKiller.SendRecordUpdate(Strings.Records.enemykilled.ToString(recordKilled, Name));
                    }

                    // Does this mob have a champion?
                    if (MapController.TryGetInstanceFromMap(SpawnMapId, MapInstanceId, out var beastInstance) && Base.ChampionId != Guid.Empty && Base.ChampionSpawnChance > 0f)
                    {
                        var bestiaryComplete = lastUnlock < recordKilled;
                        if ((Base.NotInBestiary || bestiaryComplete) && beastInstance.TryAddChampionOf(Base.Id, Base.ChampionId, playerKiller))
                        {
                            // A champ is prepped - tell the server!
                            PacketSender.SendGlobalMsg($"A champion {Base.Name} is stirring... ({MapController.GetName(SpawnMapId)})",
                                Color.FromName("Purple", Strings.Colors.presets));
                            PacketSender.SendToast(playerKiller, "A champion approaches...");
                        }
                    }

                    ChallengeUpdateProcesser.UpdateChallengesOf(new BeastsKilledOverTime(playerKiller, Base.Id), TierLevel);
                }

                if (validTransform)
                {
                    TransformNpc(Base.DeathTransform); // doesn't do anything if no transform available
                }
                else
                {
                    AggroCenterMap = null;
                    AggroCenterX = 0;
                    AggroCenterY = 0;
                    AggroCenterZ = 0;
                }

                // If this was a champion, remove it
                if (Base.IsChampion && MapController.TryGetInstanceFromMap(SpawnMapId, MapInstanceId, out var instance))
                {
                    instance.RemoveActiveChampion(Base.Id, killedByPlayer);
                }
            }
        }

        public bool TargetHasStealth(Entity target)
        {
            return target == null || target.CachedStatuses.Any(s => s.Type == StatusTypes.Stealth);
        }

        //Targeting
        public void AssignTarget(Entity en)
        {
            var oldTarget = Target;

            // Are we resetting? If so, do not allow for a new target.
            var pathTarget = mPathFinder?.GetTarget();
            if (AggroCenterMap != null && pathTarget != null &&
                pathTarget.TargetMapId == AggroCenterMap.Id && pathTarget.TargetX == AggroCenterX && pathTarget.TargetY == AggroCenterY)
            {
                if (en == null)
                {
                    return;
                }
                else
                {
                    return;
                }
            }

            //Why are we doing all of this logic if we are assigning a target that we already have?
            if (en != null && en != Target)
            {
                // Can't assign a new target if taunted, unless we're resetting their target somehow.
                // Also make sure the taunter is in range.. If they're dead or gone, we go for someone else!
                if ((pathTarget != null && AggroCenterMap != null && (pathTarget.TargetMapId != AggroCenterMap.Id || pathTarget.TargetX != AggroCenterX || pathTarget.TargetY != AggroCenterY)))
                {
                    foreach (var status in CachedStatuses)
                    {
                        if (status.Type == StatusTypes.Taunt && en != status.Attacker && GetDistanceTo(status.Attacker) != 9999)
                        {
                            return;
                        }
                    }
                }

                if (en.GetType() == typeof(Projectile))
                {
                    if (((Projectile)en).Owner != this && !TargetHasStealth((Projectile)en))
                    {
                        Target = ((Projectile)en).Owner;
                    }
                }
                else
                {
                    if (en.GetType() == typeof(Npc))
                    {
                        if (((Npc)en).Base == Base)
                        {
                            if (Base.AttackAllies == false)
                            {
                                return;
                            }
                        }
                    }

                    if (en.GetType() == typeof(Player))
                    {
                        //TODO Make sure that the npc can target the player
                        if (this != en && !TargetHasStealth(en))
                        {
                            Target = en;
                        }
                    }
                    else
                    {
                        if (this != en && !TargetHasStealth(en))
                        {
                            Target = en;
                        }
                    }
                }

                // Are we configured to handle resetting NPCs after they chase a target for a specified amount of tiles?
                if (Options.Npc.AllowResetRadius)
                {
                    // Are we configured to allow new reset locations before they move to their original location, or do we simply not have an original location yet?
                    if (Options.Npc.AllowNewResetLocationBeforeFinish || AggroCenterMap == null)
                    {
                        AggroCenterMap = Map;
                        AggroCenterX = X;
                        AggroCenterY = Y;
                        AggroCenterZ = Z;
                    }
                }
            }
            else
            {
                Target = en;
            }
            
            if (Target != oldTarget)
            {
                CombatTimer = Timing.Global.Milliseconds + Options.CombatTime;
                PacketSender.SendNpcAggressionToProximity(this);
            }
            mTargetFailCounter = 0;
        }

        public void RemoveFromDamageMap(Entity en)
        {
            DamageMap.TryRemove(en, out _);
        }

        public void RemoveTarget()
        {
            AssignTarget(null);
        }

        public override int CalculateAttackTime()
        {
            if (Base.AttackSpeedModifier == 1) //Static
            {
                return Base.AttackSpeedValue;
            }

            return base.CalculateAttackTime();
        }

        public override bool CanAttack(Entity entity, SpellBase spell)
        {
            if (!base.CanAttack(entity, spell))
            {
                return false;
            }

            if (IsExhausted)
            {
                return false;
            }

            if (entity.GetType() == typeof(EventPageInstance))
            {
                return false;
            }

            //Check if the attacker is stunned or blinded.
            foreach (var status in CachedStatuses)
            {
                if (status.Type == StatusTypes.Stun || status.Type == StatusTypes.Sleep)
                {
                    return false;
                }
            }

            if (TargetHasStealth(entity))
            {
                return false;
            }

            if (entity.GetType() == typeof(Resource))
            {
                if (!entity.Passable)
                {
                    return false;
                }
            }
            else if (entity.GetType() == typeof(Npc))
            {
                return CanNpcCombat(entity, spell != null && spell.Combat.Friendly) || entity == this;
            }
            else if (entity.GetType() == typeof(Player))
            {
                var player = (Player) entity;
                if (player.PlayerDead || player.FadeWarp)
                {
                    return false;
                }
                var friendly = spell != null && spell.Combat.Friendly;
                if (friendly && IsAllyOf(player))
                {
                    return true;
                }

                if (!friendly && !IsAllyOf(player))
                {
                    return true;
                }

                return false;
            }

            return true;
        }

        public bool CanNpcCombat(Entity enemy, bool friendly = false)
        {
            //Check for NpcVsNpc Combat, both must be enabled and the attacker must have it as an enemy or attack all types of npc.
            if (!friendly)
            {
                if (enemy != null && enemy.GetType() == typeof(Npc) && Base != null)
                {
                    if (((Npc) enemy).Base.NpcVsNpcEnabled == false)
                    {
                        return false;
                    }

                    if (Base.AttackAllies && ((Npc) enemy).Base == Base)
                    {
                        return true;
                    }

                    for (var i = 0; i < Base.AggroList.Count; i++)
                    {
                        if (NpcBase.Get(Base.AggroList[i]) == ((Npc) enemy).Base)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (enemy != null && enemy.GetType() == typeof(Player))
                {
                    return true;
                }
            }
            else
            {
                if (enemy != null &&
                    enemy.GetType() == typeof(Npc) &&
                    Base != null &&
                    ((Npc) enemy).Base == Base &&
                    Base.AttackAllies == false)
                {
                    return true;
                }
                else if (enemy != null && enemy.GetType() == typeof(Player))
                {
                    return false;
                }
            }

            return false;
        }

        private static bool PredicateStunnedOrSleeping(Status status)
        {
            switch (status?.Type)
            {
                case StatusTypes.Sleep:
                case StatusTypes.Stun:
                    return true;
                default:
                    return false;
            }
        }

        private static bool PredicateStatusMovementBlocked(Status status)
        {
            switch (status?.Type)
            {
                case StatusTypes.Snare:
                case StatusTypes.Sleep:
                case StatusTypes.Stun:
                    return true;
                default:
                    return false;
            }
        }

        private static bool PredicateUnableToCastSpells(Status status)
        {
            switch (status?.Type)
            {
                case StatusTypes.Silence:
                case StatusTypes.Sleep:
                case StatusTypes.Stun:
                    return true;
                default:
                    return false;
            }
        }

        public bool IsWandering => Target == null && !mResetting && !IsFleeing();

        public override int CanMove(int moveDir)
        {
            var canMove = base.CanMove(moveDir);

            // If configured & blocked by an entity, ignore the entity and proceed to move
            if (Options.Instance.NpcOpts.IntangibleDuringReset && canMove > -1 )
            {
                canMove = mResetting ? -1 : canMove;
            }

            var tile = new TileHelper(MapId, X, Y);
            var validTile = tile.Translate(moveDir);

            var distanceToDesiredTile = GetDistanceBetween(SpawnMap, tile.GetMap(), SpawnX, tile.GetX(), SpawnY, tile.GetY());
            // If we're in wander state
            if (IsWandering && distanceToDesiredTile >= Options.Npc.WanderRadius)
            {
                return -2;
            }

            if ((canMove == -1 || canMove == -4) 
                && IsFleeing() 
                && Options.Instance.NpcOpts.AllowResetRadius
                && validTile)
            {
                //If this would move us past our reset radius then we cannot move.
                var dist = GetDistanceBetween(AggroCenterMap, tile.GetMap(), AggroCenterX, tile.GetX(), AggroCenterY, tile.GetY());
                if (dist > Math.Max(Options.Npc.ResetRadius, Base.ResetRadius))
                {
                    return -2;
                }
            }
            return canMove;
        }

        private void TryCastSpells()
        {
            if (CastFreq >= Timing.Global.Milliseconds)
            {
                return;
            }

            // If we are in the middle of a casting chain, nothing can stop us!
            var forced = ChainedSpell != Guid.Empty;

            // Add some randomness to spellcasting - especially at first cast
            if (!Base.NeverSkipSpellCasting && !forced)
            {
                var upperBound = CastFreq == default ? 3 : 11;
                if (Randomization.Next(1, upperBound) == 1)
                {
                    ProgressCastFrequency();
                    return;
                }
            }

            var target = Target;

            if (target == null || mPathFinder.GetTarget() == null)
            {
                CancelSpellChain();
                return;
            }

            // Check if NPC is stunned/sleeping
            if (IsStunnedOrSleeping)
            {
                CancelSpellChain();
                return;
            }

            //Check if NPC is casting a spell
            if (CastTime > Timing.Global.Milliseconds)
            {
                return; //can't move while casting
            }

            // Check if the NPC is able to cast spells
            if (IsUnableToCastSpells)
            {
                CancelSpellChain();
                return;
            }

            if (Base.Spells == null || Base.Spells.Count <= 0)
            {
                return;
            }

            // Pick a random spell
            var spellSlotIdx = -1;
            if (!SpellBase.TryGet(ChainedSpell, out var spellDescriptor))
            {
                if (!TrySelectSpell(out spellDescriptor, out spellSlotIdx))
                {
                    return;
                }
            }

            if (spellDescriptor.Combat == null)
            {
                Log.Warn($"Combat data missing for {spellDescriptor.Id}.");
            }

            var range = spellDescriptor.GetRange();
            var targetType = spellDescriptor.Combat?.TargetType ?? SpellTargetTypes.Single;
            var projectileBase = spellDescriptor.Combat?.Projectile;

            // Aim at the target if casting a projectile spell
            if (Options.Instance.CombatOpts.NpcSpellAiming && SpellIsAimableAt(spellDescriptor, target))
            {
                var dirToEnemy = DirToEnemy(target);
                if (dirToEnemy != Dir)
                {
                    if (LastRandomMove >= Timing.Global.Milliseconds)
                    {
                        return;
                    }

                    //Face the target -- next frame fire -- then go on with life
                    ChangeDir(dirToEnemy); // Gotta get dir to enemy
                    IncrementRandomMoveTimer();

                    return;
                }
            }

            if (spellDescriptor.VitalCost == null)
            {
                return;
            }

            if (spellDescriptor.VitalCost[(int) Vitals.Mana] > GetVital(Vitals.Mana))
            {
                return;
            }

            if (spellDescriptor.VitalCost[(int) Vitals.Health] > GetVital(Vitals.Health))
            {
                return;
            }

            // Check cooldown status if we're not within a spell chain
            if (!forced)
            {
                var spell = Spells[spellSlotIdx];
                if (spell == null)
                {
                    return;
                }

                if (SpellCooldowns.ContainsKey(spell.SpellId) && SpellCooldowns[spell.SpellId] >= Timing.Global.MillisecondsUtc)
                {
                    return;
                }
            }

            if (!forced && !InRangeOf(target, range) && targetType == SpellTargetTypes.Single)
            {
                // ReSharper disable once SwitchStatementMissingSomeCases
                return;
            }

            CastTime = Timing.Global.Milliseconds + spellDescriptor.CastDuration;

            if ((spellDescriptor.Combat?.Friendly ?? false) && spellDescriptor.SpellType != SpellTypes.WarpTo)
            {
                CastTarget = this;
            }
            else
            {
                CastTarget = target;
            }

            PrepNextSpell(spellDescriptor);
            NextSpell = spellDescriptor;
            SpellCastSlot = spellSlotIdx;

            if (spellDescriptor.CastAnimationId != Guid.Empty)
            {
                PacketSender.SendAnimationToProximity(spellDescriptor.CastAnimationId, 1, Id, MapId, 0, 0, (sbyte) Dir, MapInstanceId);

                //Target Type 1 will be global entity
            }

            InterruptThreshold = -1;
            if (spellDescriptor.InterruptThreshold > 0 && spellDescriptor.CastDuration > 0)
            {
                InterruptThreshold = (int)Math.Floor(spellDescriptor.InterruptThreshold * ScaleFactor);
                PacketSender.SendCombatNumber(CombatNumberType.Interrupt, this, 0, threshold: InterruptThreshold);
            }

            PacketSender.SendEntityCastTime(this, spellDescriptor.Id);
        }

        private void IncrementRandomMoveTimer()
        {
            LastRandomMove = Timing.Global.Milliseconds + Randomization.Next(800, 2500);
        }

        public void ProgressCastIndex()
        {
            if (!Base.SequentialCasting)
            {
                return;
            }

            CastIndex++;
            if (CastIndex >= Base.Spells.Count)
            {
                CastIndex = 0;
            }
        }

        public bool TrySelectSpell(out SpellBase spellDescriptor, out int spellSlotIdx)
        {
            spellDescriptor = null;
            spellSlotIdx = 0;


            if (Base == null || Base.Spells.Count == 0)
            {
                return false;
            }
            var spells = Base.Spells.ToArray();

            if (!Base.SequentialCasting)
            {
                // Pick a random spell
                spellSlotIdx = Randomization.Next(0, spells.Length);
                var spellId = spells[spellSlotIdx];
                if (!SpellBase.TryGet(spellId, out spellDescriptor))
                {
                    return false;
                }
            }
            else
            {
                if (Base.Spells.Count <= 0)
                {
                    return false;
                }

                if (CastIndex >= spells.Length)
                {
                    CastIndex = 0;
                }
                var spellId = spells[CastIndex];
                if (!SpellBase.TryGet(spellId, out spellDescriptor))
                {
                    return false;
                }
                spellSlotIdx = CastIndex;
            }

            // Not sure why this doesn't short-circuit but just leaving for now
            if (spellDescriptor?.Combat == null)
            {
                Log.Warn($"Combat data missing for {spellDescriptor.Id}.");
            }

            return true;
        }

        public void ProgressCastFrequency()
        {

            switch (Base.SpellFrequency)
            {
                case 0:
                    CastFreq = Timing.Global.Milliseconds + 30000;

                    break;

                case 1:
                    CastFreq = Timing.Global.Milliseconds + 15000;

                    break;

                case 2:
                    CastFreq = Timing.Global.Milliseconds + 8000;

                    break;

                case 3:
                    CastFreq = Timing.Global.Milliseconds + 4000;

                    break;

                case 4:
                    CastFreq = Timing.Global.Milliseconds + 2000;

                    break;
            }
        }

        public bool IsFleeing()
        {
            if (Base.FleeHealthPercentage == 0)
            {
                return false;
            }
            
            var fleeHpCutoff = GetMaxVital(Vitals.Health) * ((float)Base.FleeHealthPercentage / 100f);
            if (GetVital(Vitals.Health) < fleeHpCutoff || (Base.FleeHealthPercentage >= 100 && Target != default))
            {
                return true;
            }

            return false;
        }

        private bool TryRefreshThreatLevels(long timeMs)
        {
            if (timeMs <= LastThreatLevelReset)
            {
                return false;
            }

            LastThreatLevelReset = timeMs + Options.Instance.CombatOpts.ResetThreatLevelAggroTime;
            PlayerThreatLevels.Clear();

            return true;
        }

        private bool TryRefreshScaling()
        {
            if (DamageMap.Count <= 0)
            {
                return false;
            }
            
            LastAggressorCount = AggressorCount;
            AggressorCount = GetAggressorCount();

            // Has the amount of aggressors changed?
            if (AggressorCount != LastAggressorCount)
            {
                // Scale us - this function will check if scaling is truly necessary
                ScaleToAggressors();
                return true;
            }

            return false;
        }

        // TODO: Improve NPC movement to be more fluid like a player
        //General Updating
        public override void Update(long timeMs)
        {
            var lockObtained = false;
            try
            {
                Monitor.TryEnter(EntityLock, ref lockObtained);
                if (!lockObtained)
                {
                    return;
                }

                base.Update(timeMs);

                // If this NPC is a champion, but has been around too long, remove it.
                if (Base.IsChampion && Timing.Global.MillisecondsUtc - SpawnedAt >= Options.Combat.ChampionDespawnTimeSeconds * 1000)
                {
                    Die(false);
                    return;
                }

                if (IsStunnedOrSleeping || IsExhausted)
                {
                    return;
                }

                // If we don't have a target, keep the attack timer at bay. This prevents unfair insta-attacking when aggro is received
                if (Target == null)
                {
                    IncrementAttackTimer(0.5f);
                    CastFreq = timeMs + 1000;
                }

                TryRefreshThreatLevels(timeMs);

                TryRefreshScaling();

                MoveNpc(timeMs);
            }
            finally
            {
                if (lockObtained)
                {
                    Monitor.Exit(EntityLock);
                }
            }
        }

        private sbyte GetFleeDirection(sbyte dir)
        {
            switch (dir)
            {
                case 0:
                    return 1;

                case 1:
                    return 0;

                case 2:
                    return 2;

                case 3:
                    return 3;

                default: 
                    return 0;
            }
        }

        private void MoveNpc(long timeMs)
        {
            var curMapLink = MapId;
            var tempTarget = Target;
            var fleeing = IsFleeing();
            if (MoveTimer < Timing.Global.Milliseconds)
            {
                var targetMap = Guid.Empty;
                var targetX = 0;
                var targetY = 0;
                var targetZ = 0;

                //TODO Clear Damage Map if out of combat (target is null and combat timer is to the point that regen has started)
                if (tempTarget != null && Options.Instance.NpcOpts.ResetIfCombatTimerExceeded && Timing.Global.Milliseconds > CombatTimer)
                {
                    if (CheckForResetLocation(true))
                    {
                        if (Target != tempTarget)
                        {
                            PacketSender.SendNpcAggressionToProximity(this);
                        }
                        return;
                    }
                }

                // Are we resetting? If so, regenerate completely!
                if (mResetting)
                {
                    var distance = GetDistanceTo(AggroCenterMap, AggroCenterX, AggroCenterY);
                    // Have we reached our destination? If so, clear it.
                    if (distance < 1)
                    {
                        ResetAggroCenter(out targetMap);
                    }

                    ResetNpc(Options.Instance.NpcOpts.ContinuouslyResetVitalsAndStatuses);
                    tempTarget = Target;

                    if (distance != mResetDistance)
                    {
                        mResetDistance = distance;
                    }
                    else
                    {
                        // Something is fishy here.. We appear to be stuck in a reset loop?
                        // Give it a few more attempts and reset the NPC's center if we're stuck!
                        mResetCounter++;
                        if (mResetCounter > mResetMax)
                        {
                            ResetAggroCenter(out targetMap);
                            mResetCounter = 0;
                            mResetDistance = 0;
                        }
                    }

                }

                if (tempTarget != null && (tempTarget.IsDead() || !InRangeOf(tempTarget, Options.MapWidth * 2)))
                {
                    TryFindNewTarget(Timing.Global.Milliseconds, tempTarget.Id);
                    tempTarget = Target;
                }

                //Check if there is a target, if so, run their ass down.
                if (tempTarget != null)
                {
                    if (!tempTarget.IsDead() && CanAttack(tempTarget, null))
                    {
                        targetMap = tempTarget.MapId;
                        targetX = tempTarget.X;
                        targetY = tempTarget.Y;
                        targetZ = tempTarget.Z;
                        foreach (var targetStatus in tempTarget.CachedStatuses)
                        {
                            if (targetStatus.Type == StatusTypes.Stealth)
                            {
                                targetMap = Guid.Empty;
                                targetX = 0;
                                targetY = 0;
                                targetZ = 0;
                            }
                        }
                    }
                }
                else //Find a target if able
                {
                    // Check if attack on sight or have other npc's to target
                    TryFindNewTarget(timeMs);
                    tempTarget = Target;
                }

                if (targetMap != Guid.Empty)
                {
                    //Check if target map is on one of the surrounding maps, if not then we are not even going to look.
                    if (targetMap != MapId)
                    {
                        var found = false;
                        foreach (var map in MapController.Get(MapId).SurroundingMaps)
                        {
                            if (map.Id == targetMap)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            targetMap = Guid.Empty;
                        }
                    }
                }

                if (targetMap != Guid.Empty)
                {
                    if (mPathFinder.GetTarget() != null)
                    {
                        if (targetMap != mPathFinder.GetTarget().TargetMapId ||
                            targetX != mPathFinder.GetTarget().TargetX ||
                            targetY != mPathFinder.GetTarget().TargetY)
                        {
                            mPathFinder.SetTarget(null);
                        }
                    }

                    if (mPathFinder.GetTarget() == null)
                    {
                        mPathFinder.SetTarget(new PathfinderTarget(targetMap, targetX, targetY, targetZ));

                        if (tempTarget != Target)
                        {
                            tempTarget = Target;
                        }
                    }

                }

                if (mPathFinder.GetTarget() != null && Movement != NpcMovement.Static)
                {
                    TryCastSpells();

                    var isPursuing = !mResetting && !PathIsOneBlockAway();
                    var isResetting = mResetting && GetDistanceTo(AggroCenterMap, AggroCenterX, AggroCenterY) != 0;

                    if (isPursuing || isResetting)
                    {
                        switch (mPathFinder.Update(timeMs))
                        {
                            case PathfinderResult.Success:

                                var dir = mPathFinder.GetMove();
                                if (dir > -1)
                                {
                                    if (fleeing)
                                    {
                                        dir = GetFleeDirection(dir);
                                    }

                                    if (CanMove(dir) == -1 || CanMove(dir) == -4)
                                    {
                                        //check if NPC is snared or stunned
                                        foreach (var status in CachedStatuses)
                                        {
                                            if (status.Type == StatusTypes.Stun ||
                                                status.Type == StatusTypes.Snare ||
                                                status.Type == StatusTypes.Sleep)
                                            {
                                                return;
                                            }
                                        }

                                        Move((byte)dir, null);
                                    }
                                    else
                                    {
                                        if (Base.StandStill && Target != null)
                                        {
                                            var dirTarget = GetDirectionTo(Target);
                                            if (mLastTargetDir < 0 || mLastTargetDir != dirTarget)
                                            {
                                                mLastTargetDir = dirTarget;
                                                Dir = dirTarget;
                                                PacketSender.SendEntityDir(this);
                                            }
                                            if ((Base.SpellAttackOverrideId == default && CanAttack(Target, null)) || (Base.SpellAttackOverrideId != default && CanAttack(Target, SpellBase.Get(Base.SpellAttackOverrideId))))
                                            {
                                                MeleeAttack(Target, false);
                                            }
                                        }
                                        mPathFinder.PathFailed(timeMs);
                                    }

                                    // Were we resetting and made it back to our pull point?
                                    if (mResetting && IsAtResetPoint)
                                    {
                                        ResetAggroCenter(out targetMap);
                                        IncrementRandomMoveTimer();
                                    }
                                }

                                break;
                            case PathfinderResult.OutOfRange:
                            case PathfinderResult.NoPathToTarget:
                            case PathfinderResult.Failure:
                                TryFindNewTarget(timeMs, tempTarget?.Id ?? Guid.Empty, true);
                                targetMap = Guid.Empty;

                                break;

                            case PathfinderResult.Wait:
                                targetMap = Guid.Empty;

                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    else
                    {
                        var fleed = false;
                        if (tempTarget != null && fleeing)
                        {
                            var dir = DirToEnemy(tempTarget);
                            dir = (byte)GetFleeDirection((sbyte)dir);

                            if (CanMove(dir) == -1 || CanMove(dir) == -4)
                            {
                                //check if NPC is snared or stunned
                                foreach (var status in CachedStatuses)
                                {
                                    if (status.Type == StatusTypes.Stun ||
                                        status.Type == StatusTypes.Snare ||
                                        status.Type == StatusTypes.Sleep)
                                    {
                                        return;
                                    }
                                }

                                if (Timing.Global.Milliseconds > DashTransmissionTimer)
                                {
                                    Move(dir, null);
                                    fleed = true;
                                }
                            }
                        }

                        if (!fleed)
                        {
                            if (tempTarget != null)
                            {
                                if (Dir != DirToEnemy(tempTarget) && DirToEnemy(tempTarget) != -1 && timeMs > DirChangeTime)
                                {
                                    ChangeDir(DirToEnemy(tempTarget));
                                }
                                else
                                {
                                    if (tempTarget.IsDisposed)
                                    {
                                        TryFindNewTarget(timeMs);
                                        tempTarget = Target;
                                    }
                                    else
                                    {
                                        if ((Base.SpellAttackOverrideId == default && CanAttack(Target, null)) || (Base.SpellAttackOverrideId != default && CanAttack(Target, SpellBase.Get(Base.SpellAttackOverrideId))))
                                        {
                                            MeleeAttack(tempTarget, false);
                                        }
                                    }
                                }
                            }
                        }

                        // This is done to prevent an enemy from ALWAYS, immediately, facing you.
                        if (timeMs > DirChangeTime)
                        {
                            DirChangeTime = timeMs + Options.Instance.CombatOpts.NpcDirChangeTimer - (Base.Stats[(int)Stats.Speed] * Options.Instance.CombatOpts.NpcDirChangeSpeedMult);
                        }
                    }
                }

                CheckForResetLocation();

                //Move randomly
                if (targetMap != Guid.Empty)
                {
                    return;
                }

                if (LastRandomMove >= Timing.Global.Milliseconds || CastTime > 0)
                {
                    return;
                }

                if (Movement == NpcMovement.StandStill)
                {
                    IncrementRandomMoveTimer();

                    return;
                }
                else if (Movement == NpcMovement.TurnRandomly)
                {
                    ChangeDir((byte)Randomization.Next(0, 4));
                    IncrementRandomMoveTimer();

                    return;
                }

                var i = Randomization.Next(1, 101);
                // 65% chance to move, and only if movement isn’t blocked by a status
                if (i < 65 && !IsStatusMovementBlocked)
                {
                    var validDirections = new List<int>();

                    // Add only directions that are valid
                    for (int dir = 0; dir < 4; dir++)
                    {
                        if (CanMove(dir) == -1)
                        {
                            validDirections.Add(dir);
                        }
                    }

                    // Pick a random valid direction, if any
                    if (validDirections.Count > 0)
                    {
                        Move((byte)validDirections[Randomization.Next(validDirections.Count)], null);
                    }
                }


                IncrementRandomMoveTimer();

                if (fleeing)
                {
                    LastRandomMove = Timing.Global.Milliseconds + (long)GetMovementTime();
                }
            }

            //If we switched maps, lets update the maps
            if (curMapLink != MapId)
            {
                if (curMapLink == Guid.Empty)
                {
                    if (MapController.TryGetInstanceFromMap(curMapLink, MapInstanceId, out var instance))
                    {
                        instance.RemoveEntity(this);
                    }
                }

                if (MapId != Guid.Empty)
                {
                    if (MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var instance))
                    {
                        instance.AddEntity(this);
                    }
                }
            }
        }

        private bool PathIsOneBlockAway()
        {
            if (mPathFinder == null)
            {
                return false;
            }

            var target = mPathFinder.GetTarget();
            if (target == null)
            {
                return false;
            }

            return IsOneBlockAway(target.TargetMapId, target.TargetX, target.TargetY, target.TargetZ);
        }

        /// <summary>
        /// Resets the NPCs position to be "pulled" from
        /// </summary>
        /// <param name="targetMap">For referencing the map that the enemy's target WAS on before a reset.</param>
        private void ResetAggroCenter(out Guid targetMap)
        {
            targetMap = Guid.Empty;

            // Reset our aggro center so we can get "pulled" again.
            AggroCenterMap = null;
            AggroCenterX = 0;
            AggroCenterY = 0;
            AggroCenterZ = 0;
            mPathFinder?.SetTarget(null);
            mResetting = false;
        }

        private bool IsAtResetPoint => AggroCenterMap != null && GetDistanceTo(AggroCenterMap, AggroCenterX, AggroCenterY) == 0;
        
        private bool IsOutsideRadius => AggroCenterMap != null 
            && Options.Instance.NpcOpts.AllowResetRadius 
            && GetDistanceTo(AggroCenterMap, AggroCenterX, AggroCenterY) > ResetRadius;

        private int ResetRadius => Math.Max(Options.Npc.ResetRadius, Base.ResetRadius);

        private bool CheckForResetLocation(bool forceDistance = false)
        {
            // Check if we've moved out of our range we're allowed to move from after being "aggro'd" by something.
            // If so, remove target and move back to the origin point.
            if (AggroCenterMap == null || !Options.Npc.AllowResetRadius)
            {
                return false;
            }

            if (!IsOutsideRadius && !forceDistance)
            {
                return false;
            }

            ResetNpc(Options.Npc.ResetVitalsAndStatusses);

            mResetCounter = 0;
            mResetDistance = 0;

            // Try and move back to where we came from before we started chasing something.
            mResetting = true;
            mPathFinder.SetTarget(new PathfinderTarget(AggroCenterMap.Id, AggroCenterX, AggroCenterY, AggroCenterZ));
            return true;
        }

        private void ResetNpc(bool resetVitals = true, bool clearLocation = false)
        {
            // Remove our target.
            RemoveTarget();

            DamageMap.Clear();
            LootMap.Clear();
            LootMapCache = Array.Empty<Guid>();

            if (clearLocation)
            {
                mPathFinder.SetTarget(null);
                AggroCenterMap = null;
                AggroCenterX = 0;
                AggroCenterY = 0;
                AggroCenterZ = 0;
            }
            
            // Reset our vitals and statusses when configured.
            if (resetVitals)
            {
                Statuses.Clear();
                CachedStatuses = Statuses.Values.ToArray();
                DoT.Clear();
                CachedDots = DoT.Values.ToArray();
                for (var v = 0; v < (int)Vitals.VitalCount; v++)
                {
                    RestoreVital((Vitals)v);
                }
            }
        }

        public override void Reset()
        {
            if (AggroCenterMap != null)
            {
                Warp(AggroCenterMap.Id, AggroCenterX, AggroCenterY);
            }

            ResetNpc(true, true);
        }

        public override void NotifySwarm(Entity attacker)
        {
            if (!MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var instance))
            {
                return;
            }
            
            foreach (var en in instance.GetEntities(true))
            {
                if (en.GetType() != typeof(Npc))
                {
                    continue;
                }
                var npc = (Npc)en;
                
                if (npc.Target != null | !npc.Base.Swarm || !IsAllyOf(npc) || !npc.Base.Aggressive && npc.Base.Id != Base.Id)
                {
                    continue;
                }

                // Swarm if an aggressive enemy, or it's direct relative is being attacked
                var range = Range;
                if (attacker is Player player)
                {
                    range = (int)Math.Ceiling(range * player.GetBonusEffectPercent(EffectType.Phantom, false));
                }

                if (npc.InRangeOf(attacker, range))
                {
                    npc.AssignTarget(attacker);
                    npc.EnemySighted(attacker);
                }
            }
        }

        public bool CanPlayerAttack(Player en)
        {
            //Check to see if the npc is a friend/protector...
            if (IsAllyOf(en))
            {
                return false;
            }

            if (Base.VulnerableOnlyWhenExhausted && !IsExhausted)
            {
                return false;
            }

            //If not then check and see if player meets the conditions to attack the npc...
            if (Base.PlayerCanAttackConditions.Lists.Count != 0 && !Conditions.MeetsConditionLists(Base.PlayerCanAttackConditions, en, null))
            {
                return false;
            }

            return true;
        }

        public override bool IsAllyOf(Entity otherEntity)
        {
            switch (otherEntity)
            {
                case Npc otherNpc:
                    return Base == otherNpc.Base || !CanNpcCombat(otherEntity);
                case Player otherPlayer:
                    var conditionLists = Base.PlayerFriendConditions;
                    if ((conditionLists?.Count ?? 0) == 0)
                    {
                        return false;
                    }

                    return Conditions.MeetsConditionLists(conditionLists, otherPlayer, null);
                default:
                    return base.IsAllyOf(otherEntity);
            }
        }

        public bool ShouldAttackPlayerOnSight(Player player)
        {
            if (player == null)
            {
                return false;
            }

            if (IsAllyOf(player))
            {
                return false;
            }

            if (InstanceProcessor.TryGetInstanceController(player.MapInstanceId, out var instanceController) && instanceController.Dungeon?.State == DungeonState.Complete)
            {
                return false;
            }

            var aggroConditions = Base.AttackOnSightConditions.Lists.Count > 0 && Conditions.MeetsConditionLists(Base.AttackOnSightConditions, player, null);

            if (Base.Aggressive)
            {
                if (aggroConditions)
                {
                    return false;
                }

                return IsOverworldDefaultAggroToward(player) || IsDungeonAggroToward(player);
            }
            else
            {
                if (aggroConditions)
                {
                    return true;
                }
            }

            return false;
        }

        public void TryFindNewTarget(long timeMs, Guid avoidId = new Guid(), bool ignoreTimer = false, Entity attackedBy = null)
        {
            if (!ignoreTimer && FindTargetWaitTime > timeMs)
            {
                return;
            }

            // Are we resetting? If so, do not allow for a new target.
            var pathTarget = mPathFinder?.GetTarget();
            if (AggroCenterMap != null && pathTarget != null &&
                pathTarget.TargetMapId == AggroCenterMap.Id && pathTarget.TargetX == AggroCenterX && pathTarget.TargetY == AggroCenterY)
            {
                if (!Options.Instance.NpcOpts.AllowEngagingWhileResetting || attackedBy == null || attackedBy.GetDistanceTo(AggroCenterMap, AggroCenterX, AggroCenterY) > Math.Max(Options.Instance.NpcOpts.ResetRadius, Base.ResetRadius))
                {
                    return;
                }
                else
                {
                    //We're resetting and just got attacked, and we allow reengagement.. let's stop resetting and fight!
                    mPathFinder?.SetTarget(null);
                    mResetting = false;
                    AssignTarget(attackedBy);
                    return;
                }
            }

            var possibleTargets = new List<Entity>();
            var closestRange = Range + 1; //If the range is out of range we didn't find anything.
            var closestIndex = -1;
            var highestDmgIndex = -1;
           
            if (DamageMap.Count > 0)
            {
                // Go through all of our potential targets in order of damage done as instructed and select the first matching one.
                long highestDamage = 0;
                foreach (var en in DamageMap.ToArray())
                {
                    // Are we supposed to avoid this one?
                    if (en.Key.Id == avoidId)
                    {
                        continue;
                    }
                    
                    // Is this entry dead?, if so skip it.
                    if (en.Key.IsDead())
                    {
                        continue;   
                    }

                    // Is this entity on our instance anymore? If not skip it, but don't remove it in case they come back and need item drop determined
                    if (en.Key.MapInstanceId != MapInstanceId)
                    {
                        continue;
                    }

                    // Are we at a valid distance? (9999 means not on this map or somehow null!)
                    if (GetDistanceTo(en.Key) != 9999)
                    {
                        possibleTargets.Add(en.Key);

                        // Do we have the highest damage?
                        if (en.Value > highestDamage)
                        {
                            highestDmgIndex = possibleTargets.Count - 1;
                            highestDamage = en.Value;
                        }    
                        
                    }
                }
            }

            // Scan for nearby targets
            foreach (var instance in MapController.GetSurroundingMapInstances(MapId, MapInstanceId, true))
            {
                foreach (var entity in instance.GetCachedEntities())
                {
                    if (entity is Player pl && pl.PlayerDead)
                    {
                        continue;
                    }
                    if (entity != null && !entity.IsDead() && entity != this && entity.Id != avoidId)
                    {
                        //TODO Check if NPC is allowed to attack player with new conditions
                        if (entity is Player player)
                        {
                            // Are we aggressive towards this player or have they hit us?
                            if (ShouldAttackPlayerOnSight((Player)entity) || (DamageMap.ContainsKey(entity) && entity.MapInstanceId == MapInstanceId))
                            {
                                var dist = GetDistanceTo(entity);
                                var playerRange = Math.Ceiling(Range * player.GetBonusEffectPercent(EffectType.Phantom, false));

                                if (dist <= playerRange && dist < closestRange)
                                {
                                    possibleTargets.Add(entity);
                                    closestIndex = possibleTargets.Count - 1;
                                    closestRange = dist;
                                }
                            }
                        }
                        else if (entity.GetType() == typeof(Npc))
                        {
                            if (Base.Aggressive && Base.AggroList.Contains(((Npc)entity).Base.Id))
                            {
                                var dist = GetDistanceTo(entity);
                                if (dist <= Range && dist < closestRange)
                                {
                                    possibleTargets.Add(entity);
                                    closestIndex = possibleTargets.Count - 1;
                                    closestRange = dist;
                                }
                            }
                        }
                    }
                }
            }

            // Assign our target if we've found one!
            if (Base.FocusHighestDamageDealer && highestDmgIndex != -1)
            {
                // We're focussed on whoever has the most threat! o7
                AssignTarget(possibleTargets[highestDmgIndex]);
            }
            else if (Target != null && possibleTargets.Count > 0)
            {
                // Time to randomize who we target.. Since we don't actively care who we attack!
                // 10% chance to just go for someone else.
                if (Randomization.Next(1, 101) > 90)
                {
                    if (possibleTargets.Count > 1)
                    {
                        var target = Randomization.Next(0, possibleTargets.Count - 1);
                        AssignTarget(possibleTargets[target]);
                    }
                    else
                    {
                        AssignTarget(possibleTargets[0]);
                    }
                }
            }
            else if (Target == null && Base.Aggressive && closestIndex != -1)
            {
                // Aggressively attack closest person!
                AssignTarget(possibleTargets[closestIndex]);
                EnemySighted(possibleTargets[closestIndex]);
            }
            else if (possibleTargets.Count > 0)
            {
                // Not aggressive but no target, so just try and attack SOMEONE on the damage table!
                if (possibleTargets.Count > 1)
                {
                    var target = Randomization.Next(0, possibleTargets.Count - 1);
                    AssignTarget(possibleTargets[target]);
                    EnemySighted(possibleTargets[target]);
                }
                else
                {
                    AssignTarget(possibleTargets[0]);
                    EnemySighted(possibleTargets[0]);
                }
            }
            else
            {
                // ??? What the frick is going on here?
                // We can't find a valid target somehow, keep it up a few times and reset if this keeps failing!
                mTargetFailCounter += 1;
                if (mTargetFailCounter > mTargetFailMax)
                {
                    CheckForResetLocation(true);
                }
            }

            FindTargetWaitTime = timeMs + FindTargetDelay;
        }

        public override float GetVitalRegenRate(int vital)
        {
            if (Base == null)
            {
                return 0f;
            }

            return Base.VitalRegen[vital] / 100f;
        }

        private void EnemySighted(Entity target)
        {
            var dirToEnemy = DirToEnemy(target);
            if (dirToEnemy != Dir)
            {
                ChangeDir(dirToEnemy);
            }

            ExhaustionEndTime = Timing.Global.MillisecondsUtc + Options.Instance.CombatOpts.AggroSurpriseTime;
            
            if (!(target is Player player))
            {
                return;
            }

            PacketSender.SendAnimationTo(Guid.Parse(Options.Instance.CombatOpts.AggroAnimationId),
                1,
                Id,
                MapId,
                (byte)X,
                (byte)Y,
                (sbyte)Directions.Up,
                MapInstanceId,
                player);
            PacketSender.SendCombatEffectPacket(player.Client, Id, 0.0f, Color.White, string.Empty, 0f, 0f, Color.White);
        }

        public override void Warp(
            Guid newMapId,
            float newX,
            float newY,
            byte newDir,
            bool adminWarp = false,
            byte zOverride = 0,
            bool mapSave = false,
            bool fromWarpEvent = false,
            MapInstanceType mapInstanceType = MapInstanceType.NoChange,
            bool fromLogin = false,
            bool forceInstanceChange = false, 
            int instanceLives = 0
        )
        {
            if (!MapController.TryGetInstanceFromMap(newMapId, MapInstanceId, out var map))
            {
                return;
            }

            X = (int)newX;
            Y = (int)newY;
            Z = zOverride;
            Dir = newDir;
            if (newMapId != MapId)
            {
                if (MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var oldMap))
                {
                    oldMap.RemoveEntity(this);
                }

                PacketSender.SendEntityLeave(this);
                MapId = newMapId;
                PacketSender.SendEntityDataToProximity(this);
                PacketSender.SendEntityPositionToAll(this);
            }
            else
            {
                PacketSender.SendEntityPositionToAll(this);
                PacketSender.SendEntityStatsToProximity(this);
            }
        }

        public int GetAggression(Player player)
        {
            //Determines the aggression level of this npc to send to the player
            if (this.Target != null)
            {
                return -1;
            }
            else
            {
                //Guard = 3
                //Will attack on sight = 1
                //Will attack if attacked = 0
                //Can't attack nor can attack = 2
                var ally = IsAllyOf(player);
                var attackOnSight = ShouldAttackPlayerOnSight(player);
                var canPlayerAttack = CanPlayerAttack(player);
                if (ally && !canPlayerAttack)
                {
                    return 3;
                }

                if (attackOnSight)
                {
                    return 1;
                }

                if (!ally && !attackOnSight && canPlayerAttack)
                {
                    return 0;
                }

                if (!ally && !attackOnSight && !canPlayerAttack)
                {
                    return 2;
                }
            }

            return 2;
        }

        public override EntityPacket EntityPacket(EntityPacket packet = null, Player forPlayer = null)
        {
            if (packet == null)
            {
                packet = new NpcEntityPacket();
            }

            packet = base.EntityPacket(packet, forPlayer);

            var pkt = (NpcEntityPacket) packet;
            pkt.Aggression = GetAggression(forPlayer);
            pkt.NpcId = Base.Id;

            return pkt;
        }

    }

    public partial class Npc : AttackingEntity
    {
        public void DropIndividualizedLoot()
        {
            var lootGeneratedFor = new List<Player>();
            // Generate loot for every player that has helped damage this monster, as well as their party members.
            // Keep track of who already got loot generated for them though, or this gets messy!
            foreach (var entityEntry in LootMapCache)
            {
                var player = Player.FindOnline(entityEntry);
                if (player != null)
                {
                    // is this player in a party?
                    if (player.Party.Count > 0 && Options.Instance.LootOpts.IndividualizedLootAutoIncludePartyMembers)
                    {
                        // They are, so check for all party members and drop if still eligible!
                        foreach (var partyMember in player.Party.ToArray())
                        {
                            if (!lootGeneratedFor.Contains(partyMember))
                            {
                                DropItems(partyMember);
                                lootGeneratedFor.Add(partyMember);
                            }
                        }
                    }
                    else
                    {
                        // They're not in a party, so drop the item if still eligible!
                        if (!lootGeneratedFor.Contains(player))
                        {
                            DropItems(player);
                            lootGeneratedFor.Add(player);
                        }
                    }
                }
            }

            // Clear their loot table and threat table.
            DamageMap.Clear();
            LootMap.Clear();
            LootMapCache = Array.Empty<Guid>();
        }

        public override void DropItems(Entity killer, bool sendUpdate = true)
        {
            if (!(killer is Player))
            {
                return;
            }
            var playerKiller = killer as Player;

            // Check to see if we hit the secondary or tertiary tables
            var luck = playerKiller?.GetLuckModifier();

            Guid lootOwner = Guid.Empty;
            // Set owner to player that killed this, if there is any.
            if (playerKiller != null)
            {
                // Yes, so set the owner to the player that killed it.
                lootOwner = playerKiller.Id;
            }

            var rolledItems = new List<Item>();
            var baseDropTable = LootTableServerHelpers.GenerateDropTable(Base.Drops, playerKiller);
            rolledItems.Add(LootTableServerHelpers.GetItemFromTable(baseDropTable));

            // Check for secondary/tertiary tables
            if (Randomization.Next(1, 100001) < (Base.SecondaryChance * luck * 1000))
            {
                var secondaryDropTable = LootTableServerHelpers.GenerateDropTable(Base.SecondaryDrops, playerKiller);
                rolledItems.Add(LootTableServerHelpers.GetItemFromTable(secondaryDropTable));
            }
            if (Randomization.Next(1, 100001) < (Base.TertiaryChance * luck * 1000))
            {
                var tertiaryDropTable = LootTableServerHelpers.GenerateDropTable(Base.TertiaryDrops, playerKiller);
                rolledItems.Add(LootTableServerHelpers.GetItemFromTable(tertiaryDropTable));
            }

            var ownershipTime = Base.PlayerLockedLoot ? long.MaxValue : -1;

            LootTableServerHelpers.SpawnItemsOnMap(rolledItems, MapId, MapInstanceId, X, Y, lootOwner, sendUpdate, ownershipTime: ownershipTime);
        }

        public bool IsOverworldDefaultAggroToward(Player player)
        {
            if (!PlayerThreatLevels.TryGetValue(player.Id, out var threatLevel))
            {
                threatLevel = SetThreatLevelFor(player);
            }

            return Map?.ZoneType != MapZones.Safe || threatLevel < ThreatLevel.Wimpy;
        }

        public ThreatLevel SetThreatLevelFor(Player player)
        {
            if (Base == null)
            {
                return ThreatLevel.Trivial;
            }

            var playerProjectile = Guid.Empty;
            if (player.TryGetEquippedItem(Options.WeaponIndex, out var playerWeapon)) 
            {
                playerProjectile = playerWeapon.Descriptor?.ProjectileId ?? Guid.Empty;
            }

            var damageScalar = 100;
            if (Base.IsSpellcaster)
            {
                Globals.CachedNpcSpellScalar.TryGetValue(Base.Id, out damageScalar);
            }

            var threatLevel = ThreatLevelUtilities.DetermineNpcThreatLevel(player.MaxVitals,
                player.StatVals,
                Base.MaxVital,
                Base.Stats,
                player.GetMeleeAttackTypes(),
                Base.AttackTypes,
                player.GetRawAttackSpeed(),
                Base.AttackSpeedValue,
                playerProjectile != Guid.Empty,
                Base.IsSpellcaster,
                damageScalar);

            PlayerThreatLevels[player.Id] = threatLevel;

            return threatLevel;
        }

        public bool IsDungeonAggroToward(Player player)
        {
            if (player == null)
            {
                return false;
            }
            
            // These instance types are used when we ALWAYS want aggro NPCs within
            if (player.InstanceType == MapInstanceType.PersonalDungeon || player.InstanceType == MapInstanceType.PartyDungeon || player.InstanceType == MapInstanceType.ClanWar)
            {
                return true;
            }
            
            if (player.MapInstanceId == MapInstanceId && player.InstanceType != MapInstanceType.Overworld
                && InstanceProcessor.TryGetInstanceController(MapInstanceId, out var instanceController))
            {
                return instanceController.Dungeon?.State == DungeonState.Active;
            }
            return false;
        }

        /// <summary>
        /// Adds an attacking entity to this NPC's damage map
        /// </summary>
        /// <param name="attacker">The attacking entity</param>
        /// <param name="newDamage">The amount of damage to add</param>
        public void AddToDamageAndLootMaps(Entity attacker, int newDamage)
        {
            if (attacker == null || attacker.Id == Id || newDamage < 0)
            {
                return;
            }

            if (attacker is Player player)
            {
                newDamage = (int)Math.Round(newDamage * player.GetBonusEffectPercent(EffectType.Phantom, false));
            }

            var dmgMap = DamageMap;
            dmgMap.TryGetValue(attacker, out var damage);
            dmgMap[attacker] = damage + newDamage;

            LootMap.TryAdd(attacker?.Id ?? Guid.Empty, true);
            LootMapCache = LootMap.Keys.ToArray();
            TryFindNewTarget(Timing.Global.Milliseconds, default, false, attacker);
        }

        public override bool IsNonTrivialTo(Player player)
        {
            if (player == null)
            {
                return false;
            }

            
            if (player.Party != null && player.Party.Count > 2)
            {
                return player.GetThreatLevelInPartyFor(this) < ThreatLevel.Trivial;
            }
            return SetThreatLevelFor(player) < ThreatLevel.Trivial;
        }

        [NotMapped, JsonIgnore]
        public override int TierLevel => Base?.Level ?? 0;

        [NotMapped, JsonIgnore]
        public int MaxSpellScalar => Base.IsSpellcaster ? Globals.CachedNpcSpellScalar[Base.Id] : 100;

        /// <summary>
        /// Gets the total number of players considered to be aggressors, for difficulty scaling purposes.
        /// </summary>
        /// <returns>The number of combined unique combatants/healers</returns>
        private int GetAggressorCount()
        {
            if (Base.NpcScaleType == (int)NpcScaleType.PlayersInInstance && InstanceProcessor.TryGetInstanceController(MapInstanceId, out var instanceController))
            {
                // Never take _away_ from the initial max amount of aggressors. This prevents people from leaving the instance to cheese scaling after the mob has spawned
                return Math.Max(instanceController.PlayerCount, AggressorCount);
            }

            var aggressorMap = DamageMap.Keys.Select(en => en.Id).ToList();

            foreach (var entity in DamageMap.Keys)
            {
                aggressorMap.AddRange(entity.RecentAllies.Keys);
            }

            return aggressorMap.Distinct().ToArray().Length;
        }

        /// <summary>
        /// Takes care of scaling NPC's stats/vitals that have scaling enabled
        /// </summary>
        private void ScaleToAggressors()
        {
            if (Base.NpcScaleType == (int)NpcScaleType.None || Base.ScaledTo <= 0)
            {
                return;
            }

            // Reset to default values if we're not currently in need of scaling
            if (!ShouldScale)
            {
                ScaleVitals(1.0f);
            }
            // Scale values according to aggressor count (attackers & attacker allies - the amount of players the content is scaled to)
            else
            {
                ScaleVitals(ScaleFactor);
            }
        }

        private float ScaleFactor
        {
            get
            {
                var scalingAggressors = Math.Min(AggressorCount - Base.ScaledTo, Base.MaxScaledTo);
                return 1.0f + (scalingAggressors * Base.VitalScaleModifier);
            }
        }

        private bool ShouldScale => AggressorCount > Base.ScaledTo;

        private void ScaleVitals(float factor)
        {
            for (var vital = 0; vital < (int)Vitals.VitalCount; vital++)
            {
                float vitalRatio = GetVital(vital) / (float)GetMaxVital(vital);
                
                SetMaxVital(vital, (int)Math.Round(Base.MaxVital[vital] * factor));
                
                int newVital = (int)Math.Round(GetMaxVital(vital) * vitalRatio);
                SetVital(vital, newVital);
            }
        }

        public override bool IsInvincibleTo(Entity entity)
        {
            if (entity is Player player)
            {
                return base.IsInvincibleTo(player) || !CanPlayerAttack(player);
            }
            else
            {
                return base.IsInvincibleTo(entity);
            }
        }


        protected override bool TryLifesteal(int damage, Entity target, out float recovered)
        {
            recovered = 0;

            if (IsDead())
            {
                return false;
            }

            if (damage <= 0 || target == null || target.IsDisposed || target.IsDead())
            {
                return false;
            }

            var lifesteal = (Base.MeleeLifesteal / 100f);
            if (lifesteal <= 0)
            {
                return false;
            }

            var healthRecovered = Math.Max(1, lifesteal * damage);
            if (healthRecovered <= 0)
            {
                return false;
            }

            healthRecovered = Math.Min(target.GetVital((int)Vitals.Health), healthRecovered);
            if (healthRecovered == 0)
            {
                return false;
            }

            AddVital(Vitals.Health, (int)healthRecovered);
            recovered = healthRecovered;
            return true;
        }

        protected override bool TryManasteal(int damage, Entity target, out float recovered)
        {
            recovered = 0;

            if (IsDead())
            {
                return false;
            }

            if (damage <= 0 || target == null || target.IsDisposed || target.IsDead())
            {
                return false;
            }

            var manasteal = (Base.MeleeManasteal / 100f);
            if (manasteal <= 0)
            {
                return false;
            }

            var manaRecovered = Math.Max(1, manasteal * damage);
            if (manaRecovered <= 0)
            {
                return false;
            }

            manaRecovered = Math.Min(target.GetVital((int)Vitals.Mana), manaRecovered);
            if (manaRecovered == 0)
            {
                return false;
            }

            AddVital(Vitals.Mana, (int)manaRecovered);
            target.SubVital(Vitals.Mana, (int)manaRecovered);
            recovered = manaRecovered;
            return true;
        }

        public bool OverrideMovement = false;

        public NpcMovement OverriddenMovement;

        private bool OverriddeRange = false;

        private int OverriddenRange;

        /// <summary>
        /// Determines the movement type of the spawned NPC
        /// </summary>
        public NpcMovement Movement => OverrideMovement ? OverriddenMovement : (NpcMovement)(Base?.Movement);

        public int Range => OverriddeRange ? OverriddenRange : Base.SightRange;

        public override void PlayerBackstabbed(Player player, int damage)
        {
            ChallengeUpdateProcesser.UpdateChallengesOf(new BackstabDamageUpdate(player, damage), Base.Level);
        }

        [NotMapped, JsonIgnore]
        public Guid ChainedSpell { get; set; } = Guid.Empty;

        public void PrepNextSpell(SpellBase currentSpell)
        {
            // Last check prevents infinite recursion
            if (currentSpell == null || currentSpell.ChainSpellId == Guid.Empty || currentSpell.ChainSpellId == currentSpell.Id)
            {
                ChainedSpell = Guid.Empty;
                ProgressCastFrequency();
                ProgressCastIndex();
                return;
            }

            ChainedSpell = currentSpell.ChainSpellId;
            CastFreq = Timing.Global.Milliseconds + currentSpell.ChainDelayMs;
        }

        /// <summary>
        /// Used to force cancel a spell chain
        /// </summary>
        public void CancelSpellChain()
        {
            ChainedSpell = Guid.Empty;
            ProgressCastFrequency();
        }

        /// <summary>
        /// Used to keep an enemy from moving after casting a spell.
        /// </summary>
        [NotMapped, JsonIgnore]
        public long ExhaustionEndTime { get; set; } = 0L;

        public void ProcSpellExhaustion(SpellBase spell)
        {
            if (spell == null)
            {
                return;
            }

            var customExhaustion = spell.ExhaustionCastTime > 0;
            if (!Options.Instance.CombatOpts.NpcSpellExhaustion && !customExhaustion)
            {
                return;
            }

            // Don't proc exhaustion during a spell chain
            if (ChainedSpell != Guid.Empty)
            {
                return;
            }

            if (customExhaustion)
            {
                ExhaustionEndTime = Timing.Global.MillisecondsUtc + spell.ExhaustionCastTime;
                SendExhaustion(ExhaustionEndTime);
            }
            else if (!Base.DisableAutoExhaustion)
            {
                ExhaustionEndTime = Timing.Global.MillisecondsUtc + Options.Instance.CombatOpts.NpcSpellExhaustionTime;
            }
        }

        public void ProcSpellInterruptExhaustion(long duration = 0L)
        {
            foreach (var entity in DamageMap.Keys)
            {
                if (entity is Player player)
                {
                    PacketSender.SendCombatEffectPacket(player.Client,
                        Id,
                        7.0f,
                        CustomColors.Combat.DamageTakenFlashColor,
                        Options.Instance.CombatOpts.InterruptSound,
                        200f,
                        750,
                        Color.White);
                }
            }

            if (duration > 0)
            {
                ExhaustionEndTime = Timing.Global.MillisecondsUtc + duration;
            }
            else
            {
                var spell = NextSpell;
                if (spell == null)
                {
                    return;
                }
                ExhaustionEndTime = Timing.Global.MillisecondsUtc + spell.ExhaustionInterruptTime;
            }

            CancelSpellChain();
            SendExhaustion(ExhaustionEndTime);
        }

        public void ProcMeleeExhaustion()
        {
            if (!Options.Instance.CombatOpts.NpcMeleeExhaustion || Base.DisableAutoExhaustion)
            {
                return;
            }

            ExhaustionEndTime = Timing.Global.MillisecondsUtc + MeleeExhaustionTime;
        }

        public void SendExhaustion(long endTimestamp)
        {
            PacketSender.SendEntityExhaustedPacketToAll(this, endTimestamp);
        }

        [NotMapped, JsonIgnore]
        public bool IsExhausted => Timing.Global.MillisecondsUtc < ExhaustionEndTime;

        [NotMapped, JsonIgnore]
        public long MeleeExhaustionTime => Math.Min(CalculateAttackTime(), Options.Instance.CombatOpts.NpcMeleeExhaustionTime);

        public override bool ValidForChallenges => Base != null && !Base.InvalidForChallenges;
    }
}
