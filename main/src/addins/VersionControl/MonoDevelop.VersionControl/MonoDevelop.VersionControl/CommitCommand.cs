
using System;
using MonoDevelop.Core;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MonoDevelop.VersionControl.Dialogs;

namespace MonoDevelop.VersionControl
{
	class CommitCommand
	{
		public static async Task CommitAsync (Repository vc, ChangeSet changeSet)
		{
			try {
				VersionControlService.NotifyPrepareCommit (vc, changeSet);
				if (!await VerifyUnsavedChangesAsync (changeSet))
					return;
				CommitDialog dlg = new CommitDialog (changeSet);
				try {
					if (MessageService.RunCustomDialog (dlg) == (int) Gtk.ResponseType.Ok) {
						VersionControlService.NotifyBeforeCommit (vc, changeSet);
							new CommitWorker (vc, changeSet, dlg).Start();
							return;
						}
					dlg.EndCommit (false);
				} finally {
					dlg.Destroy ();
					dlg.Dispose ();
				}
				VersionControlService.NotifyAfterCommit (vc, changeSet, false);
			}
			catch (Exception ex) {
				MessageService.ShowError (GettextCatalog.GetString ("Version control command failed."), ex);
			}
		}

		static async Task<bool> VerifyUnsavedChangesAsync (ChangeSet changeSet)
		{
			// In case we have local unsaved files with changes, throw a dialog for the user.
			List<Document> docList = new List<Document> ();
			foreach (var item in IdeApp.Workbench.Documents) {
				if (item.IsDirty && !changeSet.Items.Any (csi => csi.LocalPath == item.FileName))
					docList.Add (item);
			}

			if (docList.Count != 0) {
				AlertButton response = MessageService.GenericAlert (
					Stock.Question,
					GettextCatalog.GetString ("You are trying to commit files which have unsaved changes."),
					GettextCatalog.GetString ("Do you want to save the changes before committing?"),
					new AlertButton [] {
						AlertButton.Cancel,
						new AlertButton (GettextCatalog.GetString ("Don't Save")),
						AlertButton.Save
					}
				);

				if (response == AlertButton.Cancel) {
					return false;
				}

				if (response == AlertButton.Save) {
					// Go through all the items and save them.
					foreach (var item in docList) {
						await item.Save ();
					}

					// Check if save failed on any item.
					foreach (var item in docList)
						if (item.IsDirty) {
							MessageService.ShowMessage (GettextCatalog.GetString (
								"Some files could not be saved."));
						}
				}

				docList.Clear ();
			}

			return true;
		}

		private class CommitWorker : VersionControlTask
		{
			Repository vc;
			ChangeSet changeSet;
			CommitDialog dlg;
			bool success;
						
			public CommitWorker (Repository vc, ChangeSet changeSet, CommitDialog dlg)
			{
				this.vc = vc;
				this.changeSet = changeSet;
				this.dlg = dlg;
				OperationType = VersionControlOperationType.Push;
			}
			
			protected override string GetDescription()
			{
				return GettextCatalog.GetString ("Committing {0}...", changeSet.BaseLocalPath);
			}
			
			protected override void Run ()
			{
				success = true;
				try {
					// store global comment before commit.
					VersionControlService.SetCommitComment (changeSet.BaseLocalPath, changeSet.GlobalComment, true);
					
					vc.Commit (changeSet, Monitor);
					Monitor.ReportSuccess (GettextCatalog.GetString ("Commit operation completed."));
					
					// Reset the global comment on successful commit.
					VersionControlService.SetCommitComment (changeSet.BaseLocalPath, "", true);
				} catch {
					success = false;
					throw;
				}
			}
			
			protected override void Finished ()
			{
				dlg.EndCommit (success);
				dlg.Destroy ();
				FileUpdateEventArgs args = new FileUpdateEventArgs ();
				foreach (ChangeSetItem it in changeSet.Items)
					args.Add (new FileUpdateEventInfo (vc, it.LocalPath, it.IsDirectory));

				if (args.Count > 0)
					VersionControlService.NotifyFileStatusChanged (args);

				VersionControlService.NotifyAfterCommit (vc, changeSet, success);
			}
		}
	}
}
