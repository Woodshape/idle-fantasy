#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public enum AdventurerIntentionState
{
	IdleInTown,
	ChooseTarget,
	TravelToTarget,
	FightMonster,
	CollectLoot,
	ReturnToTown,
	RecoverInTown,
	Dead
}

public partial class AdventurerController : Node
{
	[Export]
	public float MeleeApproachDistance { get; set; } = 42.0f;

	[Export(PropertyHint.Range, "0.0,1.0,0.05")]
	public float RestHealthRatio { get; set; } = 0.45f;

	[Export(PropertyHint.Range, "0.0,1.0,0.05")]
	public float DamageDealerRestHealthRatio { get; set; } = 0.70f;

	[Export(PropertyHint.Range, "0.0,1.0,0.05")]
	public float WoundedRetreatHealthRatio { get; set; } = 0.50f;

	[Export]
	public int MaxEncounterMonsters { get; set; } = 1;

	[Export]
	public int MaxEncounterAdventurers { get; set; } = 1;

	private Adventurer? _adventurer;
	private GameController? _game;
	private Town? _town;
	private readonly List<Monster> _encounterMonsters = new();
	private readonly HashSet<string> _lootedMonsterIds = new(StringComparer.Ordinal);
	private double _stateTimer;
	private bool _combatStartedForTarget;
	private bool _movementPausedForCurrentCast;

	public AdventurerIntentionState State { get; private set; } = AdventurerIntentionState.IdleInTown;

	public override void _Ready()
	{
		_adventurer = GetParentOrNull<Adventurer>();
		ChangeState(AdventurerIntentionState.IdleInTown);
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!ResolveContext())
		{
			return;
		}

		_stateTimer -= delta;

		if (_adventurer is null || _game is null || _town is null)
		{
			return;
		}

		if (!_adventurer.IsAlive && State != AdventurerIntentionState.Dead)
		{
			ChangeState(AdventurerIntentionState.Dead);
		}

		switch (State)
		{
			case AdventurerIntentionState.IdleInTown:
				if (_stateTimer <= 0.0)
				{
					ChangeState(AdventurerIntentionState.ChooseTarget);
				}
				break;
			case AdventurerIntentionState.ChooseTarget:
				ChooseTarget();
				break;
			case AdventurerIntentionState.TravelToTarget:
				UpdateTravel(delta);
				break;
			case AdventurerIntentionState.FightMonster:
				UpdateFight(delta);
				break;
			case AdventurerIntentionState.CollectLoot:
				CollectLoot();
				break;
			case AdventurerIntentionState.ReturnToTown:
				UpdateReturn(delta);
				break;
			case AdventurerIntentionState.RecoverInTown:
				if (_town.Recover(_adventurer, delta))
				{
					ChangeState(AdventurerIntentionState.IdleInTown);
				}
				break;
			case AdventurerIntentionState.Dead:
				break;
		}

