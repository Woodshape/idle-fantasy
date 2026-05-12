#nullable enable

using Godot;
using GDict = Godot.Collections.Dictionary;

public partial class Monster : Node2D
{
	[Export]
	public string MonsterName { get; set; } = "Slime";

	[Export]
	public int Level { get; set; } = 1;

	[Export]
	public int MaxHealth { get; set; } = 18;

	[Export]
	public int Attack { get; set; } = 3;

	[Export]
	public double Accuracy { get; set; } = 0.0;

	[Export]
	public int Defense { get; set; } = 1;

	[Export]
	public double Evasion { get; set; } = 0.0;

	[Export]
	public int GoldReward { get; set; } = 7;

	[Export]
	public int ExperienceReward { get; set; } = 10;

	public int Health { get; private set; }
	public AdventurerCombatState CombatState { get; private set; } = AdventurerCombatState.OutOfCombat;
	public bool IsAlive => Health > 0;

	public override void _Ready()
	{
		Health = MaxHealth;
		PublishState();
	}

	public int ApplyDamage(int amount)
	{
		if (!IsAlive)
		{
			return 0;
		}

		int previousHealth = Health;
		Health = Mathf.Max(0, Health - Mathf.Max(0, amount));

		if (Health <= 0)
		{
			CombatState = AdventurerCombatState.Defeated;
		}

		QueueRedraw();
		PublishState();
		return previousHealth - Health;
	}

	public void SetCombatState(AdventurerCombatState state)
	{
		if (CombatState == state)
		{
			return;
		}

		CombatState = state;
		PublishState();
	}

	public void ResetForNextHunt()
	{
		Health = MaxHealth;
		CombatState = AdventurerCombatState.OutOfCombat;
		QueueRedraw();
		GD.Print($"MONSTER_RESPAWNED monster={MonsterName}");
		PublishState();
	}

	public void PublishState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		TestBridge.Instance.EmitState("monster", new GDict
		{
			{ "source", nameof(Monster) },
			{ "name", MonsterName },
			{ "level", Level },
			{ "health", Health },
			{ "max_health", MaxHealth },
			{ "attack", Attack },
			{ "accuracy", Accuracy },
			{ "defense", Defense },
			{ "evasion", Evasion },
			{ "gold_reward", GoldReward },
			{ "experience_reward", ExperienceReward },
			{ "is_alive", IsAlive },
			{ "combat_state", CombatState.ToString() },
			{ "position", BridgePayload.VectorToArray(GlobalPosition) }
		});
	}

	public override void _Draw()
	{
		Color bodyColor = IsAlive ? new Color(0.71f, 0.20f, 0.23f) : new Color(0.25f, 0.25f, 0.25f);
		DrawCircle(Vector2.Zero, 18.0f, bodyColor);
		DrawArc(Vector2.Zero, 24.0f, 0.0f, Mathf.Tau, 32, new Color(0.12f, 0.08f, 0.09f), 2.0f);
	}
}
