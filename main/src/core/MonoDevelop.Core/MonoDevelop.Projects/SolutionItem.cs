// SolutionEntityItem.cs
//
// Author:
//   Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (c) 2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//

using System;
using System.Linq;
using System.Xml;
using System.IO;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using System.CodeDom.Compiler;

using MonoDevelop.Core;
using MonoDevelop.Projects;
using MonoDevelop.Core.Serialization;
using MonoDevelop.Projects.Extensions;
using MonoDevelop.Core.StringParsing;
using MonoDevelop.Core.Execution;
using Mono.Addins;
using MonoDevelop.Core.Instrumentation;
using MonoDevelop.Core.Collections;
using System.Threading.Tasks;
using MonoDevelop.Projects.Formats.MSBuild;

namespace MonoDevelop.Projects
{
	public abstract class SolutionItem : SolutionFolderItem, IWorkspaceFileObject, IConfigurationTarget, ILoadController, IBuildTarget
	{
		internal object MemoryProbe = Counters.ItemsInMemory.CreateMemoryProbe ();

		int loading;
		ProjectItemCollection items;
		ProjectItemCollection wildcardItems;
		ItemCollection<SolutionItem> dependencies = new ItemCollection<SolutionItem> ();

		SolutionItemEventArgs thisItemArgs;
		
		FileStatusTracker<SolutionItemEventArgs> fileStatusTracker;

		FilePath fileName;
		string name;
		
		FileFormat fileFormat;
		
		SolutionItemConfiguration activeConfiguration;
		SolutionItemConfigurationCollection configurations;

		public event EventHandler ConfigurationsChanged;
		public event ConfigurationEventHandler DefaultConfigurationChanged;
		public event ConfigurationEventHandler ConfigurationAdded;
		public event ConfigurationEventHandler ConfigurationRemoved;
		public event EventHandler<ProjectItemEventArgs> ProjectItemAdded;
		public event EventHandler<ProjectItemEventArgs> ProjectItemRemoved;

		// When set, it means this item is saved as part of a global solution save operation
		internal bool SavingSolution { get; set; }
		
		public SolutionItem ()
		{
			var fmt = Services.ProjectService.FileFormats.GetFileFormat (MSBuildProjectService.DefaultFormat);
			TypeGuid = MSBuildProjectService.GetTypeGuidForItem (this);

			SetSolutionFormat ((MSBuildFileFormat)fmt.Format, true);
			ProjectExtensionUtil.LoadControl (this);
			items = new ProjectItemCollection (this);
			wildcardItems = new ProjectItemCollection (this);
			thisItemArgs = new SolutionItemEventArgs (this);
			configurations = new SolutionItemConfigurationCollection (this);
			configurations.ConfigurationAdded += OnConfigurationAddedToCollection;
			configurations.ConfigurationRemoved += OnConfigurationRemovedFromCollection;
			Counters.ItemsLoaded++;
			fileStatusTracker = new FileStatusTracker<SolutionItemEventArgs> (this, OnReloadRequired, new SolutionItemEventArgs (this));
		}

		SolutionItemExtension itemExtension;

		SolutionItemExtension ItemExtension {
			get {
				if (itemExtension == null)
					itemExtension = ExtensionChain.GetExtension<SolutionItemExtension> ();
				return itemExtension;
			}
		}

		protected override IEnumerable<WorkspaceObjectExtension> CreateDefaultExtensions ()
		{
			foreach (var e in base.CreateDefaultExtensions ())
				yield return e;
			yield return new DefaultMSBuildItemExtension ();
		}

		internal protected virtual IEnumerable<string> GetItemTypeGuids ()
		{
			yield return TypeGuid;
		}

		public override void Dispose ()
		{
			base.Dispose ();
			Counters.ItemsLoaded--;

			foreach (var item in items.Concat (wildcardItems)) {
				IDisposable disp = item as IDisposable;
				if (disp != null)
					disp.Dispose ();
			}
			
			// items = null;
			// wildcardItems = null;
			// thisItemArgs = null;
			// fileStatusTracker = null;
			// fileFormat = null;
			// activeConfiguration = null;
			// configurations = null;
		}

		void HandleSolutionItemAdded (object sender, SolutionItemChangeEventArgs e)
		{
			if (e.Reloading && dependencies.Count > 0 && (e.SolutionItem is SolutionItem) && (e.ReplacedItem is SolutionItem)) {
				int i = dependencies.IndexOf ((SolutionItem)e.ReplacedItem);
				if (i != -1)
					dependencies [i] = (SolutionItem) e.SolutionItem;
			}
		}

		void HandleSolutionItemRemoved (object sender, SolutionItemChangeEventArgs e)
		{
			if (!e.Reloading && (e.SolutionItem is SolutionItem))
				dependencies.Remove ((SolutionItem)e.SolutionItem);
		}

		void ILoadController.BeginLoad ()
		{
			loading++;
			OnBeginLoad ();
		}

		void ILoadController.EndLoad ()
		{
			loading--;
			OnEndLoad ();
		}

		/// <summary>
		/// Called when a load operation for this solution item has started
		/// </summary>
		protected virtual void OnBeginLoad ()
		{
		}

		/// <summary>
		/// Called when a load operation for this solution item has finished
		/// </summary>
		protected virtual void OnEndLoad ()
		{
			fileStatusTracker.ResetLoadTimes ();

			if (syncReleaseVersion && ParentSolution != null)
				releaseVersion = ParentSolution.Version;
		}

		[ItemProperty ("ReleaseVersion", DefaultValue="0.1")]
		string releaseVersion = "0.1";
		
