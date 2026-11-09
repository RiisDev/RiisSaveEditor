using System.Globalization;
using System.Numerics;
using System.Text;

namespace RiisSaveEditor.Saves
{
	// Unreal Engine SaveGame (GVAS). Two property tag layouts exist: the UE 5.5+ one with complete type
// names, and the older one with a type string followed by type specific fields. Both are mapped onto
// the same TypeName tree so the value code is shared.
// Anything the parser cannot fully explain is kept as opaque bytes so it still round-trips.
	public sealed class GvasFormat : ISaveFormat
	{
		private sealed record TypeName(string Name, List<TypeName> Params)
		{
			public override string ToString() => Params.Count == 0 ? Name : $"{Name}<{string.Join(",", Params)}>";
		}

		private sealed record PropertyMeta(TypeName Type, byte Flags, int ArrayIndex, byte[]? Guid, bool Opaque, object? Inner, byte[]? StructGuid = null);

		private sealed record TextMeta(sbyte History, bool HasInvariant);

		private sealed record NativeMeta(bool Wide);

		private sealed record LegacyStructArray(string StructName, byte[] Guid);

		private sealed record State(byte[] Header, byte[] Trailer, bool Legacy, bool Wide);

		private enum Scalar { Real, Float32, Byte, Int32, Int64 }

		private sealed record Layout(string[] Parts, Scalar Kind);

		private static readonly Dictionary<string, Layout> Natives = new()
		{
			["Vector"] = new Layout(["X", "Y", "Z"], Scalar.Real),
			["Rotator"] = new Layout(["Pitch", "Yaw", "Roll"], Scalar.Real),
			["Quat"] = new Layout(["X", "Y", "Z", "W"], Scalar.Real),
			["Vector4"] = new Layout(["X", "Y", "Z", "W"], Scalar.Real),
			["Vector2D"] = new Layout(["X", "Y"], Scalar.Real),
			["LinearColor"] = new Layout(["R", "G", "B", "A"], Scalar.Float32),
			["Color"] = new Layout(["B", "G", "R", "A"], Scalar.Byte),
			["IntPoint"] = new Layout(["X", "Y"], Scalar.Int32),
			["IntVector"] = new Layout(["X", "Y", "Z"], Scalar.Int32),
			["DateTime"] = new Layout(["Ticks"], Scalar.Int64),
			["Timespan"] = new Layout(["Ticks"], Scalar.Int64),
		};

		private const byte HasArrayIndex = 0x01;
		private const byte HasPropertyGuid = 0x02;
		private const byte HasExtensions = 0x04;
		private const byte BoolTrue = 0x10;
		private const int TypeNameVersion = 1012;
		private const int LargeCoordinatesVersion = 1004;
		private const int MaxDepth = 128;

		public string Name => "Unreal Engine (GVAS)";

		public bool CanLoad(string fileName, ReadOnlySpan<byte> data) =>
			data.Length > 8 && data[0] == 'G' && data[1] == 'V' && data[2] == 'A' && data[3] == 'S';

		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			try
			{
				using BinaryReader r = new(new MemoryStream(data));
				r.ReadInt32();
				int saveVersion = r.ReadInt32();
				if (saveVersion is not (2 or 3))
				{
					throw new SaveException($"GVAS save version {saveVersion} is not supported");
				}

				r.ReadInt32();
				int ue5 = saveVersion >= 3 ? r.ReadInt32() : 0;
				r.ReadBytes(10);
				r.ReadFString();
				r.ReadInt32();
				int customVersions = r.ReadInt32();
				r.BaseStream.Seek(customVersions * 20L, SeekOrigin.Current);
				string saveClass = r.ReadFString();
				long afterClass = r.BaseStream.Position;

				bool legacy = ue5 < TypeNameVersion;
				bool wide = ue5 >= LargeCoordinatesVersion;

				// UE 5.5+ files carry one extra byte before the first property. Older engines are
				// assumed to have none, with the other layout tried as a fallback.
				SaveException? failure = null;
				foreach (int gap in legacy ? (int[])[0, 1] : [1, 0])
				{
					try
					{
						r.BaseStream.Position = afterClass + gap;
						int headerEnd = (int)r.BaseStream.Position;
						Codec codec = new(legacy, wide);
						SaveNode root = new(fileName, NodeKind.Object) { TypeLabel = saveClass };
						while (codec.ReadProperty(r) is { } property)
						{
							root.Children.Add(property);
						}

						int end = (int)r.BaseStream.Position;
						return new SaveDocument(this, fileName, root, new State(data[..headerEnd], data[end..], legacy, wide));
					}
					catch (Exception e) when (e is SaveException or EndOfStreamException or ArgumentException or OverflowException)
					{
						failure ??= new SaveException(e is SaveException ? e.Message : "Unexpected end of GVAS file");
					}
				}

				throw failure!;
			}
			catch (EndOfStreamException)
			{
				throw new SaveException("Unexpected end of GVAS file");
			}
		}

