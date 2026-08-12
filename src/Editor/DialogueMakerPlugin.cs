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

			// The pieces are decoupled by design - a graph holds keys, a .vasset holds the text, a runner
			// plays it - and nothing in the editor shows how they connect. The manual is where that lives.
			context.RegisterWindow(DialogueManualWindow.Instance);
			context.AddMenuItem("DialogueMaker/Manual", () => DialogueManualWindow.Instance.IsOpen = true);

			// Without this the browser shows .vdialogue as an unsupported file. No drop factory: a graph
			// has no scene presence, so dragging one into the viewport is correctly rejected.
			//
			// Open matters as much as the registration: without it, double-clicking a graph falls back to the
			// behaviour for its kind and hands the file to the built-in data-asset viewer, which reads a
			// different format and reports the graph as having no "data" object.
			AssetTypeRegistry.Register(new AssetTypeDescriptor(
				new[] { DialogueGraphIO.FileExtension },
				IconPath: null,
				Kind: AssetKind.Data)
			{
				Open = path => _window.Open(path),
			});

			context.ProjectClosing += OnProjectClosing;
		}

		public void Shutdown()
		{
			_window?.Close();
			_window = null;
			DialogueManualWindow.Instance.IsOpen = false;
		}

		private void OnProjectClosing() => _window?.Close();
	}
}
