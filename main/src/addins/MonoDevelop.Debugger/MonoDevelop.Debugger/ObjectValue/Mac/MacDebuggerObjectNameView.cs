//
// MacDebuggerObjectNameView.cs
//
// Author:
//       Jeffrey Stedfast <jestedfa@microsoft.com>
//
// Copyright (c) 2019 Microsoft Corp.
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
using System.Collections.Generic;
using System.ComponentModel.Composition;

using AppKit;

using MonoDevelop.Ide;
using MonoDevelop.Core;
using MonoDevelop.Ide.Composition;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Text.Editor;

namespace MonoDevelop.Debugger
{
	/// <summary>
	/// The NSTableViewCell used for the "Name" column.
	/// </summary>
	class MacDebuggerObjectNameView : MacDebuggerObjectCellViewBase
	{
		[Export]
		[BaseDefinition ("text")]
		[Name (DebuggerCompletion.ContentType)]
		ContentTypeDefinition debuggerCompletionContentTypeDefinition;

		readonly List<NSLayoutConstraint> constraints = new List<NSLayoutConstraint> ();
		readonly ITextBufferFactoryService textBufferFactory;
		readonly IContentType contentType;
		readonly ICocoaTextView editor;
		readonly NSView editorTextView;
		PreviewButtonIcon currentIcon;
		bool addNewExpressionVisible;
		bool editorTextViewVisible;
		bool previewIconVisible;
		bool disposed;
		bool editing;

		public MacDebuggerObjectNameView (MacObjectValueTreeView treeView) : base (treeView, "name")
		{
			ImageView = new NSImageView {
				TranslatesAutoresizingMaskIntoConstraints = false
			};

			TextField = new MacDebuggerTextField (this) {
				TranslatesAutoresizingMaskIntoConstraints = false,
				MaximumNumberOfLines = 1,
				DrawsBackground = false,
				Bordered = false,
				Editable = false
			};

			AddSubview (ImageView);
			AddSubview (TextField);

			AddNewExpressionButton = new NSButton {
				TranslatesAutoresizingMaskIntoConstraints = false,
				AccessibilityTitle = GettextCatalog.GetString ("Add new expression"),
				Image = GetImage ("gtk-add", Gtk.IconSize.Menu),
				BezelStyle = NSBezelStyle.Inline,
				Bordered = false
			};
			AddNewExpressionButton.Activated += OnAddNewExpressionButtonClicked;

			PreviewButton = new NSButton {
				TranslatesAutoresizingMaskIntoConstraints = false,
				Image = GetImage ("md-empty", Gtk.IconSize.Menu),
				BezelStyle = NSBezelStyle.Inline,
				Bordered = false
			};
			PreviewButton.Activated += OnPreviewButtonClicked;

			var contentTypeRegistry = CompositionManager.Instance.GetExportedValue<IContentTypeRegistryService> ();
			textBufferFactory = CompositionManager.Instance.GetExportedValue<ITextBufferFactoryService> ();
			var factory = CompositionManager.Instance.GetExportedValue<ICocoaTextEditorFactoryService> ();
			contentType = contentTypeRegistry.GetContentType (DebuggerCompletion.ContentType);
			var textBuffer = textBufferFactory.CreateTextBuffer ("", contentType);
			editor = factory.CreateTextView (textBuffer);

			editor.VisualElement.TranslatesAutoresizingMaskIntoConstraints = false;
			editorTextView = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
			editorTextView.Layer.BackgroundColor = NSColor.White.CGColor;
			editorTextView.AddSubview (editor.VisualElement);

			editor.VisualElement.TopAnchor.ConstraintEqualToAnchor (editorTextView.TopAnchor).Active = true;
			editor.VisualElement.LeftAnchor.ConstraintEqualToAnchor (editorTextView.LeftAnchor).Active = true;
			editor.VisualElement.RightAnchor.ConstraintEqualToAnchor (editorTextView.RightAnchor).Active = true;
			editor.VisualElement.BottomAnchor.ConstraintEqualToAnchor (editorTextView.BottomAnchor).Active = true;

			editor.LostAggregateFocus += OnEditorLostFocus;
		}

		public MacDebuggerObjectNameView (IntPtr handle) : base (handle)
		{
		}

		public NSButton AddNewExpressionButton {
			get; private set;
		}

		public NSButton PreviewButton {
			get; private set;
		}

		public void Edit ()
		{
			if (editing)
				return;

			editing = true;
			UpdateContents ();
			editor.Focus ();
			TreeView.OnStartEditing ();
		}