		public byte[] Save(SaveDocument document)
		{
			State state = (State)document.State!;
			return Write(document.Root, state.Header, state.Trailer, new Codec(state.Legacy, state.Wide));
		}

		// Test hook: re-encodes a document with the pre-5.5 tag layout so that path can be exercised
		// without real legacy saves.
		internal static byte[] SaveAsLegacyForTest(SaveDocument document)
		{
			State state = (State)document.State!;
			byte[] header = state.Legacy ? [.. state.Header] : state.Header[..^1];
			BitConverter.GetBytes(LargeCoordinatesVersion).CopyTo(header, 12);
			return Write(document.Root, header, state.Trailer, new Codec(legacy: true, wide: true));
		}

		private static byte[] Write(SaveNode root, byte[] header, byte[] trailer, Codec codec)
		{
			using MemoryStream ms = new();
			using BinaryWriter w = new(ms);
			w.Write(header);
			foreach (SaveNode property in root.Children)
			{
				codec.WriteProperty(w, property);
			}

			w.WriteFString("None");
			w.Write(trailer);
			w.Flush();
			return ms.ToArray();
		}

		private sealed class Codec(bool legacy, bool wide)
		{
			// Nested structs and arrays recurse, so depth is capped: otherwise a small crafted file overflows the
			// stack, which ends the whole server process.
			private int _depth;

			public SaveNode? ReadProperty(BinaryReader r) => Guarded(() => ReadPropertyCore(r));

			private T Guarded<T>(Func<T> read)
			{
				if (++_depth > MaxDepth)
				{
					throw new SaveException("The save is nested too deeply");
				}

				try
				{
					return read();
				}
				finally
				{
					_depth--;
				}
			}

