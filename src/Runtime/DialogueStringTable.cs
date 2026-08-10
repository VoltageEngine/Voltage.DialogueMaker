using System;
using System.Collections.Generic;
using Voltage.Data;

namespace Voltage.Dialogue
{
	/// <summary>One localised line.</summary>
	public class DialogueStringEntry : ISerializableData
	{
		public string Key;
		public string Text;
	}

	/// <summary>
	/// The text for one locale. Nodes store keys, so translation and graph structure version separately -
	/// a rewritten line never touches the graph, and a restructured graph never touches translation.
	/// </summary>
	[AssetTypeId("dialogue_string_table")]
	public partial class DialogueStringTable : DataAsset
	{
		/// <summary>BCP-47-ish tag: "en", "en-GB", "el".</summary>
		public string Locale = "en";

		public List<DialogueStringEntry> Entries = new();

		private Dictionary<string, string> _index;

		/// <summary>Call after editing <see cref="Entries"/>.</summary>
		public void InvalidateIndex() => _index = null;

		/// <summary>Null when the key is absent.</summary>
		public string Find(string key)
		{
			if (string.IsNullOrEmpty(key))
				return null;

			var index = _index;
			if (index == null)
			{
				index = new Dictionary<string, string>(Entries.Count, StringComparer.Ordinal);
				foreach (var entry in Entries)
				{
					if (!string.IsNullOrEmpty(entry?.Key))
						index[entry.Key] = entry.Text;
				}

				_index = index;
			}

			return index.TryGetValue(key, out var text) ? text : null;
		}
	}

	/// <summary>
	/// Resolves keys against the active table.
	///
	/// <para>A missing key returns the key itself rather than empty text. A visible "ch1.greeting" on
	/// screen is a bug report; a blank line looks like a rendering fault and hides the cause.</para>
	/// </summary>
	public static class DialogueLocalisation
	{
		private static readonly HashSet<string> _reportedMisses = new(StringComparer.Ordinal);

		public static DialogueStringTable Table { get; private set; }

		/// <summary>Fired on the first miss for a key, so a game can log or collect them.</summary>
		public static event Action<string> KeyMissing;

		public static void SetTable(DialogueStringTable table)
		{
			Table = table;
			_reportedMisses.Clear();
		}

		public static string Localise(string key)
		{
			if (string.IsNullOrEmpty(key))
				return string.Empty;

			var text = Table?.Find(key);
			if (text != null)
				return text;

			if (_reportedMisses.Add(key))
				KeyMissing?.Invoke(key);

			return key;
		}

		/// <summary>Convenience for the common "show this line" path.</summary>
		public static string TextOf(LineNode line) => line == null ? string.Empty : Localise(line.TextKey);

		/// <summary>Convenience for the common "show these options" path.</summary>
		public static string TextOf(DialogueChoiceOption option) =>
			option == null ? string.Empty : Localise(option.TextKey);
	}
}
