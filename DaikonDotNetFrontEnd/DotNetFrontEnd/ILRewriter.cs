//-----------------------------------------------------------------------------
//
// Copyright (c) Microsoft, Kellen Donohue. All rights reserved.
// This code is licensed under the Microsoft Public License.
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Cci;
using Microsoft.Cci.MutableCodeModel;

// This file and ProgramRewriter.cs originally came from the CCIMetadata (http://ccimetadata.codeplex.com/)
// sample programs. Specifically, it was Samples/ILMutator/ILMutator.cs. The original code inserted
// a System.WriteLine call for each store to a local variable defined by the programmer. It has been
// modified to insert the call to the instrumentation function at the beginning and exit of every
// method. This is mostly done in the ProcessOperations() method, with many support methods added.

namespace DotNetFrontEnd
{
  /// <summary>
  /// A mutator that modifies method bodies at the IL level.
  /// It injects a call to a visitor function for each function argument and class field and the
  /// beginning and end of every method.
  /// </summary>
  public class ILRewriter : MetadataRewriter, IDisposable
  {
    #region Public Constants

    /// <summary>
    /// The name of the class storing the DNFE arguments when the program is run in offline mode.
    /// </summary>
    public static readonly string ArgumentStoringClassName = "DNFE_ArgumentStoringClass";

    /// <summary>
    /// The name of the method storing the DNFE arguments when the program is run in offline mode.
    /// </summary>
    public static readonly string ArgumentStoringMethodName = "DNFE_ArgumentStroingMethod";

    #endregion

    #region Constants

    /// <summary>
    /// Name of the variable indicating the exception thrown at the exit of a method
    /// </summary>
    private static readonly string ExceptionVariableName = "exception";

    /// <summary>
    /// Program point suffix (appears after method name) for the runtime exception exit
    /// </summary>
    private static readonly string RuntimeExceptionExitProgamPointSuffix = "_EX_Runtime";

    /// <summary>
    /// Suffix to appear at the end of the exception-specific exceptional program point exit,
    /// before the exception name
    /// </summary>
    private static readonly string ExceptionExitSuffix = "_EX_";

    /// <summary>
    /// The string that ends every constructor
    /// </summary>
    private static readonly string ConstructorSuffix = "ctor";

    #endregion

    #region Private Members

    private AssemblyIdentity assemblyIdentity;

    // Convenience references
    private INameTable nameTable;
    private INamespaceTypeReference systemString;
    private INamespaceTypeReference systemVoid;
    private INamespaceTypeReference systemObject;

    // DNFE-specific variables
    private ITypeDefinition variableVisitorType;
    private DotNetFrontEnd.DeclarationPrinter declPrinter;
    private DotNetFrontEnd.FrontEndArgs frontEndArgs;
    private DotNetFrontEnd.TypeManager typeManager;

    // Variables used during rewriting
    PdbReader/*?*/ pdbReader = null;
    ILGenerator generator;
    IEnumerator<ILocalScope>/*?*/ scopeEnumerator;
    bool scopeEnumeratorIsValid;
    Stack<ILocalScope> scopeStack = new Stack<ILocalScope>();

    /// <summary>
    /// Reference to the VariableVisitor method that loads assembly name and path
    /// </summary>
    private Microsoft.Cci.MethodReference argumentStoringMethodReference;

    /// <summary>
    /// Reference to the VariableVisitor method that will write the program point name
    /// </summary>
    private Microsoft.Cci.MethodReference programPointWriterMethod;

    /// <summary>
    /// Reference to the VariableVisitor method that will print the invocation nonce
    /// </summary>
    private Microsoft.Cci.MethodReference invocationNonceWriterMethod;

    /// <summary>
    /// Reference to the VariableVisitor method that will set the invocation nonce
    /// </summary>
    private Microsoft.Cci.MethodReference invocationNonceSetterMethod;

    /// <summary>
    /// True if we are printing declarations, otherwise false
    /// </summary>
    private bool printDeclarations;

    /// <summary>
    /// Map from method to the nonce for that method
    /// </summary>
    private Dictionary<IMethodDefinition, LocalDefinition> nonceVariableDictionary;

    /// <summary>
    /// The type holding the arguments needed when assembly is run in offline mode
    /// </summary>
    private ITypeDefinition argumentStoringType;

    #endregion

    private enum MethodTransition
    {
      ENTER,
      EXIT,
    }

    #region CCI Code

    public ILRewriter(IMetadataHost host, PdbReader pdbReader, DotNetFrontEnd.FrontEndArgs frontEndArgs,
        DotNetFrontEnd.TypeManager typeManager)
      : base(host)
    {
      this.pdbReader = pdbReader;
      if (host == null)
      {
        throw new ArgumentNullException("host");
      }
      this.frontEndArgs = frontEndArgs;
      this.typeManager = typeManager;
      this.nameTable = this.host.NameTable;

      this.systemString = host.PlatformType.SystemString;
      this.systemVoid = host.PlatformType.SystemVoid;
      this.systemObject = host.PlatformType.SystemObject;

      this.nonceVariableDictionary = new Dictionary<IMethodDefinition, LocalDefinition>();
    }

