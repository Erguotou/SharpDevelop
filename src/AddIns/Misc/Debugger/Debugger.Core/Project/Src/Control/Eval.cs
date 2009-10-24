﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="David Srbecký" email="dsrbecky@gmail.com"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Debugger.MetaData;
using Debugger.Wrappers.CorDebug;
using ICSharpCode.NRefactory.Ast;

namespace Debugger
{
	public enum EvalState {
		Evaluating,
		EvaluatedSuccessfully,
		EvaluatedException,
		EvaluatedNoResult,
		EvaluatedTimeOut,
	};
	
	/// <summary>
	/// This class holds information about function evaluation.
	/// </summary>
	public class Eval: DebuggerObject
	{
		delegate void EvalStarter(Eval eval);
		
		AppDomain     appDomain;
		Process       process;
		
		string        description;
		ICorDebugEval corEval;
		Value         result;
		EvalState     state;
		
		public AppDomain AppDomain {
			get { return appDomain; }
		}
		
		public Process Process {
			get { return process; }
		}
		
		public string Description {
			get { return description; }
		}
		
		ICorDebugEval CorEval {
			get { return corEval; }
		}

	    /// <exception cref="GetValueException">Evaluating...</exception>
	    public Value Result {
			get {
				switch(this.State) {
					case EvalState.Evaluating:            throw new GetValueException("Evaluating...");
					case EvalState.EvaluatedSuccessfully: return result;
					case EvalState.EvaluatedException:    return result;
					case EvalState.EvaluatedNoResult:     return null;
					case EvalState.EvaluatedTimeOut:      throw new GetValueException("Timeout");
					default: throw new DebuggerException("Unknown state");
				}
			}
		}
		
		public EvalState State {
			get { return state; }
		}
		
		public bool Evaluated {
			get {
				return state == EvalState.EvaluatedSuccessfully ||
				       state == EvalState.EvaluatedException ||
				       state == EvalState.EvaluatedNoResult ||
				       state == EvalState.EvaluatedTimeOut;
			}
		}
		
		Eval(AppDomain appDomain, string description, EvalStarter evalStarter)
		{
			this.appDomain = appDomain;
			this.process = appDomain.Process;
			this.description = description;
			this.state = EvalState.Evaluating;
			
			this.corEval = CreateCorEval(appDomain);
			
			try {
				evalStarter(this);
			} catch (COMException e) {
				if ((uint)e.ErrorCode == 0x80131C26) {
					throw new GetValueException("Can not evaluate in optimized code");
				} else if ((uint)e.ErrorCode == 0x80131C28) {
					throw new GetValueException("Object is in wrong AppDomain");
				} else if ((uint)e.ErrorCode == 0x8013130A) {
					// Happens on getting of Sytem.Threading.Thread.ManagedThreadId; See SD2-1116
					throw new GetValueException("Function does not have IL code");
				} else if ((uint)e.ErrorCode == 0x80131C23) {
					// The operation failed because it is a GC unsafe point. (Exception from HRESULT: 0x80131C23)
					// This can probably happen when we break and the thread is in native code
					throw new GetValueException("Thread is in GC unsafe point");
				} else if ((uint)e.ErrorCode == 0x80131C22) {
					// The operation is illegal because of a stack overflow.
					throw new GetValueException("Can not evaluate after stack overflow");
				} else {
					throw;
				}
			}
			
			appDomain.Process.ActiveEvals.Add(this);
			appDomain.Process.AsyncContinue(DebuggeeStateAction.Keep);
		}

	    static ICorDebugEval CreateCorEval(AppDomain appDomain)
		{
			appDomain.Process.AssertPaused();
			
			// TODO: Select thread in the correct AppDomain
			Thread targetThread = appDomain.Process.SelectedThread;
			
			if (targetThread == null) {
				throw new GetValueException("Can not evaluate because no thread is selected");
			}
			if (targetThread.IsMostRecentStackFrameNative) {
				throw new GetValueException("Can not evaluate because native frame is on top of stack");
			}
			if (!targetThread.IsAtSafePoint) {
				throw new GetValueException("Can not evaluate because thread is not at a safe point");
			}
			if (targetThread.Suspended) {
				throw new GetValueException("Can not evaluate on suspended thread");
			}
			
			return targetThread.CorThread.CreateEval();
		}

		internal bool IsCorEval(ICorDebugEval corEval)
		{
			return this.corEval == corEval;
		}