		[ItemProperty ("SynchReleaseVersion", DefaultValue = true)]
		bool syncReleaseVersion = true;

		public string Version {
			get {
				// If syncReleaseVersion is set, releaseVersion will already contain the solution's version
				// That's because the version must be up to date even when loading the project individually
				return releaseVersion;
			}
			set {
				releaseVersion = value;
				NotifyModified ("Version");
			}
		}
		
		public bool SyncVersionWithSolution {
			get {
				return syncReleaseVersion;
			}
			set {
				syncReleaseVersion = value;
				if (syncReleaseVersion && ParentSolution != null)
					Version = ParentSolution.Version;
				NotifyModified ("SyncVersionWithSolution");
			}
		}
		
		protected override string OnGetName ()
		{
			return name ?? string.Empty;
		}

		protected override void OnSetName (string value)
		{
			name = value;
			if (!Loading && SyncFileName) {
				if (string.IsNullOrEmpty (fileName))
					FileName = value;
				else {
					string ext = fileName.Extension;
					FileName = fileName.ParentDirectory.Combine (value) + ext;
				}
			}
		}

		/// <summary>
		/// Returns a value indicating whether the name of the solution item should be the same as the name of the file
		/// </summary>
		/// <value>
		/// <c>true</c> if the file name must be in sync with the solution item name; otherwise, <c>false</c>.
		/// </value>
		protected virtual bool SyncFileName {
			get { return true; }
		}
		
		public virtual FilePath FileName {
			get {
				return fileName;
			}
			set {
				if (FileFormat != null)
					value = FileFormat.GetValidFileName (this, value);
				if (value != fileName) {
					fileName = value;
					if (SyncFileName)
						Name = fileName.FileNameWithoutExtension;
					NotifyModified ("FileName");
				}
			}
		}

		public bool Enabled {
			get { return ParentSolution != null ? ParentSolution.IsSolutionItemEnabled (FileName) : true; }
			set { 
				if (ParentSolution != null)
					ParentSolution.SetSolutionItemEnabled (FileName, value);
			}
		}

		public FileFormat FileFormat {
			get {
				if (ParentSolution != null) {
					if (ParentSolution.FileFormat.Format.SupportsMixedFormats && fileFormat != null)
						return fileFormat;
					return ParentSolution.FileFormat;
				}
				if (fileFormat == null)
					fileFormat = Services.ProjectService.GetDefaultFormat (this);
				return fileFormat; 
			}
			set {
				if (ParentSolution != null && !ParentSolution.FileFormat.Format.SupportsMixedFormats)
					throw new InvalidOperationException ("The file format can't be changed when the item belongs to a solution.");
				InstallFormat (value);
				fileFormat.Format.ConvertToFormat (this);
				NeedsReload = false;
				NotifyModified ("FileFormat");
			}
		}
			
		protected override object OnGetService (Type t)
		{
			return null;
		}

		public ProjectItemCollection Items {
			get { return items; }
		}

		internal ProjectItemCollection WildcardItems {
			get { return wildcardItems; }
		}
		
		/// <summary>
		/// Projects that need to be built before building this one
		/// </summary>
		/// <value>The dependencies.</value>
		public ItemCollection<SolutionItem> ItemDependencies {
			get { return dependencies; }
		}

		/// <summary>
		/// Gets a value indicating whether this item is currently being loaded from a file
		/// </summary>
		/// <remarks>
		/// While an item is loading, some events such as project file change events may be fired.
		/// This flag can be used to check if change events are caused by data being loaded.
		/// </remarks>
		public bool Loading {
			get { return loading > 0; }
		}

		/// <summary>
		/// Gets solution items referenced by this instance (items on which this item depends)
		/// </summary>
		/// <returns>
		/// The referenced items.
		/// </returns>
		/// <param name='configuration'>
		/// Configuration for which to get the referenced items
		/// </param>
		public IEnumerable<SolutionItem> GetReferencedItems (ConfigurationSelector configuration)
		{
			return ItemExtension.OnGetReferencedItems (configuration);
		}

		protected virtual IEnumerable<SolutionItem> OnGetReferencedItems (ConfigurationSelector configuration)
		{
			return dependencies;
		}

		Task IWorkspaceFileObject.ConvertToFormat (FileFormat format, bool convertChildren)
		{
			this.FileFormat = format;
			return Task.FromResult (0);
		}
		
		public bool SupportsFormat (FileFormat format)
		{
			return ItemExtension.OnGetSupportsFormat (format);
		}
		
		protected virtual bool OnGetSupportsFormat (FileFormat format)
		{
			return true;
		}

		internal void InstallFormat (FileFormat format)
		{
			fileFormat = format;
			if (fileName != FilePath.Null)
				fileName = fileFormat.GetValidFileName (this, fileName);
		}
		
		/// <summary>
		/// Initializes a new instance of this item, using an xml element as template
		/// </summary>
		/// <param name='template'>
		/// The template
		/// </param>
		public void InitializeFromTemplate (XmlElement template)
		{
			ItemExtension.OnInitializeFromTemplate (template);
		}

		protected virtual void OnInitializeFromTemplate (XmlElement template)
		{
		}

		public virtual void InitializeNew (ProjectCreateInformation projectCreateInfo, XmlElement projectOptions)
		{
		}

		protected override FilePath GetDefaultBaseDirectory ( )
		{
			return ItemExtension.OnGetDefaultBaseDirectory ();
		}

		internal Task LoadAsync (ProgressMonitor monitor, FilePath fileName, MSBuildFileFormat format)
		{
			FileName = fileName;
			Name = Path.GetFileNameWithoutExtension (fileName);
			SetSolutionFormat (format ?? new MSBuildFileFormatVS12 (), false);
			return ItemExtension.OnLoad (monitor);
		}

