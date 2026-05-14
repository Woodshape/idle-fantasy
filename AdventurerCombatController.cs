#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public partial class AdventurerCombatController : Node
{
	private readonly RandomNumberGenerator _rng = new();
	private ICombatLoadoutSource _loadoutSource = new FallbackLoadoutSource();
	private Adventurer? _adventurer;
	private CombatEncounter? _encounter;
	private int _encounterId;

	public CombatState State => _encounter?.GetRunner(_adventurer)?.State ?? _adventurer?.CombatState ?? CombatState.OutOfCombat;
	public int AttackCooldownTicksRemaining => _encounter?.GetRunner(_adventurer)?.BasicAttackCooldownTicksRemaining ?? 0;
	public IReadOnlyList<Adventurer> EncounterAdventurers => _encounter?.Adventurers ?? Array.Empty<Adventurer>();
	public IReadOnlyList<Monster> EncounterMonsters => _encounter?.Monsters ?? Array.Empty<Monster>();
	public bool HasActiveEncounter => _encounter?.IsActive == true;
	public bool HasLivingMonsters => _encounter?.Monsters.Any(monster => monster.IsAlive) == true;

	public override void _Ready()
	{
		_rng.Randomize();
		_adventurer = GetParentOrNull<Adventurer>();
	}

	public void SetLoadoutSource(ICombatLoadoutSource loadoutSource)
	{
		_loadoutSource = loadoutSource;
	}

	public double GetLongestTargetActionRange(ICombatant combatant)
	{
		return _loadoutSource
			.ResolveLoadout(combatant)
			.Actions
			.Where(action => action.RequiresTarget)
			.Select(action => action.Range)
			.DefaultIfEmpty(0.0)
			.Max();
	}

	public void StartCombat(Monster target, long currentTick)
	{
		_adventurer ??= GetParentOrNull<Adventurer>();

		if (_adventurer is null)
		{
			return;
		}

		StartCombat(new[] { _adventurer }, new[] { target }, currentTick);
	}

	public void StartCombat(IEnumerable<Adventurer> adventurers, IEnumerable<Monster> monsters, long currentTick)
	{
		_adventurer ??= GetParentOrNull<Adventurer>();

		StopCombat();
		CombatEncounter encounter = new(
			++_encounterId,
			adventurers,
			monsters,
			_loadoutSource,
			_rng,
			EmitBridgeEvent,
			ApplySceneSocialAggro);

		if (!encounter.CanStart)
		{
			PublishEncounterState();
			return;
		}

		_encounter = encounter;
		_encounter.Start(currentTick);

		GD.Print($"COMBAT_STARTED tick={currentTick} encounter={_encounterId} adventurers={_encounter.Adventurers.Count} monsters={_encounter.Monsters.Count}");
		EmitBridgeEvent("combat_started", new GDict
		{
			{ "source", nameof(AdventurerCombatController) },
			{ "encounter_id", _encounterId },
			{ "tick", currentTick },
			{ "adventurer", _encounter.Adventurers[0].AdventurerName },
			{ "monster", _encounter.Monsters[0].MonsterName },
			{ "adventurers", BuildCombatantNames(_encounter.Adventurers) },
			{ "monsters", BuildCombatantNames(_encounter.Monsters) },
			{ "adventurer_count", _encounter.Adventurers.Count },
			{ "monster_count", _encounter.Monsters.Count },
			{ "adventurer_attack_speed_ticks", _encounter.Adventurers[0].AttackSpeed },
			{ "monster_attack_speed_ticks", _encounter.Monsters[0].AttackSpeed },
			{ "tick_interval_seconds", 0.25 },
			{ "timing_model", "simulation_ticks" }
		});
		PublishEncounterState();
	}

	public bool TryAddAggroMonster(Monster monster, Adventurer aggroTarget, string aggroTrigger, long currentTick, string actionId = "")
	{
		bool added = _encounter?.TryAddMonster(monster, aggroTarget, aggroTrigger, currentTick, actionId) == true;

		if (added)
		{
			PublishEncounterState();
		}

		return added;
	}

	public void ProcessSimulationTick(long tick, double tickIntervalSeconds)
	{
		if (_encounter?.IsActive != true)
		{
			return;
		}

		_encounter.ProcessTick(tick, tickIntervalSeconds);
		PublishEncounterState();
	}

	public void StopCombat()
	{
		if (_encounter is null)
		{
			return;
		}

		_encounter.Stop();
		PublishEncounterState();
		_encounter = null;
	}

	public Monster? GetCurrentMonsterTarget(Adventurer adventurer)
	{
		return _encounter?.GetRunner(adventurer)?.Target as Monster
			?? _encounter?.Monsters.FirstOrDefault(monster => monster.IsAlive);
	}

	private void ApplySceneSocialAggro(Monster primaryTarget, Adventurer attacker, string actionId, long currentTick)
	{
		(GetTree().CurrentScene as GameController)?.ApplySocialAggro(primaryTarget, attacker, actionId, currentTick);
	}

	private void PublishEncounterState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		CombatEncounterState state = _encounter?.BuildState()
			?? new CombatEncounterState(
				_encounterId,
				false,
				0,
				0.25,
				Array.Empty<CombatantEncounterState>(),
				Array.Empty<CombatantEncounterState>());
		TestBridge.Instance.EmitState("combat_encounter", state.ToBridgeDictionary(nameof(AdventurerCombatController), _adventurer));
	}

	private static Godot.Collections.Array BuildCombatantNames<TCombatant>(IEnumerable<TCombatant> combatants)
		where TCombatant : ICombatant
	{
		Godot.Collections.Array names = new();

		foreach (TCombatant combatant in combatants)
		{
			names.Add(combatant.DisplayName);
		}

		return names;
	}

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}
}
