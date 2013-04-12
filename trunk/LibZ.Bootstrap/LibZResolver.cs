﻿#region License

/*
 * Copyright (c) 2013, Milosz Krajewski
 * 
 * Microsoft Public License (Ms-PL)
 * This license governs use of the accompanying software. 
 * If you use the software, you accept this license. 
 * If you do not accept the license, do not use the software.
 * 
 * 1. Definitions
 * The terms "reproduce," "reproduction," "derivative works," and "distribution" have the same 
 * meaning here as under U.S. copyright law.
 * A "contribution" is the original software, or any additions or changes to the software.
 * A "contributor" is any person that distributes its contribution under this license.
 * "Licensed patents" are a contributor's patent claims that read directly on its contribution.
 * 
 * 2. Grant of Rights
 * (A) Copyright Grant- Subject to the terms of this license, including the license conditions 
 * and limitations in section 3, each contributor grants you a non-exclusive, worldwide, 
 * royalty-free copyright license to reproduce its contribution, prepare derivative works of 
 * its contribution, and distribute its contribution or any derivative works that you create.
 * (B) Patent Grant- Subject to the terms of this license, including the license conditions and 
 * limitations in section 3, each contributor grants you a non-exclusive, worldwide, 
 * royalty-free license under its licensed patents to make, have made, use, sell, offer for sale, 
 * import, and/or otherwise dispose of its contribution in the software or derivative works of 
 * the contribution in the software.
 * 
 * 3. Conditions and Limitations
 * (A) No Trademark License- This license does not grant you rights to use any contributors' name, 
 * logo, or trademarks.
 * (B) If you bring a patent claim against any contributor over patents that you claim are infringed 
 * by the software, your patent license from such contributor to the software ends automatically.
 * (C) If you distribute any portion of the software, you must retain all copyright, patent, trademark, 
 * and attribution notices that are present in the software.
 * (D) If you distribute any portion of the software in source code form, you may do so only under this 
 * license by including a complete copy of this license with your distribution. If you distribute 
 * any portion of the software in compiled or object code form, you may only do so under a license 
 * that complies with this license.
 * (E) The software is licensed "as-is." You bear the risk of using it. The contributors give no express
 * warranties, guarantees or conditions. You may have additional consumer rights under your local 
 * laws which this license cannot change. To the extent permitted under your local laws, the 
 * contributors exclude the implied warranties of merchantability, fitness for a particular 
 * purpose and non-infringement.
 */

#endregion

#region conditionals

#if !LIBZ_MANAGER && !LIBZ_BOOTSTRAP
	#define LIBZ_INTERNAL
#endif

#endregion

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

/*
 * NOTE: This file is a little bit messy and contains multiple classes and namespaces.
 * It is much easier to embed it directly into other library, without referencing
 * Lib.Bootstrap. It does not look nice, but makes embedding LibZResolver easy. 
 * Just drag this file into assembly and you will have access to fully functional LibZResolver.
 */