			private SaveNode? ReadPropertyCore(BinaryReader r)
			{
				string name = r.ReadFString();
				if (name == "None")
				{
					return null;
				}

				TypeName type;
				int size;
				byte flags = 0;
				int arrayIndex = 0;
				byte[]? guid = null;
				byte[]? structGuid = null;

				if (legacy)
				{
					string typeName = r.ReadFString();
					size = r.ReadInt32();
					arrayIndex = r.ReadInt32();
					switch (typeName)
					{
						case "StructProperty":
							string structName = r.ReadFString();
							structGuid = r.ReadBytes(16);
							type = new TypeName(typeName, [new TypeName(structName, [])]);
							break;
						case "BoolProperty":
							flags = r.ReadByte() != 0 ? BoolTrue : (byte)0;
							type = new TypeName(typeName, []);
							break;
						case "ByteProperty" or "EnumProperty":
							string enumName = r.ReadFString();
							type = new TypeName(typeName, enumName == "None" ? [] : [new TypeName(enumName, [])]);
							break;
						case "ArrayProperty" or "SetProperty":
							type = new TypeName(typeName, [new TypeName(r.ReadFString(), [])]);
							break;
						case "MapProperty":
							TypeName key = new(r.ReadFString(), []);
							type = new TypeName(typeName, [key, new TypeName(r.ReadFString(), [])]);
							break;
						default:
							type = new TypeName(typeName, []);
							break;
					}

					if (r.ReadByte() != 0)
					{
						guid = r.ReadBytes(16);
						flags |= HasPropertyGuid;
					}
				}
				else
				{
					type = ReadType(r);
					size = r.ReadInt32();
					flags = r.ReadByte();
					arrayIndex = (flags & HasArrayIndex) != 0 ? r.ReadInt32() : 0;
					guid = (flags & HasPropertyGuid) != 0 ? r.ReadBytes(16) : null;
					if ((flags & HasExtensions) != 0)
					{
						throw new SaveException($"Property '{name}' uses tag extensions, which are not supported");
					}
				}

				long start = r.BaseStream.Position;
				long end = start + size;
				if (size < 0 || end > r.BaseStream.Length)
				{
					throw new SaveException($"Property '{name}' has an invalid size");
				}

				SaveNode node;
				bool opaque = false;
				try
				{
					node = ReadValue(r, name, type, size, flags, inArray: false);
					if (r.BaseStream.Position != end)
					{
						throw new SaveException("size mismatch");
					}
				}
				catch (Exception e) when (e is SaveException or EndOfStreamException or ArgumentException or OverflowException or FormatException)
				{
					r.BaseStream.Position = start;
					node = new SaveNode(name, NodeKind.Binary, Convert.ToHexString(r.ReadBytes(CheckCount(r, size)))) { ReadOnly = true };
					opaque = true;
				}

				node.TypeLabel = type.ToString();
				node.Meta = new PropertyMeta(type, flags, arrayIndex, guid, opaque, node.Meta, structGuid);
				return node;
			}

			private SaveNode ReadValue(BinaryReader r, string name, TypeName type, int size, byte flags, bool inArray) =>
				Guarded(() => ReadValueCore(r, name, type, size, flags, inArray));

			private SaveNode ReadValueCore(BinaryReader r, string name, TypeName type, int size, byte flags, bool inArray)
			{
				switch (type.Name)
				{
					case "BoolProperty":
						return Bool(name, inArray ? r.ReadByte() != 0 : (flags & BoolTrue) != 0);
					case "Int8Property": return Int(name, r.ReadSByte(), sbyte.MinValue, sbyte.MaxValue);
					case "Int16Property": return Int(name, r.ReadInt16(), short.MinValue, short.MaxValue);
					case "IntProperty": return Int(name, r.ReadInt32(), int.MinValue, int.MaxValue);
					case "Int64Property": return Int(name, r.ReadInt64(), long.MinValue, long.MaxValue);
					case "UInt16Property": return Int(name, r.ReadUInt16(), 0, ushort.MaxValue);
					case "UInt32Property": return Int(name, r.ReadUInt32(), 0, uint.MaxValue);
					case "UInt64Property": return Int(name, r.ReadUInt64(), 0, ulong.MaxValue);
					case "ByteProperty":
						bool named = inArray ? type.Params.Count > 0 && type.Params[0].Name != "None" : size != 1;
						return named ? Str(name, r.ReadFString()) : Int(name, r.ReadByte(), 0, byte.MaxValue);
					case "FloatProperty":
						return new SaveNode(name, NodeKind.Float, r.ReadSingle().ToString("R", CultureInfo.InvariantCulture));
					case "DoubleProperty":
						return new SaveNode(name, NodeKind.Float, r.ReadDouble().ToString("R", CultureInfo.InvariantCulture));
					case "StrProperty":
					case "NameProperty":
					case "ObjectProperty":
					case "EnumProperty":
						return Str(name, r.ReadFString());
					case "TextProperty":
						return ReadText(r, name);
					case "StructProperty":
						return ReadStruct(r, name, type, size, flags, inArray);
					case "ArrayProperty":
					case "SetProperty":
						return ReadList(r, name, type, size, isSet: type.Name == "SetProperty");
					case "MapProperty":
						return ReadMap(r, name, type);
					default:
						throw new SaveException($"Unsupported property type {type.Name}");
				}
			}

