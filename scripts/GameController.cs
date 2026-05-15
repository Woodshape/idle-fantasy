#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public partial class GameController : Node2D
{
	private const double SimulationTickInterval = 0.25;
	private const double MonsterWaveRespawnDelaySeconds = 5.0;
	private const float CharacterSelectionRadius = 34.0f;

	[Export]
	public NodePath TownPath { get; set; } = new("Town");

	[Export]
	public NodePath AdventurerPath { get; set; } = new("Adventurer");

	[Export]
	public NodePath MonsterContainerPath { get; set; } = new("Monsters");

	[Export]
	public PackedScene? AdventurerScene { get; set; }

	[Export]
	public PackedScene? MonsterScene { get; set; }

	[Export]
	public GameContentLibrary? ContentLibrary { get; set; }

	[Export]
	public bool AutoSpawnDefaultAdventurers { get; set; } = true;

	[Export]
	public bool AutoSpawnDefaultMonsters { get; set; } = true;

	private Town? _town;
	private Adventurer? _adventurer;
	private readonly List<Adventurer> _adventurers = new();
	private readonly List<Monster> _monsters = new();
	private Label? _stateLabel;
	private Label? _combatLabel;
	private Label? _rewardLabel;
	private int _completedLoops;
	private bool _loopStopped;
	private double _simulationAccumulator;
	private long _simulationTickCount;
	private bool _monsterWaveRespawnPending;
	private double _monsterWaveRespawnTimer;
	private ICombatant? _selectedCombatant;
	private bool _contentLibraryValid;
	private ICombatLoadoutSource _loadoutSource = new FallbackLoadoutSource();

	public Town? Town => _town;
	public Adventurer? Adventurer => _adventurer;
	public IReadOnlyList<Adventurer> Adventurers => _adventurers;
	public int CompletedLoops => _completedLoops;
	public bool CompletedOnce => _completedLoops > 0;
	public long SimulationTickCount => _simulationTickCount;

	public override void _Ready()
	{
		_town = GetNodeOrNull<Town>(TownPath);
		_adventurer = GetNodeOrNull<Adventurer>(AdventurerPath);
		Node monsterContainer = GetOrCreateMonsterContainer();
		_monsters.Clear();
		ValidateContentLibrary();

		_adventurers.Clear();

		foreach (Node child in GetChildren())
		{
			if (child is Adventurer adventurer && !_adventurers.Contains(adventurer))
			{
				_adventurers.Add(adventurer);
			}
		}

		if (AutoSpawnDefaultAdventurers)
		{
			EnsureDefaultAdventurers();
		}

		if (_adventurer is not null && !_adventurers.Contains(_adventurer))
		{
			_adventurers.Insert(0, _adventurer);
		}

		_adventurer ??= _adventurers.FirstOrDefault();
		_selectedCombatant ??= _adventurer;

		foreach (Node child in monsterContainer.GetChildren())
		{
			if (child is Monster monster)
			{
				_monsters.Add(monster);
			}
		}

		if (_monsters.Count == 0 && AutoSpawnDefaultMonsters)
		{
			SpawnDefaultMonsters(monsterContainer);
		}

		ConfigureCombatControllers();

		_stateLabel = GetNodeOrNull<Label>("Hud/Panel/VBoxContainer/StateLabel");
		_combatLabel = GetNodeOrNull<Label>("Hud/Panel/VBoxContainer/CombatLabel");
		_rewardLabel = GetNodeOrNull<Label>("Hud/Panel/VBoxContainer/RewardLabel");

		UpdateSelectionOutlines();
		UpdateHud();
		PublishState();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton mouseButton
			|| mouseButton.ButtonIndex != MouseButton.Left
			|| !mouseButton.Pressed)
		{
			return;
		}

		Vector2 worldPosition = GetViewport().GetCanvasTransform().AffineInverse() * mouseButton.Position;

		if (!TrySelectCharacterAt(worldPosition))
		{
			return;
		}

		GetViewport().SetInputAsHandled();
		UpdateSelectionOutlines();
		UpdateHud();
		PublishState();
	}

	private Node GetOrCreateMonsterContainer()
	{
		Node? monsterContainer = GetNodeOrNull(MonsterContainerPath);

		if (monsterContainer is not null)
		{
			return monsterContainer;
		}

		Node2D createdContainer = new()
		{
			Name = MonsterContainerPath.GetName(MonsterContainerPath.GetNameCount() - 1)
		};
		AddChild(createdContainer);
		return createdContainer;
	}

	private void EnsureDefaultAdventurers()
	{
		if (_contentLibraryValid && ContentLibrary?.DefaultAdventurerSpawns.Length > 0)
		{
			SpawnDefaultAdventurersFromContent();
			return;
		}

		bool hasWarrior = _adventurers.Any(adventurer => adventurer.Archetype == AdventurerArchetype.Warrior);
		bool hasMage = _adventurers.Any(adventurer => adventurer.Archetype == AdventurerArchetype.Mage);

		if (!hasWarrior)
		{
			Adventurer? warrior = SpawnDefaultAdventurer(
				nodeName: "Warrior",
				adventurerName: "Warrior",
				archetype: AdventurerArchetype.Warrior,
				stats: new CombatStats(8, 0.22, 2, 0.12, 3, 4, 36, 36),
				positionOffset: new Vector2(0.0f, -28.0f),
				spriteModulate: new Color(0.25f, 0.55f, 1.0f));

			if (warrior is not null)
			{
				_adventurer ??= warrior;
			}
		}

		if (!hasMage)
		{
			SpawnDefaultAdventurer(
				nodeName: "Mage",
				adventurerName: "Mage",
				archetype: AdventurerArchetype.Mage,
				stats: new CombatStats(7, 0.30, 1, 0.18, 5, 5, 32, 32),
				positionOffset: new Vector2(0.0f, 28.0f),
				spriteModulate: new Color(0.75f, 0.45f, 1.0f));
		}
	}

	private void SpawnDefaultAdventurersFromContent()
	{
		if (ContentLibrary is null)
		{
			return;
		}

		foreach (AdventurerSpawnDefinition spawn in ContentLibrary.DefaultAdventurerSpawns.Where(spawn => spawn is not null && spawn.Enabled))
		{
			AdventurerDefinition? definition = spawn.ActorDefinition;

			if (definition is null || HasAdventurerDefinition(definition.DefinitionId))
			{
				continue;
			}

			Vector2 position = spawn.PositionIsTownRelative
				? (_town?.ReturnPosition ?? Vector2.Zero) + spawn.Position
				: spawn.Position;
			Adventurer? adventurer = SpawnDefaultAdventurer(spawn, definition, position);

			if (adventurer is not null)
			{
				_adventurer ??= adventurer;
			}
		}
	}

	private bool HasAdventurerDefinition(string definitionId)
	{
		return _adventurers.Any(adventurer => string.Equals(adventurer.DefinitionId, definitionId, StringComparison.Ordinal));
	}

	private Adventurer? SpawnDefaultAdventurer(
		AdventurerSpawnDefinition spawn,
		AdventurerDefinition definition,
		Vector2 position)
	{
		if (AdventurerScene is null)
		{
			GD.PushError("AdventurerScene is not assigned; cannot spawn runtime adventurer.");
			return null;
		}

		Adventurer adventurer = AdventurerScene.Instantiate<Adventurer>();
		adventurer.Name = string.IsNullOrWhiteSpace(spawn.NodeName) ? definition.DisplayName : spawn.NodeName;
		adventurer.SetupFromDefinition(definition, spawn.DisplayNameOverride, position);
		AddChild(adventurer);
		_adventurers.Add(adventurer);
		adventurer.CombatController?.SetLoadoutSource(_loadoutSource);
		return adventurer;
	}

	private Adventurer? SpawnDefaultAdventurer(
		string nodeName,
		string adventurerName,
		AdventurerArchetype archetype,
		CombatStats stats,
		Vector2 positionOffset,
		Color spriteModulate)
	{
		if (AdventurerScene is null)
		{
			GD.PushError("AdventurerScene is not assigned; cannot spawn runtime adventurer.");
			return null;
		}

		Adventurer adventurer = AdventurerScene.Instantiate<Adventurer>();
		adventurer.Name = nodeName;
		adventurer.Setup(
			adventurerName: adventurerName,
			archetype: archetype,
			stats: stats,
			position: (_town?.ReturnPosition ?? adventurer.Position) + positionOffset);
		if (adventurer.GetNodeOrNull<Sprite2D>("Sprite2D") is Sprite2D sprite)
		{
			sprite.Modulate = spriteModulate;
		}
		AddChild(adventurer);
		_adventurers.Add(adventurer);
		return adventurer;
	}

	private void SpawnDefaultMonsters(Node monsterContainer)
	{
		if (MonsterScene is null)
		{
			GD.PushError("MonsterScene is not assigned; cannot spawn runtime monsters.");
			return;
		}

		if (_contentLibraryValid && ContentLibrary?.DefaultMonsterSpawns.Length > 0)
		{
			SpawnDefaultMonstersFromContent(monsterContainer);
			return;
		}

		SpawnMonster(monsterContainer, "Slime", "Slime", new Vector2(580.0f, 300.0f));
		SpawnMonster(monsterContainer, "Slime2", "Slime 2", new Vector2(670.0f, 240.0f));
		SpawnMonster(monsterContainer, "Slime3", "Slime 3", new Vector2(670.0f, 360.0f));
	}

	private void SpawnDefaultMonstersFromContent(Node monsterContainer)
	{
		if (ContentLibrary is null)
		{
			return;
		}

		foreach (MonsterSpawnDefinition spawn in ContentLibrary.DefaultMonsterSpawns.Where(spawn => spawn is not null && spawn.Enabled))
		{
			MonsterDefinition? definition = spawn.ActorDefinition;

			if (definition is null)
			{
				continue;
			}

			Vector2 position = spawn.PositionIsTownRelative
				? (_town?.ReturnPosition ?? Vector2.Zero) + spawn.Position
				: spawn.Position;
			SpawnMonster(monsterContainer, spawn, definition, position);
		}
	}

	private void SpawnMonster(Node monsterContainer, MonsterSpawnDefinition spawn, MonsterDefinition definition, Vector2 position)
	{
		if (MonsterScene is null)
		{
			return;
		}

		Monster monster = MonsterScene.Instantiate<Monster>();
		monster.Name = string.IsNullOrWhiteSpace(spawn.NodeName) ? definition.DisplayName : spawn.NodeName;
		monster.SetupFromDefinition(definition, spawn.DisplayNameOverride, position);
		monsterContainer.AddChild(monster);
		_monsters.Add(monster);
	}

	private void SpawnMonster(Node monsterContainer, string nodeName, string monsterName, Vector2 position)
	{
		if (MonsterScene is null)
		{
			return;
		}

		Monster monster = MonsterScene.Instantiate<Monster>();
		monster.Name = nodeName;
		monster.Setup(
			monsterName: monsterName,
			stats: monster.CreateStartingStats(),
			position: position);
		monsterContainer.AddChild(monster);
		_monsters.Add(monster);
	}

	private void ValidateContentLibrary()
	{
		_contentLibraryValid = false;
		_loadoutSource = new FallbackLoadoutSource();

		if (ContentLibrary is null)
		{
			EmitBridgeEvent("content_validation_failed", new GDict
			{
				{ "source", nameof(GameController) },
				{ "reason", "missing_content_library" }
			});
			GD.PushError("ContentLibrary is not assigned; falling back to hardcoded combat defaults.");
			return;
		}

		IReadOnlyList<string> errors = ContentLibrary.ValidateAndBuild();

		if (errors.Count > 0)
		{
			Godot.Collections.Array errorState = new();

			foreach (string error in errors)
			{
				errorState.Add(error);
				GD.PushError(error);
			}

			EmitBridgeEvent("content_validation_failed", new GDict
			{
				{ "source", nameof(GameController) },
				{ "reason", "invalid_content_library" },
				{ "errors", errorState }
			});
			return;
		}

		_contentLibraryValid = true;
		_loadoutSource = new DataBackedLoadoutSource(ContentLibrary, EmitBridgeEvent);
		EmitBridgeEvent("content_validation_completed", new GDict
		{
			{ "source", nameof(GameController) },
			{ "combat_action_count", ContentLibrary.CombatActions.Length },
			{ "combat_loadout_count", ContentLibrary.CombatLoadouts.Length },
			{ "adventurer_count", ContentLibrary.Adventurers.Length },
			{ "monster_count", ContentLibrary.Monsters.Length },
			{ "default_adventurer_spawn_count", ContentLibrary.DefaultAdventurerSpawns.Length },
			{ "default_monster_spawn_count", ContentLibrary.DefaultMonsterSpawns.Length }
		});
	}

	private void ConfigureCombatControllers()
	{
		foreach (Adventurer adventurer in _adventurers)
		{
			adventurer.CombatController?.SetLoadoutSource(_loadoutSource);
		}
	}

	public override void _Process(double delta)
	{
		UpdateMonsterWaveRespawn(delta);
		UpdateHud();
		PublishState();
	}

	public override void _PhysicsProcess(double delta)
	{
		_simulationAccumulator += delta;

		while (_simulationAccumulator >= SimulationTickInterval)
		{
			_simulationAccumulator -= SimulationTickInterval;
			_simulationTickCount++;
			EmitBridgeEvent("simulation_tick", new GDict
			{
				{ "source", nameof(GameController) },
				{ "tick", _simulationTickCount },
				{ "interval", SimulationTickInterval },
				{ "responsibility", "world_and_combat" }
			});
			foreach (Monster monster in _monsters)
			{
				monster.ProcessSimulationTick(this, _simulationTickCount);
			}

			foreach (Adventurer adventurer in _adventurers)
			{
				adventurer.CombatController?.ProcessSimulationTick(_simulationTickCount, SimulationTickInterval);
			}

			PublishSimulationClockState();
		}
	}

	public Monster? FindHuntTarget(Adventurer adventurer)
	{
		return FindHuntTargets(adventurer, 1).FirstOrDefault();
	}

	public IReadOnlyList<Monster> FindHuntTargets(Adventurer adventurer, int maximumTargets)
	{
		return _monsters
			.Where(monster => monster.IsAlive)
			.Where(monster => !IsMonsterClaimedByAnotherAdventurer(monster, adventurer))
			.OrderBy(monster => monster.GlobalPosition.DistanceSquaredTo(adventurer.GlobalPosition))
			.Take(Math.Max(1, maximumTargets))
			.ToArray();
	}

	private bool IsMonsterClaimedByAnotherAdventurer(Monster monster, Adventurer adventurer)
	{
		return _adventurers.Any(candidate =>
			!ReferenceEquals(candidate, adventurer)
			&& candidate.IsAlive
			&& ReferenceEquals(candidate.CurrentMonsterTarget, monster));
	}

	public IReadOnlyList<Adventurer> FindEncounterAdventurers(Adventurer leader, int maximumAdventurers)
	{
		return _adventurers
			.Where(adventurer => adventurer.IsAlive)
			.OrderBy(adventurer => ReferenceEquals(adventurer, leader) ? 0 : 1)
			.ThenBy(adventurer => adventurer.GlobalPosition.DistanceSquaredTo(leader.GlobalPosition))
			.Take(Math.Max(1, maximumAdventurers))
			.ToArray();
	}

	public bool TryAddAggroMonsterToEncounter(Monster monster, Adventurer aggroTarget, string aggroTrigger, long currentTick, string actionId = "")
	{
		if (!monster.IsAlive || !aggroTarget.IsAlive)
		{
			return false;
		}

		AdventurerCombatController? encounterController = _adventurers
			.Select(adventurer => adventurer.CombatController)
			.FirstOrDefault(controller =>
				controller?.HasActiveEncounter == true
				&& controller.EncounterAdventurers.Any(encounterAdventurer => ReferenceEquals(encounterAdventurer, aggroTarget)));

		return encounterController?.TryAddAggroMonster(monster, aggroTarget, aggroTrigger, currentTick, actionId) == true;
	}

	public void ApplySocialAggro(Monster sourceMonster, Adventurer aggroTarget, string actionId, long currentTick)
	{
		if (!sourceMonster.IsAlive || !aggroTarget.IsAlive)
		{
			return;
		}

		foreach (Monster monster in _monsters)
		{
			if (ReferenceEquals(monster, sourceMonster)
				|| !monster.IsAlive
				|| monster.GlobalPosition.DistanceTo(sourceMonster.GlobalPosition) > monster.AggroRange)
			{
				continue;
			}

			monster.SetAggroTarget(aggroTarget, actionId, "social_aggro", currentTick);
			TryAddAggroMonsterToEncounter(monster, aggroTarget, "social_aggro", currentTick, actionId);
		}
	}

	public void NotifyLoopCompleted()
	{
		_completedLoops++;
		GD.Print($"GAME_LOOP_COMPLETED count={_completedLoops}");
		EmitBridgeEvent("game_loop_completed", new GDict
		{
			{ "source", nameof(GameController) },
			{ "completed_loops", _completedLoops }
		});

		PublishState();
	}

	private void UpdateMonsterWaveRespawn(double delta)
	{
		if (_monsters.Count == 0 || _monsters.Any(monster => monster.IsAlive))
		{
			return;
		}

		if (!_monsterWaveRespawnPending)
		{
			_monsterWaveRespawnPending = true;
			_monsterWaveRespawnTimer = MonsterWaveRespawnDelaySeconds;
			NotifyLoopCompleted();
			EmitBridgeEvent("monster_wave_cleared", new GDict
			{
				{ "source", nameof(GameController) },
				{ "completed_loops", _completedLoops },
				{ "respawn_delay_seconds", MonsterWaveRespawnDelaySeconds }
			});
		}

		_monsterWaveRespawnTimer -= delta;

		if (_monsterWaveRespawnTimer > 0.0)
		{
			return;
		}

		foreach (Monster monster in _monsters.Where(monster => !monster.IsAlive))
		{
			monster.ResetForNextHunt();
		}

		_monsterWaveRespawnPending = false;
		_monsterWaveRespawnTimer = 0.0;
		EmitBridgeEvent("monster_wave_respawned", new GDict
		{
			{ "source", nameof(GameController) },
			{ "completed_loops", _completedLoops },
			{ "monster_count", _monsters.Count }
		});
		PublishState();
	}

	public void NotifyAdventurerDied()
	{
		_loopStopped = true;
		PublishState();
	}

	private void UpdateHud()
	{
		ICombatant? selectedCombatant = GetSelectedCombatant();

		if (selectedCombatant is null)
		{
			return;
		}

		switch (selectedCombatant)
		{
			case Adventurer adventurer:
				UpdateAdventurerHud(adventurer);
				break;
			case Monster monster:
				UpdateMonsterHud(monster);
				break;
		}
	}

	private void UpdateAdventurerHud(Adventurer adventurer)
	{
		if (_stateLabel is not null)
		{
			_stateLabel.Text = $"Adventurer: {adventurer.AdventurerName} | Role: {adventurer.Archetype} | Intention: {adventurer.IntentionStateName} | HP: {adventurer.Health}/{adventurer.MaxHealth}";
		}

		if (_combatLabel is not null)
		{
			string targetName = adventurer.CurrentMonsterTarget?.MonsterName ?? adventurer.CurrentCombatTargetName;
			_combatLabel.Text = $"Combat: {adventurer.CombatStateName} | Target: {FormatNone(targetName)} | Action: {GetDisplayedAction(adventurer)} | Cooldown: {GetLastAbilityCooldown(adventurer.SkillCooldowns)} ticks | GlobalCooldown: {adventurer.GlobalCooldownTicksRemaining} ticks";
		}

		if (_rewardLabel is not null)
		{
			_rewardLabel.Text = $"Gold: {adventurer.Gold} | XP: {adventurer.Experience} | Loops: {_completedLoops}";
		}
	}

	private void UpdateMonsterHud(Monster monster)
	{
		if (_stateLabel is not null)
		{
			_stateLabel.Text = $"Monster: {monster.MonsterName} | HP: {monster.Health}/{monster.MaxHealth} | Alive: {monster.IsAlive}";
		}

		if (_combatLabel is not null)
		{
			string targetName = monster.CurrentCombatTargetName != string.Empty
				? monster.CurrentCombatTargetName
				: monster.AggroTarget?.AdventurerName ?? string.Empty;
			_combatLabel.Text = $"Combat: {monster.CombatState} | Target: {FormatNone(targetName)} | Action: {GetDisplayedAction(monster)} | Cooldown: {GetLastAbilityCooldown(monster.SkillCooldowns)} ticks | GlobalCooldown: {monster.GlobalCooldownTicksRemaining} ticks";
		}

		if (_rewardLabel is not null)
		{
			_rewardLabel.Text = $"Gold Reward: {monster.GoldReward} | XP Reward: {monster.ExperienceReward} | Loops: {_completedLoops}";
		}
	}

	private ICombatant? GetSelectedCombatant()
	{
		return _selectedCombatant switch
		{
			Adventurer adventurer when _adventurers.Contains(adventurer) => adventurer,
			Monster monster when _monsters.Contains(monster) => monster,
			_ => _adventurer
		};
	}

	private void UpdateSelectionOutlines()
	{
		ICombatant? selectedCombatant = GetSelectedCombatant();

		foreach (Adventurer adventurer in _adventurers)
		{
			adventurer.SetSelected(ReferenceEquals(adventurer, selectedCombatant));
		}
	}

	private bool TrySelectCharacterAt(Vector2 worldPosition)
	{
		ICombatant? closestCombatant = null;
		float closestDistanceSquared = CharacterSelectionRadius * CharacterSelectionRadius;

		foreach (Adventurer adventurer in _adventurers)
		{
			UpdateClosest(adventurer, adventurer);
		}

		foreach (Monster monster in _monsters)
		{
			UpdateClosest(monster, monster);
		}

		if (closestCombatant is null)
		{
			return false;
		}

		_selectedCombatant = closestCombatant;
		EmitBridgeEvent("character_selected", new GDict
		{
			{ "source", nameof(GameController) },
			{ "name", closestCombatant.DisplayName },
			{ "kind", closestCombatant.CombatantKind },
			{ "position", BridgePayload.VectorToArray(((Node2D)closestCombatant).GlobalPosition) }
		});
		return true;

		void UpdateClosest(Node2D node, ICombatant combatant)
		{
			float distanceSquared = node.GlobalPosition.DistanceSquaredTo(worldPosition);

			if (distanceSquared > closestDistanceSquared)
			{
				return;
			}

			closestDistanceSquared = distanceSquared;
			closestCombatant = combatant;
		}
	}

	private static string GetDisplayedAction(Adventurer adventurer)
	{
		return FormatNone(adventurer.ActiveActionId != string.Empty
			? adventurer.ActiveActionId
			: adventurer.LastActionId);
	}

	private static string GetDisplayedAction(Monster monster)
	{
		return FormatNone(monster.ActiveActionId != string.Empty
			? monster.ActiveActionId
			: monster.LastActionId);
	}

	private static string FormatNone(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? "none" : value;
	}

	private void PublishState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		TestBridge.Instance.EmitState("game_loop", new GDict
		{
			{ "source", nameof(GameController) },
			{ "completed_loops", _completedLoops },
			{ "completed_once", CompletedOnce },
			{ "loop_stopped", _loopStopped },
			{ "living_adventurers", _adventurers.Count(adventurer => adventurer.IsAlive) },
			{ "adventurer_count", _adventurers.Count },
			{ "living_monsters", _monsters.Count(monster => monster.IsAlive) },
			{ "monster_count", _monsters.Count },
			{ "monster_wave_respawn_pending", _monsterWaveRespawnPending },
			{ "monster_wave_respawn_seconds_remaining", Math.Max(0.0, _monsterWaveRespawnTimer) },
			{ "selected_combatant", GetSelectedCombatant()?.DisplayName ?? string.Empty },
			{ "selected_combatant_kind", GetSelectedCombatant()?.CombatantKind ?? string.Empty }
		});
		PublishSimulationClockState();
	}

	private void PublishSimulationClockState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		TestBridge.Instance.EmitState("simulation_clock", new GDict
		{
			{ "source", nameof(GameController) },
			{ "interval", SimulationTickInterval },
			{ "tick_count", _simulationTickCount },
			{ "accumulator", _simulationAccumulator },
			{ "drives_basic_attacks", true }
		});
	}

	private static int GetLastAbilityCooldown(IReadOnlyDictionary<string, int> skillCooldowns)
	{
		return skillCooldowns.Values.LastOrDefault();
	}

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}
}
