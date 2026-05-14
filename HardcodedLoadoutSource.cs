#nullable enable

using System;
using System.Collections.Generic;

public sealed class HardcodedLoadoutSource : ICombatLoadoutSource
{
	public CombatLoadout ResolveLoadout(ICombatant combatant)
	{
		if (combatant is Monster)
		{
			return new CombatLoadout(
				"slime_starting",
				"slime",
				CreateMonsterActions());
		}

		if (combatant is Adventurer adventurer)
		{
			string loadoutId = adventurer.Archetype == AdventurerArchetype.Mage
				? "mage_starting"
				: "warrior_starting";
			string definitionId = adventurer.Archetype == AdventurerArchetype.Mage
				? "mage"
				: "warrior";
			IReadOnlyList<CombatAction> actions = CreateAdventurerActions(adventurer);

			return new CombatLoadout(loadoutId, definitionId, actions);
		}

		throw new ArgumentException($"Unsupported combatant type: {combatant.GetType()}");
	}

	public static IReadOnlyList<CombatAction> CreateLegacyAdventurerActions()
	{
		return new[]
		{
			CreateHeavyStrike(),
			CreateSpark(),
			CombatAction.BasicAttack()
		};
	}

	public static IReadOnlyList<CombatAction> CreateAdventurerActions(Adventurer adventurer)
	{
		return adventurer.Archetype switch
		{
			AdventurerArchetype.Mage => new[]
			{
				CreateSpark(),
				CombatAction.BasicAttack(160.0, "basic_attack_ranged")
			},
			_ => new[]
			{
				CreateHeavyStrike(),
				CombatAction.BasicAttack()
			}
		};
	}

	public static IReadOnlyList<CombatAction> CreateMonsterActions()
	{
		return new[]
		{
			CombatAction.BasicAttack()
		};
	}

	public static CombatAction CreateHeavyStrike()
	{
		return new CombatAction(
			"heavy_strike",
			"Heavy Strike",
			CombatActionKind.Skill,
			48.0,
			12,
			0,
			1,
			true,
			false,
			10,
			1.5,
			false);
	}

	public static CombatAction CreateSpark()
	{
		return new CombatAction(
			"spark",
			"Spark",
			CombatActionKind.Spell,
			160.0,
			8,
			8,
			0,
			true,
			false,
			4,
			1.2,
			false);
	}
}
