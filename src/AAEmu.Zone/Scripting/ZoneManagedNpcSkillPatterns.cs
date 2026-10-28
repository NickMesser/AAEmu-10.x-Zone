using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scripting;

public readonly record struct ZoneNpcPickedSkill(uint SkillId, uint TargetId, bool BaseSkill, bool Strafe,
    float DelaySeconds, float MinimumRange, float MaximumRange);
public readonly record struct ZoneNpcSkillFailure(uint SkillId, byte Code);
public sealed record ZoneNpcSkillPick(ZoneNpcPickedSkill? Selected, IReadOnlyList<ZoneNpcSkillFailure> Failures, bool WaitForUnitStatus);
public readonly record struct ZoneNpcSkillPatternSnapshot(int Pattern, bool ListReceived, IReadOnlyList<uint> Skills,
    ZoneNpcPickedSkill? Picked, int GoalState, int AcknowledgementState, uint Timeline, SkillCastResult? Result);

/// <summary>
/// Dedicated SUP state, authored skill-list input, common picker and actual UseSkill request/ACK
/// consumer. The admitted execution profile is a clean NPC without skill controllers,
/// requirements, cost modifiers or special casting/movement owners. Nonself targets require
/// the actual authored model aim-shape owner. Those dependencies refuse at
/// their use site; installing a pattern does not claim complete COPUseSkill or NPC gameplay parity.
/// </summary>
public sealed class ZoneManagedNpcSkillPatterns : IDisposable
{
    private readonly IZoneLuaRuntime _lua;
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly ZoneNpcSkillContent _content;
    private readonly ZoneStatContent _stats;
    private readonly ZoneStatCalculator _calculator;
    private readonly Action<ZoneFrame> _send;
    private readonly Func<uint> _random;
    private readonly Func<long> _frameMicroseconds;
    private readonly ZoneManagedNpcSkillGeometry? _geometry;
    private readonly Action<ZoneManagedNpcActor, ZoneFrame>? _scopedSend;
    private readonly ZoneLuaReference _entries;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcSkillPatternLease> _actors = [];
    private readonly Queue<ZoneManagedNpcScriptFault> _faults = [];
    public bool IsDisposed { get; private set; }
    public int Count => _actors.Count;