			private static SaveNode ReadText(BinaryReader r, string name)
			{
				int textFlags = r.ReadInt32();
				sbyte history = r.ReadSByte();
				SaveNode node = new(name, NodeKind.Object);
				node.Children.Add(new SaveNode("Flags", NodeKind.Integer, textFlags.ToString(CultureInfo.InvariantCulture)) { ReadOnly = true });
				bool invariant = true;
				switch (history)
				{
					case -1:
						invariant = r.ReadInt32() != 0;
						if (invariant)
						{
							node.Children.Add(Str("Text", r.ReadFString()));
						}

						break;
					case 0:
						node.Children.Add(Str("Namespace", r.ReadFString()));
						node.Children.Add(Str("Key", r.ReadFString()));
						node.Children.Add(Str("Source", r.ReadFString()));
						break;
					default:
						throw new SaveException($"Text history type {history} is not supported");
				}

				node.Meta = new TextMeta(history, invariant);
				return node;
			}

			private SaveNode ReadStruct(BinaryReader r, string name, TypeName type, int size, byte flags, bool inArray)
			{
				string structName = type.Params.Count > 0 ? type.Params[0].Name : "";
				if (Natives.TryGetValue(structName, out Layout? layout))
				{
					return ReadNative(r, name, layout, size, inArray);
				}

				if (structName == "Guid")
				{
					return new SaveNode(name, NodeKind.Binary, Convert.ToHexString(r.ReadBytes(16))) { ReadOnly = true };
				}

				if (!inArray && (flags & 0x08) != 0)
				{
					throw new SaveException($"Native struct {structName} is not supported");
				}

				SaveNode node = new(name, NodeKind.Object);
				while (ReadProperty(r) is { } property)
				{
					node.Children.Add(property);
				}

				return node;
			}

			private SaveNode ReadNative(BinaryReader r, string name, Layout layout, int size, bool inArray)
			{
				bool isWide = wide;
				if (layout.Kind == Scalar.Real && !inArray)
				{
					isWide = size == layout.Parts.Length * 8;
					if (!isWide && size != layout.Parts.Length * 4)
					{
						throw new SaveException("Unexpected native struct size");
					}
				}

				SaveNode node = new(name, NodeKind.Object) { Meta = new NativeMeta(isWide) };
				foreach (string part in layout.Parts)
				{
					node.Children.Add(layout.Kind switch
					{
						Scalar.Real when isWide => new SaveNode(part, NodeKind.Float, r.ReadDouble().ToString("R", CultureInfo.InvariantCulture)),
						Scalar.Real or Scalar.Float32 => new SaveNode(part, NodeKind.Float, r.ReadSingle().ToString("R", CultureInfo.InvariantCulture)),
						Scalar.Byte => Int(part, r.ReadByte(), 0, byte.MaxValue),
						Scalar.Int32 => Int(part, r.ReadInt32(), int.MinValue, int.MaxValue),
						_ => Int(part, r.ReadInt64(), long.MinValue, long.MaxValue),
					});
				}

				return node;
			}

			private SaveNode ReadList(BinaryReader r, string name, TypeName type, int size, bool isSet)
			{
				TypeName inner = type.Params.Count > 0 ? type.Params[0] : throw new SaveException("Container without element type");
				if (isSet && r.ReadInt32() != 0)
				{
					throw new SaveException("Sets with removed entries are not supported");
				}

				int count = CheckCount(r, r.ReadInt32());
				object? meta = null;
				if (legacy && inner.Name == "StructProperty")
				{
					r.ReadFString();
					r.ReadFString();
					r.ReadInt32();
					r.ReadInt32();
					string structName = r.ReadFString();
					byte[] guid = r.ReadBytes(16);
					if (r.ReadByte() != 0)
					{
						r.ReadBytes(16);
					}

					inner = new TypeName("StructProperty", [new TypeName(structName, [])]);
					meta = new LegacyStructArray(structName, guid);
				}

				if (inner.Name == "ByteProperty")
				{
					// Legacy tags do not say whether the bytes are an enum, so the payload size decides.
					int payload = size - 4 - (isSet ? 4 : 0);
					bool raw = legacy ? payload == count : inner.Params.Count == 0;
					if (raw)
					{
						return new SaveNode(name, NodeKind.Binary, Convert.ToHexString(r.ReadBytes(count))) { ReadOnly = true };
					}

					if (legacy)
					{
						inner = new TypeName("ByteProperty", [new TypeName("Enum", [])]);
					}
				}

				SaveNode node = new(name, NodeKind.Array) { Meta = meta };
				for (int i = 0; i < count; i++)
				{
					node.Children.Add(ReadValue(r, $"[{i}]", inner, -1, 0, inArray: true));
				}

				return node;
			}

