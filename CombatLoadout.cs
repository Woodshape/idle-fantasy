#nullable enable

using System.Collections.Generic;

public sealed record CombatLoadout(
	string LoadoutId,
	string DefinitionId,
	// Action order is the deterministic priority contract for decision policy.
	IReadOnlyList<CombatAction> Actions);

public interface ICombatLoadoutSource
{
	CombatLoadout ResolveLoadout(ICombatant combatant);
}

public sealed record CombatDecision(
	CombatAction? Action,
	ICombatant? Target,
	string Reason);

public interface ICombatDecisionPolicy
{
	CombatDecision ChooseAction(
		ICombatant actor,
		CombatLoadout loadout,
		IReadOnlyList<ICombatant> targetCandidates);
}