    public ZoneManagedNpcSkillPatterns(IZoneLuaRuntime lua, ZoneUnitRegistry units, ZoneSceneNpcCatalog catalog,
        ZoneNpcSkillContent content, ZoneStatContent stats, Action<ZoneFrame> send, Func<uint> random,
        Func<long> frameMicroseconds, ZoneManagedNpcSkillGeometry? geometry = null,
        Action<ZoneManagedNpcActor, ZoneFrame>? scopedSend = null)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua)); _units = units ?? throw new ArgumentNullException(nameof(units));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _content = content ?? throw new ArgumentNullException(nameof(content));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats)); _calculator = new(stats);
        _send = send ?? throw new ArgumentNullException(nameof(send)); _random = random ?? throw new ArgumentNullException(nameof(random));
        _frameMicroseconds = frameMicroseconds ?? throw new ArgumentNullException(nameof(frameMicroseconds));
        _geometry = geometry;
        _scopedSend = scopedSend;
        // Keep the VM's real traversal order, including duplicate numeric values. This is
        // inspection of an authored table, not a replacement behavior implementation.
        using var helper = lua.Execute("return function(t) local r={} for k,v in pairs(t) do r[#r+1]={k,v} end return r end", "=npc-skill-list-entries");
        _entries = ((ZoneLuaReference)helper[0]!).Retain();
        _units.SkillAcknowledged += Acknowledged;
    }

    public ZoneManagedNpcSkillPatternLease BindEntity(ZoneManagedNpcScriptBinding binding, ZoneManagedNpcBodyLease body)
    {
        Check(); ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(body); binding.RequireCurrent();
        if (body.Actor != binding.Actor.Handle || !ReferenceEquals(body.Npc, binding.Actor.Npc) || !body.IsCurrent)
            throw new InvalidOperationException("NPC skills require the exact admitted physical generation.");
        if (_actors.ContainsKey(binding.Actor.Handle)) throw new InvalidOperationException("This NPC generation already owns skill-pattern state.");
        var lease = new ZoneManagedNpcSkillPatternLease(this, binding, body);
        _actors.Add(binding.Actor.Handle, lease);
        return lease;
    }

    /// <summary>30535D70 →31043C84: the native setter retains any signed integer; only value3 is special in PickSkill.</summary>
    public void SetPattern(ZoneManagedNpcActor actor, int pattern) => Require(actor).Pattern = pattern;

    /// <summary>
    ///39265E50's actual source-order six child lists. Call after Server.OnInit/ResetCommon
    ///and before the character constructor, when this generation's entity.AI exists.
    /// </summary>
    public void InstallAuthoredSkillLists(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        using var ai = _lua.Get(owner.Binding.Script, "AI", 5) as ZoneLuaReference ??
            throw new ZoneLuaException("NPC skill child installation requires entity.AI after ResetCommon.");
        using var skills = _lua.CreateTable();
        var roles = new Dictionary<int, string> { [0] = "combat", [1] = "idle", [3] = "alert", [4] = "dead", [7] = "onAlert", [10] = "onCombat" };
        foreach (var (condition, name) in roles)
        {
            using var list = _lua.CreateTable(); var index = 0;
            foreach (var row in _content.ForNpc(actor.Npc.TemplateId))
                if (row.Condition == condition) list.Set(++index, (float)row.SkillId);
            skills.Set(name, list);
        }
        Require(actor); ai.Set("skills", skills);
    }

    /// <summary>39354860: clear old list/map, preserve native traversal, numbers or keyed {delay,boolean} entries.</summary>
    public void SetSkillList(ZoneManagedNpcActor actor, ZoneLuaReference table)
    {
        var owner = Require(actor); ArgumentNullException.ThrowIfNull(table);
        if (!table.IsTable || table.IsDisposed || !ReferenceEquals(table.Runtime, _lua)) throw new ArgumentException("NPC skill lists require a live table in this VM.", nameof(table));
        var ids = new List<uint>(); var parameters = new Dictionary<uint, (float Delay, bool Strafe)>();
        using var copied = _lua.Call(_entries, table);
        using var entries = ((ZoneLuaReference)copied[0]!).Retain();
        for (var index = 1; ; index++)
        {
            using var entry = _lua.Get(entries, index, 5) as ZoneLuaReference;
            if (entry is null) break;
            var key = entry.Get(1); var value = entry.Get(2);
            try
            {
                if (value is long or double) ids.Add(unchecked((uint)ZoneStatMath.ToInt32(Number(value))));
                else if (value is string or byte[]) ids.Add(0); //39354860 tag5 takes its zero-initialized integer branch
                else if (value is ZoneLuaReference { IsTable: true } metadata)
                {
                    var delay = metadata.Get(1); var strafe = metadata.Get(2);
                    try
                    {
                        if (delay is not (long or double) || strafe is not bool flag) continue;
                        if (key is not (long or double)) throw new NotSupportedException("NPC keyed skill metadata requires a numeric native skill key.");
                        var id = unchecked((uint)ZoneStatMath.ToInt32(Number(key)));
                        ids.Add(id); parameters.TryAdd(id, (Number(delay), flag)); //native map insert keeps its first value
                    }
                    finally { (delay as ZoneLuaReference)?.Dispose(); (strafe as ZoneLuaReference)?.Dispose(); }
                }
            }
            finally { (key as ZoneLuaReference)?.Dispose(); (value as ZoneLuaReference)?.Dispose(); }
        }
        Require(actor); owner.Skills = ids.ToArray(); owner.Parameters = parameters; owner.ListReceived = true;
    }

    public ZoneNpcSkillPatternSnapshot CopyState(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor); var goal = owner.ActiveGoal;
        return new(owner.Pattern, owner.ListReceived, Array.AsReadOnly(owner.Skills.ToArray()), owner.Picked,
            goal?.State ?? 0, goal?.AcknowledgementState ?? 0, goal?.Timeline ?? 0, goal?.Result);
    }

    /// <summary>The exact lease token for Registry.QueueNotification(frame, actor.Npc.Unit, scope).</summary>
    public object GetRequestScope(ZoneManagedNpcActor actor) => Require(actor);

    /// <summary>Metadata preflight for one actual NP child role. This does not admit mutable Unit state or future behavior paths.</summary>
    public IReadOnlyList<string> PreflightRole(uint templateId, int condition)
    {
        Check(); var failures = new List<string>();
        foreach (var row in _content.ForNpc(templateId).Where(row => row.Condition == condition))
        {
            if (!_content.Skills.TryGetValue(row.SkillId, out var skill)) continue;
            try { CheckDefinition(skill); _ = Range(skill); }
            catch (NotSupportedException error) { failures.Add(error.Message); }
        }
        return Array.AsReadOnly(failures.ToArray());
    }

    /// <summary>Actual ordered candidates, random in-range choice and pattern-controlled out-of-range selection.</summary>
    public ZoneNpcSkillPick Pick(ZoneManagedNpcActor actor, int flags)
    {
        var owner = Require(actor); var target = PrimaryTarget(actor);
        if (owner.ActiveGoal is { State: >= 3 }) throw new InvalidOperationException("An active skill request already owns this NPC's selection.");
        var failures = new List<ZoneNpcSkillFailure>(); var candidates = new List<ZoneNpcSkillDefinition>();
        Collect(target);
        if (candidates.Count != 0)
        {
            var sample = _random(); Require(actor);
            var skill = candidates[(int)(((ulong)(uint)candidates.Count * sample) >> 32)];
            return Publish(skill, target, false);
        }
        //39328290 is enabled by auto-stick and suppressed by SUP_FLYTRAP. This
        //caller passes false for minimum-range recovery, so only code15 competes.
        if ((flags & 4) != 0 && owner.Pattern != 3)
        {
            ZoneNpcSkillDefinition? nearest = null; var best = float.MaxValue;
            var distance = ReferenceEquals(target, actor.Npc.Unit) ? 0 : Geometry().Distance(actor.Npc.Unit, target);
            foreach (var id in owner.Skills)
            {
                if (!_content.Skills.TryGetValue(id, out var skill)) continue;
                var code = Validate(owner, skill, target);
                if (code == 0) return Publish(skill, target, false); //first newly valid candidate; no random draw
                if (code is not (14 or 15)) { failures.Add(new(id, code)); continue; }
                if (code == 14) continue;
                var (_, maximum) = Range(skill); var gap = distance - maximum;
                if (gap < best) { best = gap; nearest = skill; }
            }
            if (nearest is not null && Range(nearest).Minimum < distance) return Publish(nearest, target, true);
        }
        //The original falls back to later threat entries before the optional base skill.
        foreach (var entry in actor.Npc.Aggro.Entries.Skip(1))
        {
            if (!_units.Units.TryGetValue(entry.TargetId, out var other)) continue;
            candidates.Clear(); Collect(other);
            if (candidates.Count == 0) continue;
            var sample = _random(); Require(actor);
            return Publish(candidates[(int)(((ulong)(uint)candidates.Count * sample) >> 32)], other, false);
        }
        if ((flags & 1) != 0) throw Missing("base-skill/autoattack selection and controller execution");
        owner.Picked = null; owner.PickedTarget = null;
        return new(null, Array.AsReadOnly(failures.ToArray()), failures.Any(failure => failure.Code is 7 or 19 or 22 or 23 or 24));

        void Collect(ZoneUnit candidateTarget)
        {
            foreach (var id in owner.Skills)
            {
                if (!_content.Skills.TryGetValue(id, out var skill)) continue;
                var code = Validate(owner, skill, candidateTarget);
                if (code == 0) candidates.Add(skill); else failures.Add(new(id, code));
            }
            Require(actor);
        }
        ZoneNpcSkillPick Publish(ZoneNpcSkillDefinition skill, ZoneUnit selectedTarget, bool needsMovement)
        {
            RequireUnit(selectedTarget);
            var handle = _catalog.TryGet(selectedTarget.Snapshot.ObjectId);
            if (handle is null || !ReferenceEquals(_catalog.Find(handle.Value)?.Unit, selectedTarget))
                throw Missing("selected target's actual managed AI object");
            var (minimum, maximum) = Range(skill);
            var metadata = owner.Parameters.GetValueOrDefault(skill.Id, (Delay: 0f, Strafe: true));
            var selected = new ZoneNpcPickedSkill(skill.Id, 0, needsMovement, metadata.Strafe, metadata.Delay, minimum, maximum);
            actor.SetTarget(handle); Require(actor);
            owner.Picked = selected; owner.PickedTarget = selectedTarget;
            return new(selected, Array.AsReadOnly(failures.ToArray()), false);
        }
    }

    public IZoneGoalOperation CreateUseSkillGoal(ZoneManagedNpcActor actor, int flags, string tag = "")
    {
        var owner = Require(actor);
        //310EA0B8 receives a byte and consumes only these four bits.
        return new ZoneManagedNpcUseSkillGoal(this, owner, flags & 15, ZoneAiSignalName.CString(tag));
    }

    /// <summary>3936D1F0/39388C80: source bc, skill uint32, Unit target byte0/bc, almighty bool.</summary>
    public static ZoneFrame StartSkillFrame(uint casterId, uint skillId, uint targetId, bool almighty = false)
    {
        var writer = new ZoneWriter(); writer.WriteBc(casterId); writer.WriteUInt32(skillId);
        writer.WriteByte(0); writer.WriteBc(targetId); writer.WriteBoolean(almighty);
        return new(ZwOpcodes.StartSkill, writer.ToArray());
    }

    public IReadOnlyList<ZoneManagedNpcScriptFault> DrainFaults()
    {
        Check(); var result = _faults.ToArray(); _faults.Clear(); return Array.AsReadOnly(result);
    }

    private ZoneUnit PrimaryTarget(ZoneManagedNpcActor actor)
    {
        if (actor.Npc.Aggro.Entries.Count != 0)
            return _units.Units.GetValueOrDefault(actor.Npc.Aggro.Entries[0].TargetId) ??
                throw Missing("the selected threat target's actual Unit allocation");
        if (actor.AttentionTarget is { } attention && !ReferenceEquals(attention, actor.AiObject))
        {
            if (attention.EntityId == 0) return actor.Npc.Unit; //native special objects have no Unit proxy
            var id = actor.Scene.Actors.GetUnitObjectId(attention.EntityId);
            return id != 0 && _units.Units.TryGetValue(id, out var unit) ? unit :
                throw Missing("attention-target proxy to World Unit identity resolution");
        }
        return actor.Npc.Unit;
    }

    private byte Validate(ZoneManagedNpcSkillPatternLease owner, ZoneNpcSkillDefinition skill, ZoneUnit target, bool finalSelf = false)
    {
        var actor = owner.Actor; Require(actor); var unit = actor.Npc.Unit;
        if (unit.Snapshot.Identity.Kind != ZoneUnitKind.Npc) throw Missing("non-NPC skill source validation");
        CheckDefinition(skill);
        EnsureCleanBuffs(unit);
        //3925ED90's admitted constructor-default, clean restriction profile. Buff-driven
        //restrictions/modifiers have already refused above; the real World life/cast/maps apply.
        if (!skill.SkipValidateSource)
        {
            if (!unit.IsDead && (skill.UseConditionBits & 1) == 0) return 3;
            if (unit.IsDead && (skill.UseConditionBits & 2) == 0) return 2;
            var cast = unit.Casting.CopySnapshot();
            if (cast.CastTimeline != 0 || cast.CurrentSkill != 0 || cast.ChannelTimeline != 0 || cast.ChannelSkill != 0) return 7;
            if (unit.Cooldowns.GetRemaining(skill.Id, !skill.IgnoreGlobalCooldown) != 0) return 8;
            if (unit.PreciseMana / 100 < 0) return 11; //the admitted resolved cost is0
        }
        if (skill.TargetType == 4)
        {
            RequireUnit(target); EnsureCleanBuffs(target);
            var kind = (int)target.Snapshot.Identity.Kind;
            if ((skill.TargetUnitMask & (1u << (kind & 31))) == 0) return 34;
            if (target.IsDead && !skill.TargetDead) return 6;
            if (!target.IsDead && !skill.TargetAlive) return kind < 2 || kind == 5 ? (byte)4 : (byte)5;
            if (skill.TargetMyNpc) return 56; //native NPC source is not the required Character owner
        }
        if (finalSelf && skill.TargetType == 0) return 0; //final CanUseSkill does not run the picker's extra self-type range test
        var (minimum, maximum) = Range(skill);
        if (!ReferenceEquals(target, unit)) return Geometry().ValidateLocation(unit, target, skill, minimum, maximum);
        if (skill.CheckObstacle || skill.WeaponAngleSlot != -1 || skill.TargetAngle is > 0 and < 360)
            return Geometry().ValidateLocation(unit, target, skill, minimum, maximum);
        //A nonempty admitted collider compared with itself has zero distance. Avoid
        //substituting entity-center distance for native general proxy shape distances.
        if (minimum > 0) return 14;
        if (maximum < 0) return 15;
        return 0;
    }

    private void CheckDefinition(ZoneNpcSkillDefinition skill)
    {
        if (skill.PositionTarget || skill.TargetType is not (0 or 4))
            throw Missing($"skill{skill.Id} target type{skill.TargetType}: exact proxy range/target validation");
        if (_content.Requirements(skill.Id).Count != 0) throw Missing($"skill{skill.Id} unit requirement evaluation");
        if (skill.ControllerId != 0 || skill.ToggleBuffId != 0 || skill.Id is 2 or 3 or 4)
            throw Missing($"skill{skill.Id} controller/toggle/autoattack execution");
        if (skill.ManaCost != 0 || skill.ManaLevelMultiplier != 0 || skill.CombatResourceId != 0)
            throw Missing($"skill{skill.Id} resolved mana/combat-resource costs");
        if (!_stats.Skills.Skills.TryGetValue(skill.Id, out var shared)) throw Missing($"skill{skill.Id} shared descriptor/cooldown owner");
        if (shared.ChargeCount != 0 || skill.WeaponGlobalCooldownSlot != -1)
            throw Missing($"skill{skill.Id} charge/weapon cooldown duration");
    }

    private void EnsureCleanBuffs(ZoneUnit unit)
    {
        foreach (var buff in unit.Buffs.Good.Concat(unit.Buffs.Bad).Concat(unit.Buffs.Hidden)) CheckBuff(buff.TemplateId);
        foreach (var id in unit.Progression.Passives.Current)
        {
            if (!_stats.Buffs.Passives.TryGetValue(id, out var passive)) throw Missing($"passive{id} source descriptor");
            CheckBuff(passive.BuffId);
        }
        if (unit.AttributeOverrides.Values.Count != 0) throw Missing("skill validation after unit attribute overrides");
        if (unit.GmModes.Any(mode => mode != 0)) throw Missing("skill validation with GM mode overrides");
        void CheckBuff(uint id)
        {
            if (!_content.CleanEligibilityBuffs.Contains(id))
                throw Missing($"buff{id} skill restrictions, status-tag rules or skill modifiers");
        }
    }

    private static (float Minimum, float Maximum) Range(ZoneNpcSkillDefinition skill)
    {
        if (skill.WeaponRangeSlot != -1) throw Missing($"skill{skill.Id} default/equipped weapon range");
        var minimum = (float)Math.Max(0, skill.MinimumRange); var maximum = (float)Math.Max(0, skill.MaximumRange);
        //39259970: modifiers17/2 first, then picker's +.2/-.2 tolerances.
        if (minimum > .01f) minimum = MathF.Max(0, minimum + .2f);
        maximum = MathF.Max(0, maximum - .2f);
        if (minimum > maximum) maximum = minimum + .5f;
        return (minimum, maximum);
    }

    internal int Execute(ZoneManagedNpcUseSkillGoal goal)
    {
        var owner = Require(goal.Owner.Actor); var actor = owner.Actor;
        if (owner.ActiveGoal is { } active && !ReferenceEquals(active, goal))
            throw Missing("concurrent use-skill goals sharing CPuppet selection state");
        owner.ActiveGoal = goal;
        if (goal.Fault is { } fault) throw Missing(fault);
        switch (goal.State)
        {
            case 0:
                if (actor.Npc.Unit.Cooldowns.Global.RemainingMilliseconds != 0) return 1;
                goal.SetClock(_frameMicroseconds()); goal.State = 1;
                actor.Npc.AiSignals.Submit(0, "OnRequestSkillInfo" + (goal.Tag.Length == 0 ? "" : "_" + goal.Tag), actor.AiObject.EntityId);
                return 1;
            case 1:
                if (!owner.ListReceived)
                    return goal.Elapsed(_frameMicroseconds()) < 2 ? 1 : Complete(goal, 3);
                var previousCast = actor.Npc.Unit.Casting.CopySnapshot();
                if (IsCasting(previousCast) || IsChanneling(previousCast))
                {
                    if (!goal.StopRequested) { StopCasting(owner); goal.StopRequested = true; }
                    return 1;
                }
                var picked = Pick(actor, goal.Flags);
                if (picked.Selected is null) return (goal.Flags & 8) != 0 && picked.WaitForUnitStatus ? 1 : Complete(goal, 3);
                if (picked.Selected.Value.BaseSkill && (goal.Flags & 4) != 0 && !ReferenceEquals(owner.PickedTarget, actor.Npc.Unit))
                    throw Missing("COPStick execution for selected out-of-range skill");
                goal.State = 3; return 1; //self target: no COPStick or height-approach owner is invoked
            case 3:
                if (owner.Picked is not { } selection) throw new InvalidOperationException("UseSkill has no picked skill.");
                var skill = _content.Skills[selection.SkillId];
                if (!skill.IgnoreGlobalCooldown && actor.Npc.Unit.Cooldowns.Global.RemainingMilliseconds != 0) return 1;
                var currentTarget = skill.TargetType == 0 ? actor.Npc.Unit : _units.Units.GetValueOrDefault(actor.Npc.Unit.TargetId);
                if (currentTarget is null || Validate(owner, skill, currentTarget, finalSelf: true) != 0) return Complete(goal, 3);
                //39326730 does not change casting fields. Unit-target wire uses the
                //actual Unit.TargetId even for a self-target descriptor, often invalid0.
                goal.State = 4; goal.SetClock(_frameMicroseconds());
                var frame = StartSkillFrame(actor.Npc.ObjectId, selection.SkillId, actor.Npc.Unit.TargetId);
                Send(owner, frame); return 1;
            case 4:
                if (goal.AcknowledgementState == 0)
                    return goal.Elapsed(_frameMicroseconds()) < 2 ? 1 : Complete(goal, 3);
                if (goal.AcknowledgementState == 4) return Complete(goal, 3);
                var channel = _stats.Skills.Skills[owner.Picked!.Value.SkillId].ChannelingMilliseconds;
                if (goal.AcknowledgementState == 1 && _content.Skills[owner.Picked.Value.SkillId].CastingMilliseconds <= 0 && channel <= 0)
                    return 1; //original has no end timeout in this zero-cast/zero-channel branch
                if (goal.AcknowledgementState == 2 && channel <= 0)
                    return IsCasting(actor.Npc.Unit.Casting.CopySnapshot()) ? 1 : FinishAcknowledgedSkill();
                if (goal.AcknowledgementState < 3)
                {
                    if (TargetRangeResult(owner) != 0) { StopCasting(owner); return Complete(goal, 3); }
                    return goal.Elapsed(_frameMicroseconds()) < 60 ? 1 : Complete(goal, 3);
                }
                return FinishAcknowledgedSkill();
            case 5:
                if (owner.Picked!.Value.DelaySeconds > .001f && owner.Picked.Value.DelaySeconds > goal.Elapsed(_frameMicroseconds())) return 1;
                return Complete(goal, 2);
            default: throw new InvalidOperationException("Unknown managed use-skill goal state.");
        }
        int FinishAcknowledgedSkill()
        {
            if (owner.Picked!.Value.DelaySeconds <= .001f) return Complete(goal, 2);
            goal.State = 5; goal.SetClock(_frameMicroseconds()); return 1;
        }
    }

    private int Complete(ZoneManagedNpcUseSkillGoal goal, int result)
    { Reset(goal); return result; }

    internal void Reset(ZoneManagedNpcUseSkillGoal goal)
    {
        var owner = goal.Owner;
        if (ReferenceEquals(owner.ActiveGoal, goal))
        { owner.ActiveGoal = null; owner.ListReceived = false; owner.Skills = []; owner.Picked = null; owner.PickedTarget = null; }
        goal.State = 0; goal.AcknowledgementState = 0; goal.Timeline = 0; goal.Result = null; goal.StopRequested = false;
    }

    private static bool IsCasting(ZoneUnitCastingSnapshot state) => state.CastTimeline != 0 || state.CurrentSkill != 0;
    private static bool IsChanneling(ZoneUnitCastingSnapshot state) => state.ChannelTimeline != 0 || state.ChannelSkill != 0;
    private void StopCasting(ZoneManagedNpcSkillPatternLease owner)
    {
        Require(owner.Actor); var unit = owner.Actor.Npc.Unit; var fields = unit.Casting.CopySnapshot();
        if (fields.CurrentSkill != 0) Send(owner, ZoneManagedNpcGameScriptRequests.StopCastingFrame(unit.Snapshot.ObjectId, 0, fields.CurrentSkill));
        var timeline = fields.CastTimeline != 0 ? fields.CastTimeline : fields.ChannelTimeline;
        if (timeline != 0) Send(owner, ZoneManagedNpcGameScriptRequests.StopCastingFrame(unit.Snapshot.ObjectId, timeline, 0));
    }
    private int TargetRangeResult(ZoneManagedNpcSkillPatternLease owner)
    {
        var actor = owner.Actor; var selected = owner.Picked!.Value; var attention = actor.AttentionTarget;
        if (attention is null || attention.EntityId == 0) return 1;
        var id = actor.Scene.Actors.GetUnitObjectId(attention.EntityId);
        if (!_units.Units.TryGetValue(id, out var target)) return 1;
        var distance = ReferenceEquals(target, actor.Npc.Unit) ? .01f : MathF.Max(.01f, Geometry().Distance(actor.Npc.Unit, target));
        if (selected.MinimumRange > distance) return 14;
        return distance <= selected.MaximumRange + 7f ? 0 : 15; //310D9B08, called by310DF928 with(7,1)
    }
    private void Send(ZoneManagedNpcSkillPatternLease owner, ZoneFrame frame)
    {
        Require(owner.Actor);
        if (_scopedSend is { } scoped) scoped(owner.Actor, frame); else _send(frame);
        Require(owner.Actor);
    }

    private void Acknowledged(ZoneUnit unit, ZoneUnitSkillAcknowledgement message)
    {
        if (IsDisposed) return;
        var owner = _actors.Values.FirstOrDefault(value => ReferenceEquals(value.Actor.Npc.Unit, unit));
        if (owner is null || !owner.IsCurrent || owner.ActiveGoal is not { State: 4 } goal || owner.Picked is not { } selected) return;
        var state = goal.AcknowledgementState;
        var accepts = message.Kind switch
        {
            ZoneUnitSkillAcknowledgementKind.Started => state < 1 && goal.Timeline == 0 && selected.SkillId == message.SkillId,
            ZoneUnitSkillAcknowledgementKind.Fired => state < 2 && selected.SkillId == message.SkillId && (state != 1 || goal.Timeline == message.Timeline),
            ZoneUnitSkillAcknowledgementKind.Ended => state < 3 && goal.Timeline == message.Timeline,
            _ => false
        };
        if (!accepts) return;
        goal.Timeline = message.Timeline; goal.Result = message.Result;
        if (message.Kind == ZoneUnitSkillAcknowledgementKind.Ended) { goal.AcknowledgementState = 3; return; }
        goal.AcknowledgementState = message.Result.Code != 0 ? 4 : message.Kind == ZoneUnitSkillAcknowledgementKind.Started ? 1 : 2;
        goal.SetClock(_frameMicroseconds());
        if (message.Result.Code != 0) return;
        try
        {
            Require(owner.Actor); EnsureCleanBuffs(unit);
            var descriptor = _content.Skills[selected.SkillId]; var shared = _stats.Skills.Skills[selected.SkillId];
            if (message.Kind == ZoneUnitSkillAcknowledgementKind.Started && shared.TimingId == 1)
            {
                var duration = descriptor.DefaultGlobalCooldown ? 1000 : Math.Max(0, descriptor.CustomGlobalCooldownMilliseconds);
                if (duration != 0)
                {
                    var values = _calculator.CalculateAttributes(unit.Snapshot, [74, 218], unit.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
                    if (!values.IsComplete) throw Missing("global cooldown attack-speed attributes: " + string.Join(", ", values.UnresolvedSources));
                    var speed = unchecked((int)(values.Values[74] + values.Values[218]));
                    if (_stats.Limits.TryGetValue(74, out var limits)) speed = unchecked((int)Math.Clamp(speed, limits.Minimum, limits.Maximum));
                    unit.Cooldowns.StartGlobal(unchecked((uint)(1000 * duration)) / (uint)(Math.Max(speed, -999) + 1000));
                }
            }
            else if (message.Kind == ZoneUnitSkillAcknowledgementKind.Fired && shared.TimingId != 0 && descriptor.ToggleBuffId == 0)
            {
                if (!unit.Cooldowns.StartSkill(selected.SkillId, shared.CooldownMilliseconds))
                    throw Missing("acknowledged skill cooldown map admission");
            }
        }
        catch (Exception error)
        {
            //Never interrupt World's casting-field producer halfway through Fire. The owning
            //scene must observe this fault and retire/refuse this generation before more AI runs.
            goal.Fault = "acknowledged skill effect: " + error.Message;
            _faults.Enqueue(new(owner.Actor.Handle, "NPC skill acknowledgment", goal.Fault));
        }
    }

    internal ZoneManagedNpcSkillPatternLease Require(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor);
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Actor, actor) || !owner.IsCurrent)
            throw new InvalidOperationException("NPC skill execution requires its exact live script, body and World Unit generation.");
        return owner;
    }
    internal bool Current(ZoneManagedNpcSkillPatternLease owner) => !IsDisposed && !owner.IsDisposed && owner.Binding.IsCurrent &&
        owner.Body.IsCurrent && ReferenceEquals(_catalog.Find(owner.Actor.Handle), owner.Actor.Npc) &&
        _units.Units.TryGetValue(owner.Actor.Npc.ObjectId, out var unit) && ReferenceEquals(unit, owner.Actor.Npc.Unit);
    internal void Retire(ZoneManagedNpcSkillPatternLease owner)
    {
        if (_actors.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(owner, current)) _actors.Remove(owner.Actor.Handle);
        if (owner.ActiveGoal is { } goal) Reset(goal);
        _units.DiscardNotifications(owner.Actor.Npc.Unit, owner);
    }
    private static float Number(object? value)
    {
        var number = value switch { long integer => (float)(double)integer, double scalar => (float)scalar, _ => float.NaN };
        if (!float.IsFinite(number)) throw new ArgumentException("NPC skill input requires a finite native float."); return number;
    }
    private static NotSupportedException Missing(string dependency) => new("Managed NPC skill execution requires " + dependency + ".");
    private ZoneManagedNpcSkillGeometry Geometry() => _geometry ?? throw Missing("the actual model aim-shape range owner");
    private void RequireUnit(ZoneUnit unit)
    {
        if (!_units.Units.TryGetValue(unit.Snapshot.ObjectId, out var current) || !ReferenceEquals(unit, current))
            throw new InvalidOperationException("NPC skill target no longer belongs to the current World Unit allocation.");
    }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true; _units.SkillAcknowledged -= Acknowledged;
        foreach (var owner in _actors.Values.ToArray()) owner.Dispose();
        _entries.Dispose(); _faults.Clear();
    }
}