			private SaveNode ReadMap(BinaryReader r, string name, TypeName type)
			{
				if (type.Params.Count < 2)
				{
					throw new SaveException("Map without key and value types");
				}

				if (r.ReadInt32() != 0)
				{
					throw new SaveException("Maps with removed entries are not supported");
				}

				int count = CheckCount(r, r.ReadInt32());
				SaveNode node = new(name, NodeKind.Array);
				for (int i = 0; i < count; i++)
				{
					SaveNode entry = new($"[{i}]", NodeKind.Object);
					entry.Children.Add(ReadValue(r, "Key", type.Params[0], -1, 0, inArray: true));
					entry.Children.Add(ReadValue(r, "Value", type.Params[1], -1, 0, inArray: true));
					node.Children.Add(entry);
				}

				return node;
			}

			public void WriteProperty(BinaryWriter w, SaveNode node)
			{
				PropertyMeta meta = (PropertyMeta)node.Meta!;
				using MemoryStream body = new();
				using (BinaryWriter bw = new(body, Encoding.UTF8, leaveOpen: true))
				{
					if (meta.Opaque)
					{
						bw.Write(Convert.FromHexString(node.Value!));
					}
					else
					{
						WriteValue(bw, node, meta.Type, inArray: false);
					}
				}

				byte flags = meta.Flags;
				if (meta.Type.Name == "BoolProperty" && !meta.Opaque)
				{
					flags = (byte)(node.Value == "true" ? flags | BoolTrue : flags & ~BoolTrue);
				}

				w.WriteFString(node.Name);
				if (legacy)
				{
					WriteLegacyTag(w, meta, (int)body.Length, flags);
				}
				else
				{
					WriteType(w, meta.Type);
					w.Write((int)body.Length);
					w.Write(flags);
					if ((flags & HasArrayIndex) != 0)
					{
						w.Write(meta.ArrayIndex);
					}

					if ((flags & HasPropertyGuid) != 0)
					{
						w.Write(meta.Guid!);
					}
				}

				body.WriteTo(w.BaseStream);
			}

			private static void WriteLegacyTag(BinaryWriter w, PropertyMeta meta, int size, byte flags)
			{
				TypeName type = meta.Type;
				w.WriteFString(type.Name);
				w.Write(size);
				w.Write(meta.ArrayIndex);
				switch (type.Name)
				{
					case "StructProperty":
						w.WriteFString(type.Params[0].Name);
						w.Write(meta.StructGuid ?? new byte[16]);
						break;
					case "BoolProperty":
						w.Write((byte)((flags & BoolTrue) != 0 ? 1 : 0));
						break;
					case "ByteProperty" or "EnumProperty":
						w.WriteFString(type.Params.Count > 0 ? type.Params[0].Name : "None");
						break;
					case "ArrayProperty" or "SetProperty":
						w.WriteFString(type.Params[0].Name);
						break;
					case "MapProperty":
						w.WriteFString(type.Params[0].Name);
						w.WriteFString(type.Params[1].Name);
						break;
				}

				if ((flags & HasPropertyGuid) != 0)
				{
					w.Write((byte)1);
					w.Write(meta.Guid!);
				}
				else
				{
					w.Write((byte)0);
				}
			}

