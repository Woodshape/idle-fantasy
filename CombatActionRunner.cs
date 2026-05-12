#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public sealed class CombatActionRunner
{
	private const double BaseHitChance = 0.75;
	private const double MinHitChance = 0.05;
	private const double MaxHitChance = 0.95;

	private readonly ICombatant _owner;
	private readonly IReadOnlyList<CombatAction> _actions;
	private readonly RandomNumberGenerator _rng;
	private readonly Action<string, GDict> _emitEvent;
	private readonly Dictionary<string, double> _skillCooldowns = new(StringComparer.Ordinal);
	private CombatAction? _queuedAction;
	private CombatAction? _activeAction;
	private ICombatant? _target;

	public CombatActionRunner(
		ICombatant owner,
		IReadOnlyList<CombatAction> actions,
		RandomNumberGenerator rng,
		Action<string, GDict> emitEvent)
	{
		_owner = owner;
		_actions = actions;
		_rng = rng;
		_emitEvent = emitEvent;

		foreach (CombatAction action in _actions.Where(action => action.Kind != CombatActionKind.BasicAttack))
		{
			_skillCooldowns[action.ActionId] = 0.0;
		}
	}

	public ICombatant Owner => _owner;
	public ICombatant? Target => _target;
	public CombatState State { get; private set; } = CombatState.OutOfCombat;
	public double BasicAttackCooldownRemaining { get; private set; }
	public double CastRemaining { get; private set; }
	public double RecoveryRemaining { get; private set; }
	public IReadOnlyDictionary<string, double> SkillCooldowns => _skillCooldowns;
	public string ActiveActionId => _activeAction?.ActionId ?? string.Empty;
	public string QueuedActionId => _queuedAction?.ActionId ?? string.Empty;
	public bool IsDisabled => State == CombatState.Disabled;
	public bool CanAct => State == CombatState.Ready && _owner.IsAlive && _target?.IsAlive == true;

	public void Start(ICombatant target)
	{
		_target = target;
		_queuedAction = null;
		_activeAction = null;
		CastRemaining = 0.0;
		RecoveryRemaining = 0.0;
		ChangeState(CombatState.Engaging);
		PublishSnapshot();
	}

	public void Stop()
	{
		_target = null;
		_queuedAction = null;
		_activeAction = null;
		BasicAttackCooldownRemaining = 0.0;
		CastRemaining = 0.0;
		RecoveryRemaining = 0.0;

		foreach (string key in _skillCooldowns.Keys.ToArray())
		{
			_skillCooldowns[key] = 0.0;
		}

		ChangeState(_owner.IsAlive ? CombatState.OutOfCombat : CombatState.Defeated);
		PublishSnapshot();
	}

	public void Update(double delta)
	{
		if (State is CombatState.OutOfCombat or CombatState.Defeated)
		{
			return;
		}

		if (!_owner.IsAlive)
		{
			ChangeState(CombatState.Defeated);
			PublishSnapshot();
			return;
		}

		if (_target is null || !_target.IsAlive)
		{
			_target = null;
			_queuedAction = null;
			_activeAction = null;
			ChangeState(CombatState.OutOfCombat);
			PublishSnapshot();
			return;
		}

		UpdateCooldowns(delta);

		switch (State)
		{
			case CombatState.Engaging:
				ChangeState(CombatState.Ready);
				EmitReady();
				TrySelectAndStartAction();
				break;
			case CombatState.Casting:
				UpdateCast(delta);
				break;
			case CombatState.Recovering:
				UpdateRecovery(delta);
				break;
			case CombatState.Ready:
				TrySelectAndStartAction();
				break;
		}

		PublishSnapshot();
	}

	private void UpdateCooldowns(double delta)
	{
		if (BasicAttackCooldownRemaining > 0.0)
		{
			double previous = BasicAttackCooldownRemaining;
			BasicAttackCooldownRemaining = Math.Max(0.0, BasicAttackCooldownRemaining - delta);

			if (previous > 0.0 && BasicAttackCooldownRemaining <= 0.0)
			{
				EmitCooldownReady("basic_attack", "basic_attack");
			}
		}

		foreach (string actionId in _skillCooldowns.Keys.ToArray())
		{
			double previous = _skillCooldowns[actionId];

			if (previous <= 0.0)
			{
				continue;
			}

			double current = Math.Max(0.0, previous - delta);
			_skillCooldowns[actionId] = current;

			if (current <= 0.0)
			{
				EmitCooldownReady(actionId, "skill");
			}
		}
	}

	private void UpdateCast(double delta)
	{
		if (_activeAction is null)
		{
			ChangeState(CombatState.Ready);
			return;
		}

		CastRemaining = Math.Max(0.0, CastRemaining - delta);

		if (CastRemaining > 0.0)
		{
			return;
		}

		_emitEvent("combat_cast_completed", BuildActionPayload(_activeAction));
		ResolveActiveAction();
	}

	private void UpdateRecovery(double delta)
	{
		RecoveryRemaining = Math.Max(0.0, RecoveryRemaining - delta);

		if (RecoveryRemaining > 0.0)
		{
			return;
		}

		_emitEvent("combat_recovery_completed", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind }
		});
		ChangeState(CombatState.Ready);
		EmitReady();
		TrySelectAndStartAction();
	}

	private void TrySelectAndStartAction()
	{
		if (!CanAct)
		{
			return;
		}

		CombatAction? action = SelectAction();

		if (action is null)
		{
			return;
		}

		_queuedAction = action;
		_emitEvent("combat_action_selected", BuildActionPayload(action));
		StartAction(action);
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
		if (action.RequiresTarget && _target?.IsAlive != true)
		{
			return false;
		}

		return action.Kind switch
		{
			CombatActionKind.BasicAttack => BasicAttackCooldownRemaining <= 0.0,
			_ => !_skillCooldowns.TryGetValue(action.ActionId, out double remaining) || remaining <= 0.0
		};
	}

	private void StartAction(CombatAction action)
	{
		_queuedAction = null;
		_activeAction = action;
		_emitEvent("combat_action_started", BuildActionPayload(action));

		if (action.CastTime > 0.0)
		{
			CastRemaining = action.CastTime;
			ChangeState(CombatState.Casting);
			_emitEvent("combat_cast_started", BuildActionPayload(action));
			return;
		}

		ChangeState(CombatState.UsingAction);
		ResolveActiveAction();
	}

	private void ResolveActiveAction()
	{
		if (_activeAction is not CombatAction action)
		{
			ChangeState(CombatState.Ready);
			return;
		}

		ActionResolution resolution = ResolveAction(action);
		StartCooldowns(action);
		_emitEvent("combat_action_resolved", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "action_id", action.ActionId },
			{ "action_kind", action.Kind.ToString() },
			{ "hit", resolution.Hit },
			{ "damage", resolution.Damage }
		});

		_activeAction = null;

		if (!_owner.IsAlive)
		{
			ChangeState(CombatState.Defeated);
			return;
		}

		if (_target?.IsAlive != true)
		{
			ChangeState(CombatState.Ready);
			return;
		}

		if (action.RecoveryTime > 0.0)
		{
			RecoveryRemaining = action.RecoveryTime;
			ChangeState(CombatState.Recovering);
			_emitEvent("combat_recovery_started", new GDict
			{
				{ "source", nameof(CombatActionRunner) },
				{ "combatant", _owner.DisplayName },
				{ "combatant_kind", _owner.CombatantKind },
				{ "duration", RecoveryRemaining }
			});
			return;
		}

		ChangeState(CombatState.Ready);
		EmitReady();
	}

	private ActionResolution ResolveAction(CombatAction action)
	{
		if (_target is null || !_target.IsAlive)
		{
			return new ActionResolution(false, 0);
		}

		double hitChance = Math.Clamp(BaseHitChance + _owner.Accuracy - _target.Evasion, MinHitChance, MaxHitChance);
		double roll = _rng.Randf();
		bool hit = roll <= hitChance;
		int rawDamage = (int)Math.Round(_owner.Attack * action.DamageMultiplier, MidpointRounding.AwayFromZero);
		int damage = hit ? Math.Max(1, rawDamage - _target.Defense) : 0;

		GD.Print($"ATTACK_ROLL attacker={_owner.DisplayName} defender={_target.DisplayName} action={action.ActionId} hit_chance={hitChance:0.00} roll={roll:0.00} hit={hit} damage={damage}");
		_emitEvent("attack_roll_resolved", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "attacker", _owner.DisplayName },
			{ "attacker_kind", _owner.CombatantKind },
			{ "defender", _target.DisplayName },
			{ "defender_kind", _target.CombatantKind },
			{ "action_id", action.ActionId },
			{ "hit_formula", "clamp(0.75 + attacker_accuracy - defender_evasion, 0.05, 0.95)" },
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
			{ "attacker", _owner.DisplayName },
			{ "attacker_kind", _owner.CombatantKind },
			{ "defender", _target.DisplayName },
			{ "defender_kind", _target.CombatantKind },
			{ "action_id", action.ActionId },
			{ "damage", appliedDamage }
		});

		return new ActionResolution(true, appliedDamage);
	}

	private void StartCooldowns(CombatAction action)
	{
		if (action.UsesBasicAttackCooldown)
		{
			double duration = 1.0 / Math.Max(0.01, _owner.AttackSpeed);
			BasicAttackCooldownRemaining = duration;
			EmitCooldownStarted(action.ActionId, "basic_attack", duration);
		}

		if (action.Kind != CombatActionKind.BasicAttack && action.Cooldown > 0.0)
		{
			_skillCooldowns[action.ActionId] = action.Cooldown;
			EmitCooldownStarted(action.ActionId, "skill", action.Cooldown);
		}
	}

	private void ChangeState(CombatState nextState)
	{
		CombatState previousState = State;

		if (previousState == nextState)
		{
			return;
		}

		State = nextState;
		GD.Print($"COMBAT_STATE combatant={_owner.DisplayName} from={previousState} to={nextState}");
		_emitEvent("combat_state_changed", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "from", previousState.ToString() },
			{ "to", nextState.ToString() }
		});
	}

	private void EmitReady()
	{
		_emitEvent("combatant_ready", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "basic_attack_cooldown_remaining", BasicAttackCooldownRemaining },
			{ "ready_skill_count", _skillCooldowns.Count(pair => pair.Value <= 0.0) }
		});
	}

	private void EmitCooldownStarted(string actionId, string cooldownKind, double duration)
	{
		_emitEvent("combat_action_cooldown_started", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "action_id", actionId },
			{ "cooldown_kind", cooldownKind },
			{ "duration", duration }
		});
	}

	private void EmitCooldownReady(string actionId, string cooldownKind)
	{
		_emitEvent("combat_action_cooldown_ready", new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "action_id", actionId },
			{ "cooldown_kind", cooldownKind }
		});
	}

	private GDict BuildActionPayload(CombatAction action)
	{
		return new GDict
		{
			{ "source", nameof(CombatActionRunner) },
			{ "combatant", _owner.DisplayName },
			{ "combatant_kind", _owner.CombatantKind },
			{ "target", _target?.DisplayName ?? "none" },
			{ "action_id", action.ActionId },
			{ "action_name", action.DisplayName },
			{ "action_kind", action.Kind.ToString() },
			{ "cooldown", action.Cooldown },
			{ "cast_time", action.CastTime },
			{ "recovery_time", action.RecoveryTime },
			{ "basic_attack_cooldown_remaining", BasicAttackCooldownRemaining }
		};
	}

	private void PublishSnapshot()
	{
		_owner.SetCombatSnapshot(new CombatantCombatSnapshot
		{
			State = State,
			CurrentTargetName = _target?.DisplayName ?? string.Empty,
			QueuedActionId = _queuedAction?.ActionId ?? string.Empty,
			ActiveActionId = _activeAction?.ActionId ?? string.Empty,
			BasicAttackCooldownRemaining = BasicAttackCooldownRemaining,
			CastRemaining = CastRemaining,
			RecoveryRemaining = RecoveryRemaining,
			SkillCooldowns = new Dictionary<string, double>(_skillCooldowns, StringComparer.Ordinal),
			IsDisabled = IsDisabled,
			CanAct = CanAct
		});
		_owner.PublishState();
	}

	private readonly record struct ActionResolution(bool Hit, int Damage);
}
