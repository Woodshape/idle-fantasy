#nullable enable

public interface ICombatant
{
	string CombatantId { get; }
	string CombatantKind { get; }
	string DisplayName { get; }
	int Attack { get; }
	double Accuracy { get; }
	int Defense { get; }
	double Evasion { get; }
	double AttackSpeed { get; }
	int Health { get; }
	int MaxHealth { get; }
	bool IsAlive { get; }
	CombatStats Stats { get; }

	int ApplyDamage(int amount);
	void SetCombatSnapshot(CombatantCombatSnapshot snapshot);
	void PublishState();
}
