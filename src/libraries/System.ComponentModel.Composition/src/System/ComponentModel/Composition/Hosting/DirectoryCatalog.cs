// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition.Primitives;
using System.Composition.Diagnostics;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Internal;
using IOPath = System.IO.Path;

namespace System.ComponentModel.Composition.Hosting
{
    /// <summary>
    /// The options controlling the behaviour of the <see cref="DirectoryCatalog"/>.
    /// </summary>
    public class DirectoryCatalogOptions
    {
        /// <summary>
        /// The default options for the <see cref="DirectoryCatalog"/>.
        /// </summary>
        public static DirectoryCatalogOptions Default { get; } = new DirectoryCatalogOptions();

        /// <summary>
        /// Any valid searchPattern that <see cref="Directory.GetFiles(string, string)"/> will accept.
        /// </summary>
        public string SearchPattern { get; init; } = "*.dll";

        /// <summary>
        /// The options for the underlying <see cref="AssemblyCatalog"/>.
        /// </summary>
        public AssemblyCatalogOptions AssemblyOptions { get; init; } = AssemblyCatalogOptions.Default;
    }

    [DebuggerTypeProxy(typeof(DirectoryCatalogDebuggerProxy))]
    public partial class DirectoryCatalog : ComposablePartCatalog, INotifyComposablePartCatalogChanged, ICompositionElement
    {
        private static bool IsWindows =>
#if NET
            OperatingSystem.IsWindows();
#else
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif

        private readonly ReadWriteLock _thisLock = new ReadWriteLock();
        private readonly DirectoryCatalogOptions _options;
        private readonly ICompositionElement? _definitionOrigin;
        private ComposablePartCatalogCollection _catalogCollection;
        private Dictionary<string, AssemblyCatalog> _assemblyCatalogs;
        private volatile bool _isDisposed;
        private string _path;
        private string _fullPath;
        private ReadOnlyCollection<string> _loadedFiles;

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the *.dll files
        ///     in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path)
            : this(path, DirectoryCatalogOptions.Default)
        {
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the *.dll files
        ///     in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="reflectionContext">
        ///     The <see cref="ReflectionContext"/> a context used by the catalog when
        ///     interpreting the types to inject attributes into the type definition.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/> or
        ///     <paramref name="reflectionContext"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, ReflectionContext reflectionContext)
            : this(path, CreateOptions(reflectionContext))
        {
            Requires.NotNull(reflectionContext, nameof(reflectionContext));
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the *.dll files
        ///     in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="definitionOrigin">
        ///     The <see cref="ICompositionElement"/> CompositionElement used by Diagnostics to identify the source for parts.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/> or
        ///     <paramref name="definitionOrigin"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, ICompositionElement definitionOrigin)
            : this(path, DirectoryCatalogOptions.Default, definitionOrigin)
        {
            Requires.NotNull(definitionOrigin, nameof(definitionOrigin));
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the given searchPattern
        ///     over the files in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="reflectionContext">
        ///     The <see cref="ReflectionContext"/> a context used by the catalog when
        ///     interpreting the types to inject attributes into the type definition.
        /// </param>
        /// <param name="definitionOrigin">
        ///     The <see cref="ICompositionElement"/> CompositionElement used by Diagnostics to identify the source for parts.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space
        ///     does not contain a valid pattern.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/> or
        ///     <paramref name="reflectionContext"/> is <see langword="null"/> or
        ///     <paramref name="definitionOrigin"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, ReflectionContext reflectionContext, ICompositionElement definitionOrigin)
            : this(path, CreateOptions(reflectionContext), definitionOrigin)
        {
            Requires.NotNull(reflectionContext, nameof(reflectionContext));
            Requires.NotNull(definitionOrigin, nameof(definitionOrigin));
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the *.dll files
        ///     in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="searchPattern">
        ///     Any valid searchPattern that <see cref="Directory.GetFiles(string, string)"/> will accept.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters. Or <paramref name="searchPattern"/>
        ///     does not contain a valid pattern.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, string searchPattern)
            : this(path, new DirectoryCatalogOptions { SearchPattern = searchPattern, })
        {
            Requires.NotNullOrEmpty(searchPattern, nameof(searchPattern));
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the *.dll files
        ///     in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="searchPattern">The search string. The format of the string should be the same as specified for the <see cref="GetFiles"/> method.</param>
        /// <param name="definitionOrigin">
        ///     The <see cref="ICompositionElement"/> CompositionElement used by Diagnostics to identify the source for parts.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters. Or <paramref name="searchPattern"/>
        ///     does not contain a valid pattern.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/>.
        ///     <paramref name="definitionOrigin"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, string searchPattern, ICompositionElement definitionOrigin)
            : this(path, CreateOptions(searchPattern), definitionOrigin)
        {
            Requires.NotNullOrEmpty(searchPattern, nameof(searchPattern));
            Requires.NotNull(definitionOrigin, nameof(definitionOrigin));
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the given searchPattern
        ///     over the files in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="searchPattern">
        ///     Any valid searchPattern that <see cref="Directory.GetFiles(string, string)"/> will accept.
        /// </param>
        /// <param name="reflectionContext">
        ///     The <see cref="ReflectionContext"/> a context used by the catalog when
        ///     interpreting the types to inject attributes into the type definition.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters. Or <paramref name="searchPattern"/>
        ///     does not contain a valid pattern.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/>
        ///     or <paramref name="searchPattern"/> is <see langword="null"/>.
        ///     or <paramref name="reflectionContext"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, string searchPattern, ReflectionContext reflectionContext)
            : this(path, CreateOptions(searchPattern, reflectionContext))
        {
            Requires.NotNullOrEmpty(searchPattern, nameof(searchPattern));
            Requires.NotNull(reflectionContext, nameof(reflectionContext));
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the given searchPattern
        ///     over the files in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="searchPattern">
        ///     Any valid searchPattern that <see cref="Directory.GetFiles(string, string)"/> will accept.
        /// </param>
        /// <param name="reflectionContext">
        ///     The <see cref="ReflectionContext"/> a context used by the catalog when
        ///     interpreting the types to inject attributes into the type definition.
        /// </param>
        /// <param name="definitionOrigin">
        ///     The <see cref="ICompositionElement"/> CompositionElement used by Diagnostics to identify the source for parts.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters. Or <paramref name="searchPattern"/>
        ///     does not contain a valid pattern.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/>
        ///     or <paramref name="searchPattern"/> is <see langword="null"/>.
        ///     or <paramref name="reflectionContext"/> is <see langword="null"/>.
        ///     or <paramref name="definitionOrigin"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, string searchPattern, ReflectionContext reflectionContext, ICompositionElement definitionOrigin)
            : this(path, CreateOptions(searchPattern, reflectionContext), definitionOrigin)
        {
            Requires.NotNullOrEmpty(searchPattern, nameof(searchPattern));
            Requires.NotNull(reflectionContext, nameof(reflectionContext));
            Requires.NotNull(definitionOrigin, nameof(definitionOrigin));
        }

        /// <summary>
        ///     Creates a catalog of <see cref="ComposablePartDefinition"/>s based on all the given searchPattern
        ///     over the files in the given directory path.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <param name="path">
        ///     Path to the directory to scan for assemblies to add to the catalog.
        ///     The path needs to be absolute or relative to <see cref="AppDomain.BaseDirectory"/>
        /// </param>
        /// <param name="options">
        ///     The options controlling the behaviour of this catalog.
        /// </param>
        /// <param name="definitionOrigin">
        ///     The <see cref="ICompositionElement"/> CompositionElement used by Diagnostics to identify the source for parts.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     If <paramref name="path"/> is a zero-length string, contains only white space, or
        ///     contains one or more implementation-specific invalid characters.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="path"/> is <see langword="null"/>
        ///     or <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified <paramref name="path"/> is invalid (for example, it is on an unmapped drive).
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified <paramref name="path"/>, file name, or both exceed the system-defined maximum length.
        ///     For example, on Windows-based platforms, paths must be less than 248 characters and file names must
        ///     be less than 260 characters.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        ///     The caller does not have the required permission.
        /// </exception>
        public DirectoryCatalog(string path, DirectoryCatalogOptions options, ICompositionElement? definitionOrigin = null)
        {
            Requires.NotNullOrEmpty(path, nameof(path));
            Requires.NotNull(options, nameof(options));

            _options = options;
            _definitionOrigin = definitionOrigin ?? this;
            Initialize(path);
        }

        /// <summary>
        ///     Translated absolute path of the path passed into the constructor of <see cref="DirectoryCatalog"/>.
        /// </summary>
        public string FullPath
        {
            get
            {
                Debug.Assert(_fullPath != null);

                return _fullPath;
            }
        }

        /// <summary>
        ///     Set of files that have currently been loaded into the catalog.
        /// </summary>
        public ReadOnlyCollection<string> LoadedFiles
        {
            get
            {
                using (new ReadLock(_thisLock))
                {
                    Debug.Assert(_loadedFiles != null);
                    return _loadedFiles;
                }
            }
        }

        /// <summary>
        ///     Path passed into the constructor of <see cref="DirectoryCatalog"/>.
        /// </summary>
        public string Path
        {
            get
            {
                Debug.Assert(_path != null);

                return _path;
            }
        }

        /// <summary>
        ///   SearchPattern passed into the constructor of <see cref="DirectoryCatalog"/>, or the default *.dll.
        /// </summary>
        public string SearchPattern
        {
            get
            {
                return _options.SearchPattern;
            }
        }

        /// <summary>
        /// Notify when the contents of the Catalog has changed.
        /// </summary>
        public event EventHandler<ComposablePartCatalogChangeEventArgs>? Changed;

        /// <summary>
        /// Notify when the contents of the Catalog has changing.
        /// </summary>
        public event EventHandler<ComposablePartCatalogChangeEventArgs>? Changing;

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    if (!_isDisposed)
                    {
                        bool disposeLock = false;
                        ComposablePartCatalogCollection? catalogs = null;

                        try
                        {
                            using (new WriteLock(_thisLock))
                            {
                                if (!_isDisposed)
                                {
                                    disposeLock = true;
                                    catalogs = _catalogCollection;
                                    _catalogCollection = null!;
                                    _assemblyCatalogs = null!;
                                    _isDisposed = true;
                                }
                            }
                        }
                        finally
                        {
                            catalogs?.Dispose();

                            if (disposeLock)
                            {
                                _thisLock.Dispose();
                            }
                        }
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public override IEnumerator<ComposablePartDefinition> GetEnumerator()
        {
            return _catalogCollection.SelectMany(catalog => catalog as IEnumerable<ComposablePartDefinition>).GetEnumerator();
        }

        /// <summary>
        ///     Returns the export definitions that match the constraint defined by the specified definition.
        /// </summary>
        /// <param name="definition">
        ///     The <see cref="ImportDefinition"/> that defines the conditions of the
        ///     <see cref="ExportDefinition"/> objects to return.
        /// </param>
        /// <returns>
        ///     An <see cref="IEnumerable{T}"/> of <see cref="Tuple{T1, T2}"/> containing the
        ///     <see cref="ExportDefinition"/> objects and their associated
        ///     <see cref="ComposablePartDefinition"/> for objects that match the constraint defined
        ///     by <paramref name="definition"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="definition"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        ///     The <see cref="DirectoryCatalog"/> has been disposed of.
        /// </exception>
        public override IEnumerable<Tuple<ComposablePartDefinition, ExportDefinition>> GetExports(ImportDefinition definition)
        {
            ThrowIfDisposed();

            Requires.NotNull(definition, nameof(definition));

            return _catalogCollection.SelectMany(catalog => catalog.GetExports(definition));
        }

        /// <summary>
        ///     Raises the <see cref="INotifyComposablePartCatalogChanged.Changed"/> event.
        /// </summary>
        /// <param name="e">
        ///     An <see cref="ComposablePartCatalogChangeEventArgs"/> containing the data for the event.
        /// </param>
        protected virtual void OnChanged(ComposablePartCatalogChangeEventArgs e)
        {
            Changed?.Invoke(this, e);
        }

        /// <summary>
        ///     Raises the <see cref="INotifyComposablePartCatalogChanged.Changing"/> event.
        /// </summary>
        /// <param name="e">
        ///     An <see cref="ComposablePartCatalogChangeEventArgs"/> containing the data for the event.
        /// </param>
        protected virtual void OnChanging(ComposablePartCatalogChangeEventArgs e)
        {
            Changing?.Invoke(this, e);
        }

        /// <summary>
        ///     Refreshes the <see cref="ComposablePartDefinition"/>s with the latest files in the directory that match
        ///     the searchPattern. If any files have been added they will be added to the catalog and if any files were
        ///     removed they will be removed from the catalog. For files that have been removed keep in mind that the
        ///     assembly cannot be unloaded from the process so <see cref="ComposablePartDefinition"/>s for those files
        ///     will simply be removed from the catalog.
        ///
        ///     Possible exceptions that can be thrown are any that <see cref="Directory.GetFiles(string, string)"/> or
        ///     <see cref="Assembly.Load(AssemblyName)"/> can throw.
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">
        ///     The specified path has been removed since object construction.
        /// </exception>
        public void Refresh()
        {
            ThrowIfDisposed();
            if (_loadedFiles == null)
            {
                throw new Exception(SR.Diagnostic_InternalExceptionMessage);
            }

            List<Tuple<string, AssemblyCatalog>> catalogsToAdd;
            List<Tuple<string, AssemblyCatalog>> catalogsToRemove;
            ComposablePartDefinition[] addedDefinitions;
            ComposablePartDefinition[] removedDefinitions;
            object changeReferenceObject;
            string[] afterFiles;
            string[] beforeFiles;

            while (true)
            {
                afterFiles = GetFiles();

                using (new ReadLock(_thisLock))
                {
                    changeReferenceObject = _loadedFiles;
                    beforeFiles = _loadedFiles.ToArray();
                }

                DiffChanges(beforeFiles, afterFiles, out catalogsToAdd, out catalogsToRemove);

                // Don't go any further if there's no work to do
                if (catalogsToAdd.Count == 0 && catalogsToRemove.Count == 0)
                {
                    return;
                }

                // Notify listeners to give them a preview before completeting the changes
                addedDefinitions = catalogsToAdd
                    .SelectMany(cat => cat.Item2 as IEnumerable<ComposablePartDefinition>)
                    .ToArray<ComposablePartDefinition>();

                removedDefinitions = catalogsToRemove
                    .SelectMany(cat => cat.Item2 as IEnumerable<ComposablePartDefinition>)
                    .ToArray<ComposablePartDefinition>();

                using (var atomicComposition = new AtomicComposition())
                {
                    var changingArgs = new ComposablePartCatalogChangeEventArgs(addedDefinitions, removedDefinitions, atomicComposition);
                    OnChanging(changingArgs);

                    // if the change went through then write the catalog changes
                    using (new WriteLock(_thisLock))
                    {
                        if (changeReferenceObject != _loadedFiles)
                        {
                            // Someone updated the list while we were diffing so we need to try the diff again
                            continue;
                        }

                        foreach (var catalogToAdd in catalogsToAdd)
                        {
                            _assemblyCatalogs.Add(catalogToAdd.Item1, catalogToAdd.Item2);
                            _catalogCollection.Add(catalogToAdd.Item2);
                        }

                        foreach (var catalogToRemove in catalogsToRemove)
                        {
                            _assemblyCatalogs.Remove(catalogToRemove.Item1);
                            _catalogCollection.Remove(catalogToRemove.Item2);
                        }

                        _loadedFiles = Array.AsReadOnly(afterFiles);

                        // Lastly complete any changes added to the atomicComposition during the change event
                        atomicComposition.Complete();

                        // Break out of the while(true)
                        break;
                    } // WriteLock
                } // AtomicComposition
            }   // while (true)

            var changedArgs = new ComposablePartCatalogChangeEventArgs(addedDefinitions, removedDefinitions, null);
            OnChanged(changedArgs);
        }

        /// <summary>
        ///     Returns a string representation of the directory catalog.
        /// </summary>
        /// <returns>
        ///     A <see cref="string"/> containing the string representation of the <see cref="DirectoryCatalog"/>.
        /// </returns>
        public override string ToString()
        {
            return GetDisplayName();
        }

        private static DirectoryCatalogOptions CreateOptions(string searchPattern, ReflectionContext? reflectionContext = null)
        {
            return new DirectoryCatalogOptions()
            {
                SearchPattern = searchPattern,
                AssemblyOptions = new AssemblyCatalogOptions()
                {
                    TypeOptions = new TypeCatalogOptions()
                    {
                        ReflectionContext = reflectionContext,
                    },
                },
            };
        }

        private static DirectoryCatalogOptions CreateOptions(ReflectionContext reflectionContext)
        {
            return new DirectoryCatalogOptions()
            {
                AssemblyOptions = new AssemblyCatalogOptions()
                {
                    TypeOptions = new TypeCatalogOptions()
                    {
                        ReflectionContext = reflectionContext,
                    },
                },
            };
        }

        private AssemblyCatalog? CreateAssemblyCatalogGuarded(string assemblyFilePath)
        {
            Exception? exception;

            try
            {
                return new AssemblyCatalog(assemblyFilePath, _options.AssemblyOptions, this);
            }
            catch (FileNotFoundException ex)
            {   // Files should always exists but don't blow up here if they don't
                exception = ex;
            }
            catch (FileLoadException ex)
            {   // File was found but could not be loaded
                exception = ex;
            }
            catch (BadImageFormatException ex)
            {   // Dlls that contain native code are not loaded, but do not invalidate the Directory
                exception = ex;
            }
            catch (ReflectionTypeLoadException ex)
            {   // Dlls that have missing Managed dependencies are not loaded, but do not invalidate the Directory
                exception = ex;
            }

            CompositionTrace.AssemblyLoadFailed(this, assemblyFilePath, exception);

            return null;
        }

        private void DiffChanges(string[] beforeFiles, string[] afterFiles,
            out List<Tuple<string, AssemblyCatalog>> catalogsToAdd,
            out List<Tuple<string, AssemblyCatalog>> catalogsToRemove)
        {
            catalogsToAdd = new List<Tuple<string, AssemblyCatalog>>();
            catalogsToRemove = new List<Tuple<string, AssemblyCatalog>>();

            IEnumerable<string> filesToAdd = afterFiles.Except(beforeFiles);
            foreach (string file in filesToAdd)
            {
                AssemblyCatalog? catalog = CreateAssemblyCatalogGuarded(file);

                if (catalog != null)
                {
                    catalogsToAdd.Add(new Tuple<string, AssemblyCatalog>(file, catalog));
                }
            }

            IEnumerable<string> filesToRemove = beforeFiles.Except(afterFiles);
            using (new ReadLock(_thisLock))
            {
                foreach (string file in filesToRemove)
                {
                    if (_assemblyCatalogs.TryGetValue(file, out AssemblyCatalog? catalog))
                    {
                        catalogsToRemove.Add(new Tuple<string, AssemblyCatalog>(file, catalog));
                    }
                }
            }
        }

        private string GetDisplayName() =>
            $"{GetType().Name} (Path=\"{_path}\")";   // NOLOC

        private string[] GetFiles()
        {
            string[] files = Directory.GetFiles(_fullPath, SearchPattern);

            if (!IsWindows)
            {
                return files;
            }

            return Array.ConvertAll<string, string>(files, (file) => file.ToUpperInvariant());
        }

        private static string GetFullPath(string path)
        {
            var fullPath = IOPath.GetFullPath(path);
            return IsWindows ? fullPath.ToUpperInvariant() : fullPath;
        }

        [MemberNotNull(nameof(_path))]
        [MemberNotNull(nameof(_fullPath))]
        [MemberNotNull(nameof(_assemblyCatalogs))]
        [MemberNotNull(nameof(_catalogCollection))]
        [MemberNotNull(nameof(_loadedFiles))]
        private void Initialize(string path)
        {
            _path = path;
            _fullPath = GetFullPath(path);
            _assemblyCatalogs = new Dictionary<string, AssemblyCatalog>();
            _catalogCollection = new ComposablePartCatalogCollection(null, null, null);

            _loadedFiles = Array.AsReadOnly(GetFiles());

            foreach (string file in _loadedFiles)
            {
                AssemblyCatalog? assemblyCatalog = CreateAssemblyCatalogGuarded(file);

                if (assemblyCatalog != null)
                {
                    _assemblyCatalogs.Add(file, assemblyCatalog);
                    _catalogCollection.Add(assemblyCatalog);
                }
            }
        }

        [DebuggerStepThrough]
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw ExceptionBuilder.CreateObjectDisposed(this);
            }
        }

        /// <summary>
        ///     Gets the display name of the directory catalog.
        /// </summary>
        /// <value>
        ///     A <see cref="string"/> containing a human-readable display name of the <see cref="DirectoryCatalog"/>.
        /// </value>
        string ICompositionElement.DisplayName
        {
            get { return GetDisplayName(); }
        }

        /// <summary>
        ///     Gets the composition element from which the directory catalog originated.
        /// </summary>
        /// <value>
        ///     This property always returns <see langword="null"/>.
        /// </value>
        ICompositionElement? ICompositionElement.Origin
        {
            get { return null; }
        }
    }
}
