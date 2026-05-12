#nullable enable

using Godot;
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
	public float CombatApproachDistance { get; set; } = 42.0f;

	private Adventurer? _adventurer;
	private GameController? _game;
	private Town? _town;
	private double _stateTimer;
	private bool _combatStartedForTarget;

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
				UpdateFight();
				break;
			case AdventurerIntentionState.CollectLoot:
				CollectLoot();
				break;
			case AdventurerIntentionState.ReturnToTown:
				UpdateReturn(delta);
				break;
			case AdventurerIntentionState.RecoverInTown:
				if (_stateTimer <= 0.0)
				{
					_town.Recover(_adventurer);
					_game.NotifyLoopCompleted();
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

		Monster? target = _game.FindHuntTarget(_adventurer);

		if (target is null)
		{
			_stateTimer = 0.5;
			ChangeState(AdventurerIntentionState.IdleInTown);
			return;
		}

		_adventurer.SetCombatTarget(target);
		Vector2 approachPosition = GetCombatApproachPosition(target);
		_adventurer.SetMoveTarget(approachPosition);
		EmitBridgeEvent("adventurer_target_selected", new GDict
		{
			{ "source", nameof(AdventurerController) },
			{ "adventurer", _adventurer.AdventurerName },
			{ "monster", target.MonsterName },
			{ "target_position", BridgePayload.VectorToArray(approachPosition) },
			{ "monster_position", BridgePayload.VectorToArray(target.GlobalPosition) },
			{ "combat_approach_distance", CombatApproachDistance }
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

		_adventurer.SetMoveTarget(GetCombatApproachPosition(target));

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

	private Vector2 GetCombatApproachPosition(Monster target)
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

		return target.GlobalPosition - approachDirection.Normalized() * CombatApproachDistance;
	}

	private void UpdateFight()
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
			_adventurer.CombatController?.StartCombat(target, _game.SimulationTickCount);
			_combatStartedForTarget = true;
		}

		if (!target.IsAlive)
		{
			ChangeState(AdventurerIntentionState.CollectLoot);
		}
	}

	private void CollectLoot()
	{
		if (_adventurer is null)
		{
			return;
		}

		Monster? target = _adventurer.CurrentMonsterTarget;

		if (target is null)
		{
			ChangeState(AdventurerIntentionState.ReturnToTown);
			return;
		}

		_adventurer.AddRewards(target.GoldReward, target.ExperienceReward);
		GD.Print($"LOOT_COLLECTED adventurer={_adventurer.AdventurerName} monster={target.MonsterName} gold={target.GoldReward} xp={target.ExperienceReward}");
		EmitBridgeEvent("loot_collected", new GDict
		{
			{ "source", nameof(AdventurerController) },
			{ "adventurer", _adventurer.AdventurerName },
			{ "monster", target.MonsterName },
			{ "gold", target.GoldReward },
			{ "experience", target.ExperienceReward },
			{ "total_gold", _adventurer.Gold },
			{ "total_experience", _adventurer.Experience }
		});
		_adventurer.ClearCombatTarget();
		ChangeState(AdventurerIntentionState.ReturnToTown);
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
			AdventurerIntentionState.RecoverInTown => 0.5,
			_ => 0.0
		};

		if (nextState != AdventurerIntentionState.FightMonster)
		{
			_combatStartedForTarget = false;
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