	    /// <exception cref="DebuggerException">Evaluation can not be stopped</exception>
	    /// <exception cref="GetValueException">Process exited</exception>
	    Value WaitForResult()
		{
			// Note that aborting is not supported for suspended threads
			try {
				process.WaitForPause(TimeSpan.FromMilliseconds(500));
				if (!Evaluated) {
					state = EvalState.EvaluatedTimeOut;
					process.TraceMessage("Aboring eval: " + Description);
					corEval.Abort();
					process.WaitForPause(TimeSpan.FromMilliseconds(500));
					if (!Evaluated) {
						process.TraceMessage("Rude aboring eval: " + Description);
						corEval.CastTo<ICorDebugEval2>().RudeAbort();
						process.WaitForPause(TimeSpan.FromMilliseconds(500));
						if (!Evaluated) {
							throw new DebuggerException("Evaluation can not be stopped");
						}
					}
				}
				process.WaitForPause();
				return this.Result;
			} catch (ProcessExitedException) {
				throw new GetValueException("Process exited");
			}
		}
		
		internal void NotifyEvaluationComplete(bool successful) 
		{
			// Eval result should be ICorDebugHandleValue so it should survive Continue()
			if (state == EvalState.EvaluatedTimeOut) {
				return;
			}
			if (corEval.Result == null) {
				state = EvalState.EvaluatedNoResult;
			} else {
				if (successful) {
					state = EvalState.EvaluatedSuccessfully;
				} else {
					state = EvalState.EvaluatedException;
				}
				result = new Value(AppDomain, corEval.Result);
			}
		}
		
		/// <summary> Synchronously calls a function and returns its return value </summary>
		public static Value InvokeMethod(DebugMethodInfo method, Value thisValue, Value[] args)
		{
			if (method.BackingField != null) {
				method.Process.TraceMessage("Using backing field for " + method.FullName);
				return Value.GetMemberValue(thisValue, method.BackingField, args);
			}
			return AsyncInvokeMethod(method, thisValue, args).WaitForResult();
		}
		
		public static Eval AsyncInvokeMethod(DebugMethodInfo method, Value thisValue, Value[] args)
		{
			return new Eval(
				method.AppDomain,
				"Function call: " + method.FullName,
				delegate(Eval eval) {
					MethodInvokeStarter(eval, method, thisValue, args);
				}
			);
		}

	    /// <exception cref="GetValueException"><c>GetValueException</c>.</exception>
	    static void MethodInvokeStarter(Eval eval, DebugMethodInfo method, Value thisValue, Value[] args)
		{
			List<ICorDebugValue> corArgs = new List<ICorDebugValue>();
			args = args ?? new Value[0];
			if (args.Length != method.ParameterCount) {
				throw new GetValueException("Invalid parameter count");
			}
			if (thisValue != null) {
				// if (!(thisValue.IsObject)) // eg Can evaluate on array
				if (!method.DeclaringType.IsInstanceOfType(thisValue)) {
					throw new GetValueException(
						"Can not evaluate because the object is not of proper type.  " + 
						"Expected: " + method.DeclaringType.FullName + "  Seen: " + thisValue.Type.FullName
					);
				}
				corArgs.Add(thisValue.CorValue);
			}
			for(int i = 0; i < args.Length; i++) {
				// It is importatnt to pass the parameted in the correct form (boxed/unboxed)
				if (method.GetParameters()[i].ParameterType.IsValueType) {
					corArgs.Add(args[i].CorGenericValue.CastTo<ICorDebugValue>());
				} else {
					if (args[i].Type.IsValueType) {
						corArgs.Add(args[i].Box().CorValue);
					} else {
						corArgs.Add(args[i].CorValue);
					}
				}
			}
			
			ICorDebugType[] genericArgs = ((DebugType)method.DeclaringType).GenericArgumentsAsCorDebugType;
			eval.CorEval.CastTo<ICorDebugEval2>().CallParameterizedFunction(
				method.CorFunction,
				(uint)genericArgs.Length, genericArgs,
				(uint)corArgs.Count, corArgs.ToArray()
			);
		}
	    