		public void CancelEdit ()
		{
			if (!editing)
				return;

			editor.LostAggregateFocus -= OnEditorLostFocus;
			try {
				editor.VisualElement.ResignFirstResponder ();
			} finally {
				editor.LostAggregateFocus += OnEditorLostFocus;
			}

			TreeView.OnEndEditing ();
			editing = false;
			UpdateContents ();
		}

		void CommitEdit ()
		{
			if (!editing)
				return;

			var newValue = editor.TextBuffer.CurrentSnapshot.GetText ();
			var oldValue = TextField.StringValue;

			TreeView.OnEndEditing ();
			editing = false;
			UpdateContents ();

			if (Node is AddNewExpressionObjectValueNode) {
				if (newValue.Length > 0)
					TreeView.OnExpressionAdded (newValue);
			} else if (newValue != oldValue) {
				TreeView.OnExpressionEdited (Node, newValue);
			}
		}

		protected override void UpdateContents ()
		{
			if (Node == null)
				return;

			foreach (var constraint in constraints) {
				constraint.Active = false;
				constraint.Dispose ();
			}
			constraints.Clear ();

			OptimalWidth = MarginSize;

			var editable = TreeView.AllowWatchExpressions && Node.Parent is RootObjectValueNode;
			var selected = Superview is NSTableRowView rowView && rowView.Selected;
			var iconName = ObjectValueTreeViewController.GetIcon (Node.Flags);
			var textColor = NSColor.ControlText;
			var showAddNewExpression = false;
			var placeholder = string.Empty;
			var name = Node.Name;

			if (Node.IsUnknown) {
				if (TreeView.DebuggerService.Frame != null)
					textColor = NSColor.FromCGColor (GetCGColor (Styles.ObjectValueTreeValueDisabledText));
			} else if (Node.IsError || Node.IsNotSupported) {
			} else if (Node.IsImplicitNotSupported) {
			} else if (Node.IsEvaluating) {
				if (Node.GetIsEvaluatingGroup ())
					textColor = NSColor.FromCGColor (GetCGColor (Styles.ObjectValueTreeValueDisabledText));
			} else if (Node.IsEnumerable) {
			} else if (Node is AddNewExpressionObjectValueNode) {
				placeholder = GettextCatalog.GetString ("Add new expression");
				showAddNewExpression = true;
				name = string.Empty;
				editable = true;
			} else if (TreeView.Controller.GetNodeHasChangedSinceLastCheckpoint (Node)) {
				textColor = NSColor.FromCGColor (GetCGColor (Styles.ObjectValueTreeValueModifiedText));
			}

			NSView firstView;

			if (showAddNewExpression) {
				firstView = AddNewExpressionButton;

				if (!addNewExpressionVisible) {
					ImageView.RemoveFromSuperview ();
					AddSubview (AddNewExpressionButton);
					addNewExpressionVisible = true;
				}
			} else {
				ImageView.Image = GetImage (iconName, Gtk.IconSize.Menu, selected);
				firstView = ImageView;

				if (addNewExpressionVisible) {
					AddNewExpressionButton.RemoveFromSuperview ();
					addNewExpressionVisible = false;
					AddSubview (ImageView);
				}
			}

			constraints.Add (firstView.CenterYAnchor.ConstraintEqualToAnchor (CenterYAnchor));
			constraints.Add (firstView.LeadingAnchor.ConstraintEqualToAnchor (LeadingAnchor, MarginSize));
			constraints.Add (firstView.WidthAnchor.ConstraintEqualToConstant (ImageSize));
			constraints.Add (firstView.HeightAnchor.ConstraintEqualToConstant (ImageSize));

			OptimalWidth += ImageSize;
			OptimalWidth += RowCellSpacing;

			TextField.PlaceholderAttributedString = GetAttributedPlaceholderString (placeholder);
			TextField.StringValue = name;
			TextField.TextColor = textColor;
			TextField.Editable = editable;
			UpdateFont (TextField);
			TextField.SizeToFit ();

			OptimalWidth += TextField.Frame.Width;

			NSView textView;

			if (editing) {
				textView = editorTextView;

				var span = editor.TextBuffer.CurrentSnapshot.GetEntireSpan ();
				editor.TextBuffer.Replace (span, name);

				if (!editorTextViewVisible) {
					TextField.RemoveFromSuperview ();
					editorTextViewVisible = true;
					AddSubview (textView);
				}

				constraints.Add (textView.TopAnchor.ConstraintEqualToAnchor (TopAnchor, -1));
				constraints.Add (textView.BottomAnchor.ConstraintEqualToAnchor (BottomAnchor, -1));
			} else {
				textView = TextField;

				if (editorTextViewVisible) {
					editorTextView.RemoveFromSuperview ();
					editorTextViewVisible = false;
					AddSubview (TextField);
				}

				constraints.Add (TextField.CenterYAnchor.ConstraintEqualToAnchor (CenterYAnchor));
			}

			constraints.Add (textView.LeadingAnchor.ConstraintEqualToAnchor (firstView.TrailingAnchor, RowCellSpacing));

			if (!editing && MacObjectValueTreeView.ValidObjectForPreviewIcon (Node)) {
				SetPreviewButtonIcon (PreviewButtonIcon.Hidden);

				if (!previewIconVisible) {
					AddSubview (PreviewButton);
					previewIconVisible = true;
				}

				constraints.Add (TextField.WidthAnchor.ConstraintGreaterThanOrEqualToConstant (TextField.Frame.Width));
				constraints.Add (PreviewButton.CenterYAnchor.ConstraintEqualToAnchor (CenterYAnchor));
				constraints.Add (PreviewButton.LeadingAnchor.ConstraintEqualToAnchor (TextField.TrailingAnchor, RowCellSpacing));
				constraints.Add (PreviewButton.WidthAnchor.ConstraintEqualToConstant (ImageSize));
				constraints.Add (PreviewButton.HeightAnchor.ConstraintEqualToConstant (ImageSize));

				OptimalWidth += RowCellSpacing;
				OptimalWidth += PreviewButton.Frame.Width;
			} else {
				if (previewIconVisible) {
					PreviewButton.RemoveFromSuperview ();
					previewIconVisible = false;
				}

				constraints.Add (textView.TrailingAnchor.ConstraintEqualToAnchor (TrailingAnchor, -MarginSize));
			}

			foreach (var constraint in constraints)
				constraint.Active = true;

			OptimalWidth += MarginSize;
		}

