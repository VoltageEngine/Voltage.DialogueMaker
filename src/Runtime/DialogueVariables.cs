using System;
using System.Collections.Generic;

namespace Voltage.Dialogue
{
	/// <summary>
	/// A conversation's mutable state, held by the session rather than the shared graph.
	/// <see cref="Snapshot"/> and <see cref="Restore"/> are the save-game surface.
	/// </summary>
	public sealed class DialogueVariables
	{
		private readonly Dictionary<string, DialogueValue> _values = new(StringComparer.Ordinal);

		/// <summary>Fires after a value actually changes, with the variable's name.</summary>
		public event Action<string> Changed;

		public IReadOnlyDictionary<string, DialogueValue> All => _values;

		/// <summary>
		/// Seeds every declared variable with its default, discarding current state. Values are cloned, so
		/// playback can never write back into the shared graph asset's declarations.
		/// </summary>
		public void ResetTo(DialogueGraph graph)
		{
			_values.Clear();
			if (graph?.Variables == null)
				return;

			foreach (var declaration in graph.Variables)
			{
				if (string.IsNullOrEmpty(declaration?.Name))
					continue;

				_values[declaration.Name] = declaration.Default?.Clone() ?? DialogueValue.Bool(false);
			}
		}

		/// <summary>Null when the variable was never declared or set.</summary>
		public DialogueValue Get(string name) =>
			name != null && _values.TryGetValue(name, out var value) ? value : null;

		public bool TryGet(string name, out DialogueValue value) =>
			_values.TryGetValue(name ?? string.Empty, out value);

		public void Set(string name, DialogueValue value)
		{
			if (string.IsNullOrEmpty(name) || value == null)
				return;

			if (_values.TryGetValue(name, out var existing) && existing.ValueEquals(value) && existing.Kind == value.Kind)
				return;

			_values[name] = value.Clone();
			Changed?.Invoke(name);
		}

		public void SetBool(string name, bool value) => Set(name, DialogueValue.Bool(value));
		public void SetInt(string name, int value) => Set(name, DialogueValue.Int(value));
		public void SetFloat(string name, float value) => Set(name, DialogueValue.Float(value));
		public void SetString(string name, string value) => Set(name, DialogueValue.String(value));

		public bool GetBool(string name, bool fallback = false) => Truthiness(Get(name), fallback);
		public int GetInt(string name, int fallback = 0) => Get(name) is { } v && v.IsNumeric ? (int)v.AsNumber() : fallback;
		public float GetFloat(string name, float fallback = 0f) => Get(name) is { } v && v.IsNumeric ? v.AsNumber() : fallback;

		public string GetString(string name, string fallback = null) =>
			Get(name) is { Kind: DialogueValueKind.String } v ? v.StringValue : fallback;

		/// <summary>A copy safe to persist; mutating it does not affect this store.</summary>
		public Dictionary<string, DialogueValue> Snapshot()
		{
			var copy = new Dictionary<string, DialogueValue>(_values.Count, StringComparer.Ordinal);
			foreach (var pair in _values)
				copy[pair.Key] = pair.Value?.Clone();

			return copy;
		}

		/// <summary>Replaces all state. Values are cloned, so the caller keeps ownership of its snapshot.</summary>
		public void Restore(IReadOnlyDictionary<string, DialogueValue> snapshot)
		{
			_values.Clear();
			if (snapshot == null)
				return;

			foreach (var pair in snapshot)
			{
				if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
					_values[pair.Key] = pair.Value.Clone();
			}
		}

		/// <summary>
		/// An unset variable is falsy rather than an error - a conversation should not throw mid-line
		/// because a declaration was removed. <see cref="DialogueGraph.Validate"/> catches it at author time.
		/// </summary>
		public bool Evaluate(DialogueCondition condition)
		{
			if (condition == null || string.IsNullOrEmpty(condition.Variable))
				return false;

			var current = Get(condition.Variable);

			switch (condition.Op)
			{
				case ConditionOperator.IsTrue:
					return Truthiness(current, false);
				case ConditionOperator.IsFalse:
					return !Truthiness(current, false);
			}

			var operand = condition.Value;
			if (current == null || operand == null)
				return false;

			switch (condition.Op)
			{
				case ConditionOperator.Equal:
					return current.ValueEquals(operand);
				case ConditionOperator.NotEqual:
					return !current.ValueEquals(operand);
			}

			// Ordering only means something for numbers. Comparing a string to an int is an authoring
			// mistake, and validation reports it; at runtime it is simply false.
			if (!current.IsNumeric || !operand.IsNumeric)
				return false;

			var a = current.AsNumber();
			var b = operand.AsNumber();

			return condition.Op switch
			{
				ConditionOperator.Greater => a > b,
				ConditionOperator.GreaterOrEqual => a >= b,
				ConditionOperator.Less => a < b,
				ConditionOperator.LessOrEqual => a <= b,
				_ => false,
			};
		}

		/// <summary>Applies an assignment. Unknown variables are created, so a game can set state the graph never declared.</summary>
		public void Apply(DialogueAssignment assignment)
		{
			if (assignment == null || string.IsNullOrEmpty(assignment.Variable))
				return;

			var name = assignment.Variable;
			var current = Get(name);

			switch (assignment.Op)
			{
				case SetOperation.Assign:
					if (assignment.Value != null)
						Set(name, assignment.Value);
					break;

				case SetOperation.Toggle:
					Set(name, DialogueValue.Bool(!Truthiness(current, false)));
					break;

				case SetOperation.Add:
				case SetOperation.Subtract:
					ApplyArithmetic(name, current, assignment);
					break;
			}
		}

		private void ApplyArithmetic(string name, DialogueValue current, DialogueAssignment assignment)
		{
			var operand = assignment.Value;
			if (operand == null)
				return;

			// String concatenation is the one sensible non-numeric case; += on a string is common enough
			// in dialogue (building a list of things the player has seen) to be worth supporting.
			if (assignment.Op == SetOperation.Add && current is { Kind: DialogueValueKind.String })
			{
				Set(name, DialogueValue.String((current.StringValue ?? string.Empty) + operand));
				return;
			}

			if (!operand.IsNumeric)
				return;

			var start = current is { IsNumeric: true } ? current.AsNumber() : 0f;
			var delta = operand.AsNumber();
			var result = assignment.Op == SetOperation.Add ? start + delta : start - delta;

			// Int stays Int unless something in the expression is a Float, so a counter does not silently
			// turn into 3.0000001 and stop comparing equal to 3.
			var staysInt = (current == null || current.Kind == DialogueValueKind.Int) &&
			               operand.Kind == DialogueValueKind.Int;

			Set(name, staysInt ? DialogueValue.Int((int)result) : DialogueValue.Float(result));
		}

		/// <summary>A bool is itself, a number is non-zero, a string is non-empty.</summary>
		private static bool Truthiness(DialogueValue value, bool fallback)
		{
			if (value == null)
				return fallback;

			return value.Kind switch
			{
				DialogueValueKind.Bool => value.BoolValue,
				DialogueValueKind.Int => value.IntValue != 0,
				DialogueValueKind.Float => Math.Abs(value.FloatValue) > 0.0001f,
				DialogueValueKind.String => !string.IsNullOrEmpty(value.StringValue),
				_ => fallback,
			};
		}
	}
}