public sealed class ZoneManagedNpcSkillPatternLease : IDisposable
{
    private readonly ZoneManagedNpcSkillPatterns _owner;
    internal ZoneManagedNpcScriptBinding Binding { get; }
    internal ZoneManagedNpcBodyLease Body { get; }
    public ZoneManagedNpcActor Actor => Binding.Actor;
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => _owner.Current(this);
    internal int Pattern;
    internal bool ListReceived;
    internal uint[] Skills = [];
    internal Dictionary<uint, (float Delay, bool Strafe)> Parameters = [];
    internal ZoneNpcPickedSkill? Picked;
    internal ZoneUnit? PickedTarget;
    internal ZoneManagedNpcUseSkillGoal? ActiveGoal;
    internal ZoneManagedNpcSkillPatternLease(ZoneManagedNpcSkillPatterns owner, ZoneManagedNpcScriptBinding binding, ZoneManagedNpcBodyLease body)
    { _owner = owner; Binding = binding; Body = body; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}

/// <summary>310EA0B8/310E6B68 common admitted phases and 310D9878/9958/9A38 ACK ordering.</summary>
internal sealed class ZoneManagedNpcUseSkillGoal(ZoneManagedNpcSkillPatterns executor, ZoneManagedNpcSkillPatternLease owner,
    int flags, string tag) : IZoneGoalOperation
{
    internal ZoneManagedNpcSkillPatternLease Owner { get; } = owner;
    internal int Flags { get; } = flags;
    internal string Tag { get; } = tag;
    internal int State, AcknowledgementState;
    internal uint Timeline;
    internal SkillCastResult? Result;
    internal string? Fault;
    internal bool StopRequested;
    private long _clock;
    private bool _disposed;
    internal void SetClock(long now) => _clock = now;
    internal float Elapsed(long now) => unchecked((int)(now - _clock)) * .000001f;
    public int Execute() { ObjectDisposedException.ThrowIf(_disposed, this); return executor.Execute(this); }
    public void Reset() { ObjectDisposedException.ThrowIf(_disposed, this); executor.Reset(this); }
    public void Dispose() { if (_disposed) return; _disposed = true; executor.Reset(this); }
}
