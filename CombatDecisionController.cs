#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public sealed class CombatDecisionController
{
	public CombatDecision ChooseDecision(
		CombatActionRunner runner,
		IReadOnlyList<ICombatant> targetCandidates)
	{
		foreach (CombatAction action in runner.Loadout.Actions)
		{
			ICombatant? target = SelectTarget(runner, action, targetCandidates);

			if (runner.IsActionReady(action, target))
			{
				return new CombatDecision(action, target, "list_priority");
			}
		}

		return new CombatDecision(null, null, "no_ready_action");
	}

	private static ICombatant? SelectTarget(
		CombatActionRunner runner,
		CombatAction action,
		IReadOnlyList<ICombatant> targetCandidates)
	{
		if (!action.RequiresTarget)
		{
			return null;
		}

		if (runner.Owner is Monster monster
			&& monster.AggroTarget is Adventurer aggroTarget
			&& targetCandidates.Any(candidate => ReferenceEquals(candidate, aggroTarget))
			&& runner.IsActionReady(action, aggroTarget))
		{
			return aggroTarget;
		}

		if (runner.Target is ICombatant currentTarget
			&& targetCandidates.Any(candidate => ReferenceEquals(candidate, currentTarget))
			&& runner.IsActionReady(action, currentTarget))
		{
			return currentTarget;
		}

		return targetCandidates
			.Where(candidate => candidate.IsAlive)
			.Where(candidate => runner.IsActionReady(action, candidate))
			.OrderBy(candidate => GetDistanceSquared(runner.Owner, candidate))
			.ThenBy(candidate => candidate.CombatantId, StringComparer.Ordinal)
			.FirstOrDefault();
	}

	private static float GetDistanceSquared(ICombatant actor, ICombatant candidate)
	{
		if (actor is Node2D actorNode && candidate is Node2D candidateNode)
		{
			return actorNode.GlobalPosition.DistanceSquaredTo(candidateNode.GlobalPosition);
		}

		return 0.0f;
	}
}