		_adventurer.PublishState();
	}

	private bool ResolveContext()
	{
		_adventurer ??= GetParentOrNull<Adventurer>();
		_game ??= GetTree().CurrentScene as GameController;
		_town ??= _game?.Town;
		return _adventurer is not null && _game is not null && _town is not null;
	}

	private void ChooseTarget()
	{
		if (_adventurer is null || _game is null)
		{
			return;
		}

		if (ShouldReturnToTownForRest())
		{
			_adventurer.ClearCombatTarget();
			EmitRetreatChosen("health_below_rest_threshold");
			ChangeState(AdventurerIntentionState.ReturnToTown);
			return;
		}

		_encounterMonsters.Clear();
		_lootedMonsterIds.Clear();
		_encounterMonsters.AddRange(_game.FindHuntTargets(_adventurer, MaxEncounterMonsters));
		Monster? target = _encounterMonsters.FirstOrDefault();

		if (target is null)
		{
			_adventurer.ClearCombatTarget();
			ChangeState(AdventurerIntentionState.ReturnToTown);
			return;
		}

		_adventurer.SetCombatTarget(target);
		float approachDistance = GetOpeningApproachDistance();
		Vector2 approachPosition = GetCombatApproachPosition(target, approachDistance);
		_adventurer.SetMoveTarget(approachPosition);
		EmitBridgeEvent("adventurer_target_selected", new GDict
		{
			{ "source", nameof(AdventurerController) },
			{ "adventurer", _adventurer.AdventurerName },
			{ "monster", target.MonsterName },
			{ "monster_count", _encounterMonsters.Count },
			{ "target_position", BridgePayload.VectorToArray(approachPosition) },
			{ "monster_position", BridgePayload.VectorToArray(target.GlobalPosition) },
			{ "combat_approach_distance", approachDistance },
			{ "combat_approach_source", "shared_combat_positioning" }
		});
		ChangeState(AdventurerIntentionState.TravelToTarget);
	}

	private void UpdateTravel(double delta)
	{
		if (_adventurer is null)
		{
			return;
		}

		Monster? target = _adventurer.CurrentMonsterTarget;

		if (target is null || !target.IsAlive)
		{
			ChangeState(AdventurerIntentionState.ChooseTarget);
			return;
		}

		_adventurer.SetMoveTarget(GetCombatApproachPosition(target, GetOpeningApproachDistance()));

		if (_adventurer.MoveTowardTarget(delta))
		{
			EmitBridgeEvent("adventurer_arrived_at_target", new GDict
			{
				{ "source", nameof(AdventurerController) },
				{ "adventurer", _adventurer.AdventurerName },
				{ "monster", target.MonsterName },
				{ "position", BridgePayload.VectorToArray(_adventurer.GlobalPosition) },
				{ "monster_position", BridgePayload.VectorToArray(target.GlobalPosition) },
				{ "distance_to_monster", _adventurer.GlobalPosition.DistanceTo(target.GlobalPosition) }
			});
			ChangeState(AdventurerIntentionState.FightMonster);
		}
	}

	private Vector2 GetCombatApproachPosition(Monster target, float approachDistance)
	{
		if (_adventurer is null)
		{
			return target.GlobalPosition;
		}

		Vector2 approachDirection = target.GlobalPosition - _adventurer.GlobalPosition;

		if (approachDirection.LengthSquared() <= 0.001f)
		{
			approachDirection = Vector2.Right;
		}

		return target.GlobalPosition - approachDirection.Normalized() * approachDistance;
	}

	private float GetOpeningApproachDistance()
	{
		if (_adventurer is null)
		{
			return MeleeApproachDistance;
		}

		return _adventurer.CombatController?.GetDesiredCombatDistance(_adventurer, MeleeApproachDistance)
			?? MeleeApproachDistance;
	}

	private void UpdateFight(double delta)
	{
		if (_adventurer is null || _game is null)
		{
			return;
		}

		Monster? target = _adventurer.CurrentMonsterTarget;

		if (!_adventurer.IsAlive)
		{
			ChangeState(AdventurerIntentionState.Dead);
			return;
		}

		if (target is null)
		{
			ChangeState(AdventurerIntentionState.ChooseTarget);
			return;
		}

		if (!_combatStartedForTarget)
		{
			IReadOnlyList<Adventurer> adventurers = _game.FindEncounterAdventurers(_adventurer, MaxEncounterAdventurers);
			IReadOnlyList<Monster> monsters = _encounterMonsters.Count > 0
				? _encounterMonsters.Where(monster => monster.IsAlive).ToArray()
				: new[] { target };
			_adventurer.CombatController?.StartCombat(adventurers, monsters, _game.SimulationTickCount);
			_combatStartedForTarget = true;
		}

		if (target.IsAlive)
		{
			if (_adventurer.CombatState == CombatState.Casting)
			{
				PauseMovementForCast(target);
			}
			else if (_adventurer.CombatState is not CombatState.OutOfCombat and not CombatState.Engaging)
			{
				_movementPausedForCurrentCast = false;
				MoveIntoCombatRange(target, delta);
			}
		}

		if (!target.IsAlive)
		{
			Monster? nextTarget = _adventurer.CombatController?.GetCurrentMonsterTarget(_adventurer)
				?? _encounterMonsters.FirstOrDefault(monster => monster.IsAlive);

			if (nextTarget?.IsAlive == true)
			{
				_adventurer.SetCombatTarget(nextTarget);
				return;
			}

			if (_adventurer.CombatController?.HasLivingMonsters == true)
			{
				return;
			}

			ChangeState(AdventurerIntentionState.CollectLoot);
		}
	}

	private void MoveIntoCombatRange(Monster target, double delta)
	{
		if (_adventurer is null)
		{
			return;
		}

		float desiredDistance = GetOpeningApproachDistance();
		float distanceToTarget = _adventurer.GlobalPosition.DistanceTo(target.GlobalPosition);

		if (distanceToTarget <= desiredDistance)
		{
			_adventurer.ClearMoveTarget();
			return;
		}

		_adventurer.SetMoveTarget(GetCombatApproachPosition(target, desiredDistance));
		_adventurer.MoveTowardTarget(delta);
	}

	private void PauseMovementForCast(Monster target)
	{
		if (_adventurer is null || _movementPausedForCurrentCast)
		{
			return;
		}

		bool movementWasPending = _adventurer.ClearMoveTarget();
		_movementPausedForCurrentCast = true;
		EmitBridgeEvent("adventurer_cast_movement_paused", new GDict
		{
			{ "source", nameof(AdventurerController) },
			{ "adventurer", _adventurer.AdventurerName },
			{ "monster", target.MonsterName },
			{ "action_id", _adventurer.ActiveActionId },
			{ "cast_ticks_remaining", _adventurer.CastTicksRemaining },
			{ "movement_was_pending", movementWasPending },
			{ "position", BridgePayload.VectorToArray(_adventurer.GlobalPosition) },
			{ "monster_position", BridgePayload.VectorToArray(target.GlobalPosition) },
			{ "distance_to_monster", _adventurer.GlobalPosition.DistanceTo(target.GlobalPosition) }
		});
	}

	private void CollectLoot()
	{
		if (_adventurer is null)
		{
			return;
		}

		IReadOnlyList<Monster> rewardMonsters = _adventurer.CombatController?.EncounterMonsters.Count > 0
			? _adventurer.CombatController.EncounterMonsters
			: _encounterMonsters;
		List<Monster> defeatedMonsters = rewardMonsters.Count > 0
			? rewardMonsters
				.Where(monster => !monster.IsAlive && !_lootedMonsterIds.Contains(monster.CombatantId))
				.ToList()
			: _adventurer.CurrentMonsterTarget is Monster target
				&& !target.IsAlive
				&& !_lootedMonsterIds.Contains(target.CombatantId)
					? new List<Monster> { target }
					: new List<Monster>();

		if (defeatedMonsters.Count == 0)
		{
			_encounterMonsters.Clear();
			ChangeState(AdventurerIntentionState.ReturnToTown);
			return;
		}

		foreach (Monster defeatedMonster in defeatedMonsters)
		{
			_lootedMonsterIds.Add(defeatedMonster.CombatantId);
			_adventurer.AddRewards(defeatedMonster.GoldReward, defeatedMonster.ExperienceReward);
			GD.Print($"LOOT_COLLECTED adventurer={_adventurer.AdventurerName} monster={defeatedMonster.MonsterName} gold={defeatedMonster.GoldReward} xp={defeatedMonster.ExperienceReward}");
			EmitBridgeEvent("loot_collected", new GDict
			{
				{ "source", nameof(AdventurerController) },
				{ "adventurer", _adventurer.AdventurerName },
				{ "monster", defeatedMonster.MonsterName },
				{ "gold", defeatedMonster.GoldReward },
				{ "experience", defeatedMonster.ExperienceReward },
				{ "total_gold", _adventurer.Gold },
				{ "current_experience", _adventurer.Experience },
				{ "xp_to_next_level", _adventurer.XpToNextLevel },
				{ "total_experience", _adventurer.TotalExperience }
			});
		}

		_adventurer.ClearCombatTarget();
		_encounterMonsters.Clear();
		if (ShouldReturnToTownForRest())
		{
			EmitRetreatChosen("post_combat_health_below_rest_threshold");
			ChangeState(AdventurerIntentionState.ReturnToTown);
			return;
		}

		ChangeState(AdventurerIntentionState.ChooseTarget);
	}

	private bool ShouldReturnToTownForRest()
	{
		if (_adventurer is null)
		{
			return true;
		}

		int restHealth = Mathf.CeilToInt(_adventurer.Stats.MaxHealth * GetRestHealthRatio());
		return _adventurer.Health <= restHealth;
	}

	private float GetRestHealthRatio()
	{
		float baselineRestHealthRatio = RestHealthRatio;

		if (_adventurer?.Role == CombatantRole.DamageDealer)
		{
			baselineRestHealthRatio = DamageDealerRestHealthRatio;
		}

		return Math.Max(baselineRestHealthRatio, WoundedRetreatHealthRatio);
	}

	private void EmitRetreatChosen(string reason)
	{
		if (_adventurer is null)
		{
			return;
		}

		EmitBridgeEvent("adventurer_retreat_chosen", new GDict
		{
			{ "source", nameof(AdventurerController) },
			{ "adventurer", _adventurer.AdventurerName },
			{ "reason", reason },
			{ "health", _adventurer.Health },
			{ "max_health", _adventurer.Stats.MaxHealth },
			{ "health_ratio", _adventurer.Stats.MaxHealth <= 0 ? 0.0 : _adventurer.Health / (double)_adventurer.Stats.MaxHealth },
			{ "rest_health_ratio", GetRestHealthRatio() },
			{ "state", State.ToString() }
		});
	}

	private void UpdateReturn(double delta)
	{
		if (_adventurer is null || _town is null)
		{
			return;
		}

		_adventurer.SetMoveTarget(_town.ReturnPosition);

		if (_adventurer.MoveTowardTarget(delta))
		{
			EmitBridgeEvent("adventurer_returned_to_town", new GDict
			{
				{ "source", nameof(AdventurerController) },
				{ "adventurer", _adventurer.AdventurerName },
				{ "town", _town.DisplayName },
				{ "position", BridgePayload.VectorToArray(_adventurer.GlobalPosition) }
			});
			ChangeState(AdventurerIntentionState.RecoverInTown);
		}
	}

	private void ChangeState(AdventurerIntentionState nextState)
	{
		if (_adventurer is null)
		{
			State = nextState;
			return;
		}

		AdventurerIntentionState previousState = State;
		State = nextState;
		_stateTimer = nextState switch
		{
			AdventurerIntentionState.IdleInTown => 0.35,
			_ => 0.0
		};

		if (nextState != AdventurerIntentionState.FightMonster)
		{
			_combatStartedForTarget = false;
			_movementPausedForCurrentCast = false;
		}

		GD.Print($"ADVENTURER_STATE adventurer={_adventurer.AdventurerName} from={previousState} to={nextState}");
		EmitBridgeEvent("adventurer_state_changed", new GDict
		{
			{ "source", nameof(AdventurerController) },
			{ "adventurer", _adventurer.AdventurerName },
			{ "from", previousState.ToString() },
			{ "to", nextState.ToString() }
		});

		if (nextState == AdventurerIntentionState.Dead)
		{
			_game?.NotifyAdventurerDied();
			EmitBridgeEvent("adventurer_died", new GDict
			{
				{ "source", nameof(AdventurerController) },
				{ "adventurer", _adventurer.AdventurerName }
			});
		}

		_adventurer.PublishState();
	}

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}
}
