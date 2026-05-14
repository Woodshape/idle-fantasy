#nullable enable

using Godot;
using System.Linq;

public static class CombatPositioning
{
	public const float RangePadding = 6.0f;

	public static float GetDesiredCombatDistance(
		ICombatant actor,
		CombatLoadout loadout,
		float meleeDistance)
	{
		double basicAttackRange = loadout.Actions
			.Where(action => action.RequiresTarget && action.Kind == CombatActionKind.BasicAttack)
			.Select(action => action.Range)
			.DefaultIfEmpty(meleeDistance)
			.Max();

		return Mathf.Max(meleeDistance, (float)basicAttackRange - RangePadding);
	}
}
