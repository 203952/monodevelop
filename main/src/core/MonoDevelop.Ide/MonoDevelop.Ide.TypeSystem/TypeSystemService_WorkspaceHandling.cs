﻿//
// TypeSystemService.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc. (http://xamarin.com)
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
using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using MonoDevelop.Core;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mono.Addins;
using MonoDevelop.Projects;
using System.IO;
using System.Linq;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.VisualStudio.Text;

namespace MonoDevelop.Ide.TypeSystem
{
	public static partial class TypeSystemService
	{
		//Internal for unit test
		internal static readonly MonoDevelopWorkspace emptyWorkspace;

		static object workspaceLock = new object();
		static ImmutableList<MonoDevelopWorkspace> workspaces = ImmutableList<MonoDevelopWorkspace>.Empty;
		static ConcurrentDictionary<MonoDevelop.Projects.Solution, TaskCompletionSource<MonoDevelopWorkspace>> workspaceRequests = new ConcurrentDictionary<MonoDevelop.Projects.Solution, TaskCompletionSource<MonoDevelopWorkspace>> ();

		public static ImmutableArray<Microsoft.CodeAnalysis.Workspace> AllWorkspaces {
			get {
				return workspaces.ToImmutableArray<Microsoft.CodeAnalysis.Workspace> ();
			}
		}


		public static MonoDevelopWorkspace GetWorkspace (MonoDevelop.Projects.Solution solution)
		{
			if (solution == null)
				throw new ArgumentNullException (nameof(solution));
			foreach (var ws in workspaces) {
				if (ws.MonoDevelopSolution == solution)
					return ws;
			}
			return emptyWorkspace;
		}

		public static async Task<MonoDevelopWorkspace> GetWorkspaceAsync (MonoDevelop.Projects.Solution solution, CancellationToken cancellationToken = default (CancellationToken))
		{
			var workspace = GetWorkspace (solution);
			if (workspace != emptyWorkspace)
				return workspace;
			var tcs = workspaceRequests.GetOrAdd (solution, _ => new TaskCompletionSource<MonoDevelopWorkspace> ());
			try {
				workspace = GetWorkspace (solution);
				if (workspace != emptyWorkspace)
					return workspace;
				cancellationToken.ThrowIfCancellationRequested ();
				cancellationToken.Register (() => tcs.TrySetCanceled ());
				workspace = await tcs.Task;
			} finally {
				workspaceRequests.TryRemove (solution, out tcs);
			}
			return workspace;
		}

		internal static MonoDevelopWorkspace GetWorkspace (WorkspaceId id)
		{
			foreach (var ws in workspaces) {
				if (ws.Id.Equals (id))
					return ws;
			}
			return emptyWorkspace;
		}

		public static Microsoft.CodeAnalysis.Workspace Workspace {
			get {
				var solution = IdeApp.ProjectOperations?.CurrentSelectedSolution;
				if (solution == null)
					return emptyWorkspace;
				return GetWorkspace (solution);
			}
		}


		public static void NotifyFileChange (string fileName, string text)
		{
			try {
				foreach (var ws in workspaces)
					ws.UpdateFileContent (fileName, text);
			} catch (Exception e) {
				LoggingService.LogError ("Error while notify file change.", e);
			}
		}

		internal static async Task<List<MonoDevelopWorkspace>> Load (WorkspaceItem item, ProgressMonitor progressMonitor, CancellationToken cancellationToken = default (CancellationToken), bool showStatusIcon = true)
		{
			using (Counters.ParserService.WorkspaceItemLoaded.BeginTiming ()) {
				var wsList = CreateWorkspaces (item).ToList();
				//If we want BeginTiming to work correctly we need to `await`
				await InternalLoad (wsList, progressMonitor, cancellationToken, showStatusIcon).ConfigureAwait (false);
				return wsList.ToList ();
			}
		}

