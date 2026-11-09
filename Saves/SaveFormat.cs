using System.Globalization;
using System.Numerics;

namespace RiisSaveEditor.Saves
{
	public enum NodeKind { Object, Array, String, Integer, Float, Bool, Null, Binary, Reference }

	public sealed record IntRange(BigInteger Min, BigInteger Max);

	public class SaveException(string message) : Exception(message);

// Thrown when a file is encrypted and the password is missing or wrong, so the UI can ask for it.
	public sealed class PasswordRequiredException(string message) : SaveException(message);

	public sealed class SaveNode(string name, NodeKind kind, string? value = null)
	{
		private static readonly SaveNode[] None = [];

		private List<SaveNode>? _children;
		private string? _value = value;

		public string Name { get; set; } = name;
		public NodeKind Kind { get; } = kind;

		// A node whose Meta is another node is a live alias of it (a pickle memo reference).
		public string? Value
		{
			get => Meta is SaveNode alias ? alias.Value : _value;
			set => _value = value;
		}

		public string? TypeLabel { get; set; }
		public bool ReadOnly { get; init; }
		public object? Meta { get; set; }
		public IntRange? Range { get; init; }
		public SaveNode? Parent { get; internal set; }

		public List<SaveNode> Children => _children ??= [];
		public IReadOnlyList<SaveNode> Items => _children ?? (IReadOnlyList<SaveNode>)None;
		public bool HasChildren => _children is { Count: > 0 };
		public bool IsContainer => Kind is NodeKind.Object or NodeKind.Array;
		public bool IsLeafValue => Kind is NodeKind.String or NodeKind.Integer or NodeKind.Float or NodeKind.Bool;
		public bool IsEditable => !ReadOnly && IsLeafValue;

		public bool IsValid(string text)
		{
			switch (Kind)
			{
				case NodeKind.Integer:
					return BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger v)
					       && (Range is null || (v >= Range.Min && v <= Range.Max));
				case NodeKind.Float:
					const NumberStyles plain = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
					return double.TryParse(text, plain, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d);
				case NodeKind.Bool:
					return text is "true" or "false";
				default:
					return true;
			}
		}
	}

	public sealed record SaveDocument(ISaveFormat Format, string FileName, SaveNode Root, object? State)
	{
		// Where Ren'Py saves signed for this document keep their key; null means a throwaway key.
		public string? SigningKeyPath { get; set; }
	}

	public interface ISaveFormat
	{
		string Name { get; }
		bool CanLoad(string fileName, ReadOnlySpan<byte> data);
		SaveDocument Load(string fileName, byte[] data, string? password = null);
		byte[] Save(SaveDocument document);
	}

	public sealed class SaveFormatRegistry(IEnumerable<ISaveFormat> formats)
	{
		private const int MaxTreeDepth = 256;

		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			foreach (ISaveFormat format in formats)
			{
				if (!format.CanLoad(fileName, data)) continue;

				SaveDocument document;
				try
				{
					document = format.Load(fileName, data, password);
				}
				catch (Exception e) when (e is not SaveException and not OutOfMemoryException)
				{
					// A crafted file can trip index, cast or overflow errors deep inside a parser. They all mean
					// the same thing to the user, and none should reach the circuit as an unhandled exception.
					throw new SaveException($"The file is damaged or not a valid {format.Name} save ({e.GetType().Name}).");
				}

				// The UI walks the tree recursively in places (search) and per node (depth filter), so a very deep
				// tree is refused outright. Real saves are about 30 levels deep.
				if (SaveTree.MaxDepth(document.Root) > MaxTreeDepth)
				{
					throw new SaveException($"The save is nested more than {MaxTreeDepth} levels deep, so it was rejected.");
				}

				SaveTree.LinkParents(document.Root);
				return document;
			}

			throw new SaveException("Unrecognized save format. Supported: " + string.Join(", ", formats.Select(f => f.Name)));
		}
	}
}
