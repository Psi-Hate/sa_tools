using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AuroraLib.Compression.Algorithms;
using AuroraLib.Core.IO;
using SplitTools;
using SAModel;
using IniDictionary = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>;

namespace ArchiveLib
{
	/// <summary>
	/// Skies of Arcadia MLD archives. WIP experimental implementation.
	/// </summary>
	public class MLDArchive : GenericArchive
	{
		public class MLDArchiveEntry : GenericArchiveEntry
		{	
			public MLDArchiveEntry(byte[] data, string name)
			{
				Name = name;
				Data = data;
			}

			public MLDArchiveEntry() { }
		}

		public class MLDArchiveInfo
		{
			[IniAlwaysInclude]
			public string Name { get; set; } = string.Empty;

			[IniAlwaysInclude]
			public bool BigEndian { get; set; }

			public string TextureArchive { get; set; } = string.Empty;

			[IniCollection(IniCollectionMode.SingleLine, Format = ",")]
			public List<int> TextureGBIX { get; set; } = new();

			[IniCollection(IniCollectionMode.SingleLine, Format = ",")]
			public List<int> TextureGBIX2 { get; set; } = new();

			[IniCollection(IniCollectionMode.Normal)]
			public List<MLDEntryInfo> Entries { get; set; } = new();
		}

		public class MLDEntryInfo
		{
			[IniAlwaysInclude]
			public int Index { get; set; }

			[IniAlwaysInclude]
			public int TblID { get; set; }

			[IniAlwaysInclude]
			public string Fxn { get; set; } = string.Empty;

			[IniAlwaysInclude]
			public Vertex Position { get; set; } = new();

			[IniAlwaysInclude]
			public Vertex Rotation { get; set; } = new();

			[IniAlwaysInclude]
			public Vertex Scale { get; set; } = new();

			[IniCollection(IniCollectionMode.SingleLine, Format = ",")]
			public List<int> GroundLinks { get; set; } = new();

			[IniCollection(IniCollectionMode.SingleLine, Format = ",")]
			public List<int> ParamList2 { get; set; } = new();

			[IniCollection(IniCollectionMode.SingleLine, Format = ",")]
			public List<int> FunctionParameters { get; set; } = new();

			[IniCollection(IniCollectionMode.Normal)]
			public List<string> Objects { get; set; } = new();

			[IniCollection(IniCollectionMode.Normal)]
			public List<string> Grounds { get; set; } = new();

			[IniCollection(IniCollectionMode.Normal)]
			public List<string> Motions { get; set; } = new();

			public string Texlist { get; set; } = string.Empty;
		}

		private class MLDEntryBuildState
		{
			public MLDEntryInfo Info;
			public int GroundLinksOffset;
			public int ParamList2Offset;
			public int FunctionParametersOffset;
			public int ObjectListOffset;
			public int GroundListOffset;
			public int MotionListOffset;
			public int TexlistOffset;
			public List<int> ObjectPointerOffsets = new();
			public List<int> GroundPointerOffsets = new();
			public List<int> MotionPointerOffsets = new();
		}

		private const string InfoFileName = "FileInfo.amld";
		private string sourceDirectory;
		private MLDArchiveInfo archiveInfo;

		private void ExtractEntries(nmldArchiveFile archive, string directory)
		{
			MLDArchiveInfo info = new()
			{
				Name = archive.Name,
				BigEndian = ByteConverter.BigEndian,
				TextureGBIX = archive.TextureGBIX,
				TextureGBIX2 = archive.TextureGBIX2
			};

			// Add Entries
			foreach (nmldEntry entry in archive.Entries)
			{
				// Add Objects
				foreach (nmldObject model in entry.Objects)
				{
					Entries.Add(new MLDArchiveEntry(model.File, model.Name + ".nj"));
				}

				// Add Ground/Ground Object Files
				foreach (nmldGround ground in entry.Grounds)
				{
					Entries.Add(new MLDArchiveEntry(ground.File, ground.GetFileName()));

					switch (ground.Type)
					{
						case nmldGround.GroundType.Ground:
							if (ground.ConvertedObject != null)
							{
								ModelFile mfile = new ModelFile(ModelFormat.Basic, ground.ConvertedObject, null, null);
								Entries.Add(new MLDArchiveEntry(mfile.GetBytes(Path.Combine(directory, ground.Name + ".grnd.sa2mdl")), ground.Name + ".grnd.sa2mdl"));
							}
							break;
						case nmldGround.GroundType.GroundObject:
							break;
						case nmldGround.GroundType.Unknown:
							break;
					}
				}

				// Add Motions
				foreach (nmldMotion motion in entry.Motions)
				{
					Entries.Add(new MLDArchiveEntry(motion.File, motion.GetFileName()));
				}

				// Save Texlist
				if (entry.Texlist.TexList.NumTextures > 0)
				{
					if (!Directory.Exists(directory))
						Directory.CreateDirectory(directory);

					entry.Texlist.TexList.Save(Path.Combine(directory, entry.Texlist.Name));
				}

				info.Entries.Add(entry.ToArchiveInfo());
			}

			// Add Info File
			IniDictionary ini = IniSerializer.Serialize(info);
			Entries.Add(new MLDArchiveEntry(Encoding.ASCII.GetBytes(string.Join(Environment.NewLine, IniFile.Save(ini))), InfoFileName));

			// Add Texture Archive
			if (archive.TextureFile.Entries.Count > 0)
			{
				string ext = "";
				switch (archive.TextureFile.Type)
				{
					case PuyoArchiveType.PVMFile:
						ext = ".pvm";
						break;
					case PuyoArchiveType.GVMFile:
						ext = ".gvm";
						break;
				}
				info.TextureArchive = archive.Name + ext;
				Entries.Add(new MLDArchiveEntry(archive.TextureFile.GetBytes(), info.TextureArchive));
				ini = IniSerializer.Serialize(info);
				Entries[Entries.Count - 2] = new MLDArchiveEntry(Encoding.ASCII.GetBytes(string.Join(Environment.NewLine, IniFile.Save(ini))), InfoFileName);
			}
		}

