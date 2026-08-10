using System;
using System.Globalization;

namespace Voltage.Dialogue
{
	public enum DialogueValueKind
	{
		Bool,
		Int,
		Float,
		String,
	}

	/// <summary>
	/// A single typed value - a variable's default, an assignment operand, or the right-hand side of a
	/// comparison. Written as a class rather than a struct so an absent value stays distinguishable from
	/// a zeroed one: a null <c>Value</c> on a condition means "unset", which validation reports.
	/// </summary>
	public sealed class DialogueValue
	{
		public DialogueValueKind Kind;

		public bool BoolValue;
		public int IntValue;
		public float FloatValue;
		public string StringValue;

		public DialogueValue()
		{
		}

		public static DialogueValue Bool(bool v) => new() { Kind = DialogueValueKind.Bool, BoolValue = v };
		public static DialogueValue Int(int v) => new() { Kind = DialogueValueKind.Int, IntValue = v };
		public static DialogueValue Float(float v) => new() { Kind = DialogueValueKind.Float, FloatValue = v };
		public static DialogueValue String(string v) => new() { Kind = DialogueValueKind.String, StringValue = v };

		public DialogueValue Clone() => new()
		{
			Kind = Kind,
			BoolValue = BoolValue,
			IntValue = IntValue,
			FloatValue = FloatValue,
			StringValue = StringValue,
		};

		/// <summary>Int and Float compare as numbers; every other kind pair is incomparable.</summary>
		public bool IsNumeric => Kind is DialogueValueKind.Int or DialogueValueKind.Float;

		public float AsNumber() => Kind switch
		{
			DialogueValueKind.Int => IntValue,
			DialogueValueKind.Float => FloatValue,
			_ => 0f,
		};

		public bool ValueEquals(DialogueValue other)
		{
			if (other == null)
				return false;

			if (IsNumeric && other.IsNumeric)
				return Math.Abs(AsNumber() - other.AsNumber()) < 0.0001f;

			if (Kind != other.Kind)
				return false;

			return Kind switch
			{
				DialogueValueKind.Bool => BoolValue == other.BoolValue,
				DialogueValueKind.String => string.Equals(StringValue, other.StringValue, StringComparison.Ordinal),
				_ => false,
			};
		}

		public override string ToString() => Kind switch
		{
			DialogueValueKind.Bool => BoolValue ? "true" : "false",
			DialogueValueKind.Int => IntValue.ToString(CultureInfo.InvariantCulture),
			DialogueValueKind.Float => FloatValue.ToString("0.###", CultureInfo.InvariantCulture),
			DialogueValueKind.String => StringValue ?? string.Empty,
			_ => string.Empty,
		};
	}
}
