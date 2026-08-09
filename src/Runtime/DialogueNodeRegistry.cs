using System;
using System.Collections.Generic;

namespace Voltage.Dialogue
{
	/// <summary>
	/// Stable, rename-proof identity for a <see cref="DialogueNode"/> type.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
	public sealed class NodeTypeIdAttribute : Attribute
	{
		public string Id { get; }

		public NodeTypeIdAttribute(string id) => Id = id;
	}

	/// <summary>
	/// Maps dialogue node types to their stable ids, in both directions.
	/// </summary>
	public static class DialogueNodeRegistry
	{
		private static readonly object _lock = new();
		private static readonly Dictionary<string, Type> _byId = new(StringComparer.Ordinal);
		private static readonly Dictionary<Type, string> _byType = new();

		/// <summary>Registers <paramref name="nodeType"/> under its <see cref="NodeTypeIdAttribute"/>.</summary>
		/// <exception cref="InvalidOperationException">The type has no <see cref="NodeTypeIdAttribute"/>.</exception>
		public static void Register(Type nodeType)
		{
			var id = (Attribute.GetCustomAttribute(nodeType, typeof(NodeTypeIdAttribute)) as NodeTypeIdAttribute)?.Id;
			if (string.IsNullOrEmpty(id))
			{
				throw new InvalidOperationException(
					$"Dialogue node '{nodeType?.FullName}' has no [NodeTypeId]. Give it a short, permanent id — " +
					"it is what .vdialogue files store, and it is what keeps them working when the class is renamed.");
			}

			Register(id, nodeType);
		}

		public static void Register(string id, Type nodeType)
		{
			if (string.IsNullOrEmpty(id) || nodeType == null)
				return;

			lock (_lock)
			{
				_byId[id] = nodeType;
				_byType[nodeType] = id;
			}
		}

		/// <summary>The stored id for a node type, or null when it is not registered.</summary>
		public static string IdFor(Type nodeType)
		{
			if (nodeType == null)
				return null;

			lock (_lock)
				return _byType.TryGetValue(nodeType, out var id) ? id : null;
		}

		/// <summary>The node type for a stored id, or null when it is not registered.</summary>
		public static Type TypeFor(string id)
		{
			if (string.IsNullOrEmpty(id))
				return null;

			lock (_lock)
				return _byId.TryGetValue(id, out var type) ? type : null;
		}

		/// <summary>Writer hook.</summary>
		internal static string RequireId(Type nodeType) =>
			IdFor(nodeType) ?? throw new InvalidOperationException(
				$"Dialogue node '{nodeType?.FullName}' is not registered, so it cannot be saved. Add " +
				"[NodeTypeId(\"…\")] and register it via DialogueNodeRegistry.Register from a [ModuleInitializer].");

		/// <summary>Reader hook.</summary>
		internal static Type RequireType(string id) =>
			TypeFor(id) ?? throw new InvalidOperationException(
				$"Unknown dialogue node id '{id}'. The node type may have been deleted, or its plugin is not " +
				$"loaded. Registered ids: {string.Join(", ", RegisteredIds)}.");

		public static IReadOnlyCollection<string> RegisteredIds
		{
			get
			{
				lock (_lock)
					return new List<string>(_byId.Keys);
			}
		}
	}
}