		public MLDArchive(string filepath, byte[] file)
		{
			string directory = Path.Combine(Path.GetDirectoryName(filepath), Path.GetFileNameWithoutExtension(filepath));
			string filename = Path.GetFileNameWithoutExtension(filepath);
			string aklzcheck = Encoding.ASCII.GetString(file, 0, 4);
			bool bigEndianBackup = ByteConverter.BigEndian;

			try
			{
				if (aklzcheck == "AKLZ")
					ByteConverter.BigEndian = true;
				else
					ByteConverter.BigEndian = SplitTools.HelperFunctions.CheckBigEndianInt32(file, 0xC);

				nmldArchiveFile archive;

				if (ByteConverter.BigEndian)
				{
					Console.WriteLine("Skies of Arcadia: Legends MLD File");

					if (aklzcheck == "AKLZ")
					{
						Console.WriteLine("MLD Archive is Compressed. Decompressing...");
						byte[] dfile = new byte[0];

						// Decompress File Here
						using (Stream stream = new MemoryStream(file))
						{
							using (MemoryPoolStream pool = new AKLZ().Decompress(stream))
							{
								dfile = new byte[pool.ToArray().Length];
								Array.Copy(pool.ToArray(), dfile, pool.ToArray().Length);
							}
						}

						if (dfile.Length > 0)
						{
							Console.WriteLine("File Decompressed, saving and reading decompressed archive.");
							Entries.Add(new MLDArchiveEntry(dfile, ("..\\" + filename + "_dec.mld")));
							archive = new nmldArchiveFile(dfile, filename);
						}
						else
						{
							Console.WriteLine("Decompression Failed.");
							archive = new();
						}
					}
					else
						archive = new nmldArchiveFile(file, filename);
				}
				else
				{
					Console.WriteLine("Skies of Arcadia MLD File");
					archive = new nmldArchiveFile(file, filename);
				}

				if (archive.Entries.Count > 0)
				{
					ExtractEntries(archive, directory);
				}
				else
					Console.WriteLine("Unable to read archive.");
			}
			finally
			{
				ByteConverter.BigEndian = bigEndianBackup;
			}
		}

		public MLDArchive(string directory)
		{
			sourceDirectory = directory;
			archiveInfo = IniSerializer.Deserialize<MLDArchiveInfo>(Path.Combine(directory, InfoFileName));
		}

		public static bool IsMLDFolder(string directory)
		{
			string infoFile = Path.Combine(directory, InfoFileName);

			if (!File.Exists(infoFile))
			{
				return false;
			}

			try
			{
				IniDictionary ini = IniFile.Load(infoFile);
				return ini.TryGetValue(string.Empty, out Dictionary<string, string> root)
					&& root.ContainsKey(nameof(MLDArchiveInfo.Name))
					&& root.ContainsKey(nameof(MLDArchiveInfo.BigEndian));
			}
			catch
			{
				return false;
			}
		}

		public override byte[] GetBytes()
		{
			if (archiveInfo == null || sourceDirectory == null)
			{
				throw new InvalidOperationException("MLD rebuild requires a folder created from an extracted MLD archive.");
			}

			bool bigEndianBackup = ByteConverter.BigEndian;
			ByteConverter.BigEndian = archiveInfo.BigEndian;

			try
			{
				return BuildArchive();
			}
			finally
			{
				ByteConverter.BigEndian = bigEndianBackup;
			}
		}

		public override GenericArchiveEntry NewEntry()
		{
			return new MLDArchiveEntry();
		}

		private byte[] BuildArchive()
		{
			List<byte> result = new();
			List<MLDEntryBuildState> states = new();
			Dictionary<string, int> writtenFiles = new();
			int entryCount = archiveInfo.Entries.Count;
			int entryTableOffset = 0x18;

			result.AddRange(new byte[entryTableOffset + (entryCount * 0x68)]);
			WriteBytes(result, 0x14, Encoding.ASCII.GetBytes("NMLD"));

			int functionParametersOffset = result.Count;

			foreach (MLDEntryInfo entry in archiveInfo.Entries)
			{
				MLDEntryBuildState state = new()
				{
					Info = entry,
					GroundLinksOffset = WriteIntList(result, entry.GroundLinks),
					ParamList2Offset = WriteIntList(result, entry.ParamList2),
					FunctionParametersOffset = WriteIntList(result, entry.FunctionParameters),
					ObjectListOffset = WritePointerList(result, entry.Objects, out List<int> objectPointerOffsets),
					GroundListOffset = WritePointerList(result, entry.Grounds, out List<int> groundPointerOffsets),
					MotionListOffset = WritePointerList(result, entry.Motions, out List<int> motionPointerOffsets)
				};

				state.ObjectPointerOffsets = objectPointerOffsets;
				state.GroundPointerOffsets = groundPointerOffsets;
				state.MotionPointerOffsets = motionPointerOffsets;
				state.TexlistOffset = WriteTexlist(result, entry.Texlist);
				states.Add(state);
			}

			WriteGapAlignment(result, 0x20);
			int realDataPointer = result.Count;

			foreach (MLDEntryBuildState state in states)
			{
				WriteFileList(result, state.Info.Objects, state.ObjectPointerOffsets, "object", WriteObjectFile, writtenFiles);
				WriteFileList(result, state.Info.Grounds, state.GroundPointerOffsets, "ground", WriteRawFile, writtenFiles);
				WriteFileList(result, state.Info.Motions, state.MotionPointerOffsets, "motion", WriteRawFile, writtenFiles);
			}

			int textureTablePointer = WriteTextureArchive(result, archiveInfo.TextureArchive);

			WriteInt(result, 0, entryCount);
			WriteInt(result, 4, entryTableOffset);
			WriteInt(result, 8, functionParametersOffset);
			WriteInt(result, 0x0C, realDataPointer);
			WriteInt(result, 0x10, textureTablePointer);

			for (int i = 0; i < states.Count; i++)
			{
				WriteEntry(result, entryTableOffset + (i * 0x68), states[i]);
			}

			return result.ToArray();
		}