			private void WriteValue(BinaryWriter w, SaveNode n, TypeName type, bool inArray)
			{
				string text = n.Value ?? "";
				switch (type.Name)
				{
					case "BoolProperty":
						if (inArray)
						{
							w.Write((byte)(text == "true" ? 1 : 0));
						}

						break;
					case "Int8Property": w.Write(sbyte.Parse(text, CultureInfo.InvariantCulture)); break;
					case "Int16Property": w.Write(short.Parse(text, CultureInfo.InvariantCulture)); break;
					case "IntProperty": w.Write(int.Parse(text, CultureInfo.InvariantCulture)); break;
					case "Int64Property": w.Write(long.Parse(text, CultureInfo.InvariantCulture)); break;
					case "UInt16Property": w.Write(ushort.Parse(text, CultureInfo.InvariantCulture)); break;
					case "UInt32Property": w.Write(uint.Parse(text, CultureInfo.InvariantCulture)); break;
					case "UInt64Property": w.Write(ulong.Parse(text, CultureInfo.InvariantCulture)); break;
					case "ByteProperty":
						if (n.Kind == NodeKind.Integer)
						{
							w.Write(byte.Parse(text, CultureInfo.InvariantCulture));
						}
						else
						{
							w.WriteFString(text);
						}

						break;
					case "FloatProperty": w.Write(float.Parse(text, CultureInfo.InvariantCulture)); break;
					case "DoubleProperty": w.Write(double.Parse(text, CultureInfo.InvariantCulture)); break;
					case "StrProperty":
					case "NameProperty":
					case "ObjectProperty":
					case "EnumProperty":
						w.WriteFString(text);
						break;
					case "TextProperty": WriteText(w, n); break;
					case "StructProperty": WriteStruct(w, n, type); break;
					case "ArrayProperty":
					case "SetProperty":
						WriteList(w, n, type);
						break;
					case "MapProperty":
						w.Write(0);
						w.Write(n.Children.Count);
						foreach (SaveNode entry in n.Children)
						{
							WriteValue(w, entry.Children[0], type.Params[0], inArray: true);
							WriteValue(w, entry.Children[1], type.Params[1], inArray: true);
						}

						break;
					default:
						throw new SaveException($"Cannot write property type {type.Name}");
				}
			}

			private static void WriteText(BinaryWriter w, SaveNode n)
			{
				TextMeta meta = (TextMeta)ValueMeta(n)!;
				w.Write(int.Parse(n.Children[0].Value!, CultureInfo.InvariantCulture));
				w.Write(meta.History);
				if (meta.History == -1)
				{
					w.Write(meta.HasInvariant ? 1 : 0);
					if (meta.HasInvariant)
					{
						w.WriteFString(n.Children[1].Value ?? "");
					}

					return;
				}

				for (int i = 1; i <= 3; i++)
				{
					w.WriteFString(n.Children[i].Value ?? "");
				}
			}

			private void WriteStruct(BinaryWriter w, SaveNode n, TypeName type)
			{
				string structName = type.Params.Count > 0 ? type.Params[0].Name : "";
				if (Natives.TryGetValue(structName, out Layout? layout))
				{
					bool isWide = ((NativeMeta)ValueMeta(n)!).Wide;
					foreach (SaveNode part in n.Children)
					{
						string text = part.Value ?? "";
						switch (layout.Kind)
						{
							case Scalar.Real when isWide: w.Write(double.Parse(text, CultureInfo.InvariantCulture)); break;
							case Scalar.Real or Scalar.Float32: w.Write(float.Parse(text, CultureInfo.InvariantCulture)); break;
							case Scalar.Byte: w.Write(byte.Parse(text, CultureInfo.InvariantCulture)); break;
							case Scalar.Int32: w.Write(int.Parse(text, CultureInfo.InvariantCulture)); break;
							default: w.Write(long.Parse(text, CultureInfo.InvariantCulture)); break;
						}
					}

					return;
				}

				if (n.Kind == NodeKind.Binary)
				{
					w.Write(Convert.FromHexString(n.Value!));
					return;
				}

				foreach (SaveNode property in n.Children)
				{
					WriteProperty(w, property);
				}

				w.WriteFString("None");
			}