		public void Save (ProgressMonitor monitor, FilePath fileName)
		{
			SaveAsync (monitor, fileName).Wait ();
		}

		public Task SaveAsync (ProgressMonitor monitor, FilePath fileName)
		{
			FileName = fileName;
			return SaveAsync (monitor);
		}
		
		/// <summary>
		/// Saves the solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor.
		/// </param>
		public void Save (ProgressMonitor monitor)
		{
			ItemExtension.OnSave (monitor).Wait ();
		}


		public async Task SaveAsync (ProgressMonitor monitor)
		{
			await ItemExtension.OnSave (monitor);

			if (HasSlnData && !SavingSolution && ParentSolution != null) {
				// The project has data that has to be saved in the solution, but the solution is not being saved. Do it now.
				await SolutionFormat.SlnFileFormat.WriteFile (ParentSolution.FileName, ParentSolution, false, monitor);
				ParentSolution.NeedsReload = false;
			}
		}
		
		async Task DoSave (ProgressMonitor monitor)
		{
			if (string.IsNullOrEmpty (FileName))
				throw new InvalidOperationException ("Project does not have a file name");

			try {
				fileStatusTracker.BeginSave ();
				await OnSave (monitor);
				OnSaved (thisItemArgs);
			} finally {
				fileStatusTracker.EndSave ();
			}
			FileService.NotifyFileChanged (FileName);
		}

		internal bool IsSaved {
			get {
				return !string.IsNullOrEmpty (FileName) && File.Exists (FileName);
			}
		}
		
		public override bool NeedsReload {
			get { return fileStatusTracker.NeedsReload; }
			set { fileStatusTracker.NeedsReload = value; }
		}
		
		public virtual bool ItemFilesChanged {
			get { return ItemExtension.ItemFilesChanged; }
		}
		
		bool BaseItemFilesChanged {
			get { return fileStatusTracker.ItemFilesChanged; }
		}

		public bool SupportsBuild ()
		{
			return ItemExtension.OnSupportsBuild ();
		}

		protected virtual bool OnGetSupportsBuild ()
		{
			return true;
		}

		public bool SupportsExecute ()
		{
			return ItemExtension.OnSupportsExecute ();
		}

		protected virtual bool OnGetSupportsExecute ()
		{
			return true;
		}

		public virtual bool SupportsConfigurations ()
		{
			// TODO NPM: -> extension chain
			return SupportsBuild ();
		}

		/// <summary>
		/// Gets a value indicating whether this project is supported.
		/// </summary>
		/// <remarks>
		/// Unsupported projects are shown in the solution pad, but operations such as building on executing won't be available.
		/// </remarks>
		public bool IsUnsupportedProject { get; protected set; }

		/// <summary>
		/// Gets a message that explain why the project is not supported (when IsUnsupportedProject returns true)
		/// </summary>
		public string UnsupportedProjectMessage {
			get { return IsUnsupportedProject ? (loadError ?? GettextCatalog.GetString ("Unknown project type")) : ""; }
			set { loadError = value; }
		}
		string loadError;

		public bool NeedsBuilding (ConfigurationSelector configuration)
		{
			return ItemExtension.OnNeedsBuilding (configuration);
		}

		internal protected virtual bool OnGetNeedsBuilding (ConfigurationSelector configuration)
		{
			return false;
		}

		public void SetNeedsBuilding (ConfigurationSelector configuration)
		{
			OnSetNeedsBuilding (configuration);
		}

		protected virtual void OnSetNeedsBuilding (ConfigurationSelector configuration)
		{
		}

		/// <summary>
		/// Builds the solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor
		/// </param>
		/// <param name='solutionConfiguration'>
		/// Configuration to use to build the project
		/// </param>
		public Task<BuildResult> Build (ProgressMonitor monitor, ConfigurationSelector solutionConfiguration)
		{
			return Build (monitor, solutionConfiguration, false);
		}

		/// <summary>
		/// Builds the solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor
		/// </param>
		/// <param name='solutionConfiguration'>
		/// Configuration to use to build the project
		/// </param>
		/// <param name='buildReferences'>
		/// When set to <c>true</c>, the referenced items will be built before building this item
		/// </param>
		public async Task<BuildResult> Build (ProgressMonitor monitor, ConfigurationSelector solutionConfiguration, bool buildReferences)
		{
			ITimeTracker tt = Counters.BuildProjectTimer.BeginTiming ("Building " + Name);
			try {
				if (!buildReferences) {
					try {
						SolutionItemConfiguration iconf = GetConfiguration (solutionConfiguration);
						string confName = iconf != null ? iconf.Id : solutionConfiguration.ToString ();
						monitor.BeginTask (GettextCatalog.GetString ("Building: {0} ({1})", Name, confName), 1);

						return await InternalBuild (monitor, solutionConfiguration);

					} finally {
						monitor.EndTask ();
					}
				}

				// Get a list of all items that need to be built (including this),
				// and build them in the correct order

				var referenced = new List<SolutionItem> ();
				var visited = new Set<SolutionItem> ();
				GetBuildableReferencedItems (visited, referenced, this, solutionConfiguration);

				var sortedReferenced = TopologicalSort (referenced, solutionConfiguration);

				BuildResult cres = new BuildResult ();
				cres.BuildCount = 0;
				var failedItems = new HashSet<SolutionItem> ();

				monitor.BeginTask (null, sortedReferenced.Count);
				foreach (var p in sortedReferenced) {
					if (!p.ContainsReferences (failedItems, solutionConfiguration)) {
						BuildResult res = await p.Build (monitor, solutionConfiguration, false);
						cres.Append (res);
						if (res.ErrorCount > 0)
							failedItems.Add (p);
					} else
						failedItems.Add (p);
					monitor.Step (1);
					if (monitor.CancellationToken.IsCancellationRequested)
						break;
				}
				monitor.EndTask ();
				return cres;
			} finally {
				tt.End ();
			}
		}

