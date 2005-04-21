// <file>
//     <owner name="David Srbeck�" email="dsrbecky@post.cz"/>
// </file>

using System;
using System.Diagnostics.SymbolStore;
using System.Runtime.InteropServices;
using System.Threading;

using DebuggerInterop.Core;
using DebuggerInterop.MetaData;


namespace DebuggerLibrary
{
	public class Function
	{	
		string name;
		Module module;
		SymbolToken token;
		uint parentClassToken = 0;
		uint attributes;
		ICorDebugFrame    corFrame;
		ICorDebugFunction corFunction;		

		public string Name { 
			get { 
				return name; 
			} 
		}
		
		public Module Module { 
			get { 
				return module; 
			} 
		}
	
		internal unsafe Function(ICorDebugFrame corFrame) 
		{
			this.corFrame = corFrame;
			corFrame.GetFunction(out corFunction);
            uint functionToken;
			corFunction.GetToken(out functionToken);
            this.token = new SymbolToken((int)functionToken);
			ICorDebugModule corModule;
			corFunction.GetModule(out corModule);
			module = NDebugger.Instance.GetModule(corModule);

			Init();
		}
		
		unsafe void Init()
		{	
			uint codeRVA;
			uint implFlags;
			IntPtr pSigBlob;
			uint sigBlobSize;
			module.MetaDataInterface.GetMethodProps(
			                              (uint)token.GetToken(),
                                          out parentClassToken,
                                          NDebugger.pString,
                                          NDebugger.pStringLen,
                                          out NDebugger.unused, // real string lenght
                                          out attributes,
                                          new IntPtr(&pSigBlob),
                                          out sigBlobSize,
                                          out codeRVA,
                                          out implFlags);
			name = NDebugger.pStringAsUnicode;
			
			SignatureStream sig = new SignatureStream(pSigBlob, sigBlobSize);
			
			//Marshal.FreeCoTaskMem(pSigBlob);
		}


		#region Helpping proprerties

		internal ICorDebugILFrame corILFrame {
			get	{
				return (ICorDebugILFrame) corFrame;
			}
		}

		internal uint corInstructionPtr {
			get	{
				uint corInstructionPtr;
				CorDebugMappingResult MappingResult;
				corILFrame.GetIP(out corInstructionPtr,out MappingResult);
				return corInstructionPtr;
			}
		}
		
		// Helpping properties for symbols

		internal ISymbolReader symReader {
			get	{
				if (module.SymbolsLoaded == false) throw new SymbolsNotAviableException();
				if (module.SymReader == null) throw new SymbolsNotAviableException();
				return module.SymReader;
			}
		}

		internal ISymbolMethod symMethod {
			get	{
				return symReader.GetMethod(token);
			}
		}

		#endregion

		public void StepInto()
		{
			Step(true);
		}		

		public void StepOver()
		{
			Step(false);
		}

		public void StepOut()
		{
			ICorDebugStepper stepper;
			corFrame.CreateStepper(out stepper);
			stepper.StepOut();

			NDebugger.Continue();
		}

		private unsafe void Step(bool stepIn)
		{
			if (Module.SymbolsLoaded == false) {
				System.Diagnostics.Debug.Fail("Unable to step. No symbols loaded.");
				return;
			}

			SourcecodeSegment nextSt;
			try {
				nextSt = NextStatement;// Cache
			} catch (NextStatementNotAviableException) {
				System.Diagnostics.Debug.Fail("Unable to step. Next statement not aviable");
				return;
			}			

			ICorDebugStepper stepper;
			corFrame.CreateStepper(out stepper);

			//uint rangeCount;
			//symMethod.GetRanges(nextSt.SymbolDocument, nextSt.StartLine, 0, 0, out rangeCount, IntPtr.Zero);
			//IntPtr pRanges = Marshal.AllocHGlobal(4*(int)rangeCount);
			//symMethod.GetRanges(nextSt.SymbolDocument, nextSt.StartLine, 0, rangeCount, out rangeCount, pRanges);

            int[] ranges = symMethod.GetRanges(nextSt.SymbolDocument, nextSt.StartLine, 0);
            IntPtr pRanges = Marshal.AllocHGlobal(4*ranges.Length);
            for (int i = 0; i < ranges.Length; i++) {
                ((int*)pRanges.ToPointer())[i] = ranges[i];
            }

			stepper.StepRange(stepIn?1:0, pRanges, (uint)ranges.Length);
			Marshal.FreeHGlobal(pRanges);

            

			NDebugger.Continue();
		}
		
		
		public SourcecodeSegment NextStatement {
			get {
				SourcecodeSegment retVal = new SourcecodeSegment();

				ISymbolMethod symMethod;
				try {
					symMethod = this.symMethod; 
				}
				catch (FrameNotAviableException) {
					throw new NextStatementNotAviableException();
				}
				catch (SymbolsNotAviableException) {
					throw new NextStatementNotAviableException();
				}

                int sequencePointCount = symMethod.SequencePointCount;
				
				int[] offsets     = new int[sequencePointCount];
				int[] startLine   = new int[sequencePointCount];
				int[] startColumn = new int[sequencePointCount];
				int[] endLine     = new int[sequencePointCount];
				int[] endColumn   = new int[sequencePointCount];
				
				ISymbolDocument[] Doc = new ISymbolDocument[sequencePointCount];
				
				symMethod.GetSequencePoints(
					offsets,
					Doc,
					startLine,
					startColumn,
					endLine,
					endColumn
					);

				uint corInstructionPtr = this.corInstructionPtr; // cache

				for (int i = sequencePointCount - 1; i >= 0; i--) // backwards
					if (offsets[i] <= corInstructionPtr)
					{
						// 0xFeeFee means "code generated by compiler"
						if (startLine[i] == 0xFeeFee) throw new NextStatementNotAviableException();

						retVal.SymbolDocument = Doc[i];

						retVal.SourceFullFilename = retVal.SymbolDocument.URL;
						
						retVal.ModuleFilename = module.FullPath;

						retVal.StartLine   = startLine[i];
						retVal.StartColumn = startColumn[i];
						retVal.EndLine     = endLine[i];
						retVal.EndColumn   = endColumn[i];
						return retVal;
					}			
				throw new NextStatementNotAviableException();
			}
		}
		