		static IEnumerable<MonoDevelopWorkspace> CreateWorkspaces (WorkspaceItem item)
		{
			if (item is MonoDevelop.Projects.Workspace ws) {
				foreach (var wsItem in ws.Items) {
					foreach (var mdWorkspace in CreateWorkspaces (wsItem)) {
						yield return mdWorkspace;
					}
				}
				ws.ItemAdded += OnWorkspaceItemAdded;
				ws.ItemRemoved += OnWorkspaceItemRemoved;
			} else if (item is MonoDevelop.Projects.Solution solution) {
				var workspace = new MonoDevelopWorkspace (solution);
				lock (workspaceLock)
					workspaces = workspaces.Add (workspace);
				solution.SolutionItemAdded += OnSolutionItemAdded;
				solution.SolutionItemRemoved += OnSolutionItemRemoved;
				yield return workspace;
			}
		}

		static async Task InternalLoad (List<MonoDevelopWorkspace> mdWorkspaces, ProgressMonitor progressMonitor, CancellationToken cancellationToken = default (CancellationToken), bool showStatusIcon = true)
		{
			foreach (var workspace in mdWorkspaces) {
				if (showStatusIcon)
					workspace.ShowStatusIcon ();

				var (solution, solutionInfo) = await InternalLoad (workspace, cancellationToken).ConfigureAwait (false);

				if (workspaceRequests.TryGetValue (solution, out var request)) {
					if (solutionInfo == null) {
						// Check for solutionInfo == null rather than cancellation was requested, as cancellation does not happen
						// after all project infos are loaded.
						request.TrySetCanceled ();
					} else {
						request.TrySetResult (workspace);
					}
				}

				if (showStatusIcon)
					workspace.HideStatusIcon ();

			}
		}

		internal static Task<(MonoDevelop.Projects.Solution, SolutionInfo)> Load (MonoDevelopWorkspace workspace, CancellationToken cancellationToken)
		{
			return Task.Run (() => {
				return InternalLoad (workspace, cancellationToken);
			});
		}

		static async Task<(MonoDevelop.Projects.Solution, SolutionInfo)> InternalLoad (MonoDevelopWorkspace workspace, CancellationToken cancellationToken)
		{
			// Try the cache first.
			var (solution, solutionInfo) = await workspace.TryLoadSolutionFromCache (cancellationToken).ConfigureAwait (false);
			if (solutionInfo != null) {
				// Start full load of projects in the background.
				workspace.ReloadProjects (cancellationToken).Ignore ();
				return (solution, solutionInfo);
			}

			// No cache.
			return await workspace.TryLoadSolution (cancellationToken).ConfigureAwait (false);
		}

		internal static void Unload (MonoDevelop.Projects.WorkspaceItem item)
		{
			var ws = item as MonoDevelop.Projects.Workspace;
			if (ws != null) {
				foreach (var it in ws.Items)
					Unload (it);
				ws.ItemAdded -= OnWorkspaceItemAdded;
				ws.ItemRemoved -= OnWorkspaceItemRemoved;
				MonoDocDocumentationProvider.ClearCommentCache ();
			} else {
				var solution = item as MonoDevelop.Projects.Solution;
				if (solution != null) {
					MonoDevelopWorkspace result = GetWorkspace (solution);
					if (result != emptyWorkspace) {
						lock (workspaceLock)
							workspaces = workspaces.Remove (result);

						if (workspaceRequests.TryGetValue (solution, out var request)) {
							request.TrySetCanceled ();
						}

						result.Dispose ();
					}
					solution.SolutionItemAdded -= OnSolutionItemAdded;
					solution.SolutionItemRemoved -= OnSolutionItemRemoved;
					if (solution.ParentWorkspace == null)
						MonoDocDocumentationProvider.ClearCommentCache ();
				}
			}
		}

		public static DocumentId GetDocumentId (MonoDevelop.Projects.Project project, string fileName)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			if (fileName == null)
				throw new ArgumentNullException (nameof(fileName));
			fileName = FileService.GetFullPath (fileName);
			foreach (var w in workspaces) {
				var projectId = w.GetProjectId (project);
				if (projectId != null)
					return w.GetDocumentId (projectId, fileName);
			}
			return null;
		}