		async Task<BuildResult> InternalBuild (ProgressMonitor monitor, ConfigurationSelector configuration)
		{
			if (IsUnsupportedProject) {
				var r = new BuildResult ();
				r.AddError (UnsupportedProjectMessage);
				return r;
			}

			SolutionItemConfiguration conf = GetConfiguration (configuration) as SolutionItemConfiguration;
			if (conf != null) {
				if (conf.CustomCommands.CanExecute (this, CustomCommandType.BeforeBuild, null, configuration)) {
					if (!await conf.CustomCommands.ExecuteCommand (monitor, this, CustomCommandType.BeforeBuild, configuration)) {
						var r = new BuildResult ();
						r.AddError (GettextCatalog.GetString ("Custom command execution failed"));
						return r;
					}
				}
			}

			if (monitor.CancellationToken.IsCancellationRequested)
				return new BuildResult (new CompilerResults (null), "");

			BuildResult res = await ItemExtension.OnBuild (monitor, configuration);

			if (conf != null && !monitor.CancellationToken.IsCancellationRequested && !res.Failed) {
				if (conf.CustomCommands.CanExecute (this, CustomCommandType.AfterBuild, null, configuration)) {
					if (!await conf.CustomCommands.ExecuteCommand (monitor, this, CustomCommandType.AfterBuild, configuration))
						res.AddError (GettextCatalog.GetString ("Custom command execution failed"));
				}
			}

			return res;
		}

		/// <summary>
		/// Builds the solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor
		/// </param>
		/// <param name='configuration'>
		/// Configuration to use to build the project
		/// </param>
		protected virtual Task<BuildResult> OnBuild (ProgressMonitor monitor, ConfigurationSelector configuration)
		{
			return Task.FromResult (BuildResult.Success);
		}

		void GetBuildableReferencedItems (Set<SolutionItem> visited, List<SolutionItem> referenced, SolutionItem item, ConfigurationSelector configuration)
		{
			if (!visited.Add(item))
				return;

			referenced.Add (item);

			foreach (var ritem in item.GetReferencedItems (configuration))
				GetBuildableReferencedItems (visited, referenced, ritem, configuration);
		}

		internal bool ContainsReferences (HashSet<SolutionItem> items, ConfigurationSelector conf)
		{
			foreach (var it in GetReferencedItems (conf))
				if (items.Contains (it))
					return true;
			return false;
		}

		/// <summary>
		/// Cleans the files produced by this solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor
		/// </param>
		/// <param name='configuration'>
		/// Configuration to use to clean the project
		/// </param>
		public async Task<BuildResult> Clean (ProgressMonitor monitor, ConfigurationSelector configuration)
		{
			ITimeTracker tt = Counters.BuildProjectTimer.BeginTiming ("Cleaning " + Name);
			try {
				try {
					SolutionItemConfiguration iconf = GetConfiguration (configuration);
					string confName = iconf != null ? iconf.Id : configuration.ToString ();
					monitor.BeginTask (GettextCatalog.GetString ("Cleaning: {0} ({1})", Name, confName), 1);

					SolutionItemConfiguration conf = GetConfiguration (configuration);
					if (conf != null) {
						if (conf.CustomCommands.CanExecute (this, CustomCommandType.BeforeClean, null, configuration)) {
							if (!await conf.CustomCommands.ExecuteCommand (monitor, this, CustomCommandType.BeforeClean, configuration)) {
								var r = new BuildResult ();
								r.AddError (GettextCatalog.GetString ("Custom command execution failed"));
								return r;
							}
						}
					}

					if (monitor.CancellationToken.IsCancellationRequested)
						return BuildResult.Success;

					var res = await ItemExtension.OnClean (monitor, configuration);

					if (conf != null && !monitor.CancellationToken.IsCancellationRequested) {
						if (conf.CustomCommands.CanExecute (this, CustomCommandType.AfterClean, null, configuration)) {
							if (!await conf.CustomCommands.ExecuteCommand (monitor, this, CustomCommandType.AfterClean, configuration))
								res.AddError (GettextCatalog.GetString ("Custom command execution failed"));
						}
					}
					return res;

				} finally {
					monitor.EndTask ();
				}
			}
			finally {
				tt.End ();
			}
		}

		/// <summary>
		/// Cleans the files produced by this solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor
		/// </param>
		/// <param name='configuration'>
		/// Configuration to use to clean the project
		/// </param>
		protected virtual Task<BuildResult> OnClean (ProgressMonitor monitor, ConfigurationSelector configuration)
		{
			return Task.FromResult (BuildResult.Success);
		}

		/// <summary>
		/// Sorts a collection of solution items, taking into account the dependencies between them
		/// </summary>
		/// <returns>
		/// The sorted collection of items
		/// </returns>
		/// <param name='items'>
		/// Items to sort
		/// </param>
		/// <param name='configuration'>
		/// A configuration
		/// </param>
		/// <remarks>
		/// This methods sorts a collection of items, ensuring that every item is placed after all the items
		/// on which it depends.
		/// </remarks>
		public static ReadOnlyCollection<T> TopologicalSort<T> (IEnumerable<T> items, ConfigurationSelector configuration) where T: SolutionItem
		{
			IList<T> allItems;
			allItems = items as IList<T>;
			if (allItems == null)
				allItems = new List<T> (items);

			List<T> sortedEntries = new List<T> ();
			bool[] inserted = new bool[allItems.Count];
			bool[] triedToInsert = new bool[allItems.Count];
			for (int i = 0; i < allItems.Count; ++i) {
				if (!inserted[i])
					Insert<T> (i, allItems, sortedEntries, inserted, triedToInsert, configuration);
			}
			return sortedEntries.AsReadOnly ();
		}

