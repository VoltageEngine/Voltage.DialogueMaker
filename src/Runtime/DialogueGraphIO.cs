using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Voltage.Assets;
using Voltage.Persistence;

namespace Voltage.Dialogue
{
	/// <summary>
	/// Load/save for <c>.vdialogue</c> assets.
	/// </summary>
	public static class DialogueGraphIO
	{
		public const string FileExtension = ".vdialogue";

		private const string NodesKey = "Nodes";
		private const string TypeHintKey = "@type";

		/// <summary>
		/// Needs <see cref="TypeNameHandling.Auto"/> so the polymorphic <see cref="DialogueGraph.Nodes"/> list round-trips its concrete node types — but the hint written is a <b>stable id</b> from <see cref="DialogueNodeRegistry"/>, not a CLR name, so renaming or moving a node class does not break existing graphs.
		/// </summary>
		public static readonly JsonAssetFile<DialogueGraph> Format = new(
			FileExtension,
			"Dialogue Graph",
			createDefault: _ =>
			{
				var graph = new DialogueGraph();

				var line = new LineNode { SpeakerId = "Narrator", TextKey = "line.1", EditorX = 80f, EditorY = 80f };
				var end = new EndNode { EditorX = 360f, EditorY = 80f };

				graph.AddNode(line);
				graph.AddNode(end);
				line.NextId = end.Id;

				return graph;
			},
			settings: BuildSettings(),
			afterLoad: graph => graph.InvalidateIndex());

		private static JsonSettings BuildSettings() => new()
		{
			PrettyPrint = true,
			TypeNameHandling = TypeNameHandling.Auto,
			PreserveReferencesHandling = false,
			TypeNameWriter = DialogueNodeRegistry.RequireId,
			TypeNameReader = DialogueNodeRegistry.ResolveForRead,
		};

		public static DialogueGraph CreateDefault() => Format.CreateDefault();

		public static DialogueGraph CreateAndSave(string path)
		{
			var graph = CreateDefault();
			Save(graph, path);
			return graph;
		}

		/// <summary>
		/// Serializes the graph, restoring any node whose type was not registered when it was loaded to the
		/// exact JSON it arrived with.
		/// </summary>
		public static string ToJson(DialogueGraph graph)
		{
			var json = Format.ToJson(graph);
			return graph == null ? json : RestoreUnknownNodes(json, graph);
		}

		/// <summary>
		/// Deserializes, mapping any unregistered node id to an <see cref="UnknownNode"/> that carries the
		/// original JSON so a later save can put it back untouched.
		/// </summary>
		public static DialogueGraph FromJson(string json)
		{
			var graph = Format.FromJson(json);
			if (graph != null)
				CaptureUnknownNodes(json, graph);

			return graph;
		}

		public static void Save(DialogueGraph graph, string path) => File.WriteAllText(path, ToJson(graph));

		/// <summary>Null when the file does not exist.</summary>
		public static DialogueGraph Load(string path) => File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;

		/// <summary>Both walks visit the same array in the same order, so matching by index is exact.</summary>
		private static void CaptureUnknownNodes(string json, DialogueGraph graph)
		{
			if (!HasUnknownNode(graph))
				return;

			var spans = JsonSpans.ArrayElements(json, NodesKey);
			var count = graph.Nodes.Count < spans.Count ? graph.Nodes.Count : spans.Count;

			for (var i = 0; i < count; i++)
			{
				if (graph.Nodes[i] is not UnknownNode unknown)
					continue;

				var text = json.Substring(spans[i].Start, spans[i].Length);
				unknown.RawJson = text;
				unknown.UnknownTypeId = JsonSpans.ReadStringProperty(text, TypeHintKey);
			}
		}

		/// <summary>Restores each unknown node's original text, leaving every other node as written.</summary>
		private static string RestoreUnknownNodes(string json, DialogueGraph graph)
		{
			if (!HasUnknownNode(graph))
				return json;   // the overwhelmingly common path pays nothing

			var spans = JsonSpans.ArrayElements(json, NodesKey);
			var count = graph.Nodes.Count < spans.Count ? graph.Nodes.Count : spans.Count;

			var replacements = new List<(int Start, int Length, string Text)>();
			for (var i = 0; i < count; i++)
			{
				if (graph.Nodes[i] is UnknownNode { RawJson: not null } unknown)
					replacements.Add((spans[i].Start, spans[i].Length, unknown.RawJson));
			}

			return JsonSpans.ReplaceSpans(json, replacements);
		}

		private static bool HasUnknownNode(DialogueGraph graph)
		{
			foreach (var node in graph.Nodes)
			{
				if (node is UnknownNode)
					return true;
			}

			return false;
		}

	}

	/// <summary>
	/// A <see cref="ModuleInitializerAttribute"/> rather than a lazy static: the serializer and asset
	/// browser read these registries, and both fail <i>silently</i> against an empty one.
	/// </summary>
	internal static class DialogueFormats
	{
		[ModuleInitializer]
		internal static void Register()
		{
			DialogueNodeRegistry.Register(typeof(LineNode));
			DialogueNodeRegistry.Register(typeof(ChoiceNode));
			DialogueNodeRegistry.Register(typeof(ConditionNode));
			DialogueNodeRegistry.Register(typeof(SetVariableNode));
			DialogueNodeRegistry.Register(typeof(JumpNode));
			DialogueNodeRegistry.Register(typeof(EndNode));

			// Registered so the writer has an id for it. A node normally gets its original id spliced back
			// in, so this is only ever written if the payload could not be recovered.
			DialogueNodeRegistry.Register(typeof(UnknownNode));

			AssetFileRegistry.Register(DialogueGraphIO.Format);

			// The engine registers its own tracks; a plugin's has to add itself, or a .vtimeline using it
			// fails to load with an unknown-track id.
			Cinematics.TimelineTrackRegistry.Register(typeof(TimelineDialogueTrack));
		}
	}
}
