using System.Collections.Generic;

namespace Voltage.Dialogue
{
	public enum ConditionOperator
	{
		IsTrue,
		IsFalse,
		Equal,
		NotEqual,
		Greater,
		GreaterOrEqual,
		Less,
		LessOrEqual,
	}

	/// <summary>
	/// A structured comparison rather than an expression language: built from dropdowns, so it cannot be
	/// malformed and needs no parser. Compound logic chains <see cref="ConditionNode"/>s.
	/// </summary>
	public sealed class DialogueCondition
	{
		public string Variable;
		public ConditionOperator Op;

		/// <summary>Unused by <see cref="ConditionOperator.IsTrue"/> and <see cref="ConditionOperator.IsFalse"/>.</summary>
		public DialogueValue Value;

		public bool NeedsValue => Op is not (ConditionOperator.IsTrue or ConditionOperator.IsFalse);

		public override string ToString()
		{
			var name = string.IsNullOrEmpty(Variable) ? "<unset>" : Variable;
			return Op switch
			{
				ConditionOperator.IsTrue => name,
				ConditionOperator.IsFalse => "!" + name,
				ConditionOperator.Equal => $"{name} == {Value}",
				ConditionOperator.NotEqual => $"{name} != {Value}",
				ConditionOperator.Greater => $"{name} > {Value}",
				ConditionOperator.GreaterOrEqual => $"{name} >= {Value}",
				ConditionOperator.Less => $"{name} < {Value}",
				ConditionOperator.LessOrEqual => $"{name} <= {Value}",
				_ => name,
			};
		}
	}

	public enum SetOperation
	{
		Assign,
		Add,
		Subtract,
		Toggle,
	}

	/// <summary>One variable mutation performed by a <see cref="SetVariableNode"/>.</summary>
	public sealed class DialogueAssignment
	{
		public string Variable;
		public SetOperation Op;

		/// <summary>Unused by <see cref="SetOperation.Toggle"/>.</summary>
		public DialogueValue Value;

		public bool NeedsValue => Op != SetOperation.Toggle;

		public override string ToString()
		{
			var name = string.IsNullOrEmpty(Variable) ? "<unset>" : Variable;
			return Op switch
			{
				SetOperation.Assign => $"{name} = {Value}",
				SetOperation.Add => $"{name} += {Value}",
				SetOperation.Subtract => $"{name} -= {Value}",
				SetOperation.Toggle => $"{name} = !{name}",
				_ => name,
			};
		}
	}

	/// <summary>A variable declared on the graph, with the value it starts at.</summary>
	public sealed class DialogueVariableDef
	{
		public string Name;
		public DialogueValue Default;

		public DialogueValueKind Kind => Default?.Kind ?? DialogueValueKind.Bool;
	}

	/// <summary>Result of <see cref="DialogueGraph.Validate"/>.</summary>
	public sealed class DialogueValidationIssue
	{
		public string NodeId;
		public string Message;

		public override string ToString() =>
			string.IsNullOrEmpty(NodeId) ? Message : $"[{NodeId}] {Message}";
	}

	/// <summary>Convenience alias so callers do not spell the list type out.</summary>
	public sealed class DialogueValidationReport : List<DialogueValidationIssue>
	{
	}
}
