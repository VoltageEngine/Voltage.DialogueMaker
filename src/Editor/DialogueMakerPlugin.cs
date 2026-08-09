using System;
using Voltage.Editor.Assets;
using Voltage.Editor.Plugins;

namespace Voltage.Dialogue.Editor
{
	/// <summary>Entry point: registers the graph window and teaches the asset browser about .vdialogue.</summary>
	public sealed class DialogueMakerPlugin : IEditorPlugin
	{
		private DialogueGraphWindow _window;

		public void Initialize(IEditorPluginContext context)
		{
			_window = new DialogueGraphWindow();
			context.RegisterWindow(_window);
			context.AddMenuItem("DialogueMaker/Dialogue Graph", () => _window.IsOpen = true);

			// Without this the browser shows .vdialogue as an unsupported file. No drop factory: a graph
			// has no scene presence, so dragging one into the viewport is correctly rejected.
			AssetTypeRegistry.Register(new AssetTypeDescriptor(
				new[] { DialogueGraphIO.FileExtension },
				IconPath: null,
				Kind: AssetKind.Data));

			context.ProjectClosing += OnProjectClosing;
		}

		public void Shutdown()
		{
			_window?.Close();
			_window = null;
		}

		private void OnProjectClosing() => _window?.Close();
	}
}
