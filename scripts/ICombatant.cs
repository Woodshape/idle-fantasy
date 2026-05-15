#nullable enable

public interface ICombatant
{
	string CombatantId { get; }
	string CombatantKind { get; }
	string DisplayName { get; }
	int Health { get; }
	bool IsAlive { get; }
	CombatStats Stats { get; }

	int ApplyDamage(int amount);
	void SetCombatSnapshot(CombatantCombatSnapshot snapshot);
	void PublishState();
}
