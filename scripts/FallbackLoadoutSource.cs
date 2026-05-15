#nullable enable

using System;

public sealed class FallbackLoadoutSource : ICombatLoadoutSource
{
	public CombatLoadout ResolveLoadout(ICombatant combatant)
	{
		string definitionId = combatant switch
		{
			Adventurer => "warrior",
			Monster => "slime",
			_ => throw new ArgumentException($"Unsupported combatant type: {combatant.GetType()}")
		};

		return new CombatLoadout(
			"fallback_basic_attack",
			definitionId,
			new[] { CombatAction.BasicAttack() });
	}
}