		public VariableCollection LocalVariables { 
			get{
				return GetLocalVariables();
			} 
		}

		private unsafe VariableCollection GetLocalVariables()
		{				
			VariableCollection collection = new VariableCollection();
			try {				
			// parent class variables
				ICorDebugClass corClass;
				corFunction.GetClass(out corClass);
				bool isStatic = (attributes&(uint)CorMethodAttr.mdStatic) != 0;
				ICorDebugValue argThis = null;
				if (!isStatic) {
					corILFrame.GetArgument(0, out argThis);
				}
				collection = new ObjectVariable(argThis, "this", corClass).SubVariables;
				
			// arguments
				ICorDebugValueEnum corValueEnum;
				corILFrame.EnumerateArguments(out corValueEnum);
				uint argCount;
				corValueEnum.GetCount(out argCount);
					
				IntPtr paramEnumPtr = IntPtr.Zero;
				uint paramsFetched;
				for (uint i = (uint)(isStatic?0:1); i < argCount; i++) {
					uint paramToken;
					Module.MetaDataInterface.EnumParams(ref paramEnumPtr , (uint)token.GetToken(), out paramToken, 1, out paramsFetched);
					if (paramsFetched == 0) break;
					
					ICorDebugValue arg;
					corILFrame.GetArgument(i, out arg);					
					
					uint argPos, attr, type;
					Module.MetaDataInterface.GetParamProps(
					                                       paramToken,
					                                       out NDebugger.unused,
					                                       out argPos,
					                                       NDebugger.pString,
					                                       NDebugger.pStringLen,
					                                       out NDebugger.unused, // real string lenght
					                                       out attr,
					                                       out type,
					                                       IntPtr.Zero,
					                                       out NDebugger.unused);
					

					collection.Add(VariableFactory.CreateVariable(arg, NDebugger.pStringAsUnicode));
				}
				
			// local variables
				ISymbolScope symRootScope;
				symRootScope = symMethod.RootScope;
				AddScopeToVariableCollection(symRootScope, ref collection);
				
			// Properties
				/*
				IntPtr methodEnumPtr = IntPtr.Zero;
				uint methodsFetched;
				while(true) {
					uint methodToken;
					Module.MetaDataInterface.EnumMethods(ref methodEnumPtr, parentClassToken, out methodToken, 1, out methodsFetched);
					if (methodsFetched == 0) break;
										
					uint attrib;
					module.MetaDataInterface.GetMethodProps(
					                              methodToken,
		                                          out NDebugger.unused,
		                                          NDebugger.pString,
		                                          NDebugger.pStringLen,
		                                          out NDebugger.unused, // real string lenght
		                                          out attrib,
		                                          IntPtr.Zero,
		                                          out NDebugger.unused,
		                                          out NDebugger.unused,
		                                          out NDebugger.unused);
					string name = NDebugger.pStringAsUnicode;
					if (name.StartsWith("get_") && (attrib & (uint)CorMethodAttr.mdSpecialName) != 0) {
						name = "Prop:" + name;
						
						ICorDebugValue[] evalArgs;
						ICorDebugFunction evalCorFunction;
						Module.CorModule.GetFunctionFromToken(methodToken, out evalCorFunction);
						if (isStatic) {
							evalArgs = new ICorDebugValue[0];
						} else {
							evalArgs = new ICorDebugValue[] {argThis};
						}
						Eval eval = new Eval(evalCorFunction, evalArgs);
						EvalQueue.AddEval(eval);
						collection.Add(new PropertyVariable(eval, name));
					}
				}
				*/
			} 
			catch (FrameNotAviableException) {
				System.Diagnostics.Debug.Fail("Unable to get local variables. Frame is not aviable");
			}
			catch (SymbolsNotAviableException) {
				System.Diagnostics.Debug.Fail("Unable to get local variables. Symbols are not aviable");
			}
			return collection;
		}

		private unsafe void AddScopeToVariableCollection(ISymbolScope symScope, ref VariableCollection collection)
		{
			foreach(ISymbolScope childScope in symScope.GetChildren()) {
				AddScopeToVariableCollection(childScope, ref collection);
			}
			AddVariablesToVariableCollection(symScope, ref collection);
		}

		private unsafe void AddVariablesToVariableCollection(ISymbolScope symScope, ref VariableCollection collection)
		{
			foreach (ISymbolVariable symVar in symScope.GetLocals()) {
				AddVariableToVariableCollection(symVar , ref collection);
			}
		}

		private unsafe void AddVariableToVariableCollection(ISymbolVariable symVar, ref VariableCollection collection)
		{
			ICorDebugValue runtimeVar;
			corILFrame.GetLocalVariable((uint)symVar.AddressField1, out runtimeVar);
			collection.Add(VariableFactory.CreateVariable(runtimeVar, symVar.Name));
		}
	}
}
