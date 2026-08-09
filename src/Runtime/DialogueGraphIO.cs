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
			settings: new JsonSettings
			{
				PrettyPrint = true,
				TypeNameHandling = TypeNameHandling.Auto,
				PreserveReferencesHandling = false,
				TypeNameWriter = DialogueNodeRegistry.RequireId,
				TypeNameReader = DialogueNodeRegistry.RequireType,
			},
			afterLoad: graph => graph.InvalidateIndex());

		public static DialogueGraph CreateDefault() => Format.CreateDefault();

		public static DialogueGraph CreateAndSave(string path) => Format.CreateAndSave(path);

		public static string ToJson(DialogueGraph graph) => Format.ToJson(graph);

		public static DialogueGraph FromJson(string json) => Format.FromJson(json);

		public static void Save(DialogueGraph graph, string path) => Format.Save(graph, path);

		public static DialogueGraph Load(string path) => Format.Load(path);
	}

	/// <summary>
	/// Registers the node types and the file format.
	///
	/// <para>A <see cref="ModuleInitializerAttribute"/> rather than a lazy static: the registries are read
	/// by the serializer and the asset browser, both of which would see an empty registry and fail
	/// <i>silently</i> if registration waited for someone to touch a type in this assembly first.</para>
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

			AssetFileRegistry.Register(DialogueGraphIO.Format);
		}
	}
}
