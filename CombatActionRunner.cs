#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public sealed class CombatActionRunner
{
	private const double BaseHitChance = 0.50;
	private const double MinHitChance = 0.05;
	private const double MaxHitChance = 0.95;
	private const int GlobalCooldownTicks = 4;

	private readonly ICombatant _owner;
	private readonly IReadOnlyList<CombatAction> _actions;
	private readonly RandomNumberGenerator _rng;
	private readonly Action<string, GDict> _emitEvent;
	private readonly Func<ICombatant?>? _selectTarget;
	private readonly Action<Adventurer, Monster, CombatAction, long>? _notifyMonsterAggro;
	private readonly Dictionary<string, int> _skillCooldowns = new(StringComparer.Ordinal);
	private CombatAction? _queuedAction;
	private CombatAction? _activeAction;
	private ICombatant? _target;
	private ICombatant? _queuedTarget;
	private string _lastActionId = string.Empty;
	private long _currentTick;

	public CombatActionRunner(
		ICombatant owner,
		IReadOnlyList<CombatAction> actions,
		RandomNumberGenerator rng,
		Action<string, GDict> emitEvent,
		Func<ICombatant?>? selectTarget = null,
		Action<Adventurer, Monster, CombatAction, long>? notifyMonsterAggro = null)
	{
		_owner = owner;
		_actions = actions;
		_rng = rng;
		_emitEvent = emitEvent;
		_selectTarget = selectTarget;
		_notifyMonsterAggro = notifyMonsterAggro;

		foreach (CombatAction action in _actions.Where(action => action.Kind != CombatActionKind.BasicAttack))
		{
			_skillCooldowns[action.ActionId] = 0;
		}
	}

	public ICombatant Owner => _owner;
	public ICombatant? Target => _target;
	public CombatState State { get; private set; } = CombatState.OutOfCombat;
	public int BasicAttackCooldownTicksRemaining { get; private set; }
	public int GlobalCooldownTicksRemaining { get; private set; }
	public int CastTicksRemaining { get; private set; }
	public int RecoveryTicksRemaining { get; private set; }
	public IReadOnlyDictionary<string, int> SkillCooldowns => _skillCooldowns;
	public string ActiveActionId => _activeAction?.ActionId ?? string.Empty;
	public string QueuedActionId => _queuedAction?.ActionId ?? string.Empty;
	public bool IsDisabled => State == CombatState.Disabled;
	public bool CanAct => State == CombatState.Ready && GlobalCooldownTicksRemaining <= 0 && _owner.IsAlive && _target?.IsAlive == true;

	public void Start(ICombatant target, long currentTick)
	{
		_currentTick = currentTick;
		_target = target;
		_queuedAction = null;
		_queuedTarget = null;
		_activeAction = null;
		CastTicksRemaining = 0;
		RecoveryTicksRemaining = 0;
		ChangeState(CombatState.Engaging);
		PublishSnapshot();
	}

	public void Start(long currentTick)
	{
		_currentTick = currentTick;
		_queuedAction = null;
		_queuedTarget = null;
		_activeAction = null;
		CastTicksRemaining = 0;
		RecoveryTicksRemaining = 0;

		if (!TrySelectTarget(currentTick, "encounter_started"))
		{
			ChangeState(_owner.IsAlive ? CombatState.OutOfCombat : CombatState.Defeated);
			PublishSnapshot();
			return;
		}

		ChangeState(CombatState.Engaging);
		PublishSnapshot();
	}

	public void Stop()
	{
		_target = null;
		_queuedAction = null;
		_queuedTarget = null;
		_activeAction = null;
		BasicAttackCooldownTicksRemaining = 0;
		GlobalCooldownTicksRemaining = 0;
		CastTicksRemaining = 0;
		RecoveryTicksRemaining = 0;

		foreach (string key in _skillCooldowns.Keys.ToArray())
		{
			_skillCooldowns[key] = 0;
		}

		ChangeState(_owner.IsAlive ? CombatState.OutOfCombat : CombatState.Defeated);
		PublishSnapshot();
	}

	public void AdvanceTickCounters(long tick)
	{
		_currentTick = tick;

		if (State is CombatState.OutOfCombat or CombatState.Defeated)
		{
			return;
		}

		if (!_owner.IsAlive)
		{
			_queuedAction = null;
			_activeAction = null;
			ChangeState(CombatState.Defeated);
			PublishSnapshot();
			return;
		}

		if (_target is null || !_target.IsAlive)
		{
			_queuedAction = null;
			_queuedTarget = null;
			_activeAction = null;

			if (TrySelectTarget(tick, "target_unavailable"))
			{
				ChangeState(CombatState.Engaging);
				PublishSnapshot();
				return;
			}

			_target = null;
			ChangeState(CombatState.OutOfCombat);
			PublishSnapshot();
			return;
		}

		if (State == CombatState.Engaging)
		{
			ChangeState(CombatState.Ready);
			EmitReady(tick);
		}

		UpdateCooldowns(tick);
		UpdateRecovery(tick);
		UpdateCast(tick);
		PublishSnapshot();
	}

	public QueuedCombatAction? QueueActionForTick(long tick)
	{
		_currentTick = tick;

		if (State == CombatState.Queued && _queuedAction is CombatAction queuedAction)
		{
			_target = _queuedTarget;

			if (!IsActionInRange(queuedAction, out double queuedDistance))
			{
				CancelQueuedAction(queuedAction, tick, "target_out_of_range", queuedDistance);
				ChangeState(CombatState.Ready);
				PublishSnapshot();
				return null;
			}

			return new QueuedCombatAction(this, _owner, _target, queuedAction);
		}

		if (State != CombatState.Ready || !CanAct)
		{
			return null;
		}

		CombatAction? action = SelectAction();

		if (action is null)
		{
			return null;
		}

		if (action.CastTicks > 0)
		{
			_activeAction = action;
			CastTicksRemaining = action.CastTicks;
			ChangeState(CombatState.Casting);
			_emitEvent("combat_cast_started", BuildActionPayload(action, tick));
			PublishSnapshot();
			return null;
		}

		return QueueSelectedAction(action, tick, "ready");
	}

	public void ResolveQueuedAction(QueuedCombatAction queuedAction, long tick)
	{
		_currentTick = tick;
		CombatAction action = queuedAction.Action;

		if (_queuedAction != action)
		{
			CancelQueuedAction(action, tick, "action_no_longer_queued");
			return;
		}

		ICombatant? actionTarget = queuedAction.Target;

		if (_queuedTarget is null || !ReferenceEquals(_queuedTarget, actionTarget))
		{
			CancelQueuedAction(action, tick, "target_no_longer_queued");
			return;
		}

		if (!_owner.IsAlive)
		{
			CancelQueuedAction(action, tick, "combatant_defeated");
			ChangeState(CombatState.Defeated);
			PublishSnapshot();
			return;
		}

		if (actionTarget is null || !actionTarget.IsAlive)
		{
			CancelQueuedAction(action, tick, "target_defeated");
			_queuedTarget = null;
			if (_owner.IsAlive && TrySelectTarget(tick, "queued_target_defeated"))
			{
				ChangeState(CombatState.Engaging);
			}
			else
			{
				ChangeState(_owner.IsAlive ? CombatState.Ready : CombatState.Defeated);
			}
			PublishSnapshot();
			return;
		}

		_target = actionTarget;

		if (!IsActionInRange(action, out double distanceToTarget))
		{
			CancelQueuedAction(action, tick, "target_out_of_range", distanceToTarget);
			ChangeState(CombatState.Ready);
			PublishSnapshot();
			return;
		}

		_queuedAction = null;
		_queuedTarget = null;
		_activeAction = action;
		_emitEvent("combat_action_started", BuildActionPayload(action, tick));

		ActionResolution resolution = ResolveAction(action, tick);
		StartCooldowns(action, tick);
		_emitEvent("combat_action_resolved", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "action_id", action.ActionId },
			{ "action_kind", action.Kind.ToString() },
			{ "hit", resolution.Hit },
			{ "damage", resolution.Damage }
		});
		_lastActionId = action.ActionId;

		if (action.RequiresTarget)
		{
			NotifyMonsterAggro(action, tick);
		}

		_activeAction = null;

		if (!_owner.IsAlive)
		{
			ChangeState(CombatState.Defeated);
		}
		else if (action.RecoveryTicks > 0 && _target?.IsAlive == true)
		{
			RecoveryTicksRemaining = action.RecoveryTicks;
			ChangeState(CombatState.Recovering);
			_emitEvent("combat_recovery_started", new GDict
			{
				{ "source", nameof(CombatActionRunner) },
				{ "tick", tick },
				{ "combatant", _owner.DisplayName },
				{ "combatant_kind", _owner.CombatantKind },
				{ "duration_ticks", RecoveryTicksRemaining }
			});
		}
		else
		{
			ChangeState(CombatState.Ready);
			EmitReady(tick);
		}

		PublishSnapshot();
	}

	private void UpdateCooldowns(long tick)
	{
		if (BasicAttackCooldownTicksRemaining > 0)
		{
			BasicAttackCooldownTicksRemaining--;

			if (BasicAttackCooldownTicksRemaining == 0)
			{
				EmitCooldownReady("basic_attack", "basic_attack", tick);
			}
		}

		if (GlobalCooldownTicksRemaining > 0)
		{
			GlobalCooldownTicksRemaining--;

			if (GlobalCooldownTicksRemaining == 0)
			{
				EmitCooldownReady("global_cooldown", "global", tick);
			}
		}

		foreach (string actionId in _skillCooldowns.Keys.ToArray())
		{
			int previous = _skillCooldowns[actionId];

			if (previous <= 0)
			{
				continue;
			}

			int current = Math.Max(0, previous - 1);
			_skillCooldowns[actionId] = current;

			if (current == 0)
			{
				EmitCooldownReady(actionId, "skill", tick);
			}
		}
	}

	private void UpdateRecovery(long tick)
	{
		if (State != CombatState.Recovering || RecoveryTicksRemaining <= 0)
		{
			return;
		}

		RecoveryTicksRemaining--;

		if (RecoveryTicksRemaining > 0)
		{
			return;
		}

		_emitEvent("combat_recovery_completed", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind }
		});
		ChangeState(CombatState.Ready);
		EmitReady(tick);
	}

	private void UpdateCast(long tick)
	{
		if (State != CombatState.Casting || _activeAction is not CombatAction action)
		{
			return;
		}

		CastTicksRemaining = Math.Max(0, CastTicksRemaining - 1);

		if (CastTicksRemaining > 0)
		{
			return;
		}

		_emitEvent("combat_cast_completed", BuildActionPayload(action, tick));
		_activeAction = null;
		QueueSelectedAction(action, tick, "cast_completed");
	}

	private QueuedCombatAction? QueueSelectedAction(CombatAction action, long tick, string reason)
	{
		if (!IsActionInRange(action, out double distanceToTarget))
		{
			CancelQueuedAction(action, tick, "target_out_of_range", distanceToTarget);
			ChangeState(CombatState.Ready);
			EmitReady(tick);
			PublishSnapshot();
			return null;
		}

		_queuedAction = action;
		_queuedTarget = _target;
		ChangeState(CombatState.Queued);
		GD.Print($"ACTION_QUEUED tick={tick} {_owner.DisplayName} action={action.ActionId} target={_target?.DisplayName ?? "none"} cooldown={GetActionCooldownTicks(action)}");
		_emitEvent("combat_action_queued", BuildActionPayload(action, tick, new GDict
		{
			{ "queue_reason", reason },
			{ "skill_cooldown_ticks_remaining", GetSkillCooldownTicksRemaining(action) }
		}));
		PublishSnapshot();
		return new QueuedCombatAction(this, _owner, _queuedTarget, action);
	}

	private int GetSkillCooldownTicksRemaining(CombatAction action)
	{
		return _skillCooldowns.TryGetValue(action.ActionId, out int remaining) ? remaining : 0;
	}

	private int GetActionCooldownTicks(CombatAction action)
	{
		return action.UsesGlobalAttackCooldown ? GetBasicAttackCooldownTicks() : action.CooldownTicks;
	}

	private CombatAction? SelectAction()
	{
		foreach (CombatAction action in _actions)
		{
			if (IsActionReady(action))
			{
				return action;
			}
		}

		return null;
	}

	private bool IsActionReady(CombatAction action)
	{
		if (GlobalCooldownTicksRemaining > 0)
		{
			return false;
		}

		if (action.RequiresTarget && _target?.IsAlive != true)
		{
			return false;
		}

		if (!IsActionInRange(action, out _))
		{
			return false;
		}

		return action.Kind switch
		{
			CombatActionKind.BasicAttack => BasicAttackCooldownTicksRemaining <= 0,
			_ => !_skillCooldowns.TryGetValue(action.ActionId, out int remaining) || remaining <= 0
		};
	}

	private bool IsActionInRange(CombatAction action, out double distance)
	{
		distance = 0.0;

		if (!action.RequiresTarget || _target is null)
		{
			return true;
		}

		if (_owner is not Node2D ownerNode || _target is not Node2D targetNode)
		{
			return true;
		}

		distance = ownerNode.GlobalPosition.DistanceTo(targetNode.GlobalPosition);
		return distance <= action.Range;
	}

	private ActionResolution ResolveAction(CombatAction action, long tick)
	{
		if (_target is null || !_target.IsAlive)
		{
			return new ActionResolution(false, 0);
		}

		double accuracyAdvantage = Math.Clamp(_owner.Accuracy - _target.Evasion, 0.0, 1.0);
		double hitChance = Math.Clamp(BaseHitChance + accuracyAdvantage, MinHitChance, MaxHitChance);
		double roll = _rng.Randf();
		bool hit = roll <= hitChance;
		int rawDamage = (int)Math.Round(_owner.Attack * action.DamageMultiplier, MidpointRounding.AwayFromZero);
		int damage = hit ? Math.Max(1, rawDamage - _target.Defense) : 0;

		GD.Print($"ATTACK_ROLL tick={tick} attacker={_owner.DisplayName} defender={_target.DisplayName} action={action.ActionId} hit_chance={hitChance:0.00} roll={roll:0.00} hit={hit} damage={damage}");
		_emitEvent("attack_roll_resolved", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "attacker", _owner.DisplayName },
			{ "attacker_kind", _owner.CombatantKind },
			{ "defender", _target.DisplayName },
			{ "defender_kind", _target.CombatantKind },
			{ "action_id", action.ActionId },
			{ "hit_formula", "clamp(0.5 + clamp(attacker_accuracy - defender_evasion, 0, 1), 0.05, 0.95)" },
			{ "damage_formula", "max(1, round(attacker_attack * action_multiplier) - defender_defense)" },
			{ "hit_chance", hitChance },
			{ "roll", roll },
			{ "hit", hit },
			{ "damage", damage }
		});

		if (!hit)
		{
			return new ActionResolution(false, 0);
		}

		int appliedDamage = _target.ApplyDamage(damage);
		_emitEvent("damage_applied", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "attacker", _owner.DisplayName },
			{ "attacker_kind", _owner.CombatantKind },
			{ "defender", _target.DisplayName },
			{ "defender_kind", _target.CombatantKind },
			{ "action_id", action.ActionId },
			{ "damage", appliedDamage }
		});

		return new ActionResolution(true, appliedDamage);
	}

	private void NotifyMonsterAggro(CombatAction action, long tick)
	{
		if (_target is Monster monster && _owner is Adventurer adventurer)
		{
			if (_notifyMonsterAggro is not null)
			{
				_notifyMonsterAggro(adventurer, monster, action, tick);
			}
			else
			{
				monster.SetAggroTarget(adventurer, action.ActionId, "ability_resolved", tick);
			}
		}
	}

	private void StartCooldowns(CombatAction action, long tick)
	{
		if (action.UsesGlobalAttackCooldown)
		{
			int durationTicks = GetBasicAttackCooldownTicks();
			BasicAttackCooldownTicksRemaining = durationTicks;
			EmitCooldownStarted(action.ActionId, "basic_attack", durationTicks, tick);
		}

		if (action.Kind != CombatActionKind.BasicAttack && action.CooldownTicks > 0)
		{
			_skillCooldowns[action.ActionId] = action.CooldownTicks;
			EmitCooldownStarted(action.ActionId, "skill", action.CooldownTicks, tick);
		}

		if (action.Kind is CombatActionKind.Skill or CombatActionKind.Spell)
		{
			GlobalCooldownTicksRemaining = GlobalCooldownTicks;
			EmitCooldownStarted("global_cooldown", "global", GlobalCooldownTicks, tick);
		}
	}

	private int GetBasicAttackCooldownTicks()
	{
		return Math.Max(1, _owner.AttackSpeed);
	}

	private void CancelQueuedAction(CombatAction action, long tick, string reason, double? distanceToTarget = null)
	{
		_queuedAction = null;
		_queuedTarget = null;
		_activeAction = null;
		GDict extras = new()
		{
			{ "cancel_reason", reason }
		};

		if (distanceToTarget is double distance)
		{
			extras["distance_to_target"] = distance;
		}

		_emitEvent("combat_action_cancelled", BuildActionPayload(action, tick, extras));
	}

	private bool TrySelectTarget(long tick, string reason)
	{
		if (_selectTarget is null)
		{
			return false;
		}

		ICombatant? previousTarget = _target;
		ICombatant? nextTarget = _selectTarget();

		if (nextTarget?.IsAlive != true)
		{
			return false;
		}

		_target = nextTarget;

		if (!ReferenceEquals(previousTarget, nextTarget))
		{
			_emitEvent("combat_target_changed", new GDict
			{
				{ "source", nameof(CombatActionRunner) },
				{ "tick", tick },
				{ "combatant", _owner.DisplayName },
				{ "combatant_kind", _owner.CombatantKind },
				{ "previous_target", previousTarget?.DisplayName ?? "none" },
				{ "target", nextTarget.DisplayName },
				{ "target_kind", nextTarget.CombatantKind },
				{ "reason", reason }
			});
		}

		return true;
	}

	private void ChangeState(CombatState nextState)
	{
		CombatState previousState = State;

		if (previousState == nextState)
		{
			return;
		}

		State = nextState;
		if (previousState != CombatState.Queued && nextState != CombatState.Queued)
		{
			GD.Print($"COMBAT_STATE tick={_currentTick} combatant={_owner.DisplayName} from={previousState} to={nextState}");
		}
		_emitEvent("combat_state_changed", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", _currentTick },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "from", previousState.ToString() },
			{ "to", nextState.ToString() }
		});
	}

	private void EmitReady(long tick)
	{
		_emitEvent("combatant_ready", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "basic_attack_cooldown_ticks_remaining", BasicAttackCooldownTicksRemaining },
			{ "global_cooldown_ticks_remaining", GlobalCooldownTicksRemaining },
			{ "ready_skill_count", _skillCooldowns.Count(pair => pair.Value <= 0) }
		});
	}

	private void EmitCooldownStarted(string actionId, string cooldownKind, int durationTicks, long tick)
	{
		_emitEvent("combat_action_cooldown_started", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "action_id", actionId },
			{ "cooldown_kind", cooldownKind },
			{ "duration_ticks", durationTicks }
		});
	}

	private void EmitCooldownReady(string actionId, string cooldownKind, long tick)
	{
		_emitEvent("combat_action_cooldown_ready", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "action_id", actionId },
			{ "cooldown_kind", cooldownKind }
		});
	}

	private GDict BuildActionPayload(CombatAction action, long tick, GDict? extras = null)
	{
		GDict payload = new()
		{
			{ "source", nameof(CombatActionRunner) },
			{ "tick", tick },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "action_id", action.ActionId },
			{ "action_name", action.DisplayName },
			{ "action_kind", action.Kind.ToString() },
			{ "cooldown_ticks", GetActionCooldownTicks(action) },
			{ "range", action.Range },
			{ "distance_to_target", GetDistanceToTarget() },
			{ "cast_ticks", action.CastTicks },
			{ "recovery_ticks", action.RecoveryTicks },
			{ "action_weight", action.ActionWeight },
			{ "basic_attack_cooldown_ticks_remaining", BasicAttackCooldownTicksRemaining },
			{ "global_cooldown_ticks_remaining", GlobalCooldownTicksRemaining }
		};

		if (extras is not null)
		{
			foreach (Variant key in extras.Keys)
			{
				payload[key] = extras[key];
			}
		}

		return payload;
	}

	private double GetDistanceToTarget()
	{
		if (_target is null || _owner is not Node2D ownerNode || _target is not Node2D targetNode)
		{
			return 0.0;
		}

		return ownerNode.GlobalPosition.DistanceTo(targetNode.GlobalPosition);
	}

	private void PublishSnapshot()
	{
		_owner.SetCombatSnapshot(new CombatantCombatSnapshot
		{
			State = State,
			CurrentTargetName = _target?.DisplayName ?? string.Empty,
			QueuedActionId = _queuedAction?.ActionId ?? string.Empty,
			ActiveActionId = _activeAction?.ActionId ?? string.Empty,
			LastActionId = _lastActionId,
			BasicAttackCooldownTicksRemaining = BasicAttackCooldownTicksRemaining,
			GlobalCooldownTicksRemaining = GlobalCooldownTicksRemaining,
			CastTicksRemaining = CastTicksRemaining,
			RecoveryTicksRemaining = RecoveryTicksRemaining,
			SkillCooldowns = new Dictionary<string, int>(_skillCooldowns, StringComparer.Ordinal),
			IsDisabled = IsDisabled,
			CanAct = CanAct
		});
		_owner.PublishState();
	}

	private readonly record struct ActionResolution(bool Hit, int Damage);
}

public sealed record QueuedCombatAction(
	CombatActionRunner Runner,
	ICombatant Actor,
	ICombatant? Target,
	CombatAction Action);

public sealed record RolledCombatAction(
	QueuedCombatAction QueuedAction,
	int RandomRoll,
	int InitiativeScore);
