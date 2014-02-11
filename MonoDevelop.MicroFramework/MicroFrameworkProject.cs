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

namespace MonoDevelop.MicroFramework
{
	public class MicroFrameworkProject : DotNetProject
	{
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

		public override TargetFrameworkMoniker GetDefaultTargetFrameworkForFormat(FileFormat format)
		{
			return new TargetFrameworkMoniker(".NETMicroFramework", "4.3");
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
		public PortDefinition SelectedDebugPort { get; set; }

		protected override ExecutionCommand CreateExecutionCommand(ConfigurationSelector configSel, DotNetProjectConfiguration configuration)
		{
			if(SelectedDebugPort == null && PortDefinition.Enumerate(PortFilter.Usb).Count > 0)
				SelectedDebugPort = PortDefinition.Enumerate(PortFilter.Usb)[0] as PortDefinition;//TODO menu selection
			if(SelectedDebugPort == null)
				return null;
			return new MicroFrameworkExecutionCommand() {
				OutputDirectory = configuration.OutputDirectory,
				PortDefinition = SelectedDebugPort
			};
		}

		protected override void DoExecute(IProgressMonitor monitor, ExecutionContext context, ConfigurationSelector configuration)
		{
			DotNetProjectConfiguration dotNetProjectConfig = GetConfiguration(configuration) as DotNetProjectConfiguration;

			IConsole console = dotNetProjectConfig.ExternalConsole
				? context.ExternalConsoleFactory.CreateConsole(!dotNetProjectConfig.PauseConsoleOutput)
				: context.ConsoleFactory.CreateConsole(!dotNetProjectConfig.PauseConsoleOutput);

			AggregatedOperationMonitor aggregatedOperationMonitor = new AggregatedOperationMonitor(monitor);

			try
			{
				try
				{
					ExecutionCommand executionCommand = CreateExecutionCommand(configuration, dotNetProjectConfig);
					if(context.ExecutionTarget != null)
						executionCommand.Target = context.ExecutionTarget;

					IProcessAsyncOperation asyncOp = context.ExecutionHandler.Execute(executionCommand, console);
					aggregatedOperationMonitor.AddOperation(asyncOp);
					asyncOp.WaitForCompleted();
				}
				finally
				{
					aggregatedOperationMonitor.Dispose();
					console.Dispose();
				}
			}
			catch(Exception ex)
			{
				LoggingService.LogError(string.Format("Cannot execute \"{0}\"", dotNetProjectConfig.CompiledOutputName), ex);
				monitor.ReportError(string.Format("Cannot execute \"{0}\"", dotNetProjectConfig.CompiledOutputName), ex);
			}
			;
		}

		void messages(string message)
		{
			System.Diagnostics.Debug.WriteLine(message);
		}
	}
}