#if LIBZ_MANAGER
namespace LibZ.Manager
#else
namespace LibZ.Bootstrap
#endif
{
	using Internal;
	using System.Text.RegularExpressions;

	#region declare visibility

#if LIBZ_INTERAL

	internal partial interface IComposableCatalogProxy { };
	internal partial class LibZResolver { };

	namespace Internal
	{
		internal partial class LibZReader { };
	}

#else

	public partial interface IComposableCatalogProxy { };
	public partial class LibZResolver { };

	namespace Internal
	{
		public partial class LibZReader { };
	}

#endif

	#endregion

	#region interface IComposableCatalogProxy

	/// <summary>
	/// ComposablePartCatalog proxy. The idea behind proxying it is to avoid
	/// mandatory reference to System.ComponentModel.Composition if the result
	/// is actually not used.
	/// </summary>
	partial interface IComposableCatalogProxy
	{
		/// <summary>Gets the actual catalog.</summary>
		/// <value>The catalog.</value>
		ComposablePartCatalog Catalog { get; }
	}

	#endregion

	#region class LibZResolver

	/// <summary>Assembly resolver and repository of .libz files.</summary>
	partial class LibZResolver
	{
		#region class NullComposableCatalog

		/// <summary>
		/// Empty catalog. Used to avoid returning 'null' (which requires additional check).
		/// </summary>
		private class NullComposableCatalog: ComposablePartCatalog
		{
			public override IQueryable<ComposablePartDefinition> Parts
			{
				get { return new ComposablePartDefinition[0].AsQueryable(); }
			}
		}

		#endregion

		#region class ComposableCatalogProxy

		/// <summary>Default implementation of IComposableCatalogProxy.</summary>
		private class ComposableCatalogProxy: IComposableCatalogProxy
		{
			/// <summary>Empty catalog.</summary>
			public static readonly ComposableCatalogProxy Null =
				new ComposableCatalogProxy(() => new NullComposableCatalog());

			#region fields

			/// <summary>The catalog factory.</summary>
			private readonly Func<ComposablePartCatalog> _catalogFactory;

			/// <summary>The cached catalog.</summary>
			private ComposablePartCatalog _catalog;

			#endregion

			#region constructor

			/// <summary>Initializes a new instance of the <see cref="ComposableCatalogProxy" /> class.</summary>
			/// <param name="catalogFactory">The catalog factory.</param>
			public ComposableCatalogProxy(Func<ComposablePartCatalog> catalogFactory)
			{
				if (catalogFactory == null)
					throw new ArgumentNullException("catalogFactory");
				_catalogFactory = catalogFactory;
			}

			#endregion

			#region public interface

			/// <summary>Gets the catalog.</summary>
			/// <value>The catalog.</value>
			public ComposablePartCatalog Catalog
			{
				get { lock (this) { return _catalog ?? (_catalog = _catalogFactory()); } }
			}

			#endregion
		}

		#endregion

		#region static fields

		/// <summary>The containers</summary>
		private static readonly List<LibZReader> Containers;

		#endregion

		#region shared static properies

		/// <summary>The shared dictionary.</summary>
		private static readonly GlobalDictionary SharedData =
			new GlobalDictionary("LibZResolver.71c503c0c0824d9785f4994d5034c8a0");

		/// <summary>Gets or sets the register stream callback.</summary>
		/// <value>The register stream callback.</value>
		private static Func<Stream, ComposablePartCatalog> RegisterStream
		{
			get { return SharedData.Get<Func<Stream, ComposablePartCatalog>>(0); }
			set { SharedData.Set(0, value); }
		}

		/// <summary>Gets or sets the decoders dictionary.</summary>
		/// <value>The decoders dictionary.</value>
		private static Dictionary<uint, Func<byte[], int, byte[]>> Decoders
		{
			get { return SharedData.Get<Dictionary<uint, Func<byte[], int, byte[]>>>(1); }
			set { SharedData.Set(1, value); }
		}

		/// <summary>Gets or sets the search path.</summary>
		/// <value>The search path.</value>
		public static List<string> SearchPath
		{
			get { return SharedData.Get<List<string>>(3); }
			private set { SharedData.Set(3, value); }
		}

		#endregion

		#region static constructor

		/// <summary>Initializes the <see cref="LibZResolver"/> class.</summary>
		static LibZResolver()
		{
			// this is VERY bad, I know
			// there are potentially 2 classes, they have same name, they should
			// share same data, but they are not the SAME class, so to interlock them
			// I need something known to both of them
			lock (typeof(object))
			{
				if (!SharedData.IsOwner) return;

				Containers = new List<LibZReader>();

				// intialize paths
				var searchPath = new List<string> { AppDomain.CurrentDomain.BaseDirectory };
				var systemPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
				searchPath.AddRange(systemPath.Split(';').Where(p => !string.IsNullOrWhiteSpace(p)));

				RegisterStream = (stream) => {
					var container = new LibZReader(stream);
					var existing = Containers.FirstOrDefault(c => c.ContainerId == container.ContainerId);
					if (existing == null) Containers.Add(existing = container);
					return new LibZCatalog(existing);
				};
				Decoders = new Dictionary<uint, Func<byte[], int, byte[]>>();
				SearchPath = searchPath;

				RegisterDecoder("deflate", LibZReader.DeflateDecoder);

				// initialize assembly resolver
				AppDomain.CurrentDomain.AssemblyResolve += (s, e) => Resolve(e);
			}
		}

		#endregion

		#region public interface

		/// <summary>Registers the container.</summary>
		/// <param name="stream">The stream.</param>
		/// <param name="optional">if set to <c>true</c> container is optional,
		/// so failure to load does not cause exception.</param>
		/// <returns><see cref="IComposableCatalogProxy" /></returns>
		/// <exception cref="System.ArgumentNullException">stream</exception>
		public static IComposableCatalogProxy RegisterStreamContainer(
			Stream stream, bool optional = true)
		{
			try
			{
				if (stream == null) throw new ArgumentNullException("stream");
				var catalog = RegisterStream(stream);
				return new ComposableCatalogProxy(() => catalog);
			}
			catch
			{
				if (!optional) throw;
				return ComposableCatalogProxy.Null;
			}
		}

		/// <summary>Registers the container from file.</summary>
		/// <param name="libzFileName">Name of the libz file.</param>
		/// <param name="optional">if set to <c>true</c> container is optional,
		/// so failure to load does not cause exception.</param>
		/// <returns><see cref="IComposableCatalogProxy"/></returns>
		/// <exception cref="System.IO.FileNotFoundException"></exception>
		public static IComposableCatalogProxy RegisterFileContainer(
			string libzFileName, bool optional = true)
		{
			try
			{
				var fileName = FindFile(libzFileName);

				if (fileName == null)
					throw new FileNotFoundException(
						string.Format("LibZ library '{0}' cannot be found", libzFileName));

				// file will be locked for writing but it will be possible to 
				// have multiple processes reading it
				var stream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);

				return RegisterStreamContainer(stream);
			}
			catch
			{
				if (!optional) throw;
				return ComposableCatalogProxy.Null;
			}
		}

		/// <summary>Registers the container from resources.</summary>
		/// <param name="assemblyHook">The assembly hook.</param>
		/// <param name="libzFileName">Name of the libz file.</param>
		/// <param name="optional">if set to <c>true</c> container is optional,
		/// so failure to load does not cause exception.</param>
		/// <returns><see cref="IComposableCatalogProxy" /></returns>
		public static IComposableCatalogProxy RegisterResourceContainer(
			Type assemblyHook, string libzFileName, bool optional = true)
		{
			try
			{
				var resourceName =
					string.Format("LibZ.{0:N}",
						Hash.MD5(Path.GetFileName(libzFileName) ?? string.Empty));
				var stream = assemblyHook.Assembly.GetManifestResourceStream(resourceName);
				return RegisterStreamContainer(stream);
			}
			catch
			{
				if (!optional) throw;
				return ComposableCatalogProxy.Null;
			}
		}

		/// <summary>Registers the multiple contrainers using wildcards.</summary>
		/// <param name="libzFileNamePattern">The libz file name pattern (.\*.libz is used if not provided).</param>
		/// <returns><see cref="ComposableCatalogProxy" /></returns>
		public static IComposableCatalogProxy RegisterMultipleFileContainers(string libzFileNamePattern = null)
		{
			try
			{
				if (libzFileNamePattern == null) libzFileNamePattern = ".\\";
				var folder = Path.GetDirectoryName(libzFileNamePattern);
				if (string.IsNullOrWhiteSpace(folder)) folder = ".";
				var pattern = Path.GetFileName(libzFileNamePattern);
				if (string.IsNullOrWhiteSpace(pattern)) pattern = "*.libz";
				var proxies = Directory
					.GetFiles(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder), pattern)
					.Select(fn => RegisterFileContainer(fn))
					.Where(p => p != null)
					.ToArray();

				// NOTE: actual catalog creation is deferred
				return new ComposableCatalogProxy(() => new AggregateCatalog(proxies.Select(p => p.Catalog)));
			}
			catch
			{
				// do nothing - they are all 'optional' by default
				return ComposableCatalogProxy.Null;
			}
		}

		/// <summary>Registers all contrainers from assembly.</summary>
		/// <param name="assemblyHook">The assembly hook.</param>
		/// <returns><see cref="ComposableCatalogProxy"/></returns>
		public static IComposableCatalogProxy RegisterAllResourceContainers(
			Type assemblyHook)
		{
			var regex = new Regex(@"^LibZ\.[0-9A-Fa-f]{32}$", RegexOptions.IgnoreCase);

			try
			{
				var resourceNames = assemblyHook.Assembly
					.GetManifestResourceNames()
					.Where(name => regex.IsMatch(name));
				var proxies = resourceNames
					.Select(rn => assemblyHook.Assembly.GetManifestResourceStream(rn))
					.Where(rs => rs != null)
					.Select(rs => RegisterStreamContainer(rs))
					.ToArray();

				// NOTE: actual catalog creation is deferred
				return new ComposableCatalogProxy(
					() => new AggregateCatalog(proxies.Select(p => p.Catalog)));
			}
			catch
			{
				// do nothing - they are all 'optional' by default
				return ComposableCatalogProxy.Null;
			}
		}


		/// <summary>Registers the decoder.</summary>
		/// <param name="codecName">Name of the codec.</param>
		/// <param name="decoder">The decoder function.</param>
		/// <param name="overwrite">if set to <c>true</c> overwrites previously registered decoder. 
		/// Useful when decoder has multiple versions (for example safe and unsafe one) but at startup
		/// we have access to only one of them.</param>
		/// <exception cref="System.ArgumentException">codecName is null or empty.</exception>
		/// <exception cref="System.ArgumentNullException">decoder is null.</exception>
		public static void RegisterDecoder(string codecName, Func<byte[], int, byte[]> decoder, bool overwrite = false)
		{
			if (String.IsNullOrEmpty(codecName))
				throw new ArgumentException("codecName is null or empty.", "codecName");
			if (decoder == null)
				throw new ArgumentNullException("decoder", "decoder is null.");

			var codecId = Hash.CRC(codecName);

			if (overwrite)
			{
				lock (Decoders) Decoders[codecId] = decoder;
			}
			else
			{
				try
				{
					lock (Decoders) Decoders.Add(codecId, decoder);
				}
				catch (ArgumentException e)
				{
					throw Helpers.Error(new ArgumentException(
						string.Format("Codec '{0}' ({1}) already registered", codecName, codecId), e));
				}
			}
		}

		public static void Startup(Action action, bool rethrow = true)
		{
			Startup(() => { action(); return 0; }, rethrow);
		}

		public static int Startup(Func<int> action, bool rethrow = true)
		{
			try
			{
				return action();
			}
			catch (Exception e)
			{
				if (rethrow) throw;
			}

			return -1;
		}

		#endregion

		#region private implementation

		/// <summary>Tries to load missing assembly from LibZ containers.</summary>
		/// <param name="args">The <see cref="ResolveEventArgs"/> instance containing the event data.</param>
		/// <returns>Loaded assembly (or <c>null</c>)</returns>
		private static Assembly Resolve(ResolveEventArgs args)
		{
			try
			{
				var name = args.Name;
				return
					TryLoadAssembly3(name) ??
					MatchByShortName(name).Select(TryLoadAssembly3).FirstOrDefault(a => a != null);
			}
			catch (Exception e)
			{
				Helpers.Error(e);
			}

			return null;
		}

		/// <summary>Finds all assemblies which match given short name.</summary>
		/// <param name="shortAssemblyName">The short name.</param>
		/// <returns>Collection of full assembly names.</returns>
		private static IEnumerable<string> MatchByShortName(string shortAssemblyName)
		{
			const StringComparison ignoreCase = StringComparison.InvariantCultureIgnoreCase;

			// from all containers return assemblyNames matching given string by short name
			// please note, the container is not returned, so when looking for this name
			// it will check all the containers again, waste of time but for hard to explain reason
			// it will allow to this in "better" order
			return Containers
				.SelectMany(c => c.GetAssemblyNames()
					.Where(an => string.Compare(an.Name, shortAssemblyName, ignoreCase) == 0))
				.Distinct()
				.OrderByDescending(an => an.Version)
				.Select(an => an.FullName);
		}

		/// <summary>Tries the load assembly for 3 platforms, native, any cpu, then "opossite".</summary>
		/// <param name="assemblyName">Name of the assembly.</param>
		/// <returns>Loaded assembly or <c>null</c>.</returns>
		private static Assembly TryLoadAssembly3(string assemblyName)
		{
			return
				// try native one first
				TryLoadAssembly((IntPtr.Size == 4 ? "x86:" : "x64:") + assemblyName) ??
				// ...then AnyCPU
				TryLoadAssembly(assemblyName) ??
				// ...then try the opposite platform (as far as I understand x64 may use x86)
				(IntPtr.Size == 8 ? TryLoadAssembly("x86:" + assemblyName) : null);
		}

		/// <summary>Tries to load assembly by its resource name.</summary>
		/// <param name="resourceName">Name of the resource.</param>
		/// <returns>Loaded assembly or <c>null</c>.</returns>
		private static Assembly TryLoadAssembly(string resourceName)
		{
			var guid = Hash.MD5(resourceName ?? string.Empty);
			return Containers
				.Select(c => TryLoadAssembly(c, guid))
				.FirstOrDefault(a => a != null);
		}

		/// <summary>Tries the load assembly.</summary>
		/// <param name="container">The container.</param>
		/// <param name="guid">The GUID.</param>
		/// <returns>Loaded assembly or <c>null</c></returns>
		private static Assembly TryLoadAssembly(LibZReader container, Guid guid)
		{
			if (!container.HasEntry(guid)) return null;

			try
			{
				var data = container.GetBytes(guid, Decoders);

				// managed assemblies can be loaded straight from memory
				if (container.IsManaged(guid))
					return Assembly.Load(data);

				// unmanaged ones needs to be saved first
				var folderPath = Path.Combine(
					Path.GetTempPath(),
					container.ContainerId.ToString("N"));
				Directory.CreateDirectory(folderPath);

				var filePath = Path.Combine(folderPath, guid.ToString("N") + ".dll");

				// if file exits and length is matching do not write it
				// from security point of view this is not the best approach
				// but it saves some time
				var fileInfo = new FileInfo(filePath);
				if (!fileInfo.Exists || fileInfo.Length != data.Length)
					File.WriteAllBytes(filePath, data);

				return Assembly.LoadFile(filePath);
			}
			catch (Exception e)
			{
				Helpers.Error(e);
				return null;
			}
		}

		/// <summary>Finds the file on search path.</summary>
		/// <param name="libzFileName">Name of the libz file.</param>
		/// <returns>Full path of found LibZ file, or <c>null</c>.</returns>
		private static string FindFile(string libzFileName)
		{
			if (Path.IsPathRooted(libzFileName))
			{
				return File.Exists(libzFileName) ? libzFileName : null;
			}

			foreach (var candidate in SearchPath)
			{
				var temp = Path.GetFullPath(Path.Combine(candidate, libzFileName));
				if (File.Exists(temp)) return temp;
			}

			return null;
		}

		#endregion
	}

	#endregion

	namespace Internal
	{
		using MD5Provider = MD5;

		#region class LibZCatalog

		/// <summary>Catalog (in MEF sense) for given LibZReader.</summary>
		internal class LibZCatalog: ComposablePartCatalog
		{
			#region fields

			/// <summary>The reader.</summary>
			private readonly LibZReader _reader;

			/// <summary>Flag indicating that catalog has been initialized.</summary>
			private int _initialized;

			/// <summary>The partial type catalogs.</summary>
			private List<TypeCatalog> _catalogs;

			/// <summary>The parts.</summary>
			private List<ComposablePartDefinition> _parts;

			#endregion

			#region constructor

			/// <summary>Initializes a new instance of the <see cref="LibZCatalog"/> class.</summary>
			/// <param name="reader">The reader.</param>
			public LibZCatalog(LibZReader reader)
			{
				_reader = reader;
			}

			/// <summary>Initializes this instance. Deffers querying assemblies until it is actually needed.</summary>
			private void Initialize()
			{
				if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

				var assemblyNames = _reader.GetAssemblyNames().ToList();

				_catalogs = new List<TypeCatalog>();

				foreach (var assemblyName in assemblyNames)
				{
					try
					{
						_catalogs.Add(
							new TypeCatalog(
								Assembly.Load(assemblyName)
								.GetTypes()));
					}
					catch (Exception e)
					{
						Helpers.Error(string.Format("Could not load catalog for '{0}'", assemblyName));
						Helpers.Error(e);
					}
				}

				_parts = _catalogs.SelectMany(c => c.Parts).ToList();
			}

			#endregion

			#region overrides

			/// <summary>Gets the part definitions that are contained in the catalog.</summary>
			/// <returns>The <see cref="T:System.ComponentModel.Composition.Primitives.ComposablePartDefinition" /> 
			/// contained in the <see cref="T:System.ComponentModel.Composition.Primitives.ComposablePartCatalog" />.
			/// </returns>
			public override IQueryable<ComposablePartDefinition> Parts
			{
				get
				{
					Initialize();
					return _parts.AsQueryable();
				}
			}

			/// <summary>
			/// Gets a list of export definitions that match the constraint defined by the specified
			/// <see cref="T:System.ComponentModel.Composition.Primitives.ImportDefinition" /> object.
			/// </summary>
			/// <param name="definition">The conditions of the
			/// <see cref="T:System.ComponentModel.Composition.Primitives.ExportDefinition" />
			/// objects to be returned.</param>
			/// <returns>
			/// A collection of <see cref="T:System.Tuple`2" /> containing the
			/// <see cref="T:System.ComponentModel.Composition.Primitives.ExportDefinition" /> objects
			/// and their associated <see cref="T:System.ComponentModel.Composition.Primitives.ComposablePartDefinition" />
			/// objects for objects that match the constraint specified by <paramref name="definition" />.
			/// </returns>
			public override IEnumerable<Tuple<ComposablePartDefinition, ExportDefinition>> GetExports(ImportDefinition definition)
			{
				Initialize();
				return _catalogs.SelectMany(c => c.GetExports(definition));
			}

			#endregion
		}

		#endregion

		#region class LibZReader

		/// <summary>LibZ file container. Read-only aspect.</summary>
		partial class LibZReader: IDisposable
		{
			#region enum EntryFlags

			/// <summary>Flags on every entry in container.</summary>
			[Flags]
			protected enum EntryFlags
			{
				/// <summary>None.</summary>
				None = 0x00,

				/// <summary>Indicates unamanged assembly.</summary>
				Unmanaged = 0x01,

				/// <summary>Set when assembly is targetting AnyCPU architecture.</summary>
				AnyCPU = 0x02,

				/// <summary>Set when assembly is targetting 64-bit architectule.</summary>
				AMD64 = 0x04,
			}

			#endregion

			#region class Entry

			/// <summary>Single container entry.</summary>
			protected class Entry
			{
				/// <summary>Gets or sets the hash.</summary>
				/// <value>The hash.</value>
				public Guid Hash { get; set; }

				/// <summary>Gets or sets the name of the assembly.</summary>
				/// <value>The name of the assembly.</value>
				public AssemblyName AssemblyName { get; set; }

				/// <summary>Gets or sets the flags.</summary>
				/// <value>The flags.</value>
				public EntryFlags Flags { get; set; }

				/// <summary>Gets or sets the offset.</summary>
				/// <value>The offset.</value>
				public long Offset { get; set; }

				/// <summary>Gets or sets the length of the original stream.</summary>
				/// <value>The length of the original stream.</value>
				public int OriginalLength { get; set; }

				/// <summary>Gets or sets the length of the storage.</summary>
				/// <value>The length of the storage.</value>
				public int StorageLength { get; set; }

				/// <summary>Gets or sets the codec id.</summary>
				/// <value>The codec id.</value>
				public uint CodecId { get; set; }
			}

			#endregion

			#region consts

			/// <summary>The magic identifer on container files.</summary>
			protected static readonly Guid Magic = new Guid(Encoding.ASCII.GetBytes("LibZContainer103"));

			/// <summary>The version of container format.</summary>
			protected const int CurrentVersion = 103;

			/// <summary>The length of GUID</summary>
			protected static readonly int GuidLength = Guid.Empty.ToByteArray().Length; // that's nasty, but reliable

			#endregion

			#region static fields

			/// <summary>The registered decoders.</summary>
			private static readonly Dictionary<uint, Func<byte[], int, byte[]>> Decoders
				= new Dictionary<uint, Func<byte[], int, byte[]>>();

			#endregion

			#region fields

			/// <summary>The container id</summary>
			protected Guid _containerId = Guid.Empty;

			/// <summary>The offset where metadata starts.</summary>
			protected long _magicOffset;

			/// <summary>The actual version of container file format.</summary>
			protected int _version;

			/// <summary>The map of entries.</summary>
			protected Dictionary<Guid, Entry> _entries = new Dictionary<Guid, Entry>();

			/// <summary>The container stream.</summary>
			protected Stream _stream;

			/// <summary>The binary reader.</summary>
			protected BinaryReader _reader;

			/// <summary>Indicates if object has been already disposed.</summary>
			protected bool _disposed;

			#endregion

			#region properties

			/// <summary>Gets the container id.</summary>
			/// <value>The container id.</value>
			public Guid ContainerId { get { return _containerId; } }

			#endregion

			#region static constructor

			/// <summary>Initializes the <see cref="LibZReader"/> class.</summary>
			static LibZReader()
			{
				RegisterDecoder("deflate", DeflateDecoder);
			}

			#endregion

			#region constructor

			/// <summary>Initializes a new instance of the <see cref="LibZReader"/> class.</summary>
			protected LibZReader() { }

			/// <summary>Initializes a new instance of the <see cref="LibZReader"/> class.</summary>
			/// <param name="stream">The stream.</param>
			public LibZReader(Stream stream)
			{
				_stream = stream;
				_reader = new BinaryReader(_stream);
				OpenFile();
			}

			/// <summary>Initializes a new instance of the <see cref="LibZReader"/> class.</summary>
			/// <param name="fileName">Name of the file.</param>
			public LibZReader(string fileName)
				: this(File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)) { }

			#endregion

			#region initialization

			/// <summary>Opens the file.</summary>
			/// <exception cref="System.ArgumentException">Container file seems to be corrupted.</exception>
			/// <exception cref="System.NotSupportedException">Not supported version of container file.</exception>
			protected void OpenFile()
			{
				lock (_stream)
				{
					_stream.Position = 0;
					var guid = new Guid(_reader.ReadBytes(GuidLength));
					if (guid != Magic)
						throw new ArgumentException("Invalid LibZ file header");
					_containerId = new Guid(_reader.ReadBytes(GuidLength));
					_version = _reader.ReadInt32();
					if (_version != CurrentVersion)
						throw new NotSupportedException(string.Format("Unsupported LibZ file version ({0})", _version));
					_stream.Position = _stream.Length - GuidLength - sizeof(long);
					_magicOffset = _reader.ReadInt64();
					guid = new Guid(_reader.ReadBytes(GuidLength));
					if (guid != Magic)
						throw new ArgumentException("Invalid LibZ file footer");
					_stream.Position = _magicOffset;
					int count = _reader.ReadInt32();
					for (int i = 0; i < count; i++)
					{
						var entry = ReadEntry();
						_entries.Add(entry.Hash, entry);
					}
				}
			}

			#endregion

			#region codec management

			/// <summary>Registers the decoder.</summary>
			/// <param name="codecName">The codec name.</param>
			/// <param name="decoder">The decoder.</param>
			/// <param name="overwrite">if set to <c>true</c> overwrites previously registered decoder. 
			/// Useful when decoder has multiple versions (for example safe and unsafe one) but at startup
			/// we have access to only one of them.</param>
			/// <exception cref="System.ArgumentException">codecName is null or empty</exception>
			/// <exception cref="System.ArgumentNullException">decoder is null.</exception>
			public static void RegisterDecoder(string codecName, Func<byte[], int, byte[]> decoder, bool overwrite = false)
			{
				if (String.IsNullOrEmpty(codecName))
					throw Helpers.Error(new ArgumentException("codecName is null or empty.", "codecName"));
				if (decoder == null)
					throw Helpers.Error(new ArgumentNullException("decoder", "decoder is null."));

				var codecId = Hash.CRC(codecName);
				var decoders = Decoders;

				if (overwrite)
				{
					lock (decoders) decoders[codecId] = decoder;
				}
				else
				{
					try
					{
						lock (decoders) decoders.Add(codecId, decoder);
					}
					catch (ArgumentException e)
					{
						throw Helpers.Error(new ArgumentException(
							string.Format("Codec '{0}' ({1}) already registered", codecName, codecId), e));
					}
				}
			}

			/// <summary>Decodes the specified data.</summary>
			/// <param name="codecId">The codec id.</param>
			/// <param name="data">The data.</param>
			/// <param name="outputLength">Length of the output.</param>
			/// <param name="decoders">The decoders dictionary.</param>
			/// <returns>Decoded data.</returns>
			protected static byte[] Decode(
				uint codecId, byte[] data, int outputLength,
				IDictionary<uint, Func<byte[], int, byte[]>> decoders = null)
			{
				if (codecId == 0) return data;
				if (decoders == null) decoders = Decoders;
				Func<byte[], int, byte[]> decoder;
				lock (decoders)
				{
					if (!decoders.TryGetValue(codecId, out decoder))
						throw Helpers.Error(new ArgumentException(string.Format("Unknown codec id '{0}'", codecId)));
				}
				return decoder(data, outputLength);
			}

			#endregion

			#region read

			/// <summary>Reads the entry.</summary>
			/// <returns><see cref="Entry"/></returns>
			private Entry ReadEntry()
			{
				lock (_stream)
				{
					var entry = new Entry {
						Hash = new Guid(_reader.ReadBytes(GuidLength)),
						AssemblyName = new AssemblyName(_reader.ReadString()),
						Flags = (EntryFlags)_reader.ReadInt32(),
						Offset = _reader.ReadInt64(),
						OriginalLength = _reader.ReadInt32(),
						StorageLength = _reader.ReadInt32(),
						CodecId = _reader.ReadUInt32(),
					};
					return entry;
				}
			}

			/// <summary>Reads the data associated with given entry.</summary>
			/// <param name="entry">The entry.</param>
			/// <param name="decoders">The decoders.</param>
			/// <returns>Buffer of bytes.</returns>
			private byte[] ReadData(Entry entry, IDictionary<uint, Func<byte[], int, byte[]>> decoders)
			{
				byte[] buffer;

				lock (_stream)
				{
					_stream.Position = entry.Offset;
					buffer = ReadBytes(_stream, entry.StorageLength);
				}

				// this needs to be outside lock!
				return Decode(entry.CodecId, buffer, entry.OriginalLength, decoders);
			}

			#endregion

			#region access

			/// <summary>Gets the bytes for given resource.</summary>
			/// <param name="resourceHash">The resource hash.</param>
			/// <param name="decoders">The decoders.</param>
			/// <returns>Buffer of bytes.</returns>
			public byte[] GetBytes(Guid resourceHash, IDictionary<uint, Func<byte[], int, byte[]>> decoders)
			{
				return ReadData(_entries[resourceHash], decoders);
			}

			/// <summary>Gets the bytes for given resource.</summary>
			/// <param name="resourceName">Name of the resource.</param>
			/// <param name="decoders">The decoders.</param>
			/// <returns>Buffer of bytes.</returns>
			public byte[] GetBytes(string resourceName, IDictionary<uint, Func<byte[], int, byte[]>> decoders)
			{
				return GetBytes(Hash.MD5(resourceName), decoders);
			}

			/// <summary>Determines whether the container has given entry.</summary>
			/// <param name="resourceHash">The resource hash.</param>
			/// <returns><c>true</c> if the container has given entry; otherwise, <c>false</c>.</returns>
			public bool HasEntry(Guid resourceHash)
			{
				return _entries.ContainsKey(resourceHash);
			}

			/// <summary>Determines whether the container has given entry.</summary>
			/// <param name="resourceName">Name of the resource.</param>
			/// <returns><c>true</c> if the container has given entry; otherwise, <c>false</c>.</returns>
			public bool HasEntry(string resourceName) { return HasEntry(Hash.MD5(resourceName)); }

			/// <summary>Determines whether the specified resource is managed assembly.</summary>
			/// <param name="resourceHash">The resource hash.</param>
			/// <returns><c>true</c> if the specified resource is managed; otherwise, <c>false</c>.</returns>
			public bool IsManaged(Guid resourceHash) { return (_entries[resourceHash].Flags & EntryFlags.Unmanaged) == 0; }

			/// <summary>Gets all the assembly names.</summary>
			/// <returns>Collection of assembly names.</returns>
			public IEnumerable<AssemblyName> GetAssemblyNames()
			{
				return _entries.Select(e => e.Value.AssemblyName);
			}

			#endregion

			#region utility

			/// <summary>Deflate decoder implementation.</summary>
			/// <param name="input">The input.</param>
			/// <param name="outputLength">Length of the output.</param>
			/// <returns>Decodec bytes.</returns>
			internal static byte[] DeflateDecoder(byte[] input, int outputLength)
			{
				using (var mstream = new MemoryStream(input))
				using (var zstream = new DeflateStream(mstream, CompressionMode.Decompress))
				{
					return ReadBytes(zstream, outputLength);
				}
			}

			/// <summary>Reads the buffer from stream.</summary>
			/// <param name="stream">The stream.</param>
			/// <param name="length">The length.</param>
			/// <returns>Buffer of bytes.</returns>
			protected static byte[] ReadBytes(Stream stream, int length)
			{
				var result = new byte[length];
				var read = stream.Read(result, 0, length);
				if (read < length)
					throw new IOException("Stream ended prematurely");
				return result;
			}

			#endregion

			#region IDisposable Members

			/// <summary>Clears the allocated memory.</summary>
			protected virtual void Clear()
			{
				TryDispose(ref _reader);
				TryDispose(ref _stream);
				_entries = null;
			}

			/// <summary>
			/// Releases unmanaged resources and performs other cleanup operations before the
			/// object is reclaimed by garbage collection.
			/// </summary>
			~LibZReader()
			{
				Dispose(false);
			}

			/// <summary>
			/// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
			/// </summary>
			public void Dispose()
			{
				Dispose(true);
				GC.SuppressFinalize(this);
			}

			/// <summary>
			/// Releases unmanaged and - optionally - managed resources
			/// </summary>
			/// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; 
			/// <c>false</c> to release only unmanaged resources.</param>
			private void Dispose(bool disposing)
			{
				if (_disposed) return;

				try
				{
					if (disposing)
						DisposeManaged();
					DisposeUnmanaged();
				}
				finally
				{
					_disposed = true;
				}
			}

			/// <summary>Disposes the managed resources.</summary>
			protected virtual void DisposeManaged()
			{
				Clear();
			}

			/// <summary>Disposes the unmanaged resources.</summary>
			protected virtual void DisposeUnmanaged()
			{
				// do nothing
			}

			/// <summary>Tries the dispose and object.</summary>
			/// <typeparam name="T">Type of variable.</typeparam>
			/// <param name="subject">The subject.</param>
			protected static void TryDispose<T>(ref T subject) where T: class
			{
				if (ReferenceEquals(subject, null)) return;
				var disposable = subject as IDisposable;
				if (ReferenceEquals(disposable, null)) return;
				disposable.Dispose();
				subject = null;
			}

			#endregion
		}

		#endregion

		#region internal class GlobalDictionary

		/// <summary>Dictionary accessible from whole AppDomain.</summary>
		internal class GlobalDictionary
		{
			#region fields

			/// <summary>Actual data.</summary>
			private readonly Dictionary<int, object> _data;

			#endregion

			#region constructor

			/// <summary>Initializes a new instance of the <see cref="GlobalDictionary"/> class.</summary>
			/// <param name="dictionaryName">Name of the dictionary.</param>
			public GlobalDictionary(string dictionaryName)
			{
				lock (typeof(object))
				{
					_data = AppDomain.CurrentDomain.GetData(dictionaryName) as Dictionary<int, object>;
					if (_data != null) return;

					_data = new Dictionary<int, object>();
					AppDomain.CurrentDomain.SetData(dictionaryName, _data);
					IsOwner = true;
				}
			}

			#endregion

			#region public interface

			/// <summary>Gets a value indicating whether this instance owns the dictionary.</summary>
			/// <value><c>true</c> if this instance owns the dictionary; otherwise, <c>false</c>.</value>
			public bool IsOwner { get; private set; }

			/// <summary>Gets the value in specified slot.</summary>
			/// <typeparam name="T">Type of slot.</typeparam>
			/// <param name="slot">The slot.</param>
			/// <param name="defaultValue">The default value.</param>
			/// <returns>Value stored in slot or <paramref name="defaultValue"/></returns>
			public T Get<T>(int slot, T defaultValue = default (T))
			{
				object result;
				if (!_data.TryGetValue(slot, out result)) return defaultValue;
				return (T)result;
			}

			/// <summary>Sets the value in specified slot.</summary>
			/// <param name="slot">The slot.</param>
			/// <param name="value">The value.</param>
			public void Set(int slot, object value)
			{
				_data[slot] = value;
			}

			#endregion
		}

		#endregion

		#region class Hash

		/// <summary>MD5 and CRC32 calculator.</summary>
		internal class Hash
		{
			#region fields

			/// <summary>CRC Table.</summary>
			private static readonly uint[] Crc32Table;

			/// <summary>MD5 provider.</summary>
			private readonly static MD5 MD5Provider = MD5Provider.Create();

			#endregion

			#region constructor

			/// <summary>Initializes the <see cref="Hash"/> class.</summary>
			static Hash()
			{
				const uint poly = 0xedb88320;
				Crc32Table = new uint[256];
				for (uint i = 0; i < Crc32Table.Length; ++i)
				{
					var temp = i;
					for (var j = 8; j > 0; --j) temp = (temp & 1) == 1 ? (temp >> 1) ^ poly : temp >> 1;
					Crc32Table[i] = temp;
				}
			}

			#endregion

			#region public interface

			/// <summary>Computes the CRC for specified byte array.</summary>
			/// <param name="bytes">The bytes.</param>
			/// <returns>CRC.</returns>
			public static uint CRC(byte[] bytes)
			{
				var crc = 0xffffffffu;
				for (var i = 0; i < bytes.Length; ++i)
				{
					var index = (byte)((crc & 0xff) ^ bytes[i]);
					crc = (crc >> 8) ^ Crc32Table[index];
				}
				return ~crc;
			}

			/// <summary>Computes the MD5 for specified byte array.</summary>
			/// <param name="bytes">The bytes.</param>
			/// <returns>MD5.</returns>
			public static Guid MD5(byte[] bytes)
			{
				return new Guid(MD5Provider.ComputeHash(bytes));
			}

			/// <summary>Computes CRC for the specified text (case insensitive).</summary>
			/// <param name="text">The text.</param>
			/// <returns>CRC</returns>
			public static uint CRC(string text) { return CRC(Encoding.UTF8.GetBytes(text.ToLowerInvariant())); }

			/// <summary>Computes MD5 for the specified text (case insensitive).</summary>
			/// <param name="text">The text.</param>
			/// <returns>MD5.</returns>
			public static Guid MD5(string text) { return MD5(Encoding.UTF8.GetBytes(text.ToLowerInvariant())); }

			#endregion
		}

		#endregion

		#region Helpers

		/// <summary>Simple helper functions.</summary>
		public static class Helpers
		{
			/// <summary>Sends error message.</summary>
			/// <param name="message">The message.</param>
			internal static void Error(string message)
			{
				if (message == null) return;
				Trace.TraceError(message);
			}

			/// <summary>Sends exception and error message.</summary>
			/// <param name="exception">The exception.</param>
			internal static TException Error<TException>(TException exception) where TException: Exception
			{
				if (exception != null)
					Error(string.Format("{0}: {1}", exception.GetType().Name, exception.Message));
				return exception;
			}
		}


		#endregion
	}
}