		static void Insert<T> (int index, IList<T> allItems, List<T> sortedItems, bool[] inserted, bool[] triedToInsert, ConfigurationSelector solutionConfiguration) where T: SolutionItem
		{
			if (triedToInsert[index]) {
				throw new CyclicDependencyException ();
			}
			triedToInsert[index] = true;
			var insertItem = allItems[index];

			foreach (var reference in insertItem.GetReferencedItems (solutionConfiguration)) {
				for (int j=0; j < allItems.Count; ++j) {
					SolutionFolderItem checkItem = allItems[j];
					if (reference == checkItem) {
						if (!inserted[j])
							Insert (j, allItems, sortedItems, inserted, triedToInsert, solutionConfiguration);
						break;
					}
				}
			}
			sortedItems.Add (insertItem);
			inserted[index] = true;
		}

		/// <summary>
		/// Executes this solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor
		/// </param>
		/// <param name='context'>
		/// An execution context
		/// </param>
		/// <param name='configuration'>
		/// Configuration to use to execute the item
		/// </param>
		public async Task Execute (ProgressMonitor monitor, ExecutionContext context, ConfigurationSelector configuration)
		{
			SolutionItemConfiguration conf = GetConfiguration (configuration) as SolutionItemConfiguration;
			if (conf != null) {
				ExecutionContext localContext = new ExecutionContext (Runtime.ProcessService.DefaultExecutionHandler, context.ConsoleFactory, context.ExecutionTarget);

				if (conf.CustomCommands.CanExecute (this, CustomCommandType.BeforeExecute, localContext, configuration)) {
					if (!await conf.CustomCommands.ExecuteCommand (monitor, this, CustomCommandType.BeforeExecute, localContext, configuration))
						return;
				}
			}

			if (monitor.CancellationToken.IsCancellationRequested)
				return;

			await ItemExtension.OnExecute (monitor, context, configuration);

			if (conf != null && !monitor.CancellationToken.IsCancellationRequested) {
				ExecutionContext localContext = new ExecutionContext (Runtime.ProcessService.DefaultExecutionHandler, context.ConsoleFactory, context.ExecutionTarget);

				if (conf.CustomCommands.CanExecute (this, CustomCommandType.AfterExecute, localContext, configuration))
					await conf.CustomCommands.ExecuteCommand (monitor, this, CustomCommandType.AfterExecute, localContext, configuration);
			}
		}

		/// <summary>
		/// Determines whether this solution item can be executed using the specified context and configuration.
		/// </summary>
		/// <returns>
		/// <c>true</c> if this instance can be executed; otherwise, <c>false</c>.
		/// </returns>
		/// <param name='context'>
		/// An execution context
		/// </param>
		/// <param name='configuration'>
		/// Configuration to use to execute the item
		/// </param>
		public bool CanExecute (ExecutionContext context, ConfigurationSelector configuration)
		{
			return !IsUnsupportedProject && ItemExtension.OnGetCanExecute (context, configuration);
		}

		async Task DoExecute (ProgressMonitor monitor, ExecutionContext context, ConfigurationSelector configuration)
		{
			SolutionItemConfiguration conf = GetConfiguration (configuration) as SolutionItemConfiguration;
			if (conf != null && conf.CustomCommands.HasCommands (CustomCommandType.Execute)) {
				await conf.CustomCommands.ExecuteCommand (monitor, this, CustomCommandType.Execute, context, configuration);
				return;
			}
			await OnExecute (monitor, context, configuration);
		}

		/// <summary>
		/// Executes this solution item
		/// </summary>
		/// <param name='monitor'>
		/// A progress monitor
		/// </param>
		/// <param name='context'>
		/// An execution context
		/// </param>
		/// <param name='configuration'>
		/// Configuration to use to execute the item
		/// </param>
		protected virtual Task OnExecute (ProgressMonitor monitor, ExecutionContext context, ConfigurationSelector configuration)
		{
			return Task.FromResult (0);
		}

		bool DoGetCanExecute (ExecutionContext context, ConfigurationSelector configuration)
		{
			SolutionItemConfiguration conf = GetConfiguration (configuration) as SolutionItemConfiguration;
			if (conf != null && conf.CustomCommands.HasCommands (CustomCommandType.Execute))
				return conf.CustomCommands.CanExecute (this, CustomCommandType.Execute, context, configuration);
			return OnGetCanExecute (context, configuration);
		}

		/// <summary>
		/// Determines whether this solution item can be executed using the specified context and configuration.
		/// </summary>
		/// <returns>
		/// <c>true</c> if this instance can be executed; otherwise, <c>false</c>.
		/// </returns>
		/// <param name='context'>
		/// An execution context
		/// </param>
		/// <param name='configuration'>
		/// Configuration to use to execute the item
		/// </param>
		protected virtual bool OnGetCanExecute (ExecutionContext context, ConfigurationSelector configuration)
		{
			return ItemExtension.OnGetCanExecute (context, configuration);
		}

		/// <summary>
		/// Gets the execution targets.
		/// </summary>
		/// <returns>The execution targets.</returns>
		/// <param name="configuration">The configuration.</param>
		public IEnumerable<ExecutionTarget> GetExecutionTargets (ConfigurationSelector configuration)
		{
			return ItemExtension.OnGetExecutionTargets (configuration);
		}

