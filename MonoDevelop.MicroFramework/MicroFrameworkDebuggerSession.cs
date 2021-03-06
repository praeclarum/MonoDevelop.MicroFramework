﻿using Microsoft.SPOT.Debugger;
using Microsoft.SPOT.Debugger.WireProtocol;
using Mono.Debugging.Client;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Debugging.Backend;
using System.Diagnostics.SymbolStore;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Diagnostics;
using System.Reflection;
using System.CodeDom.Compiler;

namespace MonoDevelop.MicroFramework
{
	public class MicroFrameworkDebuggerSession : DebuggerSession, IDisposable, ICorDebugManagedCallback
	{
		public new IDebuggerSessionFrontend Frontend
		{
			get { return base.Frontend; }
		}

		public ICorDebugManagedCallback ManagedCallback
		{
			get
			{
				return this;
			}
		}

		public Engine Engine
		{ 
			get
			{
				return process.Engine;
			} 
		}

		public MicroFrameworkObjectValueAdaptor ObjectAdapter = new MicroFrameworkObjectValueAdaptor();
		private Dictionary<uint,ProcessInfo> processesInfo = new Dictionary<uint, ProcessInfo>();
		public CorDebugProcess process;
		private List<CorDebugProcess> processes = new List<CorDebugProcess>();

		public void RegisterProcess(CorDebugProcess corDebugProcess)
		{
			lock(processes)
			{
				processes.Add(corDebugProcess);
				process = corDebugProcess;
			}
		}

		public void UnregisterProcess(CorDebugProcess process)
		{
			if(process == null)
				return;
			lock(processes)
			{
				if(processes.Contains(process))
					processes.Remove(process);
			}
		}

		private void AddDependencies(Hashtable table, string[] paths)
		{
			if(paths != null)
			{
				foreach(string path in paths)
				{
					if(path != null)
					{
						string filename = Path.GetFileName(path);

						// When changing target frameworks in VS2010, sometimes VS tells us we are dependent
						// on a GAC version of mscorlib (in addition to our own mscorlib), so ignore it.
						if(path.ToLower().Contains("\\gac\\") || path.ToLower().Contains("\\gac_64\\"))
						{
							continue;
						}

						if(table[filename] != null)
						{
							//nop for now....otherwise need to check and make sure everything is kosher,
							//need to check for versioning here?
						}
						else
						{
							table[filename] = path;
						}
					}
				}
			}
		}


		string[] GetDependencies(string[] m_assemblyReferences, bool fIncludeStartProgram, bool fPE, bool fTargetIsBigEndian, string frameworkVersion)
		{
			Hashtable table = new Hashtable();
			AddDependencies(table, m_assemblyReferences);

			ArrayList deps = new ArrayList(table.Values);

			string[] depsArray = (string[])deps.ToArray(typeof(string));

			if(fPE)
			{
				for(int iAssembly = 0; iAssembly < depsArray.Length; iAssembly++)
				{
					depsArray[iAssembly] = Utility.FindPEForDll(depsArray[iAssembly], fTargetIsBigEndian, frameworkVersion);
				}
			}

			return depsArray;
		}

		private bool IsDeviceInInitializeState(Engine engine)
		{
			uint executionMode = 0;

			try
			{
				if(engine.SetExecutionMode(0, 0, out executionMode))
				{
					return (executionMode & Commands.Debugging_Execution_ChangeConditions.c_State_Mask) == Commands.Debugging_Execution_ChangeConditions.c_State_Initialize;
				}
			}
			catch
			{
			}

			return false;
		}

		class CompareAssemblyName:IEqualityComparer<string>
		{
			#region IEqualityComparer implementation

			public bool Equals(string x, string y)
			{
				return string.Equals(Path.GetFileName(x), Path.GetFileName(y));
			}

			public int GetHashCode(string obj)
			{
				return obj.GetHashCode();
			}

			#endregion


		}

