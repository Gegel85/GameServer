using System;
using System.Numerics;
using LeagueSandbox.GameServer.Logic.API;
using LeagueSandbox.GameServer.Logic.Content;
using LeagueSandbox.GameServer.Logic.GameObjects.AttackableUnits;
using LeagueSandbox.GameServer.Logic.GameObjects.AttackableUnits.AI;
using LeagueSandbox.GameServer.Logic.GameObjects.Missiles;
using LeagueSandbox.GameServer.Logic.GameObjects.Other;
using LeagueSandbox.GameServer.Logic.Packets;
using LeagueSandbox.GameServer.Logic.Packets.PacketDefinitions.S2C;
using LeagueSandbox.GameServer.Logic.Scripting.CSharp;

namespace LeagueSandbox.GameServer.Logic.GameObjects.Spells
{
    public enum SpellState
    {
        STATE_READY,
        STATE_CASTING,
        STATE_COOLDOWN,
        STATE_CHANNELING
    }

    public class Spell
    {
        public static bool CooldownsEnabled;
        public static bool ManaCostsEnabled;
        public Champion Owner { get; private set; }
        public short Level { get; private set; }
        public byte Slot { get; set; }

        public float CastTime { get; private set; } = 0;

        public string SpellName { get; private set; }
        public bool HasEmptyScript => _spellGameScript.GetType() == typeof(GameScriptEmpty);

        public SpellState State { get; protected set; } = SpellState.STATE_READY;
        public float CurrentCooldown { get; protected set; }
        public float CurrentCastTime { get; protected set; }
        public float CurrentChannelDuration { get; protected set; }
        public uint FutureProjNetId { get; protected set; }
        public uint SpellNetId { get; protected set; }

        public AttackableUnit Target { get; private set; }
        public float X { get; private set; }
        public float Y { get; private set; }
        public float X2 { get; private set; }
        public float Y2 { get; private set; }

        private static CSharpScriptEngine _scriptEngine = Program.ResolveDependency<CSharpScriptEngine>();
        private static Logger _logger = Program.ResolveDependency<Logger>();
        private static Game _game = Program.ResolveDependency<Game>();

        private IGameScript _spellGameScript;
        protected NetworkIdManager _networkIdManager = Program.ResolveDependency<NetworkIdManager>();

        public SpellData SpellData { get; private set; }

        static Spell()
        {
            CooldownsEnabled = _game.Config.CooldownsEnabled;
            ManaCostsEnabled = _game.Config.ManaCostsEnabled;
        }

        public Spell(Champion owner, string spellName, byte slot)
        {
            Owner = owner;
            SpellName = spellName;
            Slot = slot;
            SpellData = _game.Config.ContentManager.GetSpellData(spellName);
            _scriptEngine = Program.ResolveDependency<CSharpScriptEngine>();

            //Set the game script for the spell
            _spellGameScript = _scriptEngine.CreateObject<IGameScript>("Spells", spellName) ?? new GameScriptEmpty();
            //Activate spell - Notes: Deactivate is never called as spell removal hasn't been added
            _spellGameScript.OnActivate(owner);
        }



        public void DeactivateSpell()
        {
            _spellGameScript.OnDeactivate(Owner);
        }

        /// <summary>
        /// Called when the character casts the spell
        /// </summary>
        public virtual bool Cast(float x, float y, float x2, float y2, AttackableUnit u = null)
        {
            if (HasEmptyScript)
            {
                return false;
            }

            var stats = Owner.Stats;
            if (SpellData.ManaCost[Level] * (1 - stats.SpellCostReduction) >= stats.CurrentMana ||
                State != SpellState.STATE_READY)
            {
                return false;
            }

            stats.CurrentMana = stats.CurrentMana - SpellData.ManaCost[Level] * (1 - stats.SpellCostReduction);
            X = x;
            Y = y;
            X2 = x2;
            Y2 = y2;
            Target = u;
            FutureProjNetId = _networkIdManager.GetNewNetId();
            SpellNetId = _networkIdManager.GetNewNetId();

            if (SpellData.TargettingType == 1 && Target != null && Target.GetDistanceTo(Owner) > SpellData.CastRange[Level])
            {
                return false;
            }

            _spellGameScript.OnStartCasting(Owner, this, Target);

            if (SpellData.GetCastTime() > 0 && (SpellData.Flags & (int)SpellFlag.SPELL_FLAG_INSTANT_CAST) == 0)
            {
                Owner.SetPosition(Owner.X, Owner.Y); //stop moving serverside too. TODO: check for each spell if they stop movement or not
                State = SpellState.STATE_CASTING;
                CurrentCastTime = SpellData.GetCastTime();
            }
            else
            {
                FinishCasting();
            }

            var response = new CastSpellResponse(this, x, y, x2, y2, FutureProjNetId, SpellNetId);
            _game.PacketHandlerManager.BroadcastPacket(response, Packets.PacketHandlers.Channel.CHL_S2_C);
            return true;
        }

