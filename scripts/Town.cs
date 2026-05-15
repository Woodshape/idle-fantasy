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

	[Export]
	public int PaidRecoveryCost { get; set; } = 5;

	[Export]
	public int PaidRecoveryHeal { get; set; } = 12;

	private readonly Dictionary<string, double> _recoveryProgress = new(StringComparer.Ordinal);
	private readonly HashSet<string> _paidRecoveryAttempted = new(StringComparer.Ordinal);
	private readonly HashSet<string> _unaffordableRecoveryEmitted = new(StringComparer.Ordinal);

	public Vector2 ReturnPosition => GlobalPosition;

	public bool Recover(Adventurer adventurer, double delta)
	{
		if (!adventurer.IsAlive)
		{
			return false;
		}

		if (adventurer.Health >= adventurer.Stats.MaxHealth)
		{
			ClearRecoveryState(adventurer.CombatantId);
			EmitRecovered(adventurer);
			return true;
		}

		string recoveryKey = adventurer.CombatantId;
		TryApplyPaidRecovery(adventurer, recoveryKey);

		if (adventurer.Health >= adventurer.Stats.MaxHealth)
		{
			ClearRecoveryState(recoveryKey);
			EmitRecovered(adventurer);
			return true;
		}

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

		ClearRecoveryState(recoveryKey);
		EmitRecovered(adventurer);
		return true;
	}

	private void TryApplyPaidRecovery(Adventurer adventurer, string recoveryKey)
	{
		if (_paidRecoveryAttempted.Contains(recoveryKey))
		{
			return;
		}

		int healAmount = Math.Max(0, PaidRecoveryHeal);

		if (healAmount <= 0)
		{
			return;
		}

		_paidRecoveryAttempted.Add(recoveryKey);
		int cost = Math.Max(0, PaidRecoveryCost);
		int goldBefore = adventurer.Gold;

		if (cost > 0 && !adventurer.SpendGold(cost))
		{
			EmitPaidRecoveryUnaffordable(adventurer, cost, goldBefore, recoveryKey);
			return;
		}

		int healthBefore = adventurer.Health;
		int healed = adventurer.Heal(healAmount);

		if (cost > 0)
		{
			EmitGoldSpent(adventurer, cost, goldBefore);
		}

		GD.Print($"TOWN_SERVICE_USED service=paid_recovery adventurer={adventurer.AdventurerName} town={DisplayName} cost={cost} healed={healed} health={adventurer.Health}/{adventurer.Stats.MaxHealth}");
		TestBridge.Instance?.EmitEvent("town_service_used", new GDict
		{
			{ "source", nameof(Town) },
			{ "service", "paid_recovery" },
			{ "adventurer", adventurer.AdventurerName },
			{ "town", DisplayName },
			{ "cost", cost },
			{ "requested_heal", healAmount },
			{ "healed", healed },
			{ "health_before", healthBefore },
			{ "health_after", adventurer.Health },
			{ "max_health", adventurer.Stats.MaxHealth },
			{ "gold_before", goldBefore },
			{ "gold_after", adventurer.Gold }
		});
	}

	private void EmitGoldSpent(Adventurer adventurer, int amount, int goldBefore)
	{
		GD.Print($"GOLD_SPENT adventurer={adventurer.AdventurerName} service=paid_recovery amount={amount} gold={adventurer.Gold}");
		TestBridge.Instance?.EmitEvent("gold_spent", new GDict
		{
			{ "source", nameof(Town) },
			{ "spender", adventurer.AdventurerName },
			{ "spender_kind", "adventurer" },
			{ "service", "paid_recovery" },
			{ "town", DisplayName },
			{ "amount", amount },
			{ "gold_before", goldBefore },
			{ "gold_after", adventurer.Gold }
		});
	}

	private void EmitPaidRecoveryUnaffordable(Adventurer adventurer, int cost, int gold, string recoveryKey)
	{
		if (!_unaffordableRecoveryEmitted.Add(recoveryKey))
		{
			return;
		}

		GD.Print($"TOWN_SERVICE_UNAFFORDABLE service=paid_recovery adventurer={adventurer.AdventurerName} town={DisplayName} cost={cost} gold={gold}");
		TestBridge.Instance?.EmitEvent("town_service_unaffordable", new GDict
		{
			{ "source", nameof(Town) },
			{ "service", "paid_recovery" },
			{ "adventurer", adventurer.AdventurerName },
			{ "town", DisplayName },
			{ "cost", cost },
			{ "gold", gold },
			{ "health", adventurer.Health },
			{ "max_health", adventurer.Stats.MaxHealth }
		});
	}

	private void ClearRecoveryState(string recoveryKey)
	{
		_recoveryProgress.Remove(recoveryKey);
		_paidRecoveryAttempted.Remove(recoveryKey);
		_unaffordableRecoveryEmitted.Remove(recoveryKey);
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