		void ResursiveAddReferences(List<string> refs, string assemblyPath)
		{
			byte[] asmData;
			using(FileStream sr = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			{
				asmData = new byte[sr.Length];
				sr.Read(asmData, 0, (int)sr.Length);
			}

			var assembly = Assembly.Load(asmData);
			var references = assembly.GetReferencedAssemblies();
			foreach(var reference in references)
			{
				var path = "/Library/Frameworks/Microsoft .NET Micro Framework/v4.3/Assemblies/le/" + reference.Name + ".dll";
				if(!File.Exists(path))
					continue;
				refs.Add(path);
				ResursiveAddReferences(refs, path);
			}
		}

		protected override void OnRun(DebuggerStartInfo startInfo)
		{
			var mfStartInfo = startInfo as MicroFrameworkDebuggerStartInfo;
			if(mfStartInfo == null)//This should never happen...
				throw new InvalidOperationException();
			var command = mfStartInfo.MFCommand;
			var portDefinition = ((MicroFrameworkExecutionTarget)command.Target).PortDefinition;
			try
			{
				using(var engine = new Engine(portDefinition))
				{
					engine.Start();
					var systemAssemblies = new Hashtable();
					string newCommand = "/CorDebug_DeployDeviceName:" + portDefinition.PersistName;
					ArrayList assemblies = new ArrayList();

					if(IsDeviceInInitializeState(engine))
					{
						engine.ResumeExecution();
						Thread.Sleep(200);                    

						while(IsDeviceInInitializeState(engine))
						{
							VsPackage.MessageCentre.DeploymentMsg(DiagnosticStrings.WaitingInitialize);

							//need to break out of this or timeout or something?
							Thread.Sleep(200);
						}
					}
					var assms = engine.ResolveAllAssemblies();

					// find out which are the system assemblies
					// we will insert each system assemblies in a hash table where the value will be the assembly version
					foreach(var resolvedAssembly in assms)
					{
						VsPackage.MessageCentre.DeploymentMsg(String.Format(DiagnosticStrings.FoundAssembly, resolvedAssembly.m_reply.Name, resolvedAssembly.m_reply.m_version.ToString()));
						if((resolvedAssembly.m_reply.m_flags & Commands.Debugging_Resolve_Assembly.Reply.c_Deployed) == 0)
						{
							systemAssemblies[resolvedAssembly.m_reply.Name.ToLower()] = resolvedAssembly.m_reply.m_version;
						}
					}

					var refs = command.ReferencedAssemblies.ToList();

					refs.AddRange(Directory.GetFiles(command.OutputDirectory, "*.dll"));

					foreach(var reference in refs.ToArray())
						ResursiveAddReferences(refs, reference);

					refs.AddRange(Directory.GetFiles(command.OutputDirectory, "*.exe"));

					refs = refs.Distinct(new CompareAssemblyName()).ToList();

					string[] pes = GetDependencies(refs.ToArray(), true, true, engine.IsTargetBigEndian, "v4.3");
					string[] dlls = GetDependencies(refs.ToArray(), true, false, engine.IsTargetBigEndian, "v4.3");

					Debug.Assert(pes.Length == dlls.Length);

					// now we will re-deploy all system assemblies
					for(int i = 0; i < pes.Length; ++i)
					{
						string assemblyPath = pes[i];
						string dllPath = dlls[i];

						//is this a system assembly?
						string fileName = Path.ChangeExtension(Path.GetFileName(assemblyPath), null).ToLower();
						bool fDeployNewVersion = true;

						if(systemAssemblies.ContainsKey(fileName))
						{
							// get the version of the assembly on the device 
							var deviceVer = (Microsoft.SPOT.Debugger.WireProtocol.Commands.Debugging_Resolve_Assembly.Version)systemAssemblies[fileName];

							// get the version of the assembly of the project
							// We need to load the bytes for the assembly because other Load methods can override the path
							// with gac or recently used paths.  This is the only way we control the exact assembly that is loaded.
							byte[] asmData = null;

							using(FileStream sr = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
							{
								asmData = new byte[sr.Length];
								sr.Read(asmData, 0, (int)sr.Length);
							}

							var assm = Assembly.Load(asmData);
							var deployVer = assm.GetName().Version;

							// compare versions strictly, and deploy whatever assembly does not match the version on the device
							if(TargetFrameworkExactMatch(deviceVer, deployVer))
							{
								fDeployNewVersion = false;
							}
							else
							{
								//////////////////////////////////////////////// 
								//            !!! SPECIAL CASE !!!            //
								//                                            //
								// MSCORLIB cannot be deployed more than once //
								////////////////////////////////////////////////

								if(assm.GetName().Name.ToLower().Contains("mscorlib"))
								{
									fDeployNewVersion = false;
									//HACK!!!
									//This is because of bug in Mono, even if we do Assembly.Load("microFrameworkMSCorLib");
									//it actually loads Mono mscorlib which has version 4.0.0.0 instead of 4.3.1.0(or w/e version microframework is)
//									string message = string.Format("Cannot deploy the base assembly '{9}', or any of his satellite assemblies, to device - {0} twice. Assembly '{9}' on the device has version {1}.{2}.{3}.{4}, while the program is trying to deploy version {5}.{6}.{7}.{8} ", "DeployDeviceDescription", deviceVer.iMajorVersion, deviceVer.iMinorVersion, deviceVer.iBuildNumber, deviceVer.iRevisionNumber, deployVer.Major, deployVer.Minor, deployVer.Build, deployVer.Revision, assm.GetName().Name);
//									//TODO: SetDeployFailure(message);
//									return;
								}
							}
						}
						// append the assembly whose version does not match, or that still is not on the device, to the blob to deploy
						if(fDeployNewVersion)
						{
							newCommand = "/load:" + assemblyPath + " " + newCommand;
							using(FileStream fs = File.Open(assemblyPath, FileMode.Open, FileAccess.Read))
							{
								VsPackage.MessageCentre.DeploymentMsg(String.Format(DiagnosticStrings.AddingPEtoBundle, assemblyPath));
								long length = (fs.Length + 3) / 4 * 4;
								byte[] buf = new byte[length];

								fs.Read(buf, 0, (int)fs.Length);
								assemblies.Add(buf);
							}
						}
					}

					VsPackage.MessageCentre.DeploymentMsg("Attempting deployment...");

					if(!engine.Deployment_Execute(assemblies, false, VsPackage.MessageCentre.DeploymentMsg))
					{
						VsPackage.MessageCentre.DeploymentMsg(DiagnosticStrings.DeployFailed);
						//SetDeployFailure();
						return;
					}
					startInfo.Command = newCommand;
					engine.RebootDevice(Engine.RebootOption.RebootClrWaitForDebugger);
				}
				VsPackage.MessageCentre.Session = this;
				try
				{
					CorDebugProcess process = CorDebugProcess.CreateProcess(new DebugPortSupplier().FindPort("USB"), startInfo.Command);
					process.StartDebugging(this, false);
					// StartDebugging() will either get a connected device into a debuggable state and start the dispatch thread, or throw.
				}
				catch(ProcessExitException)
				{
					VsPackage.MessageCentre.DeploymentMsg(DiagnosticStrings.InitializeProcessFailedProcessDied);
				}
				catch(Exception ex)
				{
					VsPackage.MessageCentre.DeploymentMsg(DiagnosticStrings.InitializeProcessFailed);
					VsPackage.MessageCentre.InternalErrorMsg(false, ex.Message);
				}
			}
			catch(Exception e)
			{

			}
		}

		internal static bool TargetFrameworkExactMatch(Commands.Debugging_Resolve_Assembly.Version left, Version right)
		{
			if(left.iMajorVersion == right.Major &&
			   left.iMinorVersion == right.Minor &&
			   left.iBuildNumber == right.Build &&
			   left.iRevisionNumber == right.Revision)
			{
				return true;
			}

			return false;
		}

		protected override void OnAttachToProcess(long processId)
		{
			throw new NotImplementedException();
		}

		protected override void OnDetach()
		{
			process.Detach();
		}

		protected override void OnSetActiveThread(long processId, long threadId)
		{
			foreach(var p in processes)
			{
				if(p.Id == processId)
				{
					process = p;
					SetActiveThread(process.GetThread((uint)threadId));
					break;
				}
			}
		}

		CorDebugThread activeThread;
		CorDebugStepper stepper;

		void SetActiveThread(CorDebugThread t)
		{
			activeThread = t;
			stepper = new CorDebugStepper(activeThread.ActiveFrame);
			stepper.SetJmcStatus(true);
		}

		ProcessInfo GetProcess(CorDebugProcess proc)
		{
			ProcessInfo info;
			lock(processes)
			{
				if(!processesInfo.TryGetValue(proc.Id, out info))
				{
					info = new ProcessInfo(proc.Id, "");
					processesInfo[proc.Id] = info;
				}
			}
			return info;
		}

		protected override void OnStop()
		{
			foreach(var p in processes)
			{
				p.Stop();
			}
			var thread = process.GetAllNonVirtualThreads()[0];
			SetActiveThread(thread);
			OnTargetEvent(new TargetEventArgs(TargetEventType.TargetStopped) {
				Process = GetProcess(process),
				Thread = process.GetThread(thread),
				Backtrace = new Backtrace(new CorDebugBacktrace(thread, this))
			});

		}

		protected override void OnExit()
		{
		}

		protected override void OnStepLine()
		{
			Step(true);
		}

		protected override void OnNextLine()
		{
			Debug.WriteLine("WWW");
			Step(false);
		}

		void Step(bool into)
		{
			try
			{
				if(stepper != null)
				{
					//stepper.IsActive ();//no meaning?
					CorDebugFrame frame = activeThread.ActiveFrame;
					var reader = frame.Function.Assembly.DebugData;
					if(reader == null)
					{
						RawContinue(into);
						return;
					}


					var met = new MethodSymbols(new MetadataToken(frame.Function.Token));
					//Ugliest hack ever
					if(reader is Mono.Cecil.Mdb.MdbReader)
					{
						for(int i = 0; i < 100; i++)
							met.Variables.Add(new VariableDefinition(null));
					}
					reader.Read(met);

					if(met == null || met.Instructions.Count == 0)
					{
						RawContinue(into);
						return;
					}

					// Find the current line
					var range = new COR_DEBUG_STEP_RANGE();
					int currentLine = -1;
					foreach(var sp in met.Instructions)
					{
						if(sp.Offset > frame.IP)
						{
							if(currentLine == -1)
							{
								currentLine = sp.SequencePoint.StartLine;
								range.startOffset = frame.IP;
								range.endOffset = (uint)sp.Offset;
							}
							else
							{
								if(sp.SequencePoint.StartLine == currentLine)
								{
									range.endOffset = (uint)sp.Offset;
								}
								else
								{
									break;
								}
							}
						}
					}
					if(currentLine == -1)
					{
						RawContinue(into);
						return;
					}
					stepper.StepRange(into, new[] { range });

					ClearEvalStatus();
					process.Continue();
				}
			}
			catch(Exception e)
			{
				OnDebuggerOutput(true, e.ToString());
			}
		}

		private void RawContinue(bool into)
		{
			stepper.Step(into);
			ClearEvalStatus();
			process.Continue();
		}

		void ClearEvalStatus()
		{
//			foreach (CorDebugProcess p in processes) {
//				if (p.Id == processId) {
//					process = p;
//					break;
//				}
//			}
		}

		protected override void OnStepInstruction()
		{
			throw new NotImplementedException();
		}

		protected override void OnNextInstruction()
		{
			throw new NotImplementedException();
		}

		protected override void OnFinish()
		{
			throw new NotImplementedException();
		}

		protected override void OnContinue()
		{
			process.Continue();//TODO: This returns boolean...
		}

		class DocInfo
		{
			public DocInfo(string url)
			{
				Url = url;
			}

			public CorDebugAssembly Assembly
			{
				get;
				set;
			}

			public MethodSymbols GetClosestMethod(int line)
			{
				for(int i = 0; i < methods.Count; i++)
				{
					//Instead proper closest method we always put to upper method :D It's just that I want to see this working :)
					if(methods[i].startLine <= line && methods[i].endLine >= line)// && method.endLine <= line)
						return methods[i].symbols;
					if(methods[i].startLine > line)
					{
						if(i == 0)
							return methods[0].symbols;
						else
						{
							if((methods[i].startLine - line) < (line - methods[i - 1].endLine))
							{
								return methods[i].symbols;
							}
							else
							{
								return methods[i - 1].symbols;
							}
						}
					}
				}
				return methods.Last().symbols;
			}

			public string Url
			{
				get;
				private set;
			}

			class Method
			{
				public int startLine;
				public int endLine;
				public MethodSymbols symbols;
			}

			List<Method> methods = new List<Method>();
			//For now I'm asuming there is no overlapping...(Is it even posible?) Delegate implementation inside method?
			public void AddMethod(MethodDefinition m, MethodSymbols methodSymbols)
			{
				int startLine = int.MaxValue;
				int endLine = -1;
				foreach(var instr in methodSymbols.Instructions)
				{
					if(instr.SequencePoint.StartLine > 0 && instr.SequencePoint.StartLine < startLine)
						startLine = instr.SequencePoint.StartLine;
					if(instr.SequencePoint.EndLine < 1000000 && instr.SequencePoint.EndLine > endLine)
						endLine = instr.SequencePoint.EndLine;
				}
				for(int i = 0; i < methods.Count; i++)
				{
					if(startLine < methods[i].startLine)
					{
						methods.Insert(i, new Method {
							startLine = startLine,
							endLine = endLine,
							symbols = methodSymbols
						});
						return;
					}
				}
				methods.Add(new Method {
					startLine = startLine,
					endLine = endLine,
					symbols = methodSymbols
				});
			}
		}

		Dictionary<string,DocInfo> documents = new Dictionary<string, DocInfo>(StringComparer.CurrentCultureIgnoreCase);

		protected override BreakEventInfo OnInsertBreakEvent(BreakEvent breakEvent)
		{
			var binfo = new BreakEventInfo();

			var bp = breakEvent as Breakpoint;

			if(bp != null)
			{
				if(bp is FunctionBreakpoint)
				{
					// FIXME: implement breaking on function name
					binfo.SetStatus(BreakEventStatus.Invalid, null);
					return binfo;
				}
				else
				{
					DocInfo doc;
					if(!documents.TryGetValue(System.IO.Path.GetFullPath(bp.FileName), out doc))
					{
						binfo.SetStatus(BreakEventStatus.NotBound, "Cannot find source file in pdb/mdb debugging database.");
						return binfo;
					}

					MethodSymbols met = doc.GetClosestMethod(bp.Line);

					int offset = -1;
					foreach(var sp in met.Instructions)
					{
						if(sp.SequencePoint.StartLine == bp.Line)
						{
							offset = sp.Offset;
							break;
						}
					}
					if(offset == -1)
					{
						if(bp.Line < met.Instructions.First().SequencePoint.StartLine)
							offset = met.Instructions.First().Offset;
						else
							offset = met.Instructions.Last().Offset;
					}

					CorDebugFunction func = doc.Assembly.GetFunctionFromTokenCLR(met.MethodToken.ToUInt32());
					CorDebugFunctionBreakpoint corBp = func.ILCode.CreateBreakpoint((uint)offset);
					corBp.Active = bp.Enabled;

					binfo.Handle = corBp;
					binfo.SetStatus(BreakEventStatus.Bound, null);
					return binfo;
				}
			}

//			var cp = breakEvent as Catchpoint;
//			if (cp != null) {
//				foreach (ModuleInfo mod in modules.Values) {
//					CorMetadataImport mi = mod.Importer;
//					if (mi != null) {
//						foreach (Type t in mi.DefinedTypes)
//							if (t.FullName == cp.ExceptionName) {
//								binfo.SetStatus (BreakEventStatus.Bound, null);
//								return binfo;
//							}
//					}
//				}
//			}

			binfo.SetStatus(BreakEventStatus.Invalid, null);
			return binfo;
		}

		protected override void OnRemoveBreakEvent(BreakEventInfo eventInfo)
		{
			if(process == null)
				return;

			if(eventInfo.Status != BreakEventStatus.Bound || eventInfo.Handle == null)
				return;

			var corBp = (CorDebugFunctionBreakpoint)eventInfo.Handle;
			corBp.Active = false;
		}

		protected override void OnUpdateBreakEvent(BreakEventInfo eventInfo)
		{
			throw new NotImplementedException();
		}

		protected override void OnEnableBreakEvent(BreakEventInfo eventInfo, bool enable)
		{
			throw new NotImplementedException();
		}

		protected override ThreadInfo[] OnGetThreads(long processId)
		{
			foreach(var p in processes)
			{
				if(p.Id == processId)
				{
					process = p;
					break;
				}
			}
			if(process == null)
				return null;
			var threads = process.GetAllNonVirtualThreads();
			var result = new ThreadInfo[threads.Length];
			for(int i = 0; i < result.Length; i++)
			{
				result[i] = new ThreadInfo(processId, threads[i].Id, "", "");
			}
			return result;
		}

		protected override ProcessInfo[] OnGetProcesses()
		{
			var result = new ProcessInfo[processes.Count];
			for(int i = 0; i < result.Length; i++)
			{
				result[i] = new ProcessInfo(processes[i].Id, "MFProcess");
			}
			return result;
		}

		protected override Backtrace OnGetThreadBacktrace(long processId, long threadId)
		{
			foreach(var p in processes)
			{
				if(p.Id != processId)
					continue;
				foreach(CorDebugThread t in p.GetAllNonVirtualThreads())
				{
					if(t.Id == (uint)threadId)
					{
						return new Backtrace(new CorDebugBacktrace(t, this));
					}
				}
			}
			return null;
		}

		#region ICorDebugManagedCallback implementation

		int ICorDebugManagedCallback.Breakpoint(CorDebugAppDomain pAppDomain, CorDebugThread pThread, CorDebugBreakpointBase pBreakpoint)
		{
			TargetEventArgs args = new TargetEventArgs(TargetEventType.TargetStopped);
			args.Process = GetProcess(pAppDomain.Process);
			args.Thread = pAppDomain.Process.GetThread(pThread);
			args.Backtrace = new Backtrace(new CorDebugBacktrace(pThread, this));
			OnTargetEvent(args);
			SetActiveThread(pThread);
			return 0;
		}

		int ICorDebugManagedCallback.StepComplete(CorDebugAppDomain pAppDomain, CorDebugThread pThread, CorDebugStepper pStepper, CorDebugStepReason reason)
		{
			TargetEventArgs args = new TargetEventArgs(TargetEventType.TargetStopped);
			args.Process = GetProcess(pAppDomain.Process);
			args.Thread = pAppDomain.Process.GetThread(pThread);
			args.Backtrace = new Backtrace(new CorDebugBacktrace(pThread, this));
			OnTargetEvent(args);
			SetActiveThread(pThread);
			return 0;
		}

		int ICorDebugManagedCallback.Break(CorDebugAppDomain pAppDomain, CorDebugThread thread)
		{
			SetActiveThread(thread);
			return 0;
		}

		int ICorDebugManagedCallback.Exception(CorDebugAppDomain pAppDomain, CorDebugThread pThread, int unhandled)
		{
			SetActiveThread(pThread);
			return 0;
		}

		int ICorDebugManagedCallback.EvalComplete(CorDebugAppDomain pAppDomain, CorDebugThread pThread, CorDebugEval pEval)
		{
			EvaluationTimestamp++;
			return 0;
		}

		int ICorDebugManagedCallback.EvalException(CorDebugAppDomain pAppDomain, CorDebugThread pThread, CorDebugEval pEval)
		{
			EvaluationTimestamp++;
			return 0;
		}

		int ICorDebugManagedCallback.CreateProcess(CorDebugProcess pProcess)
		{
			OnStarted();
			pProcess.Continue();
			return 0;
		}

		int ICorDebugManagedCallback.ExitProcess(CorDebugProcess pProcess)
		{
			OnTargetEvent(new TargetEventArgs(TargetEventType.TargetExited));
			return 0;
		}

		int ICorDebugManagedCallback.CreateThread(CorDebugAppDomain pAppDomain, CorDebugThread thread)
		{
			pAppDomain.Process.Continue();
			return 0;
		}

		int ICorDebugManagedCallback.ExitThread(CorDebugAppDomain pAppDomain, CorDebugThread thread)
		{
			pAppDomain.Process.Continue();
			return 0;
		}

		int ICorDebugManagedCallback.LoadModule(CorDebugAppDomain pAppDomain, CorDebugAssembly pModule)
		{
			pAppDomain.Process.Continue();
			return 0;
		}

		int ICorDebugManagedCallback.UnloadModule(CorDebugAppDomain pAppDomain, CorDebugAssembly pModule)
		{
			return 0;
		}

		int ICorDebugManagedCallback.LoadClass(CorDebugAppDomain pAppDomain, CorDebugClass c)
		{
			return 0;
		}

		int ICorDebugManagedCallback.UnloadClass(CorDebugAppDomain pAppDomain, CorDebugClass c)
		{
			return 0;
		}

		int ICorDebugManagedCallback.DebuggerError(CorDebugProcess pProcess, int errorHR, uint errorCode)
		{
			return 0;
		}

		int ICorDebugManagedCallback.LogMessage(CorDebugAppDomain pAppDomain, CorDebugThread pThread, int lLevel, string pLogSwitchName, string pMessage)
		{
			OnDebuggerOutput(false, pMessage);
			pAppDomain.Process.Continue();
			return 0;
		}

		int ICorDebugManagedCallback.LogSwitch(CorDebugAppDomain pAppDomain, CorDebugThread pThread, int lLevel, uint ulReason, ref ushort pLogSwitchName, ref ushort pParentName)
		{
			return 0;
		}

		int ICorDebugManagedCallback.CreateAppDomain(CorDebugProcess pProcess, CorDebugAppDomain pAppDomain)
		{
			pProcess.Continue();
			return 0;
		}

		int ICorDebugManagedCallback.ExitAppDomain(CorDebugProcess pProcess, CorDebugAppDomain pAppDomain)
		{
			return 0;
		}

		List<CorDebugAssembly> assemblies = new List<CorDebugAssembly>();

		int ICorDebugManagedCallback.LoadAssembly(CorDebugAppDomain pAppDomain, CorDebugAssembly pAssembly)
		{
			assemblies.Add(pAssembly);

			//CorMetadataImport mi = new CorMetadataImport(pAssembly);

			//Seems like this is always set on MicroFramework
			//pAssembly. JITCompilerFlags = CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION;
			List<string> docPaths = new List<string>();
			if(pAssembly.DebugData != null)
			{
				var md = pAssembly.MetaData;
				var reader = pAssembly.DebugData;
				if(!pAssembly.IsFrameworkAssembly)
				{
					foreach(var module in md.Assembly.Modules)
					{
						foreach(var t in module.Types)
						{
							foreach(var m in t.Methods)
							{
								var methodSymbols = new MethodSymbols(m.MetadataToken);
								//Ugly hack
								if(reader is Mono.Cecil.Mdb.MdbReader)
								{
									foreach(var variable in m.Body.Variables)
										methodSymbols.Variables.Add(variable);
								}
								reader.Read(methodSymbols);
								if(methodSymbols.Instructions.Count == 0)
									continue;
								DocInfo document;
								if(!documents.TryGetValue(methodSymbols.Instructions[0].SequencePoint.Document.Url, out document))
								{
									document = new DocInfo(methodSymbols.Instructions[0].SequencePoint.Document.Url);
									document.Assembly = pAssembly;
									documents.Add(document.Url, document);
								}
								document.AddMethod(m, methodSymbols);
								if(!docPaths.Contains(document.Url))
									docPaths.Add(document.Url);
							}
						}
					}
				}
				pAssembly.SetJmcStatus(true);
			}
			else
			{
				// Flag modules without debug info as not JMC. In this way
				// the debugger won't try to step into them
				pAssembly.SetJmcStatus(false);
			}
			foreach(var docPath in docPaths)
				BindSourceFileBreakpoints(docPath);
			pAppDomain.Process.Continue();
			return 0;
		}

		int ICorDebugManagedCallback.UnloadAssembly(CorDebugAppDomain pAppDomain, CorDebugAssembly pAssembly)
		{
			assemblies.Remove(pAssembly);
			return 0;
		}

		int ICorDebugManagedCallback.ControlCTrap(CorDebugProcess pProcess)
		{
			return 0;
		}

		int ICorDebugManagedCallback.NameChange(CorDebugAppDomain pAppDomain, CorDebugThread pThread)
		{
			return 0;
		}

		int ICorDebugManagedCallback.EditAndContinueRemap(CorDebugAppDomain pAppDomain, CorDebugThread pThread, CorDebugFunction pFunction, int fAccurate)
		{
			return 0;
		}

		int ICorDebugManagedCallback.BreakpointSetError(CorDebugAppDomain pAppDomain, CorDebugThread pThread, CorDebugBreakpoint pBreakpoint, uint dwError)
		{
			return 0;
		}

		void ICorDebugManagedCallback.ExceptionUnwind(CorDebugAppDomain appDomain, CorDebugThread m_thread, CorDebugExceptionUnwindCallbackType m_type, int i)
		{
		}

		int ICorDebugManagedCallback.Exception(CorDebugAppDomain pAppDomain, CorDebugThread pThread, CorDebugFrame pFrame, uint nOffset, CorDebugExceptionCallbackType dwEventType, uint dwFlags)
		{
			return 0;
		}

		#endregion

		public static int EvaluationTimestamp
		{
			get;
			private set;
		}

		public List<CorDebugAssembly> GetModules()
		{
			return assemblies;
		}

		public CorDebugValue NewArray(CorEvaluationContext ctx, CorDebugType elemType, int size)
		{
			return null;
//			ManualResetEvent doneEvent = new ManualResetEvent (false);
//			CorDebugValue result = null;
//
//			EvalEventHandler completeHandler = delegate (object o, CorEvalEventArgs eargs) {
//				OnEndEvaluating ();
//				result = eargs.Eval.Result;
//				doneEvent.Set ();
//				eargs.Continue = false;
//			};
//
//			EvalEventHandler exceptionHandler = delegate (object o, CorEvalEventArgs eargs) {
//				OnEndEvaluating ();
//				result = eargs.Eval.Result;
//				doneEvent.Set ();
//				eargs.Continue = false;
//			};
//
//			try {
//				process.OnEvalComplete += completeHandler;
//				process.OnEvalException += exceptionHandler;
//
//				ctx.Eval.NewParameterizedArray (elemType, 1, 1, 0);
//				process.SetAllThreadsDebugState (CorDebugThreadState.THREAD_SUSPEND, ctx.Thread);
//				OnStartEvaluating ();
//				ClearEvalStatus ();
//				process.Continue (false);
//
//				if (doneEvent.WaitOne (ctx.Options.EvaluationTimeout, false))
//					return result;
//				else
//					return null;
//			} finally {
//				process.OnEvalComplete -= completeHandler;
//				process.OnEvalException -= exceptionHandler;
//			}
		}

		public CorDebugValue RuntimeInvoke(CorEvaluationContext corEvaluationContext, CorDebugFunction function, CorDebugType[] typeArgs, CorDebugValue thisObj, CorDebugValue[] arguments)
		{
			return null;
		}

		public void WaitUntilStopped()
		{

		}

		public override void Dispose()
		{
			foreach(var p in processes.ToArray())
				p.Dispose();
			ObjectAdapter.Dispose();
			ObjectAdapter = null;
			Breakpoints.Clear();
			processes.Clear();
			process = null;
			VsPackage.MessageCentre.Session = null;
			documents = null;
			stepper = null;
			processesInfo = null;
			base.Dispose();
		}
	}
}