    /// <summary>
    /// Exceptions thrown during extraction. Should not escape this class.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design",
      "CA1064:ExceptionsShouldBePublic"), Serializable]
    private class ILMutatorException : Exception
    {
      /// <summary>
      /// Exception specific to an error occurring in the contract extractor
      /// </summary>
      public ILMutatorException() { }
      /// <summary>
      /// Exception specific to an error occurring in the contract extractor
      /// </summary>
      public ILMutatorException(string s) : base(s) { }
      /// <summary>
      /// Exception specific to an error occurring in the contract extractor
      /// </summary>
      /// Exception exists from CCI code, leave it for now in case they every use it
      [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance",
        "CA1811:AvoidUncalledPrivateCode")]
      public ILMutatorException(string s, Exception inner) : base(s, inner) { }
      /// <summary>
      /// Exception specific to an error occurring in the contract extractor
      /// </summary>
      protected ILMutatorException(SerializationInfo info, StreamingContext context) :
        base(info, context) { }
    }

    /// <summary>
    /// Mutate the method body by adding the instrumentation calls.
    /// </summary>
    /// <param name="methodBody">The original methodBody</param>
    /// <returns>methodBody with instrumentation calls added</returns>
    public override IMethodBody Rewrite(IMethodBody methodBody)
    {
      if (methodBody == null)
      {
        throw new ArgumentNullException("methodBody");
      }

      var method = methodBody.MethodDefinition;
      var containingType = method.ContainingType;

      // If the method is compiler generated don't insert instrumentation code.
      if (typeManager.IsTypeCompilerGenerated(containingType.ResolvedType) ||
          typeManager.IsMethodCompilerGenerated(method) ||
        // Don't add instrumentation code to Pure methods
          typeManager.GetPureMethodsForType(containingType).Select(m => m.Value.Name).Contains(method.Name.Value) ||
          !frontEndArgs.ShouldPrintProgramPoint(FormatMethodName(methodBody.MethodDefinition)))
      {
        return base.Rewrite(methodBody);
      }
      else
      {
        return ProcessOperations(methodBody);
      }
    }

    #endregion

    /// <summary>
    /// Process a method, right now this involves emitting the instrumentation code.
    /// </summary>
    /// <param name="immutableMethodBody">Describes the method to instrument</param>
    private IMethodBody ProcessOperations(IMethodBody immutableMethodBody)
    {
      // Alias operations for convenience
      List<IOperation> operations = ((immutableMethodBody.Operations == null)
        ? new List<IOperation>()
        : new List<IOperation>(immutableMethodBody.Operations));

      // There may be many nops before the ret instruction, we want to catch anything
      // branching into the last return, so we need to catch anything branching into these
      // nops.
      int operationIndexForFirstReturnJumpTarget = GetIndexOfFirstReturnJumpTarget(operations);

      this.generator = new ILGenerator(this.host, immutableMethodBody.MethodDefinition);
      if (this.pdbReader != null)
      {
        foreach (var ns in this.pdbReader.GetNamespaceScopes(immutableMethodBody))
        {
          foreach (var uns in ns.UsedNamespaces)
            generator.UseNamespace(uns.NamespaceName.Value);
        }
      }

      this.scopeEnumerator = this.pdbReader == null ?
          null : this.pdbReader.GetLocalScopes(immutableMethodBody).GetEnumerator();
      this.scopeEnumeratorIsValid = this.scopeEnumerator != null && this.scopeEnumerator.MoveNext();

      // Need the method body to be mutable to add the nonce variable
      MethodBody mutableMethodBody = (MethodBody)immutableMethodBody;

      // Add a nonce-holding variable and set it.
      mutableMethodBody.LocalVariables = CreateNonceVariable(mutableMethodBody);
      SetNonce(immutableMethodBody.MethodDefinition);
      mutableMethodBody.LocalsAreZeroed = true;

      // We will need to add at least 2, and possibly 6 items onto
      // the stack at the end to print values.
      mutableMethodBody.MaxStack = Math.Max((ushort)6, mutableMethodBody.MaxStack);

      immutableMethodBody = (IMethodBody)mutableMethodBody;

      // We need the writer lock to emit the values of method parameters.
      AcquireWriterLock();
      EmitMethodSignature(MethodTransition.ENTER, immutableMethodBody);
      ReleaseWriterLock();

      // The label that early returns should jump to, exists inside the try block that
      // contains the whole method
      ILGeneratorLabel commonExit = new ILGeneratorLabel();

      // Generate branch target map.
      Dictionary<uint, ILGeneratorLabel> offset2Label =
          LabelBranchTargets(operations, operationIndexForFirstReturnJumpTarget, commonExit);

      // Constructors need to initialize the object before the try body starts.
      if (!immutableMethodBody.MethodDefinition.IsConstructor &&
          !IsConstructorWithNoObjectConstructorCall(immutableMethodBody))
      {
        generator.BeginTryBody();
      }

      // Whether we've started the try body the whole method will be wrapped in
      bool tryBodyStarted = true;

      HashSet<uint> offsetsUsedInExceptionInformation =
          RecordExceptionHandlerOffsets(immutableMethodBody);

      List<ITypeReference> exceptions = DetermineAndSortExceptions(operations);

      // If the method is non-void, return in debug builds will contain a store before a jump to
      // the return point. Figure out what the store will be to detect these returns later.
      ISet<IOperation> synthesizedReturns = DetermineSynthesizedReturns(
        immutableMethodBody, operations, 
        operations[operationIndexForFirstReturnJumpTarget].Offset, commonExit);

      // We may need to mutate the method body during rewriting, so obtain a mutable version.
      mutableMethodBody = (MethodBody)immutableMethodBody;

      // Emit each operation, along with labels
      for (int i = 0; i < operations.Count; i++)
      {
        IOperation op = operations[i];
        MarkLabels(immutableMethodBody, offsetsUsedInExceptionInformation, offset2Label, op);
        this.EmitDebugInformationFor(op);
        EmitOperation(op, i, ref mutableMethodBody, offset2Label,
            ref tryBodyStarted, exceptions, commonExit, operations.Last(), synthesizedReturns);
      }

      while (generator.InTryBody)
      {
        generator.EndTryBody();
      }

      // Retrieve the operations (and the exception information) from the generator
      generator.AdjustBranchSizesToBestFit();
      mutableMethodBody.OperationExceptionInformation = new List<IOperationExceptionInformation>(
          generator.GetOperationExceptionInformation());
      mutableMethodBody.Operations = new List<IOperation>(generator.GetOperations());

      return mutableMethodBody;
    }

    #region Processing method exits

    /// <summary>
    /// Determine which of the operations in the given operations list are synthesized
    /// returns. These are returns in the C#, but are implemented in the CIL as jumps
    /// to the given commonExit point.
    /// </summary>
    /// <param name="originalReturnJumpOffset">The offset for the first nop for the last return 
    /// in the compiler generated IL</param>
    /// <param name="immutableMethodBody">Method body under consideration</param>
    /// <param name="operations">List of operations in which to look for the synthesized
    /// returns</param>
    /// <param name="commonExit">Common exit the synthesized returns will jump to</param>
    /// <returns>The list of operations if any, otherwise null</returns>
    private static ISet<IOperation> DetermineSynthesizedReturns(IMethodBody immutableMethodBody,
      List<IOperation> operations, uint originalReturnJumpOffset, ILGeneratorLabel commonExit)
    {
      if (immutableMethodBody.MethodDefinition.Type.TypeCode != PrimitiveTypeCode.Void)
      {
        OperationCode lastStoreOperation = GetLastStoreOperation(immutableMethodBody);
        if (lastStoreOperation != 0)
        {
          return FindAndAdjustSynthesizedReturns(operations, lastStoreOperation,
              originalReturnJumpOffset, commonExit);
        }
      }
      return null;
    }

    /// <summary>
    /// The compiler translates multiple return statements to a jump to the some
    /// number of noops inserted before the final return instruction. This method
    /// gets the index of the first return jump target in the given list of operations.
    /// If there are no noops preceeding the ret instruction, then this method returns
    /// the address of the ret instruction.
    /// </summary>
    /// <param name="operations">Operations to inspect</param>
    /// <returns>The index of the last return jump target</returns>
    private static int GetIndexOfFirstReturnJumpTarget(List<IOperation> operations)
    {
      int opCount = operations.Count;
      int operationIndexForFirstReturnJumpTarget = opCount - 2;
      if (opCount > 1)
      {
        while (operationIndexForFirstReturnJumpTarget > 0 &&
               operations[operationIndexForFirstReturnJumpTarget].OperationCode == OperationCode.Nop)
        {
          operationIndexForFirstReturnJumpTarget--;
        }
        // We went one too far, so increment again by 1
        operationIndexForFirstReturnJumpTarget++;
      }
      return operationIndexForFirstReturnJumpTarget;
    }

    /// <summary>
    /// Find the returns that have been synthesized (in a debug build) to a store then a jump,
    /// and adjust the jump operation to target commonExit.
    /// </summary>
    /// <param name="offsetForOldLastReturn">The offset for the first nop for the last return 
    /// in the compiler generated IL</param>
    /// <param name="operations">Operations to the the synthesized returns in</param>
    /// <param name="lastStoreOperation">The store operation that will precede a jump</param>
    /// <param name="commonExit">The exit point that all synthesized returns should jump to
    /// after instrumentation is complete</param>
    /// <returns>The set of store commands comprising part of the synthesized return</returns>
    private static ISet<IOperation> FindAndAdjustSynthesizedReturns(List<IOperation> operations,
        OperationCode lastStoreOperation, uint offsetForOldLastReturn, ILGeneratorLabel commonExit)
    {
      HashSet<IOperation> synthesizedReturns = new HashSet<IOperation>();
      // Need a pair of instructions for the synthesized return, so stop looking 1 before you
      // hit the end of the operations.
      for (int i = 0; i < operations.Count - 1; i++)
      {
        // Look for cases where the current instruction is a store...
        if (operations[i].OperationCode == lastStoreOperation &&
          // ... and the next instruction is a branch ... 
          (operations[i + 1].OperationCode == OperationCode.Br
          || operations[i + 1].OperationCode == OperationCode.Leave
          || operations[i + 1].OperationCode == OperationCode.Brtrue
          || operations[i + 1].OperationCode == OperationCode.Brfalse
          || operations[i + 1].OperationCode == OperationCode.Br_S
          || operations[i + 1].OperationCode == OperationCode.Leave_S
          || operations[i + 1].OperationCode == OperationCode.Brtrue_S
          || operations[i + 1].OperationCode == OperationCode.Brfalse_S) &&
          // ... and the branch location is to the old return offset.
          (uint)operations[i+1].Value >= offsetForOldLastReturn)
        {
          synthesizedReturns.Add(operations[i]);
          ((Operation)operations[i + 1]).Value = commonExit;
        }
      }
      return synthesizedReturns;
    }

    /// <summary>
    /// Get the store instruction that will precede any pseudo-return
    /// (store then a branch) found in a debug build.
    /// </summary>
    /// <param name="methodBody">Method for which to get the last store</param>
    /// <returns>The instruction called during the store, 0 for void methods</returns>
    private static OperationCode GetLastStoreOperation(IMethodBody methodBody)
    {
      if (methodBody.MethodDefinition.Type.TypeCode == PrimitiveTypeCode.Void)
      {
        return 0;
      }

      List<IOperation> operations = methodBody.Operations.ToList();
      if (operations.Count < 1)
      {
        return 0;
      }

      // Start at the end of the method, back track until we get to a load
      int opIndex = operations.Count - 1;
      IOperation op = operations[opIndex];
      while (op.OperationCode == OperationCode.Ret || op.OperationCode == OperationCode.Nop)
      {
        opIndex--;
        op = operations[opIndex];
      }
      switch (op.OperationCode)
      {
        case OperationCode.Ldloc:
          return OperationCode.Stloc;
        case OperationCode.Ldloc_0:
          return OperationCode.Stloc_0;
        case OperationCode.Ldloc_1:
          return OperationCode.Stloc_1;
        case OperationCode.Ldloc_2:
          return OperationCode.Stloc_2;
        case OperationCode.Ldloc_3:
          return OperationCode.Stloc_3;
        case OperationCode.Ldloc_S:
          return OperationCode.Stloc_S;
        default:
          return 0;
      }
    }

    #endregion

    /// <summary>
    /// CCI-Implemented method to emit debug information for an operation.
    /// </summary>
    /// <param name="operation">Operation to emit debug information for</param>
    private void EmitDebugInformationFor(IOperation operation)
    {
      this.generator.MarkSequencePoint(operation.Location);
      if (this.scopeEnumerator == null) return;
      ILocalScope/*?*/ currentScope = null;
      while (this.scopeStack.Count > 0)
      {
        currentScope = this.scopeStack.Peek();
        if (operation.Offset < currentScope.Offset + currentScope.Length) break;
        this.scopeStack.Pop();
        this.generator.EndScope();
        currentScope = null;
      }
      while (this.scopeEnumeratorIsValid)
      {
        currentScope = this.scopeEnumerator.Current;
        if (currentScope.Offset <= operation.Offset && operation.Offset < currentScope.Offset +
            currentScope.Length)
        {
          this.scopeStack.Push(currentScope);
          this.generator.BeginScope();
          foreach (var local in this.pdbReader.GetVariablesInScope(currentScope))
            this.generator.AddVariableToCurrentScope(local);
          foreach (var constant in this.pdbReader.GetConstantsInScope(currentScope))
            this.generator.AddConstantToCurrentScope(constant);
          this.scopeEnumeratorIsValid = this.scopeEnumerator.MoveNext();
        }
        else
        {
          break;
        }
      }
    }

    /// <summary>
    /// Emit the code to store the nonce as a local variable
    /// </summary>
    /// <param name="methodDefinition">Method to store the nonce for</param>
    private void SetNonce(IMethodDefinition methodDefinition)
    {
      generator.Emit(OperationCode.Ldstr, FormatMethodName(methodDefinition));
      // Call the IncGlobalCounter function
      EmitInvocationNonceSetter();
      // Store the result in the nonce variable
      generator.Emit(OperationCode.Stloc, this.nonceVariableDictionary[methodDefinition]);
    }

    /// <summary>
    /// Returns whether the given method body has a constructor and that constructor doesn't call
    /// the object constructor
    /// </summary>
    /// <param name="methodBody">Method body to test</param>
    /// <returns>True if not a constructor or not object constructor call, false otherwise</returns>
    private static bool IsConstructorWithNoObjectConstructorCall(IMethodBody methodBody)
    {
      return methodBody.MethodDefinition.IsConstructor
        && !(methodBody.Operations.ToList()[0].OperationCode == OperationCode.Ldarg_0
            && methodBody.Operations.ToList()[1].OperationCode == OperationCode.Call);
    }

    #region Process Operations Helper Methods

    /// <summary>
    /// Given a set of operations, sort the thrown exceptions by subclass hierarchy
    /// </summary>
    /// <param name="operations">Operations of a method</param>
    /// <returns>Exceptions sorted so that no exception appears after its superclass</returns>
    private List<ITypeReference> DetermineAndSortExceptions(List<IOperation> operations)
    {
      // We need to convert the CCI Types to .NET types to sort them by
      // subclass hierarchy, but we need to return CCI Types for later
      // use in the instrumentation code. The dictionary will maintain the
      // relationship between a .NET Type and a CCI Type
      Dictionary<Type, ITypeReference> dict = new Dictionary<Type, ITypeReference>();

      // TODO(#10): Should we try to combine passes over operations?
      // Look over all the method operations for thrown exceptions
      for (int i = 0; i < operations.Count; i++)
      {
        IOperation op = operations[i];
        // When we find a throw command determine which exception was thrown
        if (op.OperationCode == OperationCode.Throw)
        {
          ITypeReference exType;
          IOperation prevOp = operations[i - 1];
          if (prevOp.Value is MethodDefinition)
          {
            IMethodDefinition methDef = (MethodDefinition)prevOp.Value;
            // A new exception is being thrown
            if (methDef.IsConstructor)
            {
              exType = methDef.ContainingTypeDefinition;
            }
            else
            {
              exType = methDef.Type;
            }
          }
          else if (prevOp.Value is LocalDefinition)
          {
            exType = ((LocalDefinition)prevOp.Value).Type;
          }
          else if (prevOp.Value is Microsoft.Cci.MutableCodeModel.MethodReference)
          {
            exType = ((Microsoft.Cci.MutableCodeModel.MethodReference)prevOp.Value).ContainingType;
          }
          else if (prevOp.Value is TypeReference)
          {
            exType = (TypeReference)op.Value;
          }
          else if (prevOp.Value is ParameterDefinition)
          {
            exType = ((ParameterDefinition)prevOp.Value).Type;
          }
          else if (prevOp.Value is FieldReference)
          {
            exType = ((FieldReference)op.Value).Type;
          }
          else
          {
            throw new NotSupportedException("Unexpected operation of type " + prevOp.Value.GetType());
          }
          // Convert from CCI Type to String Type
          // Exception name must be a single class
          Type t = this.typeManager.ConvertAssemblyQualifiedNameToType(
              this.typeManager.ConvertCCITypeToAssemblyQualifiedName(exType)).GetSingleType;
          if (t != null && !dict.ContainsKey(t))
          {
            dict.Add(t, exType);
          }
        }
      }

      // Copy the exceptions to an array for insertion sort
      Type[] foundExceptions = new Type[dict.Count];
      dict.Keys.CopyTo(foundExceptions, 0);
      // Now insertion sort the exceptions
      for (int j = 1; j < foundExceptions.Length; j++)
      {
        var key = foundExceptions[j];
        int i = j - 1;
        while (i > 0 && !foundExceptions[i].IsSubclassOf(key))
        {
          foundExceptions[i + 1] = foundExceptions[i];
          i--;
        }
        foundExceptions[i + 1] = key;
      }

      // Return the list of sorted exceptions
      List<ITypeReference> cciExceptions = new List<ITypeReference>();
      foreach (Type ex in foundExceptions)
      {
        cciExceptions.Add(dict[ex]);
      }
      return cciExceptions;
    }

    /// <summary>
    /// Emit operation along with any injection
    /// </summary>
    /// <param name="exceptions">Sorted list of exceptions present in the method being
    /// emitted</param>
    /// <param name="offset2Label">Mapping of offset to label at that offset</param>
    /// <param name="op">The operation being omitted</param>
    /// <param name="returnLabelMapping">Mapping from return operation to label that operation jumps
    /// to</param>
    /// <param name="tryBodyStarted">Whether the try-body that wraps the entire method for
    /// purposes of exception handling has been started.</param>
    /// <param name="i">Index of this operation in the method's operations list</param>
    /// <param name="methodBody">Body of this method containing this operation</param>
    /// Yes this method is long, it has to have a switch statement over all operation types.
    /// <param name="commonExit">A label for the point in the IL all programs will jump to
    /// for a return</param>
    /// <param name="lastReturnInstruction">The last return instruction of the method, or the first
    /// of a series of no-ops before the last instruction</param>
    /// <param name="lastStoreInstruction">The last store instruction before a return or a
    /// pseudo-return (branch to the end of the method)</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability",
      "CA1502:AvoidExcessiveComplexity")]
    private void EmitOperation(IOperation op, int i, ref MethodBody methodBody,
      Dictionary<uint, ILGeneratorLabel> offset2Label, ref bool tryBodyStarted,
      List<ITypeReference> exceptions, ILGeneratorLabel commonExit,
      IOperation lastReturnInstruction, ISet<IOperation> synthesizedReturns)
    {
      switch (op.OperationCode)
      {
        #region Branches
        case OperationCode.Beq:
        case OperationCode.Bge:
        case OperationCode.Bge_Un:
        case OperationCode.Bgt:
        case OperationCode.Bgt_Un:
        case OperationCode.Ble:
        case OperationCode.Ble_Un:
        case OperationCode.Blt:
        case OperationCode.Blt_Un:
        case OperationCode.Bne_Un:
        case OperationCode.Br:
        case OperationCode.Brfalse:
        case OperationCode.Brtrue:
        case OperationCode.Leave:
        case OperationCode.Beq_S:
        case OperationCode.Bge_S:
        case OperationCode.Bge_Un_S:
        case OperationCode.Bgt_S:
        case OperationCode.Bgt_Un_S:
        case OperationCode.Ble_S:
        case OperationCode.Ble_Un_S:
        case OperationCode.Blt_S:
        case OperationCode.Blt_Un_S:
        case OperationCode.Bne_Un_S:
        case OperationCode.Br_S:
        case OperationCode.Brfalse_S:
        case OperationCode.Brtrue_S:
        case OperationCode.Leave_S:
          // The value may be the common exit, which is a generator label, otherwise it'll be an
          // offset, do a lookup in the map for the label corresponding to that offset.
          ILGeneratorLabel branchLoc = op.Value is ILGeneratorLabel ? (ILGeneratorLabel)op.Value :
              offset2Label[(uint)op.Value];
          if (branchLoc == commonExit)
          {
            // If the branch is taken do the instrumentation then leave to the common exit.
            // If the branch is not taken then bypass all that and continue with the program
            // as normal.
            ILGeneratorLabel branchTaken = new ILGeneratorLabel();
            ILGeneratorLabel branchNotTaken = new ILGeneratorLabel();
            // The program may normally be expecting to leave, but this would clear the stack.
            // We need the stack intact since we will be using the return variable. So replace this
            // leave with a branch (we do also leave after branching).
            if (op.OperationCode == OperationCode.Leave_S
             || op.OperationCode == OperationCode.Leave)
            {
              ((Operation)op).OperationCode = OperationCode.Br;
            }
            generator.Emit(ILGenerator.LongVersionOf(op.OperationCode), branchTaken);
            if (!(op.OperationCode == OperationCode.Br || op.OperationCode == OperationCode.Br_S))
            {
              generator.Emit(OperationCode.Br, branchNotTaken);
            }

            generator.MarkLabel(branchTaken);

            if (DeclareReturnProgramPoint(methodBody, i))
            {
              InstrumentReturn(methodBody);
            }

            // Save the return var
            ILocalDefinition returnLocal = FindLocalMatchingReturnType(ref methodBody);
            if (returnLocal != null)
            {
              generator.Emit(OperationCode.Stloc, returnLocal);
            }

            // Now jump to the common return
            generator.Emit(OperationCode.Leave, commonExit);
            generator.MarkLabel(branchNotTaken);
          }
          else
          {
            generator.Emit(ILGenerator.LongVersionOf(op.OperationCode), branchLoc);
          }
          break;
        case OperationCode.Switch:
          uint[] offsets = op.Value as uint[];
          ILGeneratorLabel[] labels = new ILGeneratorLabel[offsets.Length];
          for (int j = 0, n = offsets.Length; j < n; j++)
          {
            labels[j] = offset2Label[offsets[j]];
          }
          generator.Emit(OperationCode.Switch, labels);
          break;
        #endregion Branches
        #region Everything else
        case OperationCode.Stloc:
        case OperationCode.Stloc_S:
          ILocalDefinition loc = op.Value as ILocalDefinition;
          if (loc == null)
          {
            throw new ILMutatorException("Stloc operation found without a valid operand");
          }
          if (synthesizedReturns != null && synthesizedReturns.Contains(op))
          {
            // Don't execute the normal store -- a store will be executed when instrumentation
            // is performed, which will occur at the branch (the next instruction)
            break;
          }
          generator.Emit(op.OperationCode, loc);
          break;
        case OperationCode.Stloc_0:
        case OperationCode.Stloc_1:
        case OperationCode.Stloc_2:
        case OperationCode.Stloc_3:
          if (synthesizedReturns != null && synthesizedReturns.Contains(op))
          {
            // Don't execute the normal store -- a store will be executed when instrumentation
            // is performed, which will occur at the branch (the next instruction)
            break;
          }
          generator.Emit(op.OperationCode);
          break;
        case OperationCode.Call:
          generator.Emit(op.OperationCode, (IMethodReference)op.Value);
          // This is false only for constructors, before the base constructor is called
          if (!tryBodyStarted)
          {
            // The constructor calls a base constructor, either object or another
            // constructor in the same class (instrumentation happens before we
            // iterate over actual operations so it doesn't count). So emit the
            // call, then start the try body.
            IMethodReference methRef = (IMethodReference)op.Value;
            if (methRef.Name.ToString().Contains(ConstructorSuffix))
            {
              generator.BeginTryBody();
              tryBodyStarted = true;
            }
          }
          break;
        case OperationCode.Ret:
          ILocalDefinition returnVar = FindLocalMatchingReturnType(ref methodBody);
          // If we are really at the end of the method then we want to close the method-wide try
          // block, add handlers for all exceptions, and mark the common exit point.
          if (op == lastReturnInstruction)
          {
            methodBody = EndExceptionHandling(methodBody, exceptions);

            bool shouldInstrumentReturn = DeclareReturnProgramPoint(methodBody, i);

            if (shouldInstrumentReturn)
            {
              InstrumentReturn(methodBody);
            }

            generator.Emit(op.OperationCode);

            // Any early exits should jump here
            generator.MarkLabel(commonExit);

            if (returnVar != null)
            {
              generator.Emit(OperationCode.Ldloc, returnVar);
            }
            generator.Emit(op.OperationCode);
          }
          // If we are in the middle of a method, stash the return val, make the instrumentation,
          // and jump to the common exit point, which will unstash the return val before returning.
          else
          {
            DeclareReturnProgramPoint(methodBody, i);
            InstrumentReturn(methodBody);

            if (returnVar != null)
            {
              generator.Emit(OperationCode.Stloc, returnVar);
            }
            generator.Emit(OperationCode.Leave, commonExit);
          }
          break;
        case OperationCode.Tail_:
          // The Tail(call) instruction can't be called in a try/catch block. Since we wrap
          // the entire method in a try/catch block, we thus can't use it at all.
          break;
        default:
          if (op.Value == null)
          {
            generator.Emit(op.OperationCode);
            break;
          }
          var typeCode = System.Convert.GetTypeCode(op.Value);
          switch (typeCode)
          {
            case TypeCode.Byte:
              generator.Emit(op.OperationCode, (byte)op.Value);
              break;
            case TypeCode.Double:
              generator.Emit(op.OperationCode, (double)op.Value);
              break;
            case TypeCode.Int16:
              generator.Emit(op.OperationCode, (short)op.Value);
              break;
            case TypeCode.Int32:
              generator.Emit(op.OperationCode, (int)op.Value);
              break;
            case TypeCode.Int64:
              generator.Emit(op.OperationCode, (long)op.Value);
              break;
            case TypeCode.Object:
              IFieldReference fieldReference = op.Value as IFieldReference;
              if (fieldReference != null)
              {
                generator.Emit(op.OperationCode, this.Rewrite(fieldReference));
                break;
              }
              ILocalDefinition localDefinition = op.Value as ILocalDefinition;
              if (localDefinition != null)
              {
                generator.Emit(op.OperationCode, localDefinition);
                break;
              }
              IMethodReference methodReference = op.Value as IMethodReference;
              if (methodReference != null)
              {
                generator.Emit(op.OperationCode, this.Rewrite(methodReference));
                break;
              }
              IParameterDefinition parameterDefinition = op.Value as IParameterDefinition;
              if (parameterDefinition != null)
              {
                generator.Emit(op.OperationCode, parameterDefinition);
                break;
              }
              ISignature signature = op.Value as ISignature;
              if (signature != null)
              {
                generator.Emit(op.OperationCode, signature);
                break;
              }
              ITypeReference typeReference = op.Value as ITypeReference;
              if (typeReference != null)
              {
                generator.Emit(op.OperationCode, this.Rewrite(typeReference));
                break;
              }
              throw new ILMutatorException("Should never get here: no other IOperation argument types should exist");
            case TypeCode.SByte:
              generator.Emit(op.OperationCode, (sbyte)op.Value);
              break;
            case TypeCode.Single:
              generator.Emit(op.OperationCode, (float)op.Value);
              break;
            case TypeCode.String:
              generator.Emit(op.OperationCode, (string)op.Value);
              break;
            default:
              // The other cases are the other enum values that TypeCode has.
              // But no other argument types should be in the Operations. ILGenerator cannot handle anything else,
              // so such IOperations should never exist.
              //case TypeCode.Boolean:
              //case TypeCode.Char:
              //case TypeCode.DateTime:
              //case TypeCode.DBNull:
              //case TypeCode.Decimal:
              //case TypeCode.Empty: // this would be the value for null, but the case when op.Value is null is handled before the switch statement
              //case TypeCode.UInt16:
              //case TypeCode.UInt32:
              //case TypeCode.UInt64:
              throw new ILMutatorException("Should never get here: no other IOperation argument types should exist");
          }
          break;
        #endregion Everything else
      }
    }

    /// <summary>
    /// Print the declaration for a method return program point onto the stack, if the program point
    /// should be visited.
    /// </summary>
    /// <param name="methodBody">Method being exited</param>
    /// <param name="i">Label for the exit</param>
    /// <returns>Whether the declaration was pushed into the stack</returns>
    private bool DeclareReturnProgramPoint(IMethodBody methodBody, int i)
    {
      // If we don't want to instrument returns then don't do anything special here
      bool instrumentReturns = frontEndArgs.ShouldPrintProgramPoint(FormatMethodName(
          MethodTransition.EXIT, methodBody.MethodDefinition),
          i.ToString(CultureInfo.InvariantCulture));
      if (!instrumentReturns)
      {
        return false;
      }

      AcquireWriterLock();

      // Add the i to the end of exit to ensure uniqueness
      // A ret command is added even at the end of functions without
      // a return in the code by the compiler, so we catch that scenario
      EmitMethodSignature(MethodTransition.EXIT, methodBody, label: i.ToString(CultureInfo.InvariantCulture));

      return true;
    }

    /// <summary>
    /// Add the instrumentation calls for the return statement of the method
    /// </summary>
    /// <param name="op">The actual return operation</param>
    /// <param name="methodBody">Body of the method being returned from</param>
    private void InstrumentReturn(IMethodBody methodBody)
    {
      if (methodBody.MethodDefinition.Type.TypeCode != host.PlatformType.SystemVoid.TypeCode)
      {
        // The instrumentor method call will consume the return val
        // which is on the top of the stack. Thus we need to copy
        // the top item on the stack. We aren't guaranteed that we
        // can store this value anywhere, so we need to make a
        // non-standard instrumentation call where the value comes before its
        // name
        generator.Emit(OperationCode.Dup);
        this.EmitSpecialInstrumentationCall(methodBody.MethodDefinition.Type);
      }

      // Every exit needs to have an exception value, even if the
      // exception doesn't exist
      this.EmitExceptionInstrumentationCall(false);

      ReleaseWriterLock();
    }

    /// <summary>
    /// Ends the try block associated with the method and adds the handler.
    ///
    /// <param name="methodBody">Reference to a mutable method body, will modify it for exception
    /// handling, including adding a local stash var if necessary </param>
    /// <param name="exceptions">Sorted list of exceptions to instrument</param>
    /// </summary>
    private MethodBody EndExceptionHandling(IMethodBody methodBody, List<ITypeReference> exceptions)
    {
      MethodBody mutableMethodBody = (MethodBody)methodBody;
      // We will need to store the return value (at the top of the stack)
      // in a local variable because it will be clobbered when we cross
      // the try block boundary
      ILocalDefinition localStoringReturnValue = FindLocalMatchingReturnType(ref mutableMethodBody);

      // If we have a void method there's no return value, otherwise save the return value.
      if (localStoringReturnValue != null)
      {
        generator.Emit(OperationCode.Stloc, localStoringReturnValue);
      }

      // Branch beyond the catch handler, we don't know where to yet but the label will get marked.
      ILGeneratorLabel returnPointLabel = new ILGeneratorLabel();
      generator.Emit(ILGenerator.LongVersionOf(OperationCode.Leave), returnPointLabel);
      if (generator.InTryBody)
      {
        EmitCatchBlock(mutableMethodBody, exceptions);
        generator.EndTryBody();
      }
      generator.Emit(OperationCode.Nop);

      // Mark where we were supposed to jump to
      generator.MarkLabel(returnPointLabel);

      // Restore the return value
      if (localStoringReturnValue != null)
      {
        generator.Emit(OperationCode.Ldloc, localStoringReturnValue);
      }
      else
      {
        // No real return value that would ever be called but we need to provide one
        // to make the program structure correct.
        if (!methodBody.Operations.Any(x => x.OperationCode == OperationCode.Ret))
        {
          localStoringReturnValue = FindLocalMatchingReturnType(ref mutableMethodBody, false);
          generator.Emit(OperationCode.Ldloc, localStoringReturnValue);
        }
      }

      return mutableMethodBody;
    }

    /// <summary>
    /// Instrument a method exit via exception
    /// </summary>
    /// <param name="methodBody">Method being exited</param>
    /// <param name="ex">Exception throw at exit</param>
    /// <param name="label">Label for the program point</param>
    /// Remove the parameter when Daikon exception printing is fixed or abandoned.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage",
      "CA1801:ReviewUnusedParameters", MessageId = "ex")]
    private void InstrumentExceptionExit(MethodBody methodBody, ITypeReference ex, string label)
    {
      // If we don't want to instrument returns don't do anything special here
      bool instrumentReturns = frontEndArgs.ShouldPrintProgramPoint(FormatMethodName(
          MethodTransition.EXIT, methodBody.MethodDefinition), label);
      if (instrumentReturns)
      {
        AcquireWriterLock();

        // Add the i to the end of exit name to ensure uniqueness
        // A ret command is added even at the end of functions without
        // a return in the code by the compiler, so we catch that scenario
        // TODO(#15): Daikon seems to not like printing exceptions, when that
        // is fixed add null as a parameter on the end here
        EmitMethodSignature(MethodTransition.EXIT, methodBody, label/*, ex*/);

        // TODO(#15): Daikon seems to not like printing exceptions
        // Instrument the exception exit
        this.EmitExceptionInstrumentationCall(true);

        ReleaseWriterLock();
      }
    }

    /// <summary>
    /// Find a declared local variable that matches the method's return type, or create one.
    /// </summary>
    /// <param name="methodBody">Method to inspect, will be modified to hold a local stash variable
    /// if none already existed</param>
    /// <returns>The local that has the same type as the method's return value, or null in
    /// the case of a void method.</returns>
    private ILocalDefinition FindLocalMatchingReturnType(ref MethodBody methodBody,
        bool performNoReturnsCheck = true)
    {
      ITypeReference returnType = methodBody.MethodDefinition.Type;
      if (returnType.TypeCode == host.PlatformType.SystemVoid.TypeCode)
      {
        return null;
      }
      // If the method is not void and there's no return statement then there's no
      // return value we'll have to store to. Return null so we don't get a stack
      // underflow.
      // TODO(#70): Refactor this method not to have a boolean parameter.
      if (performNoReturnsCheck &&
          !methodBody.Operations.Any(x => x.OperationCode == OperationCode.Ret))
      {
        return null;
      }
      if (methodBody.LocalVariables != null)
      {
        foreach (ILocalDefinition local in methodBody.LocalVariables)
        {
          // Don't store the return value in the nonce
          if (this.nonceVariableDictionary[methodBody.MethodDefinition] == local)
          {
            continue;
          }
          if (returnType.InternedKey == local.Type.InternedKey)
          {
            return local;
          }
        }
      }
      else
      {
        methodBody.LocalVariables = new List<ILocalDefinition>();
        methodBody.LocalsAreZeroed = true;
      }

      // No compatible local was found so create a new one
      LocalDefinition newVar = new LocalDefinition();
      newVar.Type = returnType;
      newVar.MethodDefinition = methodBody.MethodDefinition;
      methodBody.LocalVariables.Add(newVar);
      return newVar;
    }

    /// <summary>
    /// Emit the code of a catch block to end a method.
    /// </summary>
    /// <param name="methodBody">The method body containing the catch block</param>
    /// <param name="exceptions">The exceptions to catch in this catch block</param>
    private void EmitCatchBlock(MethodBody methodBody, List<ITypeReference> exceptions)
    {
      generator.BeginCatchBlock(host.PlatformType.SystemObject);

      ILGeneratorLabel jumpPoint = null;

      foreach (ITypeReference ex in exceptions)
      {
        // Mark the jump to here if we didn't before
        if (jumpPoint != null)
        {
          generator.MarkLabel(jumpPoint);
        }
        jumpPoint = new ILGeneratorLabel();

        // Preserve the exception - which is on the top of the stack
        generator.Emit(OperationCode.Dup);
        // See if it's an instance of the one we are checking
        generator.Emit(OperationCode.Isinst, ex);
        // Complicated equality test sequence - read .NET OpCode reference for details
        // http://msdn.microsoft.com/en-us/library/system.reflection.emit.opcodes.aspx
        generator.Emit(OperationCode.Ldnull);
        generator.Emit(OperationCode.Cgt_Un);
        generator.Emit(OperationCode.Ldc_I4_0);
        generator.Emit(OperationCode.Ceq);
        // Pop the comparison, jump to next conditional if no match
        generator.Emit(ILGenerator.LongVersionOf(OperationCode.Brtrue_S), jumpPoint);
        // Otherwise make the instrumentation call
        if (this.frontEndArgs.ShouldPrintProgramPoint(
            FormatMethodName(
                MethodTransition.EXIT, methodBody.MethodDefinition),
                FormatExceptionProgramPoint(ex)))
        {
          this.InstrumentExceptionExit(methodBody, ex, FormatExceptionProgramPoint(ex));
        }
        generator.Emit(OperationCode.Rethrow);
      }

      if (jumpPoint != null)
      {
        generator.MarkLabel(jumpPoint);
      }

      // Now that expected exception has been caught, instrument the catch-all exception handler.
      if (this.frontEndArgs.ShouldPrintProgramPoint(
              FormatMethodName(
                  MethodTransition.EXIT, methodBody.MethodDefinition),
                  FormatExceptionProgramPoint(host.PlatformType.SystemObject)))
      {
        generator.Emit(OperationCode.Dup);
        InstrumentExceptionExit(methodBody, host.PlatformType.SystemObject,
            FormatExceptionProgramPoint(host.PlatformType.SystemObject));
      }

      generator.Emit(OperationCode.Rethrow);
    }

    /// <summary>
    /// CCI implemented method to mark labels with the proper locations
    /// </summary>
    private void MarkLabels(IMethodBody methodBody, HashSet<uint> offsetsUsedInExceptionInformation,
      Dictionary<uint, ILGeneratorLabel> offset2Label, IOperation op)
    {
      ILGeneratorLabel label;
      if (op.Location is IILLocation)
      {
        generator.MarkSequencePoint(op.Location);
      }

      //  Mark operation if it is a label for a branch
      if (offset2Label.TryGetValue(op.Offset, out label))
      {
        generator.MarkLabel(label);
      }

      // Mark operation if it is pointed to by an exception handler
      uint offset = op.Offset;
      if (offsetsUsedInExceptionInformation.Contains(offset))
      {
        foreach (var exceptionInfo in methodBody.OperationExceptionInformation)
        {
          if (offset == exceptionInfo.TryStartOffset)
            generator.BeginTryBody();

          // Never need to do anything when offset == exceptionInfo.TryEndOffset because
          // we pick up an EndTryBody from the HandlerEndOffset below
          // generator.EndTryBody();

          if (offset == exceptionInfo.HandlerStartOffset)
          {
            switch (exceptionInfo.HandlerKind)
            {
              case HandlerKind.Catch:
                generator.BeginCatchBlock(exceptionInfo.ExceptionType);
                break;
              case HandlerKind.Fault:
                generator.BeginFaultBlock();
                break;
              case HandlerKind.Filter:
                generator.BeginFilterBody();
                break;
              case HandlerKind.Finally:
                generator.BeginFinallyBlock();
                break;
            }
          }
          if (exceptionInfo.HandlerKind == HandlerKind.Filter && offset == exceptionInfo.FilterDecisionStartOffset)
          {
            generator.BeginFilterBlock();
          }
          if (offset == exceptionInfo.HandlerEndOffset)
          {
            generator.EndTryBody();
          }
        }
      }
    }

    /// <summary>
    /// CCI-implemented method to label branch targets
    /// </summary>
    /// <returns></returns>
    private static Dictionary<uint, ILGeneratorLabel> LabelBranchTargets(
        List<IOperation> operations, int lastReturnIndex, ILGeneratorLabel commonExit)
    {
      // Jump instructions refer to an offset. Since we are adding new code the jump targets will
      // be wrong, we will insert a label at each jump point so the jump will be correct.
      // The dictionary maps from the original offset to the label we inserted.
      Dictionary<uint, ILGeneratorLabel> offset2Label = new Dictionary<uint, ILGeneratorLabel>();
      int count = operations.Count;
      int lastReturnAddress = (int)operations[lastReturnIndex].Offset;
      for (int i = 0; i < count; i++)
      {
        IOperation op = operations[i];
        switch (op.OperationCode)
        {
          case OperationCode.Beq:
          case OperationCode.Bge:
          case OperationCode.Bge_Un:
          case OperationCode.Bgt:
          case OperationCode.Bgt_Un:
          case OperationCode.Ble:
          case OperationCode.Ble_Un:
          case OperationCode.Blt:
          case OperationCode.Blt_Un:
          case OperationCode.Bne_Un:
          case OperationCode.Br:
          case OperationCode.Brfalse:
          case OperationCode.Brtrue:
          case OperationCode.Leave:
          case OperationCode.Beq_S:
          case OperationCode.Bge_S:
          case OperationCode.Bge_Un_S:
          case OperationCode.Bgt_S:
          case OperationCode.Bgt_Un_S:
          case OperationCode.Ble_S:
          case OperationCode.Ble_Un_S:
          case OperationCode.Blt_S:
          case OperationCode.Blt_Un_S:
          case OperationCode.Bne_Un_S:
          case OperationCode.Br_S:
          case OperationCode.Brfalse_S:
          case OperationCode.Brtrue_S:
          case OperationCode.Leave_S:
            uint x = (uint)op.Value;
            if (x >= lastReturnAddress)
            {
              // This is a return instruction, so change the jump to the common exit point
              if (!offset2Label.ContainsKey(x))
              {
                offset2Label.Add(x, commonExit);
              }
            }
            else
            {
              if (!offset2Label.ContainsKey(x))
              {
                offset2Label.Add(x, new ILGeneratorLabel());
              }
            }
            break;
          case OperationCode.Switch:
            uint[] offsets = op.Value as uint[];
            foreach (var offset in offsets)
            {
              if (!offset2Label.ContainsKey(offset))
                offset2Label.Add(offset, new ILGeneratorLabel());
            }
            break;
          default:
            break;
        }
      }
      return offset2Label;
    }

    /// <summary>
    /// CCI-implemented method to gather information about exception handler
    /// offsets.
    /// </summary>
    /// <param name="methodBody">Method body exception handler offsets are being gathered
    /// from</param>
    private static HashSet<uint> RecordExceptionHandlerOffsets(
        IMethodBody methodBody)
    {
      HashSet<uint> offsetsUsedInExceptionInformation = new HashSet<uint>();
      foreach (var exceptionInfo in methodBody.OperationExceptionInformation)
      {
        uint x = exceptionInfo.TryStartOffset;
        offsetsUsedInExceptionInformation.Add(x);
        x = exceptionInfo.TryEndOffset;
        offsetsUsedInExceptionInformation.Add(x);
        x = exceptionInfo.HandlerStartOffset;
        offsetsUsedInExceptionInformation.Add(x);
        x = exceptionInfo.HandlerEndOffset;
        offsetsUsedInExceptionInformation.Add(x);
        x = exceptionInfo.FilterDecisionStartOffset;
        offsetsUsedInExceptionInformation.Add(x);
      }
      return offsetsUsedInExceptionInformation;
    }

    #endregion

    /// <summary>
    /// Emit the IL to print all the parameters of a method
    /// </summary>
    /// <param name="transition">How do we describe the transition state of the method
    /// e.g. enter, exit</param>
    /// <param name="methodBody">The body of the method to</param>
    /// <param name="label">Optional label for differentiating methods where the same
    /// transition occurs more than once, e.g. multiple exits</param>
    /// <param name="exceptionType"></param>
    /// <returns>True if the method parameters were printed, otherwise false</returns>
    private bool EmitMethodSignature(MethodTransition transition, IMethodBody methodBody,
        string label = "", ITypeReference exceptionType = null)
    {
      string methodName = FormatMethodName(transition, methodBody.MethodDefinition);

      if (!frontEndArgs.ShouldPrintProgramPoint(methodName, label))
      {
        return false;
      }

      if (this.printDeclarations)
      {
        // Print either the enter or exit statement
        this.DeclareMethodTransition(transition, methodName, label);
      }

      // If the program is being run offline, we need to save the assembly name and path
      // so they can be loaded by the variable visitor before it beings instrumentation.
      // Could be necessary anytime we enter a method because some programs, e.g. libraries
      // have no entry point.
      if (this.frontEndArgs.SaveProgram != null && transition == MethodTransition.ENTER)
      {
        this.EmitFrontEndArgInitializationCall();
      }

      // Emit the method name as a program point
      generator.Emit(OperationCode.Ldstr, methodName);
      generator.Emit(OperationCode.Ldstr, label);
      this.EmitProgramPoint();

      // Emit the nonce
      generator.Emit(OperationCode.Ldloc, nonceVariableDictionary[methodBody.MethodDefinition]);
      this.EmitWriteInvocationNonceCall();

      PrintParentNameDeclarationIfNecessary(transition, methodBody);

      EmitParentClassFields(methodBody);

      // Sometimes we want to describe 'this', which is the 0th parameter. Don't describe this if
      // the method is static, or the call is the entrance to a constructor. EmitParentObject
      // will check this.
      EmitParentObject(transition, methodBody);

      EmitParameters(methodBody);

      // If we are at a method exit we may need to print the declaration
      // for the method's return value
      if (transition == MethodTransition.EXIT && this.printDeclarations)
      {
        // Declare the return if it's not a void method and there's any return statement
        if ((methodBody.MethodDefinition.Type.TypeCode !=
            host.PlatformType.SystemVoid.TypeCode))
        {
          this.declPrinter.PrintReturn("return",
              this.typeManager.ConvertCCITypeToAssemblyQualifiedName(
                  methodBody.MethodDefinition.Type));
        }

        // Declare the exception, always present for an exceptional exit
        if (exceptionType != null)
        {
          this.declPrinter.PrintReturn(ExceptionVariableName,
              this.typeManager.ConvertCCITypeToAssemblyQualifiedName(exceptionType));
        }
        else
        {
          this.declPrinter.PrintReturn(ExceptionVariableName, "System.Exception");
        }
      }

      return true;
    }

    /// <summary>
    /// Print the declaration of the reference to the parent program point for the given method,
    /// if necessary.
    /// </summary>
    /// <param name="transition">Transition of the current program point</param>
    /// <param name="methodBody">Method body of the current program point</param>
    private void PrintParentNameDeclarationIfNecessary(MethodTransition transition,
        IMethodBody methodBody)
    {
      if (!this.printDeclarations)
      {
        return;
      }
      if (IsThisValid(transition, methodBody.MethodDefinition))
      {
        this.declPrinter.PrintParentName(methodBody.MethodDefinition.ContainingType, true);
      }
      else
      {
        this.declPrinter.PrintParentName(methodBody.MethodDefinition.ContainingType, false);
      }
    }

    /// <summary>
    /// Emit the instrumentation and print the declaration for any static fields of the parent class.
    /// </summary>
    /// <param name="methodBody">Method body for the program point</param>
    private void EmitParentClassFields(IMethodBody methodBody)
    {
      ITypeReference parentType = methodBody.MethodDefinition.ContainingType;
      this.EmitStaticInstrumentationCall(parentType);

      if (this.printDeclarations)
      {
        this.declPrinter.PrintParentClassFields(
            this.typeManager.ConvertCCITypeToAssemblyQualifiedName(parentType));
      }
    }

    /// <summary>
    /// Insert IL call to the method to release the lock on the writer
    /// </summary>
    /// Suppression safe because we control the string in the GetNameFor call
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
        "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void ReleaseWriterLock()
    {
      generator.Emit(OperationCode.Call, new Microsoft.Cci.MethodReference(
          this.host, this.variableVisitorType, CallingConvention.Default,
          this.systemVoid, this.nameTable.GetNameFor(
              VariableVisitor.ReleaseWriterLockFunctionName), 0));
    }

    /// <summary>
    /// Insert IL call to the method to acquire the lock on the writer
    /// </summary>s
    /// Suppression safe because we control the string in the GetNameFor call
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
        "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void AcquireWriterLock()
    {
      generator.Emit(OperationCode.Call, new Microsoft.Cci.MethodReference(
          this.host, this.variableVisitorType, CallingConvention.Default,
          this.systemVoid, this.nameTable.GetNameFor(
              VariableVisitor.AcquireWriterLockFunctionName), 0));
    }

    /// <summary>
    /// Insert call to set whether output should be suppressed.
    /// </summary>
    /// <param name="shouldSuppress">The desired state of output suppression</param>
    /// Suppression safe because we control the string in the GetNameFor call
    /// Performance warning suppressed because the calls to this method are inserted in the
    /// source programs
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
         "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void InsertShouldSuppressOutputCall(bool shouldSuppress)
    {
      // .NET opcode semantics for boolean values are: 0 => false, other => true
      generator.Emit(OperationCode.Ldc_I4, shouldSuppress ? 1 : 0);
      generator.Emit(OperationCode.Call, new Microsoft.Cci.MethodReference(
          this.host, this.variableVisitorType, CallingConvention.Default,
          this.systemVoid, this.nameTable.GetNameFor(
              VariableVisitor.ShouldSuppressOutputMethodName), 0, host.PlatformType.SystemBoolean));
    }

    /// <summary>
    /// Create a new list of locals with a nonce variable added. Add the nonce to the
    /// method/nonce lookup dictionary.
    /// </summary>
    /// <param name="methodBody">Method to add the local nonce too</param>
    /// <returns>A new list of local variables for the given method</returns>
    private List<ILocalDefinition> CreateNonceVariable(MethodBody methodBody)
    {
      List<ILocalDefinition> locals;
      if (methodBody.LocalVariables == null)
      {
        locals = new List<ILocalDefinition>();
      }
      else
      {
        locals = methodBody.LocalVariables.ToList();
      }
      // Create a new integer local to hold the nonce
      LocalDefinition newVariable = new LocalDefinition();
      newVariable.Type = host.PlatformType.SystemInt32;
      newVariable.MethodDefinition = methodBody.MethodDefinition;
      if (methodBody.LocalVariables == null)
      {
        locals = new List<ILocalDefinition>();
      }
      locals.Add(newVariable);

      // Add the nonce to the lookup dictionary
      this.nonceVariableDictionary[methodBody.MethodDefinition] = newVariable;
      return locals;
    }

    /// <summary>
    /// Format the method name to fit the output specification
    /// </summary>
    /// <param name="transition">The transition State of the method</param>
    /// <param name="methodDef">Definition of the method -- used to determine the name</param>
    /// <returns>The name of the method, suitable for printing to dtrace file</returns>
    private static string FormatMethodName(MethodTransition transition,
        IMethodDefinition methodDef)
    {
      return FormatMethodName(methodDef)
          + ":::" + transition.ToString().ToUpper(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Format the method name, without any transition
    /// </summary>
    /// <param name="methodDef">Definition of the method whose name to format</param>
    /// <returns>Name of the method that an be output</returns>
    private static string FormatMethodName(IMethodDefinition methodDef)
    {
      return MemberHelper.GetMemberSignature(methodDef,
            NameFormattingOptions.ParameterName
          | NameFormattingOptions.SmartTypeName
          | NameFormattingOptions.Signature);
    }

    /// <summary>
    /// Return the formatted name of the exception point
    /// </summary>
    /// <param name="ex">CCI type of the exception</param>
    /// <returns>Name of the exception program point, following Daikon
    /// convention</returns>
    private string FormatExceptionProgramPoint(ITypeReference ex)
    {
      return (ex == host.PlatformType.SystemObject ?
          RuntimeExceptionExitProgamPointSuffix : ExceptionExitSuffix + ex.ToString());
    }

    /// <summary>
    /// Insert the instrumentation calls, and possibly the definitions for the actual method
    /// parameters.
    /// </summary>
    /// <param name="methodBody">Method body being instrumented</param>
    private void EmitParameters(IMethodBody methodBody)
    {
      int i = 0;
      foreach (var param in methodBody.MethodDefinition.Parameters)
      {
        // Constructor or instance calls have 'this' as a 0th parameter.
        // We already handled it, so move on to the next index parameter
        if (i == 0 && !methodBody.MethodDefinition.IsStatic)
        {
          i++;
        }

        // Load the name of the parameter onto the stack, print the parameter
        // to the decls file if necessary.
        generator.Emit(OperationCode.Ldstr, param.Name.ToString());
        if (this.printDeclarations)
        {
          this.declPrinter.PrintParameter(param.Name.ToString(),
              this.typeManager.ConvertCCITypeToAssemblyQualifiedName(param.Type));
        }

        // In the i-th iteration, load the i-th parameter onto the stack.
        switch (i)
        {
          case 0:
            generator.Emit(OperationCode.Ldarg_0);
            break;
          case 1:
            generator.Emit(OperationCode.Ldarg_1);
            break;
          case 2:
            generator.Emit(OperationCode.Ldarg_2);
            break;
          case 3:
            generator.Emit(OperationCode.Ldarg_3);
            break;
          default:
            generator.Emit(OperationCode.Ldarg_S, param);
            break;
        }

        // Print a string with the param type and the instrumentation call.
        if (param.IsByReference)
        {
          this.EmitReferenceInstrumentationCall(param.Type);
        }
        else
        {
          this.EmitInstrumentationCall(param.Type);
        }

        i++;
      }
    }

    /// <summary>
    /// Add the calls to instrument the "this" parent object and print the declarations for,
    /// if necessary
    /// </summary>
    /// <param name="transition">Whether we are entering or exiting this method</param>
    /// <param name="methodBody">The method of interest</param>
    private void EmitParentObject(MethodTransition transition, IMethodBody methodBody)
    {
      if (!IsThisValid(transition, methodBody.MethodDefinition))
      {
        return;
      }
      generator.Emit(OperationCode.Ldstr, "this");
      generator.Emit(OperationCode.Ldarg_0);
      var parentType = methodBody.MethodDefinition.ContainingTypeDefinition;

      if (parentType is NestedTypeDefinition)
      {
        var x = ((NestedTypeDefinition)parentType);
        var container = x.ContainingTypeDefinition;
        if (container.IsGeneric)
        {
          var genericContainer = container.InstanceType.ResolvedType;
          parentType = (ITypeDefinition)genericContainer.GetMembersNamed(x.Name, true).First();
        }
        else
        {
          parentType = (ITypeDefinition)container.GetMembersNamed(x.Name, true).First();
        }
      }

      if (parentType.IsGeneric && parentType.IsValueType)
      {
        var instanceReference = parentType.InstanceType;
        if (parentType.IsValueType)
        {
          generator.Emit(OperationCode.Ldobj, instanceReference);
        }
        this.EmitInstrumentationCall(instanceReference);
      }
      else
      {
        if (parentType.IsValueType)
        {
          generator.Emit(OperationCode.Ldobj, parentType);
        }
        this.EmitInstrumentationCall(parentType);
      }
      if (this.printDeclarations)
      {
        this.declPrinter.PrintParentObjectFields(methodBody.MethodDefinition.ContainingType
            .ToString(), this.typeManager.ConvertCCITypeToAssemblyQualifiedName(parentType));
      }
    }

    /// <summary>
    /// Returns whether the "this" object is valid at the given program point.
    /// </summary>
    /// <param name="transition">Transition occurring at this program point.</param>
    /// <param name="methodBody">Method body defining the program point.</param>
    /// <returns>True if this is valid, that is we are not entering a constructor and the method
    /// is non static.</returns>
    private static bool IsThisValid(MethodTransition transition, IMethodDefinition methodDefinition)
    {
      return !(methodDefinition.IsStatic ||
               (transition == MethodTransition.ENTER && methodDefinition.IsConstructor));
    }

    /// <summary>
    /// Print the declaration for fact that we are entering or exiting the method
    /// </summary>
    /// <param name="transition">The enter/exit status</param>
    /// <param name="methodName">Name of method in transition</param>
    /// <param name="label">Optional text that can be appended to end of exit call</param>
    private void DeclareMethodTransition(MethodTransition transition, string methodName,
        string label)
    {
      switch (transition)
      {
        case MethodTransition.ENTER:
          this.declPrinter.PrintCallEntrance(methodName + label);
          break;
        case MethodTransition.EXIT:
          this.declPrinter.PrintCallExit(methodName + label);
          break;
        default:
          throw new NotSupportedException("Unexpected method transition: " + transition);
      }
    }

    /// <summary>
    /// Call to add the "exception" variable at a method exit
    /// </summary>
    /// <param name="exceptionExists">Whether there is actually an
    /// exception to instrument or not. If there isn't an exception then a nonsensical
    /// entry for the variable will be made.</param>
    /// Variable containing method name reference is private, static, final and can be trusted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitExceptionInstrumentationCall(bool exceptionExists)
    {
      if (!exceptionExists)
      {
        generator.Emit(OperationCode.Ldnull);
      }
      else
      {
        generator.Emit(OperationCode.Dup);
      }
      // Note the signature is different than the other instrumentation
      // call methods. Here we don't send name, since it's always
      // "exception", or Type, since we are limited about the type info
      // and it's always downcast to System.Object
      generator.Emit(OperationCode.Call, new Microsoft.Cci.MethodReference(
         this.host, this.variableVisitorType, CallingConvention.Default,
         this.systemVoid, this.nameTable.GetNameFor(
            VariableVisitor.ExceptionInstrumentationMethodName),
         0, this.systemObject));
    }

    /// <summary>
    /// Emit a special call to the instrumentation program, assuming the param only is
    /// loaded onto the stack. Adds "return" as the name of the variable. This type of
    /// instrumentation call is appropriate when printing a function's return value.
    /// </summary>
    /// <param name="param">The param to visit</param>
    /// <param name="label">What to label the point as, defaults to return
    /// </param>
    /// Variable containing method reference is private static final and can be trusted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitSpecialInstrumentationCall(ITypeReference param, string label = "return")
    {
      // Mostly copied from EmitInstrumentationCall below, with changes noted
      // Box first
      BoxIfNecessary(param);
      // Then add the name of the variable
      generator.Emit(OperationCode.Ldstr, label);
      generator.Emit(OperationCode.Ldstr,
          this.typeManager.ConvertCCITypeToAssemblyQualifiedName(param).ToString());
      // Make special instrumentation call instead of regular, with parameters reordered
      generator.Emit(OperationCode.Call, new Microsoft.Cci.MethodReference(
         this.host, this.variableVisitorType, CallingConvention.Default,
         this.systemVoid, this.nameTable.GetNameFor(
            VariableVisitor.ValueFirstInstrumentationMethodName), 0,
         this.systemObject, this.systemString, this.systemString));
    }

    /// <summary>
    /// Box the item at the top of the stack if necessary to convert it to an object type
    /// </summary>
    /// <param name="param">Parameter to box if necessary</param>
    private void BoxIfNecessary(ITypeReference param)
    {
      if (param.IsValueType
          || param is GenericTypeParameter || param is GenericTypeParameterReference
          || param is GenericMethodParameter || param is GenericMethodParameterReference)
      {
        generator.Emit(OperationCode.Box, param);
      }
    }

    /// <summary>
    /// Emit the call to the instrumentation program, assuming name, then the param are loaded
    /// onto the stack.
    /// </summary>
    ///
    /// <param name="param">The Type of the param to visit</param>
    /// Warning suppressed because variable containing method reference name is private, static,
    /// final and can be trusted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitStaticInstrumentationCall(ITypeReference param)
    {
      generator.Emit(OperationCode.Ldstr,
          this.typeManager.ConvertCCITypeToAssemblyQualifiedName(param).ToString());
      generator.Emit(OperationCode.Call, new Microsoft.Cci.MethodReference(
         this.host, this.variableVisitorType, CallingConvention.Default,
         this.systemVoid, this.nameTable.GetNameFor(
            VariableVisitor.StaticInstrumentationMethodName), 0, this.systemString));
    }

    /// <summary>
    /// Emit the call to the instrumentation program, assuming name, then the param are loaded
    /// onto the stack
    /// </summary>
    /// <param name="param">The Type of the param to visit</param>
    private void EmitInstrumentationCall(ITypeReference param)
    {
      BoxIfNecessary(param);
      EmitInstrumentationCall(this.typeManager.ConvertCCITypeToAssemblyQualifiedName(param).ToString());
    }

    /// <summary>
    /// Emit the call to the instrumentation program, assuming name, then the param are loaded
    /// onto the stack
    /// </summary>
    ///
    /// <param name="paramTypeName">String containing the assembly-qualified name of the type of
    /// the param to visit</param>
    /// Variable containing method name is private, static, final and can be trusted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitInstrumentationCall(string paramTypeName)
    {
      generator.Emit(OperationCode.Ldstr, paramTypeName);
      generator.Emit(OperationCode.Call, new Microsoft.Cci.MethodReference(
         this.host, this.variableVisitorType, CallingConvention.Default,
         this.systemVoid, this.nameTable.GetNameFor(
            VariableVisitor.InstrumentationMethodName), 0,
         this.systemString, this.systemObject, this.systemString));
    }

    /// <summary>
    /// Insert instrumentation call for a pass-by-reference variable
    /// </summary>
    /// <param name="paramType">Type of the variable to instrument</param>
    private void EmitReferenceInstrumentationCall(ITypeReference paramType)
    {
      // Make the correct indirect reference call, then proceed as normal
      if (!paramType.IsValueType || paramType.TypeCode == PrimitiveTypeCode.NotPrimitive)
      {
        generator.Emit(OperationCode.Ldobj, paramType);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemInt32.TypeCode
            || paramType == host.PlatformType.SystemEnum)
      {
        generator.Emit(OperationCode.Ldind_I4);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemFloat32.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_R4);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemInt64.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_I8);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemInt16.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_I2);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemInt8.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_I1);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemUInt8.TypeCode
            || paramType.TypeCode == host.PlatformType.SystemBoolean.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_U1);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemUInt16.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_U2);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemUInt32.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_U4);
      }
      else if (paramType.TypeCode == host.PlatformType.SystemFloat64.TypeCode)
      {
        generator.Emit(OperationCode.Ldind_R8);
      }
      else
      {
        // We'll get here for decimal type.
        // I don't think there's a way to indirectly load it normally.
        // The verifier will complain but the program will run.
        generator.Emit(OperationCode.Ldind_Ref);
      }
      this.EmitInstrumentationCall(paramType);
    }

    /// <summary>
    /// Add the instruction to print a print a new program printer.
    /// Assumes method name is pushed onto the IL stack.
    /// </summary>
    /// Variable containing method reference name is private, static, final and can be trusted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitProgramPoint()
    {
      if (programPointWriterMethod == null)
      {
        programPointWriterMethod = new Microsoft.Cci.MethodReference(
         this.host, this.variableVisitorType, CallingConvention.Default,
         this.systemVoid, this.nameTable.GetNameFor(
            VariableVisitor.WriteProgramPointMethodName), 0,
         this.systemString, this.systemString);
      }
      generator.Emit(OperationCode.Call, programPointWriterMethod);
    }

    /// <summary>
    /// Write the name and output path of the assembly to be instrumented and a method call to
    /// initialize the variable visitor with these.
    /// </summary>
    /// Variable containing method reference name is private, static, final and can be trusted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitFrontEndArgInitializationCall()
    {
      if (argumentStoringMethodReference == null)
      {
        argumentStoringMethodReference = new Microsoft.Cci.MethodReference(
         this.host, this.argumentStoringType, CallingConvention.Default,
         this.systemVoid, this.nameTable.GetNameFor(ArgumentStoringMethodName),
          /* genericParameterCount */ 0
          /* no param types */);
      }
      generator.Emit(OperationCode.Call, argumentStoringMethodReference);
    }

    /// <summary>
    /// Emit the call to write the invocation nonce
    /// </summary>
    /// Suppression is ok because we only lookup methods whose names we control
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitWriteInvocationNonceCall()
    {
      if (invocationNonceWriterMethod == null)
      {
        invocationNonceWriterMethod = new Microsoft.Cci.MethodReference(
         this.host, this.variableVisitorType, CallingConvention.Default,
         this.systemVoid, this.nameTable.GetNameFor(
            VariableVisitor.WriteInvocationNonceMethodName), 0,
         host.PlatformType.SystemInt32);
      }
      generator.Emit(OperationCode.Call, invocationNonceWriterMethod);
    }

    /// <summary>
    /// Emit a call to the VariableVisitor method that will set the invocation nonce variable for
    /// this method.
    /// </summary>
    /// Suppression is ok because we only lookup methods whose names we control
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
      "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void EmitInvocationNonceSetter()
    {
      if (invocationNonceSetterMethod == null)
      {
        invocationNonceSetterMethod = new Microsoft.Cci.MethodReference(
         this.host, this.variableVisitorType, CallingConvention.Default,
         this.host.PlatformType.SystemInt32, this.nameTable.GetNameFor(
            VariableVisitor.InvocationNonceSetterMethodName), 0, systemString);
      }
      generator.Emit(OperationCode.Call, invocationNonceSetterMethod);
    }

    /// <summary>
    /// Visit module and add instrumentation code to functions.
    /// </summary>
    /// <param name="module">Module to instrument</param>
    /// <param name="pathToVisitor">Path to the .dll of the reflective visitor</param>
    /// <returns>Modified assembly with instrumentation code added</returns>
    public IModule Visit(IModule module, string pathToVisitor)
    {
      var assembly = module as IAssembly;
      if (assembly == null)
      {
        throw new ILMutatorException("Couldn't modify assembly");
      }
      Assembly mutableAssembly = (Assembly)MetadataCopier.DeepCopy(host, assembly);
      this.assemblyIdentity = UnitHelper.GetAssemblyIdentity(mutableAssembly);
      this.typeManager.SetAssemblyIdentity(assemblyIdentity);
      if (!File.Exists(pathToVisitor))
      {
        throw new FileNotFoundException("DLL for Reflector doesn't exist");
      }

      IAssembly variableVisitorAssembly = this.host.LoadUnitFrom(pathToVisitor) as IAssembly;
      if (variableVisitorAssembly == null)
      {
        throw new ArgumentException("Reflector was null");
      }

      foreach (INamedTypeDefinition type in variableVisitorAssembly.GetAllTypes())
      {
        // Get the VariableVisitor Type, mostly filtering out the system assemblies.
        if (type.Name.ToString() == DotNetFrontEnd.VariableVisitor.VariableVisitorClassName)
        {
          this.variableVisitorType = type;
          break;
        }
      }

      if (this.frontEndArgs.SaveProgram != null)
      {
        WriteClassStoringArguments(mutableAssembly, host);
      }

      // We need to be able to reference to variable visitor assembly to add calls to it.
      mutableAssembly.AssemblyReferences.Add(variableVisitorAssembly);

      // If appending onto an existing dtrace then don't reprint the declarations.
      this.printDeclarations = !this.frontEndArgs.DtraceAppend;
      if (this.printDeclarations)
      {
        this.declPrinter = new DotNetFrontEnd.DeclarationPrinter(this.frontEndArgs,
                                                                    this.typeManager);

        Regex ignoreRegex = new Regex(TypeManager.RegexForTypesToIgnoreForProgramPoint);
        foreach (INamedTypeDefinition type in mutableAssembly.AllTypes)
        {
          // CCI components come up named <*>, and we want to exclude them.
          // Also exclude the name of the class storing arguments (for offline programs)
          string typeName = ((ITypeDefinition)type).ToString();
          if (!(ignoreRegex.IsMatch(type.ToString()) || type.ToString() == ArgumentStoringClassName))
          {
            this.declPrinter.PrintObjectDefinition(typeName,
                this.typeManager.ConvertCCITypeToAssemblyQualifiedName(type));
            this.declPrinter.PrintParentClassDefinition(typeName,
                this.typeManager.ConvertCCITypeToAssemblyQualifiedName(type));
          }
        }
      }
      IModule result = this.Rewrite(mutableAssembly);
      if (this.printDeclarations)
      {
        this.declPrinter.CloseWriter();
      }
      return result;
    }

    /// <summary>
    /// Creates and emits a new class, which contains a single method returning the front end
    /// arguments used during instrumentation. Necessary for running the front-end in offline
    /// mode since these would otherwise not be available.
    /// </summary>
    /// <param name="mutableAssembly">Assembly to emit the class into</param>
    /// <param name="host">IMetadataHost to use during rewriting</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security",
        "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
    private void WriteClassStoringArguments(Assembly mutableAssembly, IMetadataHost host)
    {
      RootUnitNamespace rootUnitNamespace = new RootUnitNamespace()
      {
        Unit = mutableAssembly
      };

      NamespaceTypeDefinition argumentStoringClass = new NamespaceTypeDefinition()
      {
        ContainingUnitNamespace = rootUnitNamespace,
        InternFactory = host.InternFactory,
        IsClass = true,
        IsPublic = true,
        Methods = new List<IMethodDefinition>(1),
        Name = nameTable.GetNameFor(ArgumentStoringClassName),
        MangleName = false,
      };
      rootUnitNamespace.Members.Add(argumentStoringClass);
      mutableAssembly.AllTypes.Add(argumentStoringClass);
      argumentStoringClass.BaseClasses = new List<ITypeReference>() { host.PlatformType.SystemObject };
      this.argumentStoringType = argumentStoringClass;

      MethodDefinition getArgumentsMethod = new MethodDefinition()
      {
        ContainingTypeDefinition = argumentStoringClass,
        InternFactory = host.InternFactory,
        IsCil = true,
        IsStatic = true,
        Name = nameTable.GetNameFor(ArgumentStoringMethodName),
        Type = host.PlatformType.SystemVoid,
        Visibility = TypeMemberVisibility.Public,
      };
      argumentStoringClass.Methods.Add(getArgumentsMethod);

      ILGenerator ilGenerator = new ILGenerator(host, getArgumentsMethod);

      ilGenerator.Emit(OperationCode.Ldstr, this.frontEndArgs.AssemblyName);
      System.Diagnostics.Debug.Assert(this.frontEndArgs.SaveProgram != null);
      ilGenerator.Emit(OperationCode.Ldstr, this.frontEndArgs.SaveProgram);
      ilGenerator.Emit(OperationCode.Ldstr, this.frontEndArgs.GetArgsToWrite);

      var variableVisitorMethodReference = new Microsoft.Cci.MethodReference(
       this.host, this.variableVisitorType, CallingConvention.Default,
       this.systemVoid, this.nameTable.GetNameFor(VariableVisitor.InitializeFrontEndArgumentsMethodName),
        /* genericParameterCount */ 0,
        /* param types */ systemString, systemString, systemString);

      ilGenerator.Emit(OperationCode.Call, variableVisitorMethodReference);

      ilGenerator.Emit(OperationCode.Ret);

      ILGeneratorMethodBody body = new ILGeneratorMethodBody(ilGenerator,
        /* localsAreZeroed */ true,
        /* maxStack */ 1,
        getArgumentsMethod,
        Enumerable<ILocalDefinition>.Empty,
        Enumerable<ITypeDefinition>.Empty
      );
      getArgumentsMethod.Body = body;
    }

    public void Dispose()
    {
      this.Dispose(true);
      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Only relevant resource to dispose is the declPrinter.
    /// </summary>
    protected virtual void Dispose(bool disposeManagedResources)
    {
      this.declPrinter.Dispose();
    }
  }
}
