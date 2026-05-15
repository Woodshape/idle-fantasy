#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using GDict = Godot.Collections.Dictionary;

public partial class Town : Node2D
{
	[Export]
	public string DisplayName { get; set; } = "Town";

	[Export]
	public float ServiceRadius { get; set; } = 44.0f;

	[Export]
	public double RecoveryHealthPerSecond { get; set; } = 4.0;

	private readonly Dictionary<string, double> _recoveryProgress = new(StringComparer.Ordinal);

	public Vector2 ReturnPosition => GlobalPosition;

	public bool Recover(Adventurer adventurer, double delta)
	{
		if (!adventurer.IsAlive)
		{
			return false;
		}

		if (adventurer.Health >= adventurer.Stats.MaxHealth)
		{
			_recoveryProgress.Remove(adventurer.CombatantId);
			EmitRecovered(adventurer);
			return true;
		}

		string recoveryKey = adventurer.CombatantId;
		_recoveryProgress.TryGetValue(recoveryKey, out double pendingHealth);
		pendingHealth += Math.Max(0.0, delta) * RecoveryHealthPerSecond;
		int healAmount = (int)Math.Floor(pendingHealth);

		if (healAmount <= 0)
		{
			_recoveryProgress[recoveryKey] = pendingHealth;
			return false;
		}

		_recoveryProgress[recoveryKey] = pendingHealth - healAmount;
		int healed = adventurer.Heal(healAmount);

		if (healed > 0)
		{
			GD.Print($"TOWN_RECOVERY_TICK adventurer={adventurer.AdventurerName} town={DisplayName} healed={healed} health={adventurer.Health}/{adventurer.Stats.MaxHealth}");
			TestBridge.Instance?.EmitEvent("adventurer_recovery_tick", new GDict
			{
				{ "source", nameof(Town) },
				{ "adventurer", adventurer.AdventurerName },
				{ "town", DisplayName },
				{ "healed", healed },
				{ "health", adventurer.Health },
				{ "max_health", adventurer.Stats.MaxHealth }
			});
		}

		if (adventurer.Health < adventurer.Stats.MaxHealth)
		{
			return false;
		}

		_recoveryProgress.Remove(recoveryKey);
		EmitRecovered(adventurer);
		return true;
	}

	private void EmitRecovered(Adventurer adventurer)
	{
		GD.Print($"TOWN_RECOVERED adventurer={adventurer.AdventurerName} town={DisplayName}");
		TestBridge.Instance?.EmitEvent("adventurer_recovered", new GDict
		{
			{ "source", nameof(Town) },
			{ "adventurer", adventurer.AdventurerName },
			{ "town", DisplayName },
			{ "health", adventurer.Health },
			{ "max_health", adventurer.Stats.MaxHealth }
		});
	}

	public override void _Draw()
	{
		DrawCircle(Vector2.Zero, ServiceRadius, new Color(0.18f, 0.44f, 0.30f, 0.35f));
		DrawCircle(Vector2.Zero, 18.0f, new Color(0.18f, 0.63f, 0.36f));
		DrawLine(new Vector2(-16.0f, 0.0f), new Vector2(16.0f, 0.0f), Colors.White, 3.0f);
		DrawLine(new Vector2(0.0f, -16.0f), new Vector2(0.0f, 16.0f), Colors.White, 3.0f);
	}
}
