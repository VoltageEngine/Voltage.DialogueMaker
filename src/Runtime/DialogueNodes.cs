using System.Collections.Generic;
using Voltage.Serialization;

namespace Voltage.Dialogue
{
	/// <summary>
	/// One step in a dialogue graph. Concrete types carry a <see cref="NodeTypeIdAttribute"/>; that id, not
	/// the CLR name, is what a <c>.vdialogue</c> file stores.
	/// </summary>
	public abstract class DialogueNode
	{
		/// <summary>Unique within its graph. Wiring is by id, so a node can be reordered freely.</summary>
		public string Id;

		/// <summary>Graph-editor canvas position. Layout only — it never affects playback.</summary>
		public float EditorX;
		public float EditorY;

		/// <summary>
		/// Appends every node id this one can hand control to. A method rather than a property so the
		/// serializer never sees it as state to write.
		/// </summary>
		public abstract void CollectOutputs(List<string> into);

		/// <summary>Short label for the editor canvas and validation messages.</summary>
		public abstract string DisplayName { get; }
	}

	/// <summary>A spoken line. The text itself lives in a locale table; the node stores only its key.</summary>
	[NodeTypeId("line")]
	public sealed class LineNode : DialogueNode
	{
		public string SpeakerId;
		public string TextKey;

		/// <summary>Optional voice-over clip.</summary>
		public AssetReference Voice;

		public string NextId;

		public override void CollectOutputs(List<string> into)
		{
			if (!string.IsNullOrEmpty(NextId))
				into.Add(NextId);
		}

		public override string DisplayName => string.IsNullOrEmpty(TextKey) ? "Line" : "Line: " + TextKey;
	}

	/// <summary>One selectable option on a <see cref="ChoiceNode"/>.</summary>
	public sealed class DialogueChoiceOption
	{
		public string TextKey;

		/// <summary>Null means always offered.</summary>
		public DialogueCondition Condition;

		public string NextId;
	}

	/// <summary>Presents options and waits for a selection.</summary>
	[NodeTypeId("choice")]
	public sealed class ChoiceNode : DialogueNode
	{
		/// <summary>Optional prompt shown above the options.</summary>
		public string PromptKey;

		public List<DialogueChoiceOption> Options = new();

		public override void CollectOutputs(List<string> into)
		{
			foreach (var option in Options)
			{
				if (!string.IsNullOrEmpty(option?.NextId))
					into.Add(option.NextId);
			}
		}

		public override string DisplayName => $"Choice ({Options.Count})";
	}

	/// <summary>Branches on a variable.</summary>
	[NodeTypeId("condition")]
	public sealed class ConditionNode : DialogueNode
	{
		public DialogueCondition Condition = new();
		public string TrueNextId;
		public string FalseNextId;

		public override void CollectOutputs(List<string> into)
		{
			if (!string.IsNullOrEmpty(TrueNextId))
				into.Add(TrueNextId);
			if (!string.IsNullOrEmpty(FalseNextId))
				into.Add(FalseNextId);
		}

		public override string DisplayName => "If " + Condition;
	}

	/// <summary>Mutates a variable, then continues.</summary>
	[NodeTypeId("set_variable")]
	public sealed class SetVariableNode : DialogueNode
	{
		public DialogueAssignment Assignment = new();
		public string NextId;

		public override void CollectOutputs(List<string> into)
		{
			if (!string.IsNullOrEmpty(NextId))
				into.Add(NextId);
		}

		public override string DisplayName => "Set " + Assignment;
	}

	/// <summary>
	/// Continues at an arbitrary node. Exists so the canvas can stay readable — a long wire across the
	/// graph becomes a labelled hop instead.
	/// </summary>
	[NodeTypeId("jump")]
	public sealed class JumpNode : DialogueNode
	{
		public string TargetId;

		public override void CollectOutputs(List<string> into)
		{
			if (!string.IsNullOrEmpty(TargetId))
				into.Add(TargetId);
		}

		public override string DisplayName => "Jump";
	}

	/// <summary>
	/// Stands in for a node whose type id is not registered — almost always because the plugin that
	/// declared it is not installed.
	///
	/// <para>The node's original JSON is kept verbatim in <see cref="Raw"/> and written straight back out
	/// on save, so opening a graph in an editor that lacks the authoring plugin and then saving it cannot
	/// destroy the content. Without this, the alternatives are both bad: throw on load and risk the file
	/// being rewritten empty, or drop the node silently.</para>
	/// </summary>
	[NodeTypeId(UnknownTypeIdSentinel)]
	public sealed class UnknownNode : DialogueNode
	{
		/// <summary>
		/// Only ever written when a graph containing an unknown node is saved by something that also could
		/// not restore its payload. In the normal path the original id is spliced back in its place.
		/// </summary>
		internal const string UnknownTypeIdSentinel = "__unknown";

		/// <summary>The id this node was stored under, so it can be put back exactly as it was.</summary>
		public string UnknownTypeId;

		/// <summary>
		/// The node's original JSON text, verbatim. Internal so the serializer never writes it as a member
		/// — it is spliced back in wholesale instead.
		/// </summary>
		internal string RawJson;

		public override void CollectOutputs(List<string> into)
		{
			// Its wiring is unreadable, so it contributes no edges. Reachability analysis treats it as a
			// leaf rather than guessing.
		}

		public override string DisplayName =>
			string.IsNullOrEmpty(UnknownTypeId) ? "Unknown node" : $"Unknown node ({UnknownTypeId})";
	}

	/// <summary>Ends the conversation. The tag lets the caller distinguish how it ended.</summary>
	[NodeTypeId("end")]
	public sealed class EndNode : DialogueNode
	{
		public string EndTag;

		public override void CollectOutputs(List<string> into)
		{
		}

		public override string DisplayName => string.IsNullOrEmpty(EndTag) ? "End" : "End: " + EndTag;
	}
}