		void OnAddNewExpressionButtonClicked (object sender, EventArgs e)
		{
			Edit ();
		}

		public void SetPreviewButtonIcon (PreviewButtonIcon icon)
		{
			if (!previewIconVisible || icon == currentIcon)
				return;

			var name = ObjectValueTreeViewController.GetPreviewButtonIcon (icon);
			PreviewButton.Image = GetImage (name, Gtk.IconSize.Menu);
			currentIcon = icon;

			SetNeedsDisplayInRect (PreviewButton.Frame);
		}

		void OnPreviewButtonClicked (object sender, EventArgs e)
		{
			if (!TreeView.DebuggerService.CanQueryDebugger || PreviewWindowManager.IsVisible)
				return;

			if (!MacObjectValueTreeView.ValidObjectForPreviewIcon (Node))
				return;

			// convert the buttons frame to window coords
			var buttonLocation = PreviewButton.ConvertPointToView (CoreGraphics.CGPoint.Empty, null);

			// now convert the frame to absolute screen coordinates
			buttonLocation = PreviewButton.Window.ConvertPointToScreen (buttonLocation);

			var nativeRoot = MacInterop.GtkQuartz.GetWindow (IdeApp.Workbench.RootWindow);

			// convert to root window coordinates
			buttonLocation = nativeRoot.ConvertPointFromScreen (buttonLocation);
			// the Cocoa Y axis is flipped, convert to Gtk
			buttonLocation.Y = nativeRoot.Frame.Height - buttonLocation.Y;
			// Gtk coords don't include the toolbar and decorations ofsset, so substract it
			buttonLocation.Y -= nativeRoot.Frame.Height - nativeRoot.ContentView.Frame.Height;

			int width = (int) PreviewButton.Frame.Width;
			int height = (int) PreviewButton.Frame.Height;

			var buttonArea = new Gdk.Rectangle ((int) buttonLocation.X, (int) buttonLocation.Y, width, height);
			var val = Node.GetDebuggerObjectValue ();

			SetPreviewButtonIcon (PreviewButtonIcon.Active);

			DebuggingService.ShowPreviewVisualizer (val, IdeApp.Workbench.RootWindow, buttonArea);
		}

		void OnEditorLostFocus (object sender, EventArgs e)
		{
			CommitEdit ();
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && !disposed) {
				AddNewExpressionButton.Activated -= OnAddNewExpressionButtonClicked;
				editor.LostAggregateFocus -= OnEditorLostFocus;
				PreviewButton.Activated -= OnPreviewButtonClicked;
				foreach (var constraint in constraints)
					constraint.Dispose ();
				constraints.Clear ();
				disposed = true;
			}

			base.Dispose (disposing);
		}
	}
}