		private delegate void WriteMLDFile(List<byte> result, byte[] data);

		private void WriteFileList(List<byte> result, List<string> files, List<int> pointerOffsets, string fileType, WriteMLDFile writer, Dictionary<string, int> writtenFiles)
		{
			for (int i = 0; i < files.Count; i++)
			{
				if (string.IsNullOrEmpty(files[i]))
				{
					continue;
				}

				string path = Path.Combine(sourceDirectory, files[i]);
				if (!File.Exists(path))
				{
					throw new FileNotFoundException("MLD rebuild source file is missing.", path);
				}

				byte[] data = File.ReadAllBytes(path);
				string fileKey = GetFileKey(fileType, data);
				if (!writtenFiles.TryGetValue(fileKey, out int fileOffset))
				{
					fileOffset = result.Count;
					writtenFiles[fileKey] = fileOffset;
					writer(result, data);
					if (fileType == "motion")
					{
						result.AddRange(new byte[0x10]);
					}

					result.Align(0x20);
				}

				WriteInt(result, pointerOffsets[i], fileOffset);
			}
		}

		private static string GetFileKey(string fileType, byte[] data)
		{
			using SHA1 sha1 = SHA1.Create();
			byte[] hash = sha1.ComputeHash(data);
			StringBuilder result = new(fileType);
			result.Append(':');

			for (int i = 0; i < hash.Length; i++)
			{
				result.Append(hash[i].ToString("x2"));
			}

			return result.ToString();
		}

		private static int WriteIntList(List<byte> result, List<int> values)
		{
			int offset = result.Count;
			result.AddRange(ByteConverter.GetBytes(values.Count));

			foreach (int value in values)
			{
				result.AddRange(ByteConverter.GetBytes(value));
			}

			return offset;
		}

		private static int WritePointerList(List<byte> result, List<string> files, out List<int> pointerOffsets)
		{
			int offset = result.Count;
			int pointerCount = Math.Max(files.Count, 1);
			pointerOffsets = new List<int>(files.Count);

			result.AddRange(ByteConverter.GetBytes(files.Count));

			for (int i = 0; i < pointerCount; i++)
			{
				if (i < files.Count)
				{
					pointerOffsets.Add(result.Count);
				}

				result.AddRange(new byte[4]);
			}

			return offset;
		}

		private int WriteTexlist(List<byte> result, string filename)
		{
			const int textureNameLength = 0x20;
			int offset = result.Count;
			if (string.IsNullOrEmpty(filename))
			{
				result.AddRange(ByteConverter.GetBytes(offset + 8));
				result.AddRange(new byte[4]);
				return offset;
			}

			string path = Path.Combine(sourceDirectory, filename);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException("MLD texlist file is missing.", path);
			}

			NJS_TEXLIST texlist = NJS_TEXLIST.Load(path);
			string[] textureNames = texlist.TextureNames ?? Array.Empty<string>();
			int textureCount = (int)(texlist.NumTextures > 0 ? texlist.NumTextures : (uint)textureNames.Length);
			int texnameArrayOffset = offset + 8;
			int stringOffset = texnameArrayOffset + (textureCount * 12);

			result.AddRange(ByteConverter.GetBytes(textureCount > 0 ? texnameArrayOffset : 0));
			result.AddRange(ByteConverter.GetBytes(textureCount));

			for (int i = 0; i < textureCount; i++)
			{
				string textureName = i < textureNames.Length ? textureNames[i] : null;
				if (string.IsNullOrEmpty(textureName))
				{
					result.AddRange(new byte[12]);
					continue;
				}

				result.AddRange(ByteConverter.GetBytes(stringOffset + (i * textureNameLength)));
				result.AddRange(new byte[8]);
			}

			for (int i = 0; i < textureCount; i++)
			{
				string textureName = i < textureNames.Length ? textureNames[i] : null;
				WriteFixedString(result, textureName, textureNameLength);
			}

			result.Align(4);
			return offset;
		}

		private int WriteTextureArchive(List<byte> result, string filename)
		{
			int offset = result.Count;
			if (string.IsNullOrEmpty(filename))
			{
				result.AddRange(new byte[4]);
				return offset;
			}

			string path = Path.Combine(sourceDirectory, filename);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException("MLD texture archive file is missing.", path);
			}

			PuyoFile textureArchive = new(File.ReadAllBytes(path));
			List<byte[]> textureData = new(textureArchive.Entries.Count);
			result.AddRange(ByteConverter.GetBytes(textureArchive.Entries.Count));

			for (int i = 0; i < textureArchive.Entries.Count; i++)
			{
				GenericArchiveEntry entry = textureArchive.Entries[i];
				byte[] data = GetMLDTextureData(entry.Data);
				if (i < archiveInfo.TextureGBIX.Count && data.Length >= 12)
				{
					byte[] gbixBytes = ByteConverter.GetBytes(archiveInfo.TextureGBIX[i]);
					Array.Copy(gbixBytes, 0, data, 8, gbixBytes.Length);
				}

				if (i < archiveInfo.TextureGBIX2.Count && data.Length >= 16)
				{
					byte[] gbixBytes = ByteConverter.GetBytes(archiveInfo.TextureGBIX2[i]);
					Array.Copy(gbixBytes, 0, data, 12, gbixBytes.Length);
				}

				textureData.Add(data);
				WriteMLDTextureName(result, Path.GetFileNameWithoutExtension(entry.Name));
				result.AddRange(ByteConverter.GetBytes(data.Length));
			}

			result.AddRange(new byte[0x10]);
			foreach (byte[] data in textureData)
			{
				result.AddRange(data);
			}

