﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the BSD license (for details please see \src\AddIns\Debugger\Debugger.AddIn\license.txt)

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Debugger;
using Debugger.AddIn.TreeModel;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.TreeView;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	public class LocalVarPad : AbstractPadContent
	{
		SharpTreeView tree;
		
		public override object Control {
			get { return tree; }
		}
		
		SharpTreeNodeCollection Items {
			get { return tree.Root.Children; }
		}
		
		public LocalVarPad()
		{
			var res = new CommonResources();
			res.InitializeComponent();
			
			this.tree = new SharpTreeView();
			this.tree.Root = new SharpTreeNode();
			this.tree.ShowRoot = false;
			this.tree.View = (GridView)res["variableGridView"];
			this.tree.ItemContainerStyle = (Style)res["itemContainerStyle"];
			
			WindowsDebugger.RefreshingPads += RefreshPad;
			RefreshPad();
		}
		
		void RefreshPad()
		{
			StackFrame frame = WindowsDebugger.CurrentStackFrame;
			
			if (frame == null) {
				this.Items.Clear();
			} else {
				this.Items.Clear();
				frame.Process.EnqueueForEach(
					Dispatcher.CurrentDispatcher,
					ValueNode.GetLocalVariables().ToList(),
					n => this.Items.Add(n.ToSharpTreeNode())
				);
			}
		}
	}
}