        /// <summary>
        /// Called when the spell is finished casting and we're supposed to do things such as projectile spawning, etc.
        /// </summary>
        public virtual void FinishCasting()
        {
            _spellGameScript.OnFinishCasting(Owner, this, Target);
            if (SpellData.ChannelDuration[Level] <= 0)
            {
                State = SpellState.STATE_COOLDOWN;

                CurrentCooldown = GetCooldown();

                if (Slot < 4)
                {
                    _game.PacketNotifier.NotifySetCooldown(Owner, Slot, CurrentCooldown, GetCooldown());
                }

                Owner.IsCastingSpell = false;
            }
        }

        /// <summary>
        /// Called when the spell is started casting and we're supposed to do things such as projectile spawning, etc.
        /// </summary>
        public virtual void Channel()
        {
            State = SpellState.STATE_CHANNELING;
            CurrentChannelDuration = SpellData.ChannelDuration[Level];
        }

        /// <summary>
        /// Called when the character finished channeling
        /// </summary>
        public virtual void FinishChanneling()
        {
            State = SpellState.STATE_COOLDOWN;

            CurrentCooldown = GetCooldown();

            if (Slot < 4)
            {
                _game.PacketNotifier.NotifySetCooldown(Owner, Slot, CurrentCooldown, GetCooldown());
            }

            Owner.IsCastingSpell = false;
        }

        /// <summary>
        /// Called every diff milliseconds to update the spell
        /// </summary>
        public virtual void Update(float diff)
        {
            _spellGameScript.OnUpdate(diff);
            switch (State)
            {
                case SpellState.STATE_READY:
                    break;
                case SpellState.STATE_CASTING:
                    Owner.IsCastingSpell = true;
                    CurrentCastTime -= diff / 1000.0f;
                    if (CurrentCastTime <= 0)
                    {
                        FinishCasting();
                        if (SpellData.ChannelDuration[Level] > 0)
                        {
                            Channel();
                        }
                    }
                    break;
                case SpellState.STATE_COOLDOWN:
                    CurrentCooldown -= diff / 1000.0f;
                    if (CurrentCooldown < 0)
                    {
                        State = SpellState.STATE_READY;
                    }
                    break;
                case SpellState.STATE_CHANNELING:
                    CurrentChannelDuration -= diff / 1000.0f;
                    if (CurrentChannelDuration <= 0)
                    {
                        FinishChanneling();
                    }
                    break;
            }
        }

        /// <summary>
        /// Called by projectiles when they land / hit, this is where we apply damage/slows etc.
        /// </summary>
        public void ApplyEffects(AttackableUnit u, Projectile p = null)
        {
            if (SpellData.HaveHitEffect && !string.IsNullOrEmpty(SpellData.HitEffectName))
            {
                ApiFunctionManager.AddParticleTarget(Owner, SpellData.HitEffectName, u);
            }
            _spellGameScript.ApplyEffects(Owner, u, this, p);
        }