	    public static Value CreateValue(AppDomain appDomain, object value)
	    {
	    	if (value == null) {
				ICorDebugClass corClass = DebugType.CreateFromType(appDomain, typeof(object)).CorType.Class;
				ICorDebugEval corEval = CreateCorEval(appDomain);
				ICorDebugValue corValue = corEval.CreateValue((uint)CorElementType.CLASS, corClass);
				return new Value(appDomain, corValue);
			} else if (value is string) {
	    		return Eval.NewString(appDomain, (string)value);
			} else {
	    		// TODO: Check if it is primitive type
				Value val = Eval.NewObjectNoConstructor(DebugType.CreateFromType(appDomain, value.GetType()));
				val.PrimitiveValue = value;
				return val;
			}
	    }
		
	    /*
		// The following function create values only for the purpuse of evalutaion
		// They actually do not allocate memory on the managed heap
		// The advantage is that it does not continue the process
	    /// <exception cref="DebuggerException">Can not create string this way</exception>
	    public static Value CreateValue(Process process, object value)
		{
			if (value is string) throw new DebuggerException("Can not create string this way");
			CorElementType corElemType;
			ICorDebugClass corClass = null;
			if (value != null) {
				corElemType = DebugType.TypeNameToCorElementType(value.GetType().FullName);
			} else {
				corElemType = CorElementType.CLASS;
				corClass = DebugType.Create(process, null, typeof(object).FullName).CorType.Class;
			}
			ICorDebugEval corEval = CreateCorEval(process);
			ICorDebugValue corValue = corEval.CreateValue((uint)corElemType, corClass);
			Value v = new Value(process, new Expressions.PrimitiveExpression(value), corValue);
			if (value != null) {
				v.PrimitiveValue = value;
			}
			return v;
		}
		*/
		
		#region Convenience methods
		
		public static Value NewString(AppDomain appDomain, string textToCreate)
		{
			return AsyncNewString(appDomain, textToCreate).WaitForResult();
		}
		
		#endregion
		
		public static Eval AsyncNewString(AppDomain appDomain, string textToCreate)
		{
			return new Eval(
				appDomain,
				"New string: " + textToCreate,
				delegate(Eval eval) {
					eval.CorEval.CastTo<ICorDebugEval2>().NewStringWithLength(textToCreate, (uint)textToCreate.Length);
				}
			);
		}
		
		#region Convenience methods
		
		public static Value NewObject(DebugType debugType, Value[] constructorArguments, DebugType[] constructorArgumentsTypes)
		{
			return AsyncNewObject(debugType, constructorArguments, constructorArgumentsTypes).WaitForResult();
		}
		
		public static Value NewObjectNoConstructor(DebugType debugType)
		{
			return AsyncNewObjectNoConstructor(debugType).WaitForResult();
		}
		
		#endregion
		
		public static Eval AsyncNewObject(DebugType debugType, Value[] constructorArguments, DebugType[] constructorArgumentsTypes)
		{
			ICorDebugValue[] constructorArgsCorDebug = ValuesAsCorDebug(constructorArguments);
			DebugMethodInfo constructor = (DebugMethodInfo)debugType.GetMethod(".ctor", constructorArgumentsTypes);
			if (constructor == null) {
				throw new DebuggerException(string.Format("Type {0} has no constructor overload with given argument types.", debugType.FullName));
			}
			return new Eval(
				debugType.AppDomain,
				"New object: " + debugType.FullName,
				delegate(Eval eval) {
					eval.CorEval.CastTo<ICorDebugEval2>().NewParameterizedObject(
						constructor.CorFunction, (uint)debugType.GetGenericArguments().Length, debugType.GenericArgumentsAsCorDebugType,
						(uint)constructorArgsCorDebug.Length, constructorArgsCorDebug);
				}
			);
		}
		
		public static Eval AsyncNewObjectNoConstructor(DebugType debugType)
		{
			return new Eval(
				debugType.AppDomain,
				"New object: " + debugType.FullName,
				delegate(Eval eval) {
					eval.CorEval.CastTo<ICorDebugEval2>().NewParameterizedObjectNoConstructor(debugType.CorType.Class, (uint)debugType.GetGenericArguments().Length, debugType.GenericArgumentsAsCorDebugType);
				}
			);
		}
		
		static ICorDebugValue[] ValuesAsCorDebug(Value[] values)
		{
			ICorDebugValue[] valuesAsCorDebug = new ICorDebugValue[values.Length];
			for(int i = 0; i < values.Length; i++) {
				valuesAsCorDebug[i] = values[i].CorValue;
			}
			return valuesAsCorDebug;
		}
	}
}
