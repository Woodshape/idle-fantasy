#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GDict = Godot.Collections.Dictionary;

public partial class GameController : Node2D
{
	[Export]
	public NodePath TownPath { get; set; } = new("Town");

	[Export]
	public NodePath AdventurerPath { get; set; } = new("Adventurer");

	[Export]
	public NodePath MonsterContainerPath { get; set; } = new("Monsters");

	private Town? _town;
	private Adventurer? _adventurer;
	private readonly List<Monster> _monsters = new();
	private Label? _stateLabel;
	private Label? _combatLabel;
	private Label? _rewardLabel;
	private int _completedLoops;
	private bool _loopStopped;

	public Town? Town => _town;
	public Adventurer? Adventurer => _adventurer;
	public int CompletedLoops => _completedLoops;
	public bool CompletedOnce => _completedLoops > 0;

	public override void _Ready()
	{
		_town = GetNodeOrNull<Town>(TownPath);
		_adventurer = GetNodeOrNull<Adventurer>(AdventurerPath);
		Node? monsterContainer = GetNodeOrNull(MonsterContainerPath);
		_monsters.Clear();

		if (monsterContainer is not null)
		{
			foreach (Node child in monsterContainer.GetChildren())
			{
				if (child is Monster monster)
				{
					_monsters.Add(monster);
				}
			}
		}

		_stateLabel = GetNodeOrNull<Label>("Hud/Panel/VBoxContainer/StateLabel");
		_combatLabel = GetNodeOrNull<Label>("Hud/Panel/VBoxContainer/CombatLabel");
		_rewardLabel = GetNodeOrNull<Label>("Hud/Panel/VBoxContainer/RewardLabel");

		UpdateHud();
		PublishState();
	}

	public override void _Process(double delta)
	{
		UpdateHud();
		PublishState();
	}

	public Monster? FindHuntTarget(Adventurer adventurer)
	{
		return _monsters
			.Where(monster => monster.IsAlive)
			.OrderBy(monster => monster.GlobalPosition.DistanceSquaredTo(adventurer.GlobalPosition))
			.FirstOrDefault();
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

		foreach (Monster monster in _monsters.Where(monster => !monster.IsAlive))
		{
			monster.ResetForNextHunt();
		}

		PublishState();
	}

	public void NotifyAdventurerDied()
	{
		_loopStopped = true;
		PublishState();
	}

	private void UpdateHud()
	{
		if (_adventurer is null)
		{
			return;
		}

		if (_stateLabel is not null)
		{
			_stateLabel.Text = $"Adventurer: {_adventurer.AdventurerName} | Intention: {_adventurer.IntentionStateName} | HP: {_adventurer.Health}/{_adventurer.MaxHealth}";
		}

		if (_combatLabel is not null)
		{
			string targetName = _adventurer.CurrentMonsterTarget?.MonsterName ?? "none";
			_combatLabel.Text = $"Combat: {_adventurer.CombatStateName} | Target: {targetName}";
		}

		if (_rewardLabel is not null)
		{
			_rewardLabel.Text = $"Gold: {_adventurer.Gold} | XP: {_adventurer.Experience} | Loops: {_completedLoops}";
		}
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
			{ "living_monsters", _monsters.Count(monster => monster.IsAlive) },
			{ "monster_count", _monsters.Count }
		});
	}

	private static void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}
}