			private void WriteList(BinaryWriter w, SaveNode n, TypeName type)
			{
				if (type.Name == "SetProperty")
				{
					w.Write(0);
				}

				if (n.Kind == NodeKind.Binary)
				{
					byte[] bytes = Convert.FromHexString(n.Value!);
					w.Write(bytes.Length);
					w.Write(bytes);
					return;
				}

				w.Write(n.Children.Count);
				LegacyStructArray? array = ValueMeta(n) as LegacyStructArray;
				if (legacy && array is null && type.Params[0] is { Name: "StructProperty", Params: [{ } structType, ..] })
				{
					array = new LegacyStructArray(structType.Name, new byte[16]);
				}

				if (legacy && array is not null)
				{
					TypeName elementType = new("StructProperty", [new TypeName(array.StructName, [])]);
					using MemoryStream elements = new();
					using (BinaryWriter ew = new(elements, Encoding.UTF8, leaveOpen: true))
					{
						foreach (SaveNode item in n.Children)
						{
							WriteValue(ew, item, elementType, inArray: true);
						}
					}

					w.WriteFString(n.Name);
					w.WriteFString("StructProperty");
					w.Write((int)elements.Length);
					w.Write(0);
					w.WriteFString(array.StructName);
					w.Write(array.Guid);
					w.Write((byte)0);
					elements.WriteTo(w.BaseStream);
					return;
				}

				TypeName inner = type.Params[0];
				foreach (SaveNode item in n.Children)
				{
					WriteValue(w, item, inner, inArray: true);
				}
			}

			private static TypeName ReadType(BinaryReader r, int depth = 0)
			{
				if (depth > MaxDepth)
				{
					throw new SaveException("Property types are nested too deeply");
				}

				string name = r.ReadFString();
				int count = CheckCount(r, r.ReadInt32());
				List<TypeName> parameters = [];
				for (int i = 0; i < count; i++)
				{
					parameters.Add(ReadType(r, depth + 1));
				}

				return new TypeName(name, parameters);
			}

			private static void WriteType(BinaryWriter w, TypeName type)
			{
				w.WriteFString(type.Name);
				w.Write(type.Params.Count);
				foreach (TypeName p in type.Params)
				{
					WriteType(w, p);
				}
			}

			private static object? ValueMeta(SaveNode n) => n.Meta is PropertyMeta p ? p.Inner : n.Meta;

			private static int CheckCount(BinaryReader r, int count) =>
				count >= 0 && count <= r.BaseStream.Length - r.BaseStream.Position
					? count
					: throw new SaveException("Invalid element count");

			private static SaveNode Str(string name, string value) => new(name, NodeKind.String, value);

			private static SaveNode Bool(string name, bool value) => new(name, NodeKind.Bool, value ? "true" : "false");

			private static SaveNode Int(string name, BigInteger value, BigInteger min, BigInteger max) =>
				new(name, NodeKind.Integer, value.ToString(CultureInfo.InvariantCulture)) { Range = new IntRange(min, max) };
		}
	}

	internal static class GvasStreams
	{
		public static string ReadFString(this BinaryReader r)
		{
			int length = r.ReadInt32();
			long remaining = r.BaseStream.Length - r.BaseStream.Position;
			if (length > remaining || length < -(remaining / 2))
			{
				throw new SaveException("Invalid string length");
			}

			switch (length)
			{
				case 0:
					return "";
				case > 0:
					return Encoding.Latin1.GetString(r.ReadBytes(length), 0, length - 1);
				default:
				{
					byte[] raw = r.ReadBytes(-length * 2);
					return Encoding.Unicode.GetString(raw, 0, raw.Length - 2);
				}
			}
		}

		public static void WriteFString(this BinaryWriter w, string s)
		{
			if (s.All(c => c < 0x80))
			{
				w.Write(s.Length + 1);
				w.Write(Encoding.ASCII.GetBytes(s));
				w.Write((byte)0);
				return;
			}

			w.Write(-(s.Length + 1));
			w.Write(Encoding.Unicode.GetBytes(s));
			w.Write((short)0);
		}
	}
}