		protected void NotifyExecutionTargetsChanged ()
		{
			ItemExtension.OnExecutionTargetsChanged ();
		}

		public event EventHandler ExecutionTargetsChanged;

		protected virtual void OnExecutionTargetsChanged ()
		{
			if (ExecutionTargetsChanged != null)
				ExecutionTargetsChanged (this, EventArgs.Empty);
		}

		protected virtual Task OnLoad (ProgressMonitor monitor)
		{
			return Task.FromResult (0);
		}

		protected internal virtual Task OnSave (ProgressMonitor monitor)
		{
			return Task.FromResult (0);
		}

		public FilePath GetAbsoluteChildPath (FilePath relPath)
		{
			return relPath.ToAbsolute (BaseDirectory);
		}

		public FilePath GetRelativeChildPath (FilePath absPath)
		{
			return absPath.ToRelative (BaseDirectory);
		}

		public IEnumerable<FilePath> GetItemFiles (bool includeReferencedFiles)
		{
			return ItemExtension.OnGetItemFiles (includeReferencedFiles);
		}

		protected virtual IEnumerable<FilePath> OnGetItemFiles (bool includeReferencedFiles)
		{
			List<FilePath> col = FileFormat.Format.GetItemFiles (this);
			if (!string.IsNullOrEmpty (FileName) && !col.Contains (FileName))
				col.Add (FileName);
			return col;
		}

		protected override void OnNameChanged (SolutionItemRenamedEventArgs e)
		{
			Solution solution = this.ParentSolution;

			if (solution != null) {
				foreach (DotNetProject project in solution.GetAllItems<DotNetProject>()) {
					if (project == this)
						continue;
					
					project.RenameReferences (e.OldName, e.NewName);
				}
			}
			fileStatusTracker.ResetLoadTimes ();
			base.OnNameChanged (e);
		}
		
		protected virtual void OnSaved (SolutionItemEventArgs args)
		{
			if (Saved != null)
				Saved (this, args);
		}
		
		public virtual string[] SupportedPlatforms {
			get {
				return new string [0];
			}
		}
		
		public virtual SolutionItemConfiguration GetConfiguration (ConfigurationSelector configuration)
		{
			return (SolutionItemConfiguration) configuration.GetConfiguration (this) ?? DefaultConfiguration;
		}

		ItemConfiguration IConfigurationTarget.DefaultConfiguration {
			get { return DefaultConfiguration; }
			set { DefaultConfiguration = (SolutionItemConfiguration) value; }
		}

		public SolutionItemConfiguration DefaultConfiguration {
			get {
				if (activeConfiguration == null && configurations.Count > 0) {
					return configurations[0];
				}
				return activeConfiguration;
			}
			set {
				if (activeConfiguration != value) {
					activeConfiguration = value;
					NotifyModified ("DefaultConfiguration");
					OnDefaultConfigurationChanged (new ConfigurationEventArgs (this, value));
				}
			}
		}
		
		public string DefaultConfigurationId {
			get {
				if (DefaultConfiguration != null)
					return DefaultConfiguration.Id;
				else
					return null;
			}
			set {
				DefaultConfiguration = GetConfiguration (new ItemConfigurationSelector (value));
			}
		}
		
		public virtual ReadOnlyCollection<string> GetConfigurations ()
		{
			List<string> configs = new List<string> ();
			foreach (SolutionItemConfiguration conf in Configurations)
				configs.Add (conf.Id);
			return configs.AsReadOnly ();
		}
		
		[ItemProperty ("Configurations")]
		[ItemProperty ("Configuration", ValueType=typeof(SolutionItemConfiguration), Scope="*")]
		public SolutionItemConfigurationCollection Configurations {
			get {
				return configurations;
			}
		}
		
		IItemConfigurationCollection IConfigurationTarget.Configurations {
			get {
				return Configurations;
			}
		}
		
		public SolutionItemConfiguration AddNewConfiguration (string name)
		{
			SolutionItemConfiguration config = CreateConfiguration (name);
			Configurations.Add (config);
			return config;
		}
		
		ItemConfiguration IConfigurationTarget.CreateConfiguration (string name)
		{
			return CreateConfiguration (name);
		}

		public SolutionItemConfiguration CreateConfiguration (string name)
		{
			return ItemExtension.OnCreateConfiguration (name);
		}
		
		protected virtual SolutionItemConfiguration OnCreateConfiguration (string name)
		{
			return new SolutionItemConfiguration (name);
		}

		void OnConfigurationAddedToCollection (object ob, ConfigurationEventArgs args)
		{
			NotifyModified ("Configurations");
			OnConfigurationAdded (new ConfigurationEventArgs (this, args.Configuration));
			if (ConfigurationsChanged != null)
				ConfigurationsChanged (this, EventArgs.Empty);
			if (activeConfiguration == null)
				DefaultConfigurationId = args.Configuration.Id;
		}
		
		void OnConfigurationRemovedFromCollection (object ob, ConfigurationEventArgs args)
		{
			if (activeConfiguration == args.Configuration) {
				if (Configurations.Count > 0)
					DefaultConfiguration = Configurations [0];
				else
					DefaultConfiguration = null;
			}
			NotifyModified ("Configurations");
			OnConfigurationRemoved (new ConfigurationEventArgs (this, args.Configuration));
			if (ConfigurationsChanged != null)
				ConfigurationsChanged (this, EventArgs.Empty);
		}
		
		public override StringTagModelDescription GetStringTagModelDescription (ConfigurationSelector conf)
		{
			return ItemExtension.OnGetStringTagModelDescription (conf);
		}
		
