#nullable enable

using Godot;

[GlobalClass]
public partial class CombatStatsDefinition : Resource
{
	[Export]
	public int MaxHealth { get; set; } = 1;

	[Export]
	public int Attack { get; set; } = 1;

	[Export]
	public double Accuracy { get; set; }

	[Export]
	public int Defense { get; set; }

	[Export]
	public double Evasion { get; set; }

	[Export]
	public int Initiative { get; set; }

	[Export]
	public int AttackSpeedTicks { get; set; } = 1;

	public CombatStats ToRuntimeStats()
	{
		return new CombatStats(
			Attack,
			Accuracy,
			Defense,
			Evasion,
			Initiative,
			AttackSpeedTicks,
			MaxHealth,
			MaxHealth);
	}
}