		public static DocumentId GetDocumentId (Microsoft.CodeAnalysis.Workspace workspace, MonoDevelop.Projects.Project project, string fileName)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			if (fileName == null)
				throw new ArgumentNullException (nameof(fileName));
			fileName = FileService.GetFullPath (fileName);
			var projectId = ((MonoDevelopWorkspace)workspace).GetProjectId (project);
			if (projectId != null) {
				return ((MonoDevelopWorkspace)workspace).GetDocumentId (projectId, fileName);
			} else {
				LoggingService.LogWarning ("Warning can't find " + fileName + " in project " + project.Name + "("+ projectId +")");
			}
			return null;
		}


		public static DocumentId GetDocumentId (ProjectId projectId, string fileName)
		{
			if (projectId == null)
				throw new ArgumentNullException (nameof(projectId));
			if (fileName == null)
				throw new ArgumentNullException (nameof(fileName));
			foreach (var w in workspaces) {
				if (w.Contains (projectId))
					return w.GetDocumentId (projectId, fileName);
			}
			return null;
		}

		public static IEnumerable<DocumentId> GetDocuments (string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException (nameof(fileName));
			fileName = FileService.GetFullPath (fileName);
			foreach (var w in workspaces) {
				foreach (var projectId in w.CurrentSolution.ProjectIds) {
					var docId = w.GetDocumentId (projectId, fileName);
					if (docId != null)
						yield return docId;
				}
			}
		}

		static void OnDocumentOpened (object sender, Gui.DocumentEventArgs e)
		{
			var filePath = e.Document.FileName;
			var textBuffer = e.Document.GetContent<ITextBuffer> ();
			if (textBuffer == null || filePath == null) {
				return;
			}

			// First offer the document to the primary workspace and see if it's owned by it.
			// This is the common case, so avoid adding the document to the miscellaneous workspace
			// unnecessarily, as it will be immediately removed anyway.
			TryOpenDocumentInWorkspace (e, filePath, textBuffer);

			// Only use misc workspace with the new editor; old editor has its own
			if (e.Document.Editor == null) {
				// If the primary workspace didn't claim the document notify the miscellaneous workspace
				miscellaneousFilesWorkspace.OnDocumentOpened (filePath, textBuffer);
			}
		}

		private static void TryOpenDocumentInWorkspace (Gui.DocumentEventArgs e, FilePath filePath, ITextBuffer textBuffer)
		{
			var project = e.Document.Project;
			if (project == null || !project.IsCompileable (filePath)) {
				return;
			}

			var workspace = GetWorkspace (project.ParentSolution);
			if (workspace == emptyWorkspace) {
				return;
			}

			var projectId = workspace.GetProjectId (project);

			var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath (filePath);
			var bestDoc = documentIds.FirstOrDefault ();
			if (documentIds.Length > 1) {
				foreach (var documentId in documentIds) {
					// projectId == null, when opening document from Solution pad, for file in shared project
					if (projectId == null) {
						var p = workspace.GetMonoProject (documentId.ProjectId);
						if (p == null)
							continue;
						var solConf = p.ParentSolution.GetConfiguration (IdeApp.Workspace.ActiveConfiguration);
						if (solConf == null || !solConf.BuildEnabledForItem (p))
							continue;
						if (p == p.ParentSolution.StartupItem) {
							workspace.InformDocumentOpen (documentId, textBuffer.AsTextContainer (), e.Document);
							return;
						}
						bestDoc = documentId;
					} else if (documentId.ProjectId == projectId) {
						workspace.InformDocumentOpen (documentId, textBuffer.AsTextContainer (), e.Document);
						return;
					}
				}
			}

			if (bestDoc != null) {
				workspace.InformDocumentOpen (bestDoc, textBuffer.AsTextContainer (), e.Document);
			}
		}

		static void OnDocumentClosed (object sender, Gui.DocumentEventArgs e)
		{
			var filePath = e.Document.FileName;
			var project = e.Document.Project;
			if (project == null || filePath == null || !project.IsCompileable (filePath)) {
				return;
			}

			var textBuffer = e.Document.GetContent<ITextBuffer> ();
			if (textBuffer == null) {
				return;
			}

			// Only use misc workspace with the new editor; old editor has its own
			if (e.Document.Editor == null) {
				// In the common case the primary workspace will own the document, so shut down
				// miscellaneous workspace first to avoid adding and then immediately removing
				// the document to the miscellaneous workspace
				miscellaneousFilesWorkspace.OnDocumentClosed (filePath, textBuffer);
			}

			TryCloseDocumentInWorkspace (filePath, project);
		}

		private static void TryCloseDocumentInWorkspace (FilePath filePath, MonoDevelop.Projects.Project project)
		{
			var workspace = GetWorkspace (project.ParentSolution);
			if (workspace == emptyWorkspace) {
				return;
			}

			var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath (filePath);
			foreach (var documentId in documentIds) {
				workspace.InformDocumentClose (documentId, filePath);
			}
		}

		public static Microsoft.CodeAnalysis.Project GetCodeAnalysisProject (MonoDevelop.Projects.Project project)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			foreach (var w in workspaces) {
				var projectId = w.GetProjectId (project);
				if (projectId != null)
					return w.CurrentSolution.GetProject (projectId);
			}
			return null;
		}

		public static async Task<Microsoft.CodeAnalysis.Project> GetCodeAnalysisProjectAsync (MonoDevelop.Projects.Project project, CancellationToken cancellationToken = default (CancellationToken))
		{
			var parentSolution = project.ParentSolution;
			var workspace = await GetWorkspaceAsync (parentSolution, cancellationToken);
			var projectId = workspace.GetProjectId (project);
			if (projectId == null)
				return null;
			var proj = workspace.CurrentSolution.GetProject (projectId);
			if (proj != null)
				return proj;
			//We assume that since we have projectId and project is not found in solution
			//project is being loaded(waiting MSBuild to return list of source files)
			var taskSource = new TaskCompletionSource<Microsoft.CodeAnalysis.Project> ();
			var registration = cancellationToken.Register (() => taskSource.TrySetCanceled ());
			EventHandler<WorkspaceChangeEventArgs> del = (s, e) => {
				if (e.Kind == WorkspaceChangeKind.SolutionAdded || e.Kind == WorkspaceChangeKind.SolutionReloaded) {
					proj = workspace.CurrentSolution.GetProject (projectId);
					if (proj != null) {
						registration.Dispose ();
						taskSource.TrySetResult (proj);
					}
				}
			};
			workspace.WorkspaceChanged += del;
			try {
				proj = await taskSource.Task;
			} finally {
				workspace.WorkspaceChanged -= del;
			}
			return proj;
		}

		public static Task<Compilation> GetCompilationAsync (MonoDevelop.Projects.Project project, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));

			var roslynProject = GetProject (project, cancellationToken);
			if (roslynProject != null)
				return roslynProject.GetCompilationAsync (cancellationToken);
			return Task.FromResult (default(Compilation));
		}

		internal static Microsoft.CodeAnalysis.Project GetProject (MonoDevelop.Projects.Project project, CancellationToken cancellationToken = default (CancellationToken))
		{
			foreach (var w in workspaces) {
				var projectId = w.GetProjectId (project);
				if (projectId == null)
					continue;
				var roslynProject = w.CurrentSolution.GetProject (projectId);
				if (roslynProject == null)
					continue;
				return roslynProject;
			}
			return null;
		}

		static void OnWorkspaceItemAdded (object s, MonoDevelop.Projects.WorkspaceItemEventArgs args)
		{
			TypeSystemService.Load (args.Item, null).Ignore ();
		}

		static void OnWorkspaceItemRemoved (object s, MonoDevelop.Projects.WorkspaceItemEventArgs args)
		{
			Unload (args.Item);
		}

		static async void OnSolutionItemAdded (object sender, MonoDevelop.Projects.SolutionItemChangeEventArgs args)
		{
			try {
				var project = args.SolutionItem as MonoDevelop.Projects.Project;
				if (project != null) {
					var ws = GetWorkspace (args.Solution);
					var oldProject = args.ReplacedItem as MonoDevelop.Projects.Project;

					// when loading a project that was unloaded manually before
					// args.ReplacedItem is the UnloadedSolutionItem, which is not useful
					// we need to find what was the real project previously
					if (args.Reloading && oldProject == null) {
						var existingRoslynProject = ws.CurrentSolution.Projects.FirstOrDefault (p => p.FilePath == project.FileName);
						if (existingRoslynProject != null) {
							oldProject = ws.GetMonoProject (existingRoslynProject.Id);
						}
					}

					var projectInfo = await ws.LoadProject (project, CancellationToken.None, oldProject);
					if (oldProject != null) {
						ws.OnProjectReloaded (projectInfo);
					}
					else {
						ws.OnProjectAdded (projectInfo);
					}
					ws.ReloadModifiedProject (project);
				}
			} catch (Exception ex) {
				LoggingService.LogError ("OnSolutionItemAdded failed", ex);
			}
		}

		static void OnSolutionItemRemoved (object sender, MonoDevelop.Projects.SolutionItemChangeEventArgs args)
		{
			if (args.Reloading) {
				return;
			}

			var project = args.SolutionItem as MonoDevelop.Projects.Project;
			var solution = sender as MonoDevelop.Projects.Solution;
			if (project != null) {
				var ws = GetWorkspace (solution);
				ws.RemoveProject (project);
			}
		}

		#region Tracked project handling
		static readonly List<TypeSystemOutputTrackingNode> outputTrackedProjects = new List<TypeSystemOutputTrackingNode> ();

		static void IntitializeTrackedProjectHandling ()
		{
			AddinManager.AddExtensionNodeHandler ("/MonoDevelop/TypeSystem/OutputTracking", delegate (object sender, ExtensionNodeEventArgs args) {
				var node = (TypeSystemOutputTrackingNode)args.ExtensionNode;
				switch (args.Change) {
				case ExtensionChange.Add:
					AddOutputTrackingNode (node);
					break;
				case ExtensionChange.Remove:
					outputTrackedProjects.Remove (node);
					break;
				}
			});
			if (IdeApp.ProjectOperations != null)
				IdeApp.ProjectOperations.EndBuild += HandleEndBuild;
			if (IdeApp.Workspace != null)
				IdeApp.Workspace.ActiveConfigurationChanged += HandleActiveConfigurationChanged;
		}

		/// <summary>
		/// Adds an output tracking node for unit testing purposes.
		/// </summary>
		[EditorBrowsable(EditorBrowsableState.Never)]
		internal static void AddOutputTrackingNode (TypeSystemOutputTrackingNode node)
		{
			outputTrackedProjects.Add (node);
		}

		static void HandleEndBuild (object sender, BuildEventArgs args)
		{
			var project = args.SolutionItem as DotNetProject;
			if (project == null)
				return;
			CheckProjectOutput (project, true);
		}

		static void HandleActiveConfigurationChanged (object sender, EventArgs e)
		{
			if (IdeApp.ProjectOperations.CurrentSelectedSolution != null) {
				foreach (var pr in IdeApp.ProjectOperations.CurrentSelectedSolution.GetAllProjects ()) {
					var project = pr as DotNetProject;
					if (project != null)
						CheckProjectOutput (project, true);
				}
			}
		}

		internal static bool IsOutputTrackedProject (DotNetProject project)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			return outputTrackedProjects.Any (otp => string.Equals (otp.LanguageName, project.LanguageName, StringComparison.OrdinalIgnoreCase)) ||
				project.GetTypeTags().Any (tag => outputTrackedProjects.Any (otp => string.Equals (otp.ProjectType, tag, StringComparison.OrdinalIgnoreCase)));
		}

		static void CheckProjectOutput (DotNetProject project, bool autoUpdate)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			if (IsOutputTrackedProject (project)) {
				if (autoUpdate) {
					// update documents
					foreach (var openDocument in IdeApp.Workbench.Documents) {
						openDocument.ReparseDocument ();
					}
				}
			}
		}
		#endregion
	}

}