        public Projectile AddProjectile(string nameMissile, float toX, float toY, bool isServerOnly = false)
        {
            var p = new Projectile(
                Owner.X,
               Owner.Y,
                (int)SpellData.LineWidth,
                Owner,
                new Target(toX, toY),
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectile(string nameMissile, float toX, float toY, float startX, float startY, bool isServerOnly = false)
        {
            var p = new Projectile(
                startX,
                startY,
                (int) SpellData.LineWidth,
                Owner,
                new Target(toX, toY),
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectile(string nameMissile, float toX, float toY, Vector2 start, bool isServerOnly = false)
        {
            var p = new Projectile(
                start.X,
                start.Y,
                (int)SpellData.LineWidth,
                Owner,
                new Target(toX, toY),
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectileTarget(string nameMissile, Target target, bool isServerOnly = false)
        {
            var p = new Projectile(
                Owner.X,
                Owner.Y,
                (int)SpellData.LineWidth,
                Owner,
                target,
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectileTarget(string nameMissile, Target target, float startX, float startY, bool isServerOnly = false)
        {
            var p = new Projectile(
                startX,
                startY,
                (int)SpellData.LineWidth,
                Owner,
                target,
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectileTarget(string nameMissile, Target target, Vector2 start, bool isServerOnly = false)
        {
            var p = new Projectile(
                start.X,
                start.Y,
                (int)SpellData.LineWidth,
                Owner,
                target,
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectileHitAllTargets(string nameMissile, Target target, bool isServerOnly = false)
        {
            var p = new Projectile(
                Owner.X,
                Owner.Y,
                (int)SpellData.LineWidth,
                Owner,
                target,
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags,
                hitOnlyTarget: false
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectileHitAllTargets(string nameMissile, Target target, float startX, float startY, bool isServerOnly = false)
        {
            var p = new Projectile(
                startX,
                startY,
                (int)SpellData.LineWidth,
                Owner,
                target,
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags,
                hitOnlyTarget: false
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public Projectile AddProjectileHitAllTargets(string nameMissile, Target target, Vector2 start, bool isServerOnly = false)
        {
            var p = new Projectile(
                start.X,
                start.Y,
                (int)SpellData.LineWidth,
                Owner,
                target,
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags,
                hitOnlyTarget: false
            );
            _game.ObjectManager.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
            return p;
        }

        public void AddLaser(float toX, float toY, bool affectAsCastIsOver = true)
        {
            var l = new Laser(
                Owner.X,
                Owner.Y,
                (int)SpellData.LineWidth,
                Owner,
                new Target(toX, toY),
                this,
                SpellData.Flags,
                affectAsCastIsOver
            );
            _game.ObjectManager.AddObject(l);
        }

        public void SpellAnimation(string animName, AttackableUnit target)
        {
            _game.PacketNotifier.NotifySpellAnimation(target, animName);
        }

        /// <returns>spell's unique ID</returns>
        public int GetId()
        {
            return (int)HashFunctions.HashString(SpellName);
        }

        public string GetStringForSlot()
        {
            switch (Slot)
            {
                case 0:
                    return "Q";
                case 1:
                    return "W";
                case 2:
                    return "E";
                case 3:
                    return "R";
                case 14:
                    return "Passive";
            }

            return "undefined";
        }

        public float GetCooldown()
        {
            return CooldownsEnabled ? SpellData.Cooldown[Level] * (1 - Owner.Stats.CooldownReduction.Total) : 0;
        }

        public virtual void LevelUp()
        {
            if (Level <= 5)
            {
                ++Level;
            }

            if (Slot < 4)
            {
                Owner.Stats.ManaCost[Slot] = SpellData.ManaCost[Level];
            }
            ApiEventManager.OnLevelUpSpell.Publish(this, Owner);
        }

        public void SetCooldown(byte slot, float newCd)
        {
            var targetSpell = Owner.Spells[slot];

            if (newCd <= 0)
            {
                _game.PacketNotifier.NotifySetCooldown(Owner, slot, 0, 0);
                targetSpell.State = SpellState.STATE_READY;
                targetSpell.CurrentCooldown = 0;
            }
            else
            {
                _game.PacketNotifier.NotifySetCooldown(Owner, slot, newCd, targetSpell.GetCooldown());
                targetSpell.State = SpellState.STATE_COOLDOWN;
                targetSpell.CurrentCooldown = newCd;
            }
        }

        public void LowerCooldown(byte slot, float lowerValue)
        {
            SetCooldown(slot, Owner.Spells[slot].CurrentCooldown - lowerValue);
        }
    }
}