			result.Align(4);
			return offset;
		}

		private static void WriteMLDTextureName(List<byte> result, string value)
		{
			byte[] bytes = new byte[40];
			byte[] stringBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
			Array.Copy(stringBytes, bytes, Math.Min(stringBytes.Length, bytes.Length - 1));
			bytes[bytes.Length - 1] = 0x80;
			result.AddRange(bytes);
		}

		private static byte[] GetMLDTextureData(byte[] data)
		{
			byte[] result = new byte[data.Length - 0x10];
			Array.Copy(data, result, result.Length);

			int pvrtOffset = FindChunk(result, "PVRT");
			if (pvrtOffset >= 0)
			{
				int pvrtSize = ByteConverter.ToInt32(result, pvrtOffset + 4);
				byte[] pvrtSizeBytes = ByteConverter.GetBytes(pvrtSize - 0x10);
				Array.Copy(pvrtSizeBytes, 0, result, pvrtOffset + 4, pvrtSizeBytes.Length);
			}

			return result;
		}

		private static void WriteObjectFile(List<byte> result, byte[] data)
		{
			int descriptorOffset = result.Count;
			int texlistOffset = FindChunk(data, "NJTL", "GJTL");
			int modelOffset = FindChunk(data, "NJBM", "NJCM", "GJBM", "GJCM");

			result.AddRange(new byte[16]);
			result.AddRange(data);

			WriteInt(result, descriptorOffset, modelOffset >= 0 ? modelOffset + 16 : 16);
			WriteInt(result, descriptorOffset + 4, data.Length + 16);
			WriteInt(result, descriptorOffset + 8, texlistOffset >= 0 ? texlistOffset + 16 : 0);
			WriteInt(result, descriptorOffset + 12, 0);
		}

		private static void WriteRawFile(List<byte> result, byte[] data)
		{
			result.AddRange(data);
		}

		private static void WriteGapAlignment(List<byte> result, int alignment)
		{
			byte[] gap = Encoding.ASCII.GetBytes("GAP ");
			int gapOffset = 0;

			while (result.Count % alignment != 0)
			{
				result.Add(gap[gapOffset]);
				gapOffset = (gapOffset + 1) % gap.Length;
			}
		}

		private static int FindChunk(byte[] data, params string[] magics)
		{
			for (int i = 0; i <= data.Length - 4; i++)
			{
				foreach (string magic in magics)
				{
					if (Encoding.ASCII.GetString(data, i, 4) == magic)
					{
						return i;
					}
				}
			}

			return -1;
		}

		private static void WriteEntry(List<byte> result, int offset, MLDEntryBuildState state)
		{
			WriteInt(result, offset, state.Info.Index);
			WriteInt(result, offset + 4, state.Info.TblID);
			WriteInt(result, offset + 8, state.GroundLinksOffset);
			WriteInt(result, offset + 0x0C, state.ParamList2Offset);
			WriteInt(result, offset + 0x10, state.FunctionParametersOffset);
			WriteInt(result, offset + 0x14, state.ObjectListOffset);
			WriteInt(result, offset + 0x18, state.GroundListOffset);
			WriteInt(result, offset + 0x1C, state.MotionListOffset);
			WriteInt(result, offset + 0x20, state.TexlistOffset);
			WriteFixedString(result, offset + 0x24, state.Info.Fxn, 32);
			WriteBytes(result, offset + 0x44, state.Info.Position.GetBytes());
			WriteBytes(result, offset + 0x50, state.Info.Rotation.GetBytes());
			WriteBytes(result, offset + 0x5C, state.Info.Scale.GetBytes());
		}

		private static void WriteInt(List<byte> result, int offset, int value)
		{
			WriteBytes(result, offset, ByteConverter.GetBytes(value));
		}

		private static void WriteBytes(List<byte> result, int offset, byte[] data)
		{
			for (int i = 0; i < data.Length; i++)
			{
				result[offset + i] = data[i];
			}
		}

		private static void WriteFixedString(List<byte> result, string value, int length)
		{
			byte[] bytes = new byte[length];
			byte[] stringBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
			Array.Copy(stringBytes, bytes, Math.Min(stringBytes.Length, length - 1));
			result.AddRange(bytes);
		}

		private static void WriteFixedString(List<byte> result, int offset, string value, int length)
		{
			byte[] bytes = new byte[length];
			byte[] stringBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
			Array.Copy(stringBytes, bytes, Math.Min(stringBytes.Length, length - 1));
			WriteBytes(result, offset, bytes);
		}
	}

	public class nmldObject
	{
		public string Name;
		public byte[] File;

		public nmldObject(byte[] file, int offset, string name)
		{
			int ptrNJCM = ByteConverter.ToInt32(file, offset);
			int chunksize = ByteConverter.ToInt32(file, offset + 4);
			int ptrNJTL = ByteConverter.ToInt32(file, offset + 8);
			uint unknown = ByteConverter.ToUInt32(file, offset + 12);

			if (unknown != 0)
				Console.WriteLine("Unknown Pointer in Object is populated: %s", unknown.ToString());

			int start = (ptrNJTL != 0) ? ptrNJTL : ptrNJCM;

			if (start == 0)
			{
				Console.WriteLine("Objects have no data pointers.");
				return;
			}

			if (chunksize < 16)
			{
				Console.WriteLine("Object chunk size is invalid.");
				return;
			}

			File = new byte[chunksize - 16];
			Array.Copy(file, offset + 16, File, 0, File.Length);

			Name = name;
		}
	}

	public class nmldGround
	{
		public enum GroundType
		{
			Ground = 0,
			GroundObject = 1,
			Unknown
		}

		public class GRND
		{
			// For Reference, the setup for a GRND is as follows:
			// 0x00	- "GRND"
			// 0x04	- Chunk Size (Includes first 16 bytes).
			// 0x08	- Int; null[2]

			// GRND Header begins at 0x10 in a GRND Chunk. The GRND Chunk appears to be made up of a quad-tree to check for collision, followed by the actual
			// triangle/vertex data
			// 0x00	- Pointer to Triangles Chunk
			// 0x04	- Pointer to Quadtree Chunk
			// 0x08	- Float; X Pos 0,0
			// 0x0C	- Float; Z Pos 0,0
			// 0x10	- Short; X quad Number
			// 0x12	- Short; Z quad Number
			// 0x14	- Short; X quad Length
			// 0x16 - Short; Z quad Length
			// 0x18	- Short; Triangle Count
			// 0x1A	- Short; Poly Count

			/*
			 * GRND blocks contain a quad tree for quick lookup and collision detection
			 * It is made up of two chunks, a chunk for the quad tree, and a chunk for the polygons present in the GRND
			 * The each polygon in the polygon chunk is made up of a compressed list of triangle info, and a compressed list of vertices
			 * Since the polygon chunk uses compression to overlap triangle info and vertices, and the triangle indices which define all
			 * of the triangles are not listed, this algorithm uses the quad tree chunk to first identify all used triangle info (indices into the 
			 * triangle info block)
			*/

			public Vertex[] Vertices;
			public List<NJS_MESHSET> Meshes;
			public Vertex Center;
			public short XCount;
			public short ZCount;
			public short XLen;
			public short ZLen;

			public NJS_OBJECT ToObject()
			{
				NJS_OBJECT obj = new ();
					
				BasicAttach attach = new(Vertices, Array.Empty<Vertex>(), Meshes, Array.Empty<NJS_MATERIAL>());
				obj.Attach = attach;
				//obj.Attach = obj.Attach.ToChunk();

				return obj;
			}

			public GRND(byte[] file, int address)
			{
				Meshes = new List<NJS_MESHSET>();

				int addr = 16;
				int ptr_triangles = ByteConverter.ToInt32(file, addr) + addr;
				int ptr_quadtree = ByteConverter.ToInt32(file, addr + 4) + addr + 4;
				
				Center = new Vertex(ByteConverter.ToSingle(file, addr + 8), 0.0f, ByteConverter.ToSingle(file, addr + 0xc));
				XCount = ByteConverter.ToInt16(file, addr + 0x10);   //Might be unsigned?
				ZCount = ByteConverter.ToInt16(file, addr + 0x12);   //Might be unsigned?
				XLen = ByteConverter.ToInt16(file, addr + 0x14);   //Might be unsigned?
				ZLen = ByteConverter.ToInt16(file, addr + 0x16);   //Might be unsigned?
				
				short tri_count = ByteConverter.ToInt16(file, addr + 0x18);
				short quad_count = ByteConverter.ToInt16(file, addr + 0x1a);


				// This section uses the quad tree to detect the position of all used triangle info blocks in each polygon
				List<List<ushort>> unique_triangles = new List<List<ushort>>(tri_count);
				for (int i = 0; i < tri_count; i++) { unique_triangles.Add(new List<ushort>()); }

				
				for (int i = 0; i < quad_count; i++)
				{
					int cur_quad_tri_count = ByteConverter.ToInt32(file, ptr_quadtree + (i * 8));
					int cur_quad_list_offset = ByteConverter.ToInt32(file, ptr_quadtree + (i * 8) + 4) + ptr_quadtree + (i * 8) + 4;
					for (int j = 0; j < cur_quad_tri_count; j++)
					{
						ushort tri_set = ByteConverter.ToUInt16(file, cur_quad_list_offset + j * 4);
						ushort tri_ind = ByteConverter.ToUInt16(file, cur_quad_list_offset + j * 4 + 2);
						if (!unique_triangles[tri_set].Contains(tri_ind)) {
							unique_triangles[tri_set].Add(tri_ind);
						}
					}
				}

				// This section uses the unique triangles to create triangles from the detected triangle info indices for each polygon
				List <Vertex> vert_list = new List <Vertex>(0);
				for (int i = 0;i < tri_count;i++)
				{
					List<Triangle> tris = new List<Triangle>();
					List<Vertex> verts = new List<Vertex>();
					int triInfo_offset = i * 0x18 + ptr_triangles;
					int tri_offset = ByteConverter.ToInt32(file, triInfo_offset + 0x10) + triInfo_offset + 0x10;
					int vert_offset = ByteConverter.ToInt32(file, triInfo_offset + 0xc) + triInfo_offset + 0xc;
					foreach (int j in unique_triangles[i])
					{
						int v1_ind = (int)ByteConverter.ToUInt16(file, tri_offset + j * 4);
						int v2_ind = (int) ByteConverter.ToUInt16(file, tri_offset + j * 4 + 4);
						int v3_ind = (int)ByteConverter.ToUInt16(file, tri_offset + j * 4 + 8);
						bool reversed = ByteConverter.ToInt16(file, tri_offset + j * 4 + 0xa) < 0;

						if (reversed) { tris.Add(new Triangle((ushort)(vert_list.Count + 2), (ushort) (vert_list.Count + 1), (ushort) vert_list.Count)); }
						else { tris.Add(new Triangle((ushort)vert_list.Count, (ushort)(vert_list.Count + 1), (ushort)(vert_list.Count + 2))); }

						vert_list.Add(new Vertex(file, vert_offset + v1_ind * 4));
						vert_list.Add(new Vertex(file, vert_offset + v2_ind * 4));
						vert_list.Add(new Vertex(file, vert_offset + v3_ind * 4));
					}
					
					if (tris.Count > 0)
					{
						// Create meshset from the current polygon
						Meshes.Add(new NJS_MESHSET(tris.ToArray(), false, false, false));
					}
				}

				// Convert the vertex list to an array and store it
				Vertices = vert_list.ToArray();
			}
		}

		public class GOBJ
		{
			// For Reference, the setup for a GOBJ is as follows:
			// 0x00	- "GOBJ"
			// 0x04	- Chunk Size (Includes first 16 bytes),
			// 0x08	- Int; null[2]

			// GOBJ "Header" begins at 0x10 in a GOBJ Chunk.
			// 0x00	- NJS_OBJECT
			// NJS_OBJECT should have a child.
			// Said child node will have a ChunkAttach/NJS_MODEL_CNK, the child pointer is set but it also seems to always follow the first NJS_OBJECT.
			// As stated above, all pointers are relative to the location of the pointer EXCEPT for the ChunkAttach Pointer for the child.
			// It has a 1 which does not correspond to the ChunkAttach/NJS_MODEL_CNK's location.
			// Its location will be immediately after the child NJS_OBJECT.
			// It's also in a flipped order. The Center/Radius comes first, then the VertexChunk pointer, and the PolyChunk pointer at the end.

			public NJS_OBJECT Object;
			public BoundingSphere Bounds;
			public NJS_OBJECT GroundObject;

			public GOBJ(byte[] file, int address)
			{                   
				int addr = 16;
				GroundObject = get_GOBJ_node(file, addr);
			}

			private NJS_OBJECT get_GOBJ_node(byte[] file, int address)
			{
				NJS_OBJECT obj = new NJS_OBJECT();
				int data_ptr = ByteConverter.ToInt32(file, address);

				obj.Position = new Vertex(file, address + 0x8);
				obj.Rotation = new Rotation(file, address + 0x14);
				obj.Scale = new Vertex(file, address + 0x20);

				int leftptr = ByteConverter.ToInt32(file, address + 0x2c);
				if (leftptr > 0)
				{
					leftptr += 0x2c + address;
					obj.AddChild(get_GOBJ_node(file, leftptr));
				}

				int rightptr = ByteConverter.ToInt32(file, address + 0x30);
				if (rightptr > 0)
				{
					rightptr += 0x2c + address;
					obj.AddChild(get_GOBJ_node(file, rightptr));
				}

				if (data_ptr != 0)
				{
					data_ptr += address;
					ChunkAttach attach = new ChunkAttach(true, true);
					attach.Bounds = new BoundingSphere(file, data_ptr);
					data_ptr += 0x10;

					int vertptr = ByteConverter.ToInt32(file, data_ptr) + data_ptr;
					int polyptr = data_ptr + 76;

					//The geometry structure may not be in the correct format, but leaving this here for now
					VertexChunk vertexchunk = new VertexChunk(file, vertptr);
					PolyChunk polychunk = PolyChunk.Load(file, polyptr);

					attach.Vertex.Add(vertexchunk);
					attach.Poly.Add(polychunk);

					obj.Attach = attach;
				}
				return obj;
			}
		}

		public string Name;
		public GroundType Type;
		public byte[] File;
		public NJS_OBJECT ConvertedObject;

		private GRND GRNDObj;
		//private GOBJ GOBJChunk; // TODO: Implement GOBJChunk reading proper.

		public nmldGround(byte[] file, int address, string name)
		{
			// These chunks are actually condensed chunk models.
			// GOBJ has actual NJS_OBJECTs and a "flipped" ChunkAttach/NJS_MODEL_CNK.
			// GRND does not, but does seem to have possible grid set bounds in the custom header.
			// Both use pointers that are relative to the position of the pointer in the file. 
			// Switch Case includes comments on the structures.

			string magic = Encoding.ASCII.GetString(file, address, 4);

			int filesize = ByteConverter.ToInt32(file, address + 4);

			File = new byte[filesize];
			Array.Copy(file, address, File, 0, filesize);

			Name = name;

			switch (magic)
			{
				case "GRND":
					Type = GroundType.Ground;
					GRNDObj = new GRND(File, 0);
					break;
				case "GOBJ":

					Type = GroundType.GroundObject;
					//GOBJChunk = new GOBJ(File, 0);
					break;
				default:
					Console.WriteLine("Unknown Ground Format Found: %s", magic);
					Type = GroundType.Unknown;
					break;
			}

			// Currently non-function due to weird poly format.
			// Can uncomment once conversion is fixed.
			if (Type == GroundType.Ground)
			{
				ConvertedObject = GRNDObj.ToObject();
			}
			/*
			if (Type == GroundType.GroundObject)
			{
				ConvertedObject = GOBJChunk.GroundObject;
			}
			*/
		}

		public string GetFileName()
		{
			return Type switch
			{
				GroundType.Ground => Name + ".grnd",
				GroundType.GroundObject => Name + ".gobj",
				_ => Name + ".gunk"
			};
		}
	}

	public class nmldMotion
	{
		public enum MotionType
		{
			Node = 0,
			Shape = 1,
			Camera = 2,
			Unknown
		}

		public string Name;
		public MotionType Type;
		public byte[] File;

		public nmldMotion(byte[] file, int address, string name, string idx)
		{
			string magic = Encoding.ASCII.GetString(file, address, 4);
			string suffix = "";

			switch (magic)
			{
				case "NMDM":
					Type = MotionType.Node;
					suffix = "_motion";
					break;
				case "NSSM":
					Type = MotionType.Shape;
					suffix = "_shape";
					break;
				case "NCAM":
					Type = MotionType.Camera;
					suffix = "_camera";
					break;
				default:
					Console.WriteLine("Unidentified Motion Type: %s", magic);
					Type = MotionType.Unknown;
					suffix = "_unknown";
					break;
			}

			Name = name + suffix + idx;

			int njmsize = ByteConverter.ToInt32(file, address + 4) + 8;
			int pofsize = ByteConverter.ToInt32(file, address + njmsize + 4) + 8;

			File = new byte[njmsize + pofsize];
			Array.Copy(file, address, File, 0, njmsize + pofsize);
		}

		public string GetFileName()
		{
			return Type switch
			{
				MotionType.Node => Name + ".njm",
				MotionType.Shape => Name + ".njs",
				MotionType.Camera => Name + ".njc",
				_ => Name + ".num"
			};
		}
	}

	public class nmldTextureList
	{
		public string Name;
		public NJS_TEXLIST TexList;

		public nmldTextureList()
		{
			Name = string.Empty;
			TexList = new NJS_TEXLIST();
		}

		public nmldTextureList(byte[] file, int address, string name)
		{
			TexList = NJS_TEXLIST.Load(file, address, 0);

			Name = name + ".tls";
		}
	}

	public class nmldEntry
	{
		public int Index { get; set; } = 0;
		public int TblID { get; set; } = 0;
		public List<int> GroundLinks { get; set; } = new();
		public List<int> ParamList2 { get; set; } = new();
		public List<int> FunctionParameters { get; set; } = new();
		public List<nmldObject> Objects { get; set; } = new();
		public List<nmldMotion> Motions { get; set; } = new();
		public List<nmldGround> Grounds { get; set; } = new();
		public List<string> ObjectFiles { get; set; } = new();
		public List<string> MotionFiles { get; set; } = new();
		public List<string> GroundFiles { get; set; } = new();
		public nmldTextureList Texlist { get; set; } = new();
		public string Fxn { get; set; } = string.Empty;
		public Vertex Position { get; set; } = new();
		public Vertex Rotation { get; set; } = new();
		public Vertex Scale { get; set; } = new();

		private string GetNameWithIndex()
		{
			string bitID = "";
			if (Fxn == "eventhook")
			{
				bitID = FunctionParameters[FunctionParameters.Count - 1].ToString();
			}
			return Index.ToString("D3") + "_" + Fxn + bitID;
		}

		private string GetNameAndIndex(int index)
		{
			string bitID = "";
			if (Fxn == "eventhook")
			{
				bitID = FunctionParameters[FunctionParameters.Count - 1].ToString();
			}
			return Index.ToString("D3") + "_" + Fxn + bitID + "_" + index.ToString("D2");
		}

		private void GetParamList(byte[] file, int offset, List<int> target_var)
		{
			int count = ByteConverter.ToInt32(file, offset);

			for (int i = 0; i < count; i ++)
			{
				target_var.Add(ByteConverter.ToInt32(file, offset + i * 4 + 4));
			}
		}

		private void GetObjects(byte[] file, int offset)
		{
			int count = ByteConverter.ToInt32(file, offset);

			for (int i = 0; i < count; i++)
			{
				int address = ByteConverter.ToInt32(file, offset + (4 * (i + 1)));
				
				if (address != 0)
				{
					nmldObject obj = new nmldObject(file, address, GetNameAndIndex(i));
					Objects.Add(obj);
					ObjectFiles.Add(obj.Name + ".nj");
				}
				else
				{
					ObjectFiles.Add(string.Empty);
				}
			}
		}

		private void GetMotions(byte[] file, int offset)
		{
			int count = ByteConverter.ToInt32(file, offset);

			Dictionary<int, string> filenames = new();

			for (int i = 0; i < count; i++)
			{
				int address = ByteConverter.ToInt32(file, offset + (4 * (i + 1)));

				if (address != 0)
				{
					if (!filenames.TryGetValue(address, out string filename))
					{
						nmldMotion motion = new nmldMotion(file, address, GetNameWithIndex(), filenames.Count.ToString());
						Motions.Add(motion);
						filename = motion.GetFileName();
						filenames.Add(address, filename);
					}

					MotionFiles.Add(filename);
				}
				else
				{
					MotionFiles.Add(string.Empty);
				}
			}
		}

		private void GetGrounds(byte[] file, int offset)
		{
			int count = ByteConverter.ToInt32(file, offset);

			for (int i = 0; i < count; i++)
			{
				int address = ByteConverter.ToInt32(file, offset + (4 * (i + 1)));

				if (address != 0)
				{
					nmldGround ground = new nmldGround(file, address, GetNameAndIndex(i));
					Grounds.Add(ground);
					GroundFiles.Add(ground.GetFileName());
				}
				else
				{
					GroundFiles.Add(string.Empty);
				}
			}
		}

		private void GetTextures(byte[] file, int offset)
		{
			Texlist = new nmldTextureList(file, offset, GetNameWithIndex());
		}

		public string WriteEntryInfo()
		{
			StringBuilder sb = new StringBuilder();

			for (int i = 0; i < Objects.Count; i++)
			{
				sb.AppendLine(
					Index.ToString("D3") + "_" + Fxn + "_" + i.ToString("D2") + 
					", " + Position.ToString() + 
					", " + Rotation.ToString() + 
					", " + Scale.ToString());
			}

			return sb.ToString();
		}

		public MLDArchive.MLDEntryInfo ToArchiveInfo()
		{
			return new MLDArchive.MLDEntryInfo()
			{
				Index = Index,
				TblID = TblID,
				Fxn = Fxn,
				Position = Position,
				Rotation = Rotation,
				Scale = Scale,
				GroundLinks = new List<int>(GroundLinks),
				ParamList2 = new List<int>(ParamList2),
				FunctionParameters = new List<int>(FunctionParameters),
				Objects = new List<string>(ObjectFiles),
				Grounds = new List<string>(GroundFiles),
				Motions = new List<string>(MotionFiles),
				Texlist = Texlist.TexList.NumTextures > 0 ? Texlist.Name : string.Empty
			};
		}

		public nmldEntry(int offset, byte[] file)
		{
			Index = ByteConverter.ToInt32(file, offset);
			TblID = ByteConverter.ToInt32(file, offset + 4);	

			// Get Entry Name
			int namesize = 0;
			for (int s = 0; s < 32; s++)
			{
				if (file[offset + 0x24 + s] != 0)
					namesize++;
				else
					break;
			}
			byte[] namechunk = new byte[namesize];
			Array.Copy(file, offset + 0x24, namechunk, 0, namesize);
			Fxn = Encoding.ASCII.GetString(namechunk);

			int ptrGroundLinks = ByteConverter.ToInt32(file, offset + 0x8);
			GetParamList(file, ptrGroundLinks, GroundLinks);
			int ptrParamList2 = ByteConverter.ToInt32(file, offset + 0xc);
			GetParamList(file, ptrParamList2, ParamList2);
			int ptrFunctionParameters = ByteConverter.ToInt32(file, offset + 0x10);
			GetParamList(file, ptrFunctionParameters, FunctionParameters);

			Position	= new Vertex(ByteConverter.ToSingle(file, offset + 0x44), ByteConverter.ToSingle(file, offset + 0x48), ByteConverter.ToSingle(file, offset + 0x4C));
			Rotation	= new Vertex(ByteConverter.ToSingle(file, offset + 0x50), ByteConverter.ToSingle(file, offset + 0x54), ByteConverter.ToSingle(file, offset + 0x58));
			Scale		= new Vertex(ByteConverter.ToSingle(file, offset + 0x5C), ByteConverter.ToSingle(file, offset + 0x60), ByteConverter.ToSingle(file, offset + 0x64));

			// Get Entry Objects
			int ptrObjects = ByteConverter.ToInt32(file, offset + 0x14);
			if (ByteConverter.ToInt32(file, ptrObjects) != 0)
				GetObjects(file, ptrObjects);

			// Get Entry Motions
			int ptrMotions = ByteConverter.ToInt32(file, offset + 0x1C);
			if (ByteConverter.ToInt32(file, ptrMotions) != 0)
				GetMotions(file, ptrMotions);

			// Get Entry Grounds
			int ptrGrounds = ByteConverter.ToInt32(file, offset + 0x18);
			if (ByteConverter.ToInt32(file, ptrGrounds) != 0)
				GetGrounds(file, ptrGrounds);

			// Get Entry Textures
			int ptrTextures = ByteConverter.ToInt32(file, offset + 0x20);
			if (ByteConverter.ToInt32(file, ptrTextures + 4) != 0)
				GetTextures(file, ptrTextures);
		}
	}

	public class nmldArchiveFile
	{
		public string Name { get; set; } = string.Empty;
		public List<nmldEntry> Entries { get; set; } = new();
		public PuyoFile TextureFile { get; set; }
		public List<int> TextureGBIX { get; set; } = new();
		public List<int> TextureGBIX2 { get; set; } = new();

		private void GetTextureArchive(byte[] file, int offset)
		{
			Console.WriteLine("Getting Textures...");
			if (ByteConverter.BigEndian == true)
				TextureFile = new PuyoFile(PuyoArchiveType.GVMFile);
			else
				TextureFile = new PuyoFile();

			int numtex = ByteConverter.ToInt32(file, offset);
			int texnamearray = offset + 4;
			Dictionary<string, int> texnames = new();

			if (numtex > 0)
			{
				Console.WriteLine("Textures Found! Creating Archive.");
				for (int i = 0; i < numtex; i++)
				{
					int element = texnamearray + (i * 44);
					texnames.Add(file.GetCString(element, Encoding.UTF8), ByteConverter.ToInt32(file, element + 40));
				}

				// Texture Embeds have an unspecified spacing between the end of the names and the start of the texture data.
				// So we do this to get through the padding
				int texdataoffset = offset + 4 + numtex * 44;
				if (file[texdataoffset] == 0)
				{
					do
					{
						texdataoffset += 1;
					}
					while (file[texdataoffset] == 0);
				}
				int texdataptr = texdataoffset;

				bool isBig = ByteConverter.BigEndian;

				foreach (KeyValuePair<string, int> tex in texnames)
				{
					ByteConverter.BigEndian = false;
					int texdataptr2 = texdataptr;
					string magic = Encoding.ASCII.GetString(file, texdataptr2, 4);
					int size = 0;
					int gbix = 0;
					int gbix2 = 0;

					switch (magic)
					{
						case "GBIX":
						case "GCIX":
							gbix = ByteConverter.ToInt32(file, texdataptr2 + 8);
							gbix2 = ByteConverter.ToInt32(file, texdataptr2 + 12);
							size += ByteConverter.ToInt32(file, texdataptr2 + 4) + 8;
							texdataptr2 += 16;
							break;
					}

					size += ByteConverter.ToInt32(file, texdataptr2 + 4) + 8;
					byte[] texture = new byte[size];
					Array.Copy(file, texdataptr, texture, 0, size);
					TextureGBIX.Add(gbix);
					TextureGBIX2.Add(gbix2);

					switch (TextureFile.Type)
					{
						case PuyoArchiveType.PVMFile:
							TextureFile.Entries.Add(new PVMEntry(texture, tex.Key));
							break;
						case PuyoArchiveType.GVMFile:
							TextureFile.Entries.Add(new GVMEntry(texture, tex.Key));
							break;
					}

					texdataptr += tex.Value;
				}

				ByteConverter.BigEndian = isBig;
			}
		}

		private void GetEntries(byte[] file, int offset, int count)
		{
			for (int i = 0; i < count; i++)
			{
				Entries.Add(new nmldEntry(offset + (i * 104), file));
			}
		}

		public nmldArchiveFile()
		{
			Name = string.Empty;
			Entries = new List<nmldEntry>();
			TextureFile = new();
		}

		public nmldArchiveFile(byte[] file, string name)
		{
			Name = name;

			int nmldCount		= ByteConverter.ToInt32(file, 0);
			int ptr_nmldTable	= ByteConverter.ToInt32(file, 0x04);
			int ptr_fxnparams	= ByteConverter.ToInt32(file, 0x08);
			int realdatapointer = ByteConverter.ToInt32(file, 0x0C);
			int textablepointer = ByteConverter.ToInt32(file, 0x10);
			Console.WriteLine("Number of NMLD entries: {0}, NMLD data starts at {1}, real data starts at {2}", nmldCount, ptr_nmldTable.ToString("X"), realdatapointer.ToString("X"));

			// Go ahead and extract the texture archive.
			GetTextureArchive(file, textablepointer);

			// Collect Entries and their contents
			GetEntries(file, ptr_nmldTable, nmldCount);
		}
	}
}