		StringTagModelDescription DoGetStringTagModelDescription (ConfigurationSelector conf)
		{
			StringTagModelDescription model = base.GetStringTagModelDescription (conf);
			SolutionItemConfiguration config = GetConfiguration (conf);
			if (config != null)
				model.Add (config.GetType ());
			else
				model.Add (typeof(SolutionItemConfiguration));
			return model;
		}

		public override StringTagModel GetStringTagModel (ConfigurationSelector conf)
		{
			return ItemExtension.OnGetStringTagModel (conf);
		}
		
		StringTagModel DoGetStringTagModel (ConfigurationSelector conf)
		{
			StringTagModel source = base.GetStringTagModel (conf);
			SolutionItemConfiguration config = GetConfiguration (conf);
			if (config != null)
				source.Add (config);
			return source;
		}

		internal protected override DateTime OnGetLastBuildTime (ConfigurationSelector configuration)
		{
			return ItemExtension.OnGetLastBuildTime (configuration);
		}

		DateTime DoGetLastBuildTime (ConfigurationSelector configuration)
		{
			return base.OnGetLastBuildTime (configuration);
		}

		internal protected virtual void OnItemsAdded (IEnumerable<ProjectItem> objs)
		{
			ItemExtension.OnItemsAdded (objs);
		}
		
		void DoOnItemsAdded (IEnumerable<ProjectItem> objs)
		{
			NotifyModified ("Items");
			var args = new ProjectItemEventArgs ();
			args.AddRange (objs.Select (pi => new ProjectItemEventInfo (this, pi)));
			if (ProjectItemAdded != null)
				ProjectItemAdded (this, args);
		}

		internal protected virtual void OnItemsRemoved (IEnumerable<ProjectItem> objs)
		{
			ItemExtension.OnItemsRemoved (objs);
		}
		
		void DoOnItemsRemoved (IEnumerable<ProjectItem> objs)
		{
			NotifyModified ("Items");
			var args = new ProjectItemEventArgs ();
			args.AddRange (objs.Select (pi => new ProjectItemEventInfo (this, pi)));
			if (ProjectItemRemoved != null)
				ProjectItemRemoved (this, args);
		}

		protected virtual void OnDefaultConfigurationChanged (ConfigurationEventArgs args)
		{
			ItemExtension.OnDefaultConfigurationChanged (args);
		}
		
		void DoOnDefaultConfigurationChanged (ConfigurationEventArgs args)
		{
			if (DefaultConfigurationChanged != null)
				DefaultConfigurationChanged (this, args);
		}

		protected virtual void OnConfigurationAdded (ConfigurationEventArgs args)
		{
			ItemExtension.OnConfigurationAdded (args);
		}
		
		void DoOnConfigurationAdded (ConfigurationEventArgs args)
		{
			if (ConfigurationAdded != null)
				ConfigurationAdded (this, args);
		}

		protected virtual void OnConfigurationRemoved (ConfigurationEventArgs args)
		{
			ItemExtension.OnConfigurationRemoved (args);
		}
		
		void DoOnConfigurationRemoved (ConfigurationEventArgs args)
		{
			if (ConfigurationRemoved != null)
				ConfigurationRemoved (this, args);
		}

		protected virtual void OnReloadRequired (SolutionItemEventArgs args)
		{
			ItemExtension.OnReloadRequired (args);
		}
		
		void DoOnReloadRequired (SolutionItemEventArgs args)
		{
			fileStatusTracker.FireReloadRequired (args);
		}

		protected override void OnBoundToSolution ()
		{
			ParentSolution.SolutionItemRemoved += HandleSolutionItemRemoved;
			ParentSolution.SolutionItemAdded += HandleSolutionItemAdded;
			ItemExtension.OnBoundToSolution ();
		}

		void DoOnBoundToSolution ()
		{
			base.OnBoundToSolution ();
		}

		protected override void OnUnboundFromSolution ()
		{
			ParentSolution.SolutionItemAdded -= HandleSolutionItemAdded;
			ParentSolution.SolutionItemRemoved -= HandleSolutionItemRemoved;
			ItemExtension.OnUnboundFromSolution ();
		}

		void DoOnUnboundFromSolution ()
		{
			base.OnUnboundFromSolution ();
		}


		public event SolutionItemEventHandler Saved;
		
	
		class DefaultMSBuildItemExtension: SolutionItemExtension
		{
			internal protected override void OnInitializeFromTemplate (XmlElement template)
			{
				Item.OnInitializeFromTemplate (template);
			}

			internal protected override FilePath OnGetDefaultBaseDirectory ()
			{
				return Item.FileName.IsNullOrEmpty ? FilePath.Empty : Item.FileName.ParentDirectory; 
			}

			internal protected override IEnumerable<SolutionItem> OnGetReferencedItems (ConfigurationSelector configuration)
			{
				return Item.OnGetReferencedItems (configuration);
			}

			internal protected override StringTagModelDescription OnGetStringTagModelDescription (ConfigurationSelector conf)
			{
				return Item.DoGetStringTagModelDescription (conf);
			}

			internal protected override StringTagModel OnGetStringTagModel (ConfigurationSelector conf)
			{
				return Item.DoGetStringTagModel (conf);
			}

			internal protected override bool OnGetSupportsFormat (FileFormat format)
			{
				return Item.OnGetSupportsFormat (format);
			}

			internal protected override IEnumerable<FilePath> OnGetItemFiles (bool includeReferencedFiles)
			{
				return Item.OnGetItemFiles (includeReferencedFiles);
			}

