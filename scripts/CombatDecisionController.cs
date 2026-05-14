#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public sealed class CombatDecisionController : ICombatDecisionPolicy
{
	public CombatDecision ChooseDecision(CombatDecisionContext context)
	{
		CombatActionRunner runner = context.Runner;

		foreach (CombatAction action in runner.Loadout.Actions)
		{
			ICombatant? target = SelectTarget(runner, action, context);

			if (runner.IsActionReady(action, target))
			{
				return new CombatDecision(action, target, "shared_combat_policy");
			}
		}

		return new CombatDecision(null, null, "no_ready_action");
	}

	private static ICombatant? SelectTarget(
		CombatActionRunner runner,
		CombatAction action,
		CombatDecisionContext context)
	{
		if (!action.RequiresTarget)
		{
			return null;
		}

		if (IsValidReadyTarget(runner, action, context.TargetCandidates, context.PreferredTarget))
		{
			return context.PreferredTarget;
		}

		if (runner.Target is ICombatant currentTarget
			&& IsValidReadyTarget(runner, action, context.TargetCandidates, currentTarget))
		{
			return currentTarget;
		}

		return context.TargetCandidates
			.Where(candidate => candidate.IsAlive)
			.Where(candidate => runner.IsActionReady(action, candidate))
			.OrderBy(candidate => GetDistanceSquared(runner.Owner, candidate))
			.ThenBy(candidate => candidate.CombatantId, StringComparer.Ordinal)
			.FirstOrDefault();
	}

	private static bool IsValidReadyTarget(
		CombatActionRunner runner,
		CombatAction action,
		IReadOnlyList<ICombatant> targetCandidates,
		ICombatant? target)
	{
		return target?.IsAlive == true
			&& targetCandidates.Any(candidate => ReferenceEquals(candidate, target))
			&& runner.IsActionReady(action, target);
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
