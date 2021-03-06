﻿using Microsoft.SPOT.Debugger;
using MonoDevelop.Core;
using MonoDevelop.Core.Assemblies;
using MonoDevelop.Core.Execution;
using MonoDevelop.Core.ProgressMonitoring;
using MonoDevelop.Projects;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using MonoDevelop.Core.Serialization;
using MonoDevelop.Ide;

namespace MonoDevelop.MicroFramework
{
	public class MicroFrameworkProject : DotNetProject
	{
		public override void Dispose()
		{
			ExecutionTargetsManager.DeviceListChanged -= OnExecutionTargetsChanged;
			base.Dispose();
		}

		public MicroFrameworkProject()
			: base()
		{
		}

		public MicroFrameworkProject(string languageName)
			: base(languageName)
		{
		}

		public MicroFrameworkProject(string languageName, ProjectCreateInformation projectCreateInfo, XmlElement projectOptions)
			: base(languageName, projectCreateInfo, projectOptions)
		{
		}

		protected override void OnEndLoad()
		{
			base.OnEndLoad();
			if(CompileTarget != CompileTarget.Library)
				ExecutionTargetsManager.DeviceListChanged += OnExecutionTargetsChanged;
		}

		private void OnExecutionTargetsChanged(object dummy)
		{
			base.OnExecutionTargetsChanged();
		}

		protected override IEnumerable<ExecutionTarget> OnGetExecutionTargets(ConfigurationSelector configuration)
		{
			return ExecutionTargetsManager.Targets;
		}

		public override bool SupportsFramework(TargetFramework framework)
		{
			return framework.Id.Identifier == ".NETMicroFramework";
		}

		public override bool SupportsFormat(FileFormat format)
		{
			return format.Id == "MSBuild10" || format.Id == "MSBuild12";
		}

		protected override bool OnGetCanExecute(ExecutionContext context, ConfigurationSelector configuration)
		{
			if(IdeApp.Workspace.GetAllSolutions().Any((s) => s.StartupItem == this))
			{
				return context.ExecutionTarget is MicroFrameworkExecutionTarget && base.OnGetCanExecute(context, configuration);
			}
			else
			{
				return base.OnGetCanExecute(context, configuration);
			}
		}

		public override TargetFrameworkMoniker GetDefaultTargetFrameworkForFormat(FileFormat format)
		{
			//Keep default version invalid(1.0) or MonoDevelop will omit from serialization
			return new TargetFrameworkMoniker(".NETMicroFramework", "1.0");
		}

		public override TargetFrameworkMoniker GetDefaultTargetFrameworkId()
		{
			return new TargetFrameworkMoniker(".NETMicroFramework", "4.3");
		}
		//Seems like VS is ignoring this
		//So we won't implement it my guess is they removed becauese it was causing
		//problems with version control and multi users projects
		//<DeployDevice>Netduino</DeployDevice>
		//<DeployTransport>USB</DeployTransport>

		//TODO: Add attribute Condition="'$(NetMfTargetsBaseDir)'==''"
		[ItemProperty("NetMfTargetsBaseDir")]
		string netMfTargetsBaseDir = "$(MSBuildExtensionsPath32)\\Microsoft\\.NET Micro Framework\\";

		public string NetMfTargetsBaseDir
		{
			get
			{
				return netMfTargetsBaseDir;
			}
			set
			{
				if(netMfTargetsBaseDir == value)
					return;
				netMfTargetsBaseDir = value;
				NotifyModified("NetMfTargetsBaseDir");
			}
		}

		protected override ExecutionCommand CreateExecutionCommand(ConfigurationSelector configSel, DotNetProjectConfiguration configuration)
		{
			var references = GetReferencedAssemblies(configSel, true).ToList();
			references = references.Select<string,string>((r) =>
			{
				if(r.StartsWith("/"))
					return r;
				return GetAbsoluteChildPath(r).FullPath;
			}).ToList();
			return new MicroFrameworkExecutionCommand() {
				OutputDirectory = configuration.OutputDirectory,
				ReferencedAssemblies = references
			};
		}
	}
}