			internal protected override SolutionItemConfiguration OnCreateConfiguration (string name)
			{
				return Item.OnCreateConfiguration (name);
			}

			internal protected override string[] SupportedPlatforms {
				get {
					return new string [0];
				}
			}

			internal protected override DateTime OnGetLastBuildTime (ConfigurationSelector configuration)
			{
				return Item.DoGetLastBuildTime (configuration);
			}

			internal protected override Task OnLoad (ProgressMonitor monitor)
			{
				return Item.OnLoad (monitor);
			}

			internal protected override Task OnSave (ProgressMonitor monitor)
			{
				return Item.DoSave (monitor);
			}

			internal protected override bool OnSupportsBuild ()
			{
				return Item.OnGetSupportsBuild ();
			}

			internal protected override bool OnSupportsExecute ()
			{
				return Item.OnGetSupportsExecute ();
			}

			internal protected override Task OnExecute (ProgressMonitor monitor, ExecutionContext context, ConfigurationSelector configuration)
			{
				return Item.DoExecute (monitor, context, configuration);
			}

			internal protected override bool OnGetCanExecute (ExecutionContext context, ConfigurationSelector configuration)
			{
				return Item.DoGetCanExecute (context, configuration);
			}

			internal protected override IEnumerable<ExecutionTarget> OnGetExecutionTargets (ConfigurationSelector configuration)
			{
				yield break;
			}

			internal protected override void OnExecutionTargetsChanged ()
			{
				Item.OnExecutionTargetsChanged ();
			}

			internal protected override void OnReloadRequired (SolutionItemEventArgs args)
			{
				Item.DoOnReloadRequired (args);
			}

			internal protected override void OnItemsAdded (IEnumerable<ProjectItem> objs)
			{
				Item.DoOnItemsAdded (objs);
			}

			internal protected override void OnItemsRemoved (IEnumerable<ProjectItem> objs)
			{
				Item.DoOnItemsRemoved (objs);
			}

			internal protected override void OnDefaultConfigurationChanged (ConfigurationEventArgs args)
			{
				Item.DoOnDefaultConfigurationChanged (args);
			}

			internal protected override void OnBoundToSolution ()
			{
				Item.DoOnBoundToSolution ();
			}

			internal protected override void OnUnboundFromSolution ()
			{
				Item.DoOnUnboundFromSolution ();
			}

			internal protected override void OnConfigurationAdded (ConfigurationEventArgs args)
			{
				Item.DoOnConfigurationAdded (args);
			}

			internal protected override void OnConfigurationRemoved (ConfigurationEventArgs args)
			{
				Item.DoOnConfigurationRemoved (args);
			}

			internal protected override void OnModified (SolutionItemModifiedEventArgs args)
			{
				Item.OnModified (args);
			}

			internal protected override void OnNameChanged (SolutionItemRenamedEventArgs e)
			{
				Item.OnNameChanged (e);
			}

			internal protected override IconId StockIcon {
				get {
					return "md-project";
				}
			}

			internal protected override bool ItemFilesChanged {
				get {
					return Item.BaseItemFilesChanged;
				}
			}

			internal protected override Task<BuildResult> OnBuild (ProgressMonitor monitor, ConfigurationSelector configuration)
			{
				return Item.OnBuild (monitor, configuration);
			}

			internal protected override Task<BuildResult> OnClean (ProgressMonitor monitor, ConfigurationSelector configuration)
			{
				return Item.OnClean (monitor, configuration);
			}

			internal protected override bool OnNeedsBuilding (ConfigurationSelector configuration)
			{
				return Item.OnGetNeedsBuilding (configuration);
			}
		}	
	}

	[Mono.Addins.Extension]
	class SolutionItemTagProvider: StringTagProvider<SolutionItem>, IStringTagProvider
	{
		public override IEnumerable<StringTagDescription> GetTags ()
		{
			yield return new StringTagDescription ("ProjectName", "Project Name");
			yield return new StringTagDescription ("ProjectDir", "Project Directory");
			yield return new StringTagDescription ("AuthorName", "Project Author Name");
			yield return new StringTagDescription ("AuthorEmail", "Project Author Email");
			yield return new StringTagDescription ("AuthorCopyright", "Project Author Copyright");
			yield return new StringTagDescription ("AuthorCompany", "Project Author Company");
			yield return new StringTagDescription ("AuthorTrademark", "Project Trademark");
			yield return new StringTagDescription ("ProjectFile", "Project File");
		}

		public override object GetTagValue (SolutionItem item, string tag)
		{
			switch (tag) {
			case "ITEMNAME":
			case "PROJECTNAME":
				return item.Name;
			case "AUTHORCOPYRIGHT":
				AuthorInformation authorInfo = item.AuthorInformation ?? AuthorInformation.Default;
				return authorInfo.Copyright;
			case "AUTHORCOMPANY":
				authorInfo = item.AuthorInformation ?? AuthorInformation.Default;
				return authorInfo.Company;
			case "AUTHORTRADEMARK":
				authorInfo = item.AuthorInformation ?? AuthorInformation.Default;
				return authorInfo.Trademark;
			case "AUTHOREMAIL":
				authorInfo = item.AuthorInformation ?? AuthorInformation.Default;
				return authorInfo.Email;
			case "AUTHORNAME":
				authorInfo = item.AuthorInformation ?? AuthorInformation.Default;
				return authorInfo.Name;
			case "ITEMDIR":
			case "PROJECTDIR":
				return item.BaseDirectory;
			case "ITEMFILE":
			case "PROJECTFILE":
			case "PROJECTFILENAME":
				return item.FileName;
			}
			throw new NotSupportedException ();
		}
	}
}
