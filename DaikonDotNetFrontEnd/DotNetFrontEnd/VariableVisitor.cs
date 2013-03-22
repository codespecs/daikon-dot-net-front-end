// VariableVisitor defines the methods used to inspect variables at runtime. Calls to this class 
// are inserted by the profiler, and are inserted for each variable in the source program.
// The visitor receives an object name, value, and type, and prints this
// information to the datatrace file. The object's fields are visited recursively.

// The following steps should be performed by the IL Rewriting Library to use VariableVisitor:
// 1) The name of the variable is pushed on the stack.
// 2) The variable value is pushed on the stack.
// 3) The assembly-qualified name of the variable is pushed on the stack.
// 4) The call to VisitVariable is added to the method's IL. It will consume all variable and push
//    nothing back onto the stack.

// We use assembly-qualified name because it is easy to load a string into the IL stack.
// It would be more semantically meaningful to use System.Type but that would require being
// able to push the proper reference onto the stack from the ILRewriter, and there's no good way
// to do this. The TypeManager is responsible for resolving the assembly-qualified name to a .NET
// type.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using DotNetFrontEnd.Contracts;
using System.Runtime.Remoting;

namespace DotNetFrontEnd
{
  /// <summary>
  /// The possible flags we may want to specify for a variable
  /// </summary>
  [Flags]
  internal enum VariableModifiers
  {
    none = 1,
    /*
    // TODO(#11): Implement programmatic program point recognition, until then a flag is used
    // as the name, and the program point is the value
    // Any value will be ignored, only the name will be printed
    program_point = none << 1,
    // The argument contains a string that will be sanitized, then printed
    string_to_print = program_point << 1,
     */
    to_string = none << 1,
    classname = to_string << 1,
    nonsensical = classname << 1,
    ignore_linked_list = nonsensical << 1,
  }

  /// <summary>
  /// The variable visitor, calls to VariableVisitor's VisitVariable() are inserted into the 
  /// program's IL. Each object is visited, its fields and/or elements are printed to the datatrace 
  /// file. This class is never instantiated, it is called statically by the program to be profiled 
  /// as it runs. Its arguments are set by the ProfilerLauncher driving the front-end.
  /// </summary>
  public static class VariableVisitor
  {
    #region Constants

    /// <summary>
    /// Name of the method called to nonsensically instrument a return value.
    /// </summary>
    public static readonly string NonsensicalReturnInstrumentationCall =
      "DoNonsensicalReturnInstrumentation";

    /// <summary>
    /// Name of the variable visitor's class. The ILRewriter needs this as a string to determine
    /// which Type to use for the instrumentation calls.
    /// </summary>
    public static readonly string VariableVisitorClassName = "VariableVisitor";

    /// <summary>
    /// Name of the function to be inserted into the IL to acquire a lock on the writer.
    /// </summary>
    public static readonly string AcquireWriterLockFunctionName = "AcquireLock";

    /// <summary>
    /// Name of the function to be inserted into the IL to release the lock on the writer.
    /// </summary>
    public static readonly string ReleaseWriterLockFunctionName = "ReleaseLock";

    /// <summary>
    /// Name of the function to be inserted into the IL to increment the call nesting depth.
    /// </summary>
    public static readonly string IncrementDepthFunctionName = "IncrementThreadDepth";

    /// <summary>
    /// Name of the function to be inserted into the IL to decrement the call nesting depth.
    /// </summary>
    public static readonly string DecrementDepthFunctionName = "DecrementThreadDepth";

    /// <summary>
    /// Name of the function to stop execution if an exception escaped reflective visiting
    /// </summary>
    public static readonly string KillFunctionName = "KillApplication";

    /// <summary>
    /// The name of the standard instrumentation method in VariableVisitor.cs, this is the method
    /// which will be called from the added instrumentation calls
    /// </summary>
    public static readonly string InstrumentationMethodName = "VisitVariable";

    /// <summary>
    /// The name of the static instrumentation method in VariableVisitor.cs. This is the method
    /// which will be called when only the static fields of a variable should be visited.
    /// </summary>
    public static readonly string StaticInstrumentationMethodName = "PerformStaticInstrumentation";

    /// <summary>
    /// The name of the special instrumentation method in VariableVisitor.cs that has parameter 
    /// order with the value of the variable before the name of the variable
    /// </summary>
    public static readonly string ValueFirstInstrumentationMethodName = "ValueFirstVisitVariable";

    /// <summary>
    /// The name of the special instrumentation method in VariableVisitor.cs that prints variables 
    /// of exception type
    /// </summary>
    public static readonly string ExceptionInstrumentationMethodName = "VisitException";

    /// <summary>
    /// The name of the method to print the a program point
    /// </summary>
    public static readonly string WriteProgramPointMethodName = "WriteProgramPoint";

    /// <summary>
    /// Name of the method to call to save assembly name and path
    /// </summary>
    public static readonly string InitializeFrontEndArgumentsMethodName = "InitializeFrontEndArgs";

    /// <summary>
    /// The name of the method to set an invocation nonce
    /// </summary>
    public static readonly string InvocationNonceSetterMethodName = "SetInvocationNonce";

    /// <summary>
    /// Name of the method to print the invocation nonce
    /// </summary>
    public static readonly string WriteInvocationNonceMethodName = "WriteInvocationNonce";

    /// <summary>
    /// Name of the method to suppress any output
    /// </summary>
    public static readonly string ShouldSuppressOutputMethodName = "SetOutputSuppression";

    /// <summary>
    /// The name of the exception variable added at the end of every method
    /// </summary>
    private static readonly string ExceptionVariableName = "exception";

    /// <summary>
    /// The type of the exception variable added at the end of every method
    /// </summary>
    private static readonly string ExceptionTypeName = "System.Exception";

    /// <summary>
    /// The value of the nonsensical modified bit
    /// </summary>
    private static readonly int NonsensicalModifiedBit = 2;

    /// <summary>
    /// Modified bit value that is always safe to print
    /// </summary>
    private static readonly int SafeModifiedBit = 1;

    #endregion

    #region Private Fields

    /// <summary>
    /// The args for the reflective visitor. Used statically by decl printer and reflector.
    /// </summary>
    private static FrontEndArgs frontEndArgs = null;

    /// <summary>
    /// TypeManager used to convert between assembly qualified and .NET types and during reflection
    /// </summary>
    private static TypeManager typeManager = null;

    /// <summary>
    /// Map from program point name to number of times that program point has occurred.
    /// </summary>
    private static Dictionary<String, int> occurenceCounts = null;

    /// <summary>
    /// Whether output for the program point currently being visited should be suppressed. Used in 
    /// sample-start. Must be set when each time a function in the source program is called,
    /// e.g. in WriteProgramPoint
    /// </summary>
    public static bool SuppressOutput { get; set; }

    /// <summary>
    /// The current nonce counter
    /// </summary>
    private static int globalNonce = 0;

    /// <summary>
    /// Lock to ensure that no more than one thread is writing at a time
    /// </summary>
    private static readonly object WriterLock = new object();

    /// <summary>
    /// Collection of static fields that have been visited during this program point
    /// </summary>
    private static readonly HashSet<string> staticFieldsVisitedForCurrentProgramPoint = new HashSet<string>();

    /// <summary>
    /// Collection of variables that have been visited during the current program point
    /// </summary>
    private static readonly HashSet<string> variablesVisitedForCurrentProgramPoint = new HashSet<string>();

    /// <summary>
    /// Depth of the thread in visit calls
    /// </summary>
    private static readonly ConcurrentDictionary<Thread, int> threadDepthMap = new ConcurrentDictionary<Thread, int>();

    /// <summary>
    /// Writer lock acquire time-out
    /// </summary>
    private const int MAX_LOCK_ACQUIRE_TIME_MILLIS = 20 * 1000;

    #endregion

    /// <summary>
    /// Set the canonical representation for refArgs. Allows decl printer and reflective visitor
    /// to reference the given args. Once this method is called the refArgs can't be changed.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown during an attempt to set args after they 
    /// have already been set.</exception>
    /// <param name="reflectionArgs">The reflection args to set.</param>
    public static FrontEndArgs ReflectionArgs
    {
      get
      {
        Contract.Ensures(Contract.Result<FrontEndArgs>() == VariableVisitor.frontEndArgs);
        return VariableVisitor.frontEndArgs;
      }
      set
      {
        Contract.Requires(ReflectionArgs == null, "Attempt to set arguments on the reflector twice.");
        Contract.Requires(value != null);
        Contract.Ensures(ReflectionArgs == value);
        Contract.Ensures(VariableVisitor.frontEndArgs == value);

        VariableVisitor.frontEndArgs = value;
        if (frontEndArgs.SampleStart != FrontEndArgs.NoSampleStart)
        {
          occurenceCounts = new Dictionary<string, int>();
        }
      }
    }

    /// <summary>
    /// Set the type manager for refArgs. Once this method is called the typeManager reference
    /// can't be changed.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown during an attempt to set the type manager
    /// after it has already been set.</exception>
    /// <param name="typeManager">The TypeManager to use for reflective visiting.</param>
    public static TypeManager TypeManager
    {
      get
      {
        Contract.Ensures(Contract.Result<TypeManager>() == VariableVisitor.typeManager);
        return VariableVisitor.typeManager;
      }
      set
      {
        Contract.Requires(VariableVisitor.TypeManager == null, "Attempt to set type manager on the reflector twice.");
        Contract.Requires(value != null);
        Contract.Ensures(VariableVisitor.TypeManager == value);
        Contract.Ensures(VariableVisitor.typeManager == value);
        VariableVisitor.typeManager = value;
      }
    }

    #region Safe (cannot throw exception) methods called from subject program

    /// <summary>
    /// Kill the application with exception <code>ex</code>
    /// </summary>
    /// <param name="ex">the exception that occured during reflective visiting</param>
    public static void KillApplication(Exception ex)
    {
      Trace.Fail(ex.Message ?? "<No Exception Message>", ex.StackTrace ?? "<No Exception Stack Trace>");
      Environment.Exit(1);
    }

    /// <summary>
    /// Acquire the lock on the PPT writer and increment the thread depth. Aborts thread if the lock
    /// cannot be acquired within a certain time frame (which would indicate deadlock).
    /// </summary>
    public static void AcquireLock()
    {
      bool acquired = false;
      var timer = Stopwatch.StartNew();
      do
      {
        if (timer.ElapsedMilliseconds > MAX_LOCK_ACQUIRE_TIME_MILLIS)
        {
          KillApplication(new TimeoutException("DEADLOCK?: Could not acquire writer lock after " + MAX_LOCK_ACQUIRE_TIME_MILLIS + " ms"));
        }
        Monitor.TryEnter(WriterLock, TimeSpan.FromSeconds(1), ref acquired);
      } while (!acquired);
    }

    /// <summary>
    /// Decrement the thread depth and release the lock on the PPT writer.
    /// </summary>
    public static void ReleaseLock()
    {
      Monitor.Exit(WriterLock);
    }

    /// <summary>
    ///  Safe call to UnsafeValueFirstVisitVariable
    /// </summary>
    public static void ValueFirstVisitVariable(object variable, string name, string typeName)
    {
      try
      {
        UnsafeValueFirstVisitVariable(variable, name, typeName);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeVisitVariable
    /// </summary>
    public static void VisitVariable(string name, object variable, string typeName)
    {
      try
      {
        UnsafeVisitVariable(name, variable, typeName);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafePerformStaticInstrumentation
    /// </summary>
    /// <param name="typeName"></param>
    public static void PerformStaticInstrumentation(string typeName)
    {
      try
      {
        UnsafePerformStaticInstrumentation(typeName);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeVisitException
    /// </summary>
    public static void VisitException(object exception)
    {
      try
      {
        UnsafeVisitException(exception);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeIncrementThreadDepth
    /// </summary>
    /// <returns></returns>
    public static bool IncrementThreadDepth()
    {
      try
      {
        return UnsafeIncrementThreadDepth();
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeDecrementThreadDepth
    /// </summary>
    public static void DecrementThreadDepth()
    {
      try
      {
        UnsafeDecrementThreadDepth();
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeSetInvocation nonce
    /// </summary>
    public static int SetInvocationNonce(string programPointName)
    {
      try
      {
        return UnsafeSetInvocationNonce(programPointName);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeWriteInvocationNonce
    /// </summary>
    /// <param name="nonce"></param>
    public static void WriteInvocationNonce(int nonce)
    {
      try
      {
        UnsafeWriteInvocationNonce(nonce);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeWriteProgramPoint
    /// </summary>
    public static void WriteProgramPoint(string programPointName, string label)
    {
      try
      {
        UnsafeWriteProgramPoint(programPointName, label);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Safe call to UnsafeInitializeFrontEndArgs
    /// </summary>
    public static void InitializeFrontEndArgs(string arguments)
    {
      try
      {
        UnsafeInitializeFrontEndArgs(arguments);
      }
      catch (Exception ex)
      {
        KillApplication(ex);
        throw; // (dead code)
      }
    }

    /// <summary>
    /// Will fake instrument a variable of the provided type, printing non-sensical for all values.
    /// Used to print return values during exceptional exits.
    /// </summary>
    /// <param name="typeName">Name of the type of the return value to instrument</param>
    public static void DoNonsensicalReturnInstrumentation(string typeName)
    {
      VariableVisitor.DoVisit(null, "return", typeName, VariableModifiers.nonsensical);
    }


    #endregion

    #region Unsafe (can throw an exception) implementations of methods called by subject program

    /// <summary>
    /// Returns the current PPT visiting nesting depth for the current thread.
    /// </summary>
    /// <returns>The current PPT visiting nesting depth for the current thread.</returns>
    [Pure]
    public static int NestingDepth()
    {
      Contract.Ensures(Contract.Result<int>() > 0);
      int x;
      threadDepthMap.TryGetValue(Thread.CurrentThread, out x);
      return x;
    }

    /// <summary>
    /// Special version of VisitVariable with different parameter ordering for convenient calling 
    /// convention compatibility.
    /// </summary>
    /// <param name="variable">The object</param>
    /// <param name="name">The name of the variable</param>
    /// <param name="typeName">Description of the type</param>
    private static void UnsafeValueFirstVisitVariable(object variable, string name, string typeName)
    {
      // Make the traditional call, rearrange the variables.
      DoVisit(variable, name, typeName);
    }

    /// <summary>
    /// Open the file writer if necessary, then print out the given variable.
    /// </summary>
    /// <param name="name">The name of the variable</param>
    /// <param name="variable">The value of a variable</param>
    /// <param name="declaredType">The declared type of the variable</param>
    private static void UnsafeVisitVariable(string name, object variable, string typeName)
    {
      // Make the traditional call, nothing special
      DoVisit(variable, name, typeName);
    }

    /// <summary>
    /// Visit all the static variables of the given type.
    /// </summary>
    /// <param name="typeName">Assembly-qualified name of the type to visit.</param>
    private static void UnsafePerformStaticInstrumentation(string typeName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(typeName));
      Contract.Requires(NestingDepth() == 1, "Illegal PPT callback from thread in nested call");

      using (var writer = InitializeWriter())
      {
        DNFETypeDeclaration typeDecl = typeManager.ConvertAssemblyQualifiedNameToType(typeName);

        foreach (Type type in typeDecl.GetAllTypes)
        {
          foreach (FieldInfo staticField in
            // Pass type in as originating type so we get all the fields.
            type.GetSortedFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(type, type)))
          {
            string staticFieldName = type.FullName + "." + staticField.Name;
            if (!typeManager.ShouldIgnoreField(type, staticField) &&
                !staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
            {
              staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
              // TODO(#68):
              // Static fields of generic types cause an exception, so don't visit them
              object val = type.ContainsGenericParameters ? null : staticField.GetValue(null);
              VariableModifiers flags = type.ContainsGenericParameters ? VariableModifiers.nonsensical : VariableModifiers.none;
              ReflectiveVisit(staticFieldName, val,
                    staticField.FieldType, writer, staticFieldName.Count(c => c == '.'),
                    type,
                    fieldFlags: flags);
            }
          }
        }
      }
    }

    /// <summary>
    /// Perform DoVisit, except where the exception is null.
    /// Null exceptions should actually be treated as non-sensical.
    /// </summary>
    /// <param name="exception">An exception to reflectively visit</param>
    private static void UnsafeVisitException(object exception)
    {
      // If the exception is null we actually want to print it as non-sensical.
      VariableModifiers flags = exception == null ? VariableModifiers.nonsensical : VariableModifiers.none;
      // Make the traditional call, possibly add the nonsensicalElements flag.
      // Also, downcast everything to System.Object so we call GetType().
      // TODO(#15): Allow more inspection of exceptions
      DoVisit(exception, ExceptionVariableName, ExceptionTypeName, flags);
    }

    /// <summary>
    /// Increment the depth count for the current thread. Returns <code>true</code> if the ppt
    /// should be visit, that is the depth count is 1.
    /// </summary>
    /// <returns></returns>
    private static bool UnsafeIncrementThreadDepth()
    {
      return threadDepthMap.AddOrUpdate(Thread.CurrentThread, 1, (t, x) => x + 1) == 1;
    }

    /// <summary>
    /// Decrement the depth count for the current thread.
    /// </summary>
    private static void UnsafeDecrementThreadDepth()
    {
      if (threadDepthMap.AddOrUpdate(Thread.CurrentThread, 0, (t, x) => x - 1) == 0)
      {
        int value;
        threadDepthMap.TryRemove(Thread.CurrentThread, out value);
        Contract.Assume(value == 0, "Atomicity violation when decrementing thread depth.");
      }
    }

    /// <summary>
    /// Set the invocation nonce for this method by storing it in an appropriate local var
    /// </summary>
    /// <returns>The invocation nonce for the method</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "programPointName")]
    private static int UnsafeSetInvocationNonce(string programPointName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(programPointName));
      return Interlocked.Increment(ref globalNonce);
    }

    /// <summary>
    /// Write the invocation nonce for the current method
    /// </summary>
    /// <param name="nonce">Nonce-value to print</param>
    private static void UnsafeWriteInvocationNonce(int nonce)
    {
      Contract.Requires(NestingDepth() == 1, "Illegal PPT callback from thread in nested call");

      using (var writer = InitializeWriter())
      {
        writer.WriteLine("this_invocation_nonce");
        writer.WriteLine(nonce);
      }
    }

    /// <summary>
    /// Write the given program point, sanitizing the name if necessary
    /// </summary>
    /// <param name="programPointName">Name of program point to print</param>
    /// <param name="label">Label used to differentiate the specific program point from other with 
    /// the same name.</param>
    private static void UnsafeWriteProgramPoint(string programPointName, string label)
    {
      Contract.Requires(NestingDepth() == 1, "Illegal PPT callback from thread in nested call");
      Contract.Requires(!string.IsNullOrWhiteSpace(programPointName));

      staticFieldsVisitedForCurrentProgramPoint.Clear();
      variablesVisitedForCurrentProgramPoint.Clear();

      if (frontEndArgs.SampleStart != FrontEndArgs.NoSampleStart)
      {
        int oldOccurrences;
        occurenceCounts.TryGetValue(programPointName, out oldOccurrences);
        if (oldOccurrences > 0)
        {
          // If we've seen less than SampleStart print all.
          // After we've seen the first SampleStart print 10%.
          // For each subsequent SampleStart decrease printing by a factor of 10.
          // TODO(#40): Optimize this computation.
          SuppressOutput = oldOccurrences % (int)Math.Pow(10, (oldOccurrences / frontEndArgs.SampleStart)) != 0;
        }
        occurenceCounts[programPointName] = oldOccurrences + 1;
      }

      using (var writer = InitializeWriter())
      {
        writer.WriteLine();
        programPointName =
           DeclarationPrinter.SanitizeProgramPointName(programPointName) + (label ?? String.Empty);
        writer.WriteLine(programPointName);
      }
    }

    /// <summary>
    /// When the program is run in offline mode, that is with a saved binary, the first time
    /// the variable visitor is entered we need to determine the name and path of the assembly
    /// being profiled. This is necessary so the .NET type resolve can locate and load the types in 
    /// the assembly being profiled.
    /// </summary>
    /// <remarks>Called from DNFE_ArgumentStroingMethod</remarks>
    private static void UnsafeInitializeFrontEndArgs(string arguments)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(arguments));
      Contract.Ensures(frontEndArgs != null);
      Contract.Ensures(typeManager != null);

      if (frontEndArgs == null)
      {
        frontEndArgs = new FrontEndArgs(arguments.Split());
        if (frontEndArgs.AutoDetectPure)
        {
          frontEndArgs.AddAutoDetectedPureMethods();
        }
        typeManager = new TypeManager(frontEndArgs);
      }
    }

    #endregion

    /// <summary>
    /// Common visit function, called by all public interfaces to VariableVisitor.
    /// </summary>
    /// <param name="variable">Variable to visit</param>
    /// <param name="name">Name of the function as specified by the user, or by the object's 
    /// path</param>
    /// <param name="typeName">Assembly-qualified name of the object's type as a string</param>
    /// <param name="flags">Flags providing additional information about the variable.</param>
    private static void DoVisit(object variable, string name, string typeName,
        VariableModifiers flags = VariableModifiers.none)
    {

      Contract.Requires(flags.HasFlag(VariableModifiers.nonsensical).Implies(variable == null));
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(!string.IsNullOrWhiteSpace(typeName));
      Contract.Requires(NestingDepth() == 1, "Illegal PPT callback from thread in nested call");
      Contract.Ensures(SuppressOutput == false);

      // TODO(#15): Can we pull any fields from exceptions?
      // Exceptions shouldn't be recursed any further down than the 
      // variable itself, because we only know that they are an exception.
      DNFETypeDeclaration typeDecl = typeManager.ConvertAssemblyQualifiedNameToType(typeName);

      using (var writer = InitializeWriter())
      {
        foreach (Type type in typeDecl.GetAllTypes)
        {
          if (type == null)
          {
            writer.WriteLine(name);
            writer.WriteLine("nonsensical");
            writer.WriteLine(2);
            return;
          }

          int depth = 0;

          var originatingType = name.Equals("this") ? type : typeof(DummyOriginator);
          ReflectiveVisit(name, variable, type, writer, depth, originatingType, flags);
        }
      }
    }

    /// <summary>
    /// Get a datatrace writer. Caller is responsible for closing if necessary.
    /// Warning suppressed because caller takes responsibility for disposing.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability",
        "CA2000:Dispose objects before losing scope")]
    private static TextWriter InitializeWriter()
    {
      Contract.Ensures(Contract.Result<TextWriter>() != null);
      Contract.Ensures(frontEndArgs.PrintOutput.Implies(Contract.Result<TextWriter>() == System.Console.Out));
      Contract.Ensures(SuppressOutput.Implies(Contract.Result<TextWriter>() == TextWriter.Null));

      if (SuppressOutput)
      {
        return TextWriter.Null;
      }

      TextWriter writer;
      // Append to the file since we are writing one object at a time
      if (frontEndArgs.PrintOutput)
      {
        writer = System.Console.Out;
      }
      else
      {
        string dirName = Path.GetDirectoryName(Path.GetFullPath(frontEndArgs.OutputLocation));
        if (!Directory.Exists(dirName))
        {
          Directory.CreateDirectory(dirName);
        }

        if (AppDomain.CurrentDomain.IsDefaultAppDomain())
        {
          writer = new StreamWriter(frontEndArgs.OutputLocation, true);
        }
        else
        {
          // Insert the domain name: <basename>.<domain>.dtrace
          var domain = AppDomain.CurrentDomain.FriendlyName ?? AppDomain.CurrentDomain.Id.ToString();
          var file = string.Join(".", Path.GetFileNameWithoutExtension(frontEndArgs.OutputLocation), domain, "dtrace");
          var path = Path.Combine(Path.GetDirectoryName(frontEndArgs.OutputLocation), file);
          writer = new StreamWriter(path, true);
        }
      }

      if (frontEndArgs.ForceUnixNewLine)
      {
        // Force UNIX new-line convention
        writer.NewLine = "\n";
      }

      return writer;
    }

    #region Reflective Visitor and helper methods

    /// <summary>
    /// Print the 3 line (name, value, mod bit) triple for the given variable and all its 
    /// children.
    /// </summary>
    /// <param name="name">Name of the variable to print</param>
    /// <param name="obj">The variable to print</param>
    /// <param name="type">The declared type of the variable</param>
    /// <param name="writer">The writer to use to print</param>
    /// <param name="depth">The current call depth</param>
    /// <param name="fieldFlags">Flags indicating special printing</param>
    private static void ReflectiveVisit(string name, object obj, Type type,
        TextWriter writer, int depth, Type originatingType,
        VariableModifiers fieldFlags = VariableModifiers.none)
    {
      Contract.Requires(fieldFlags.HasFlag(VariableModifiers.nonsensical).Implies(obj == null));
      Contract.Requires(type != null);
      Contract.Requires(writer != null);
      Contract.Requires(depth >= 0);

      if (PerformEarlyExitChecks(name, depth))
      {
        return;
      }

      PrintSimpleVisitFields(name, obj, type, writer, fieldFlags);

      // Don't visit the fields of variables with certain flags.
      if (fieldFlags.HasFlag(VariableModifiers.to_string) ||
          fieldFlags.HasFlag(VariableModifiers.classname))
      {
        return;
      }

      if (typeManager.IsListImplementer(type))
      {
        ProcessVariableAsList(name, obj, type, writer, depth, originatingType);
      }
      else if (typeManager.IsFSharpListImplementer(type))
      {
        object[] result = obj == null ? null : TypeManager.ConvertFSharpListToCSharpArray(obj);

        ProcessVariableAsList(name, result, result == null ? null : result.GetType(),
            writer, depth, originatingType);
      }
      else if (typeManager.IsSet(type) || typeManager.IsFSharpSet(type))
      {
        IEnumerable set = (IEnumerable)obj;
        // A set can have only one generic argument -- the element type
        Type setElementType = type.GetGenericArguments()[0];
        // We don't statically know the element type or array length so create a temporary list
        // that can take objects of any type and have any length. Then create an array of the
        // proper length and type and hand that off.
        IList result = new ArrayList();
        if (set != null)
        {
          foreach (object item in set)
          {
            result.Add(item);
          }
        }
        Array convertedList = Array.CreateInstance(setElementType, result.Count);
        result.CopyTo(convertedList, 0);
        ProcessVariableAsList(name, convertedList, convertedList.GetType(), writer, depth,
          originatingType);
      }
      else if (typeManager.IsDictionary(type))
      {
        List<DictionaryEntry> entries = new List<DictionaryEntry>();
        if (obj != null)
        {
          IDictionary dict = (IDictionary)obj;
          foreach (DictionaryEntry entry in dict)
          {
            entries.Add(entry);
          }
        }

        ProcessVariableAsList(name, entries, entries.GetType(), writer, depth, originatingType);
      }
      else if (typeManager.IsFSharpMap(type))
      {
        ArrayList entries = new ArrayList();
        if (obj != null)
        {
          foreach (var item in (IEnumerable)obj)
          {
            entries.Add(item);
          }
        }
        ProcessVariableAsList(name, entries, entries.GetType(), writer, depth, originatingType);
      }
      else
      {
        PerformNonListInspection(name, obj, type, writer, depth, fieldFlags, originatingType);
      }
    }

    /// <summary>
    /// Perform full inspection for non list variables, printing necessary
    /// values to the given writer. Includes children if any, and toString
    /// or HashCode calls.
    /// </summary>
    private static void PerformNonListInspection(string name, object obj,
      Type type, TextWriter writer, int depth, VariableModifiers fieldFlags, Type originatingType)
    {
      if (obj == null)
      {
        fieldFlags |= VariableModifiers.nonsensical;
      }

      PrintFieldValues(name, obj, type, writer, depth, fieldFlags, originatingType);

      if (!type.IsSealed)
      {
        object xType = (obj == null ? null : RuntimeType(obj));
        ReflectiveVisit(name + '.' + DeclarationPrinter.GetTypeMethodCall, xType,
            TypeManager.TypeType, writer, depth + 1, originatingType,
            (obj == null ? VariableModifiers.nonsensical : VariableModifiers.none)
            | VariableModifiers.classname);
      }

      if (type == TypeManager.StringType)
      {
        object xString = (obj == null ? null : obj.ToString());
        ReflectiveVisit(name + '.' + DeclarationPrinter.ToStringMethodCall, xString,
            TypeManager.StringType, writer, depth + 1, originatingType,
            fieldFlags | VariableModifiers.to_string);
      }

      if (!SuppressOutput)
      {
        foreach (var method in typeManager.GetPureMethodsForType(type, originatingType))
        {
          var exprName = DeclarationPrinter.SanitizedMethodExpression(method, name);
          
          if (method.IsStatic && staticFieldsVisitedForCurrentProgramPoint.Contains(exprName))
          {
            continue;
          }

          object value = null;
          var valueFlags = fieldFlags;
          
          try
          {
            if (obj == null || !TryGetMethodValue(obj, method, exprName, out value))
            {
              valueFlags |= VariableModifiers.nonsensical;
            }
          }
          catch
          {
            if (frontEndArgs.RobustMode)
            {
              valueFlags |= VariableModifiers.nonsensical;
            }
            else
            {
              throw;
            }
          }

          ReflectiveVisit(exprName, value, method.ReturnType, 
            writer, depth + 1, originatingType, fieldFlags: fieldFlags);
        }
      }

      // Don't look at linked-lists of synthetic variables to prevent children from also printing
      // linked-lists, when they are really just deeper levels of the current linked list.
      if ((fieldFlags & VariableModifiers.ignore_linked_list) == 0 &&
          frontEndArgs.LinkedLists && typeManager.IsLinkedListImplementer(type))
      {
        FieldInfo linkedListField = TypeManager.FindLinkedListField(type);
        // Synthetic list of the sequence of linked list values
        IList<object> expandedList = new List<object>();
        Object curr = obj;
        while (curr != null)
        {
          expandedList.Add(curr);
          curr = GetFieldValue(curr, linkedListField, linkedListField.Name);
        }
        ListReflectiveVisit(name + "[..]", (IList)expandedList, type, writer, depth,
            originatingType, fieldFlags);
      }
    }

    /// <summary>
    /// Print the values of the variable's instance and static fields.
    /// </summary>
    /// <param name="name">Name of the variable</param>
    /// <param name="obj">Value of the variable</param>
    /// <param name="type">Type of the variable</param>
    /// <param name="writer">Writer to use when printing</param>
    /// <param name="depth">Depth of the variable</param>
    /// <param name="fieldFlags">Flags describing the current variable</param>
    private static void PrintFieldValues(string name, object obj, Type type,
      TextWriter writer, int depth, VariableModifiers fieldFlags, Type originatingType)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null);
      Contract.Requires(writer != null);
      Contract.Requires(depth >= 0);

      foreach (FieldInfo field in
          type.GetSortedFields(frontEndArgs.GetInstanceAccessOptionsForFieldInspection(
            type, originatingType)))
      {
        if (!typeManager.ShouldIgnoreField(type, field))
        {
          ReflectiveVisit(name + "." + field.Name, GetFieldValue(obj, field, field.Name),
              field.FieldType, writer, depth + 1, originatingType,
              fieldFlags | VariableModifiers.ignore_linked_list);
        }
      }

      foreach (FieldInfo staticField in
          type.GetSortedFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(
              type, originatingType)))
      {
        string staticFieldName = type.FullName + "." + staticField.Name;
        if (!typeManager.ShouldIgnoreField(type, staticField) &&
            !staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
        {
          staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
          ReflectiveVisit(staticFieldName, GetFieldValue(obj, staticField, staticField.Name),
                staticField.FieldType, writer, staticFieldName.Count(c => c == '.'),
                originatingType, fieldFlags | VariableModifiers.ignore_linked_list);
        }
      }
    }

    /// <summary>
    /// Print simple fields of the object (e.g. name, type, etc.). Specifically excludes
    /// printing of the object's fields.
    /// </summary>
    /// <param name="name">Name of the variables</param>
    /// <param name="obj">Variable's value</param>
    /// <param name="type">Type of the variable</param>
    /// <param name="writer">Writer used to print the variable</param>
    /// <param name="fieldFlags">Flags for the field, if any</param>
    private static void PrintSimpleVisitFields(string name, object obj, Type type,
      TextWriter writer, VariableModifiers fieldFlags)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null);
      Contract.Requires(writer != null);

      // Print variable's name.
      writer.WriteLine(name);

      // Print variable's value.
      writer.WriteLine(GetVariableValue(obj, type, fieldFlags, name));

      // Print variable's modified bit.
      // Stub implementation, always print safe value.
      if (fieldFlags.HasFlag(VariableModifiers.nonsensical))
      {
        writer.WriteLine(NonsensicalModifiedBit);
      }
      else
      {
        writer.WriteLine(SafeModifiedBit);
      }
    }

    /// <summary>
    /// Checks whether the front end should exit early due to already visiting the variable,
    /// for exceeding depth, or for user-suppressed variables. Updates the list of variables
    /// visited for this program point to include this one.
    /// </summary>
    /// <param name="name">Name of the variable to be potentially instrumented</param>
    /// <param name="depth">Nesting depth of the variable</param>
    /// <returns>True if the variable should be skipped, otherwise false</returns>
    private static bool PerformEarlyExitChecks(string name, int depth)
    {
      if (variablesVisitedForCurrentProgramPoint.Contains(name))
      {
        return true;
      }
      variablesVisitedForCurrentProgramPoint.Add(name);
      return depth > frontEndArgs.MaxNestingDepth || !frontEndArgs.ShouldPrintVariable(name);
    }

    /// <summary>
    /// Process the given variable of list type, calling GetType if necessary and visiting 
    /// the children elements.
    /// </summary>
    /// <param name="name">Name of the variable</param>
    /// <param name="obj">List being visited</param>
    /// <param name="type">Type of the list</param>
    /// <param name="writer">Writer to output to</param>
    /// <param name="depth">Depth of the list variable</param>
    /// <param name="flags">Field flags for the list variable</param>
    private static void ProcessVariableAsList(string name, object obj, Type type,
        TextWriter writer, int depth, Type originatingType,
        VariableModifiers flags = VariableModifiers.none)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null);
      Contract.Requires(writer != null);
      Contract.Requires(obj == null || obj is IList, "Can't list reflective visit something that isn't a list");

      // Call GetType() on the list if necessary.
      Type elementType = TypeManager.GetListElementType(type);
      if (!elementType.IsSealed)
      {
        ReflectiveVisit(name + "." + DeclarationPrinter.GetTypeMethodCall, type,
            TypeManager.TypeType, writer, depth + 1, originatingType,
            VariableModifiers.classname);
      }

      if (type.IsArray && type.GetArrayRank() > 1)
      {
        // Daikon can't handle multidimensional arrays, so we skip them.
        return;
      }

      // Now visit each element.
      // Element inspection is at the same depth as the list.
      // null lists won't cast but we don't want to throw an exception for that
      ListReflectiveVisit(name + "[..]", (IList)obj, elementType, writer,
          depth, originatingType, flags);

    }

    /// <summary>
    /// Print the 3 line (name, array, mod bit) triple, traversing list, printing each value,
    /// and the child calls for each value
    /// </summary>
    /// <param name="name">The name of the list to print</param>
    /// <param name="list">The list to print</param>
    /// <param name="elementType">The type of the list's elements</param>
    /// <param name="writer">The writer to write with</param>
    /// <param name="depth">The depth of the current visitation</param>
    /// <param name="flags">The flags for the list, if any</param>
    /// <param name="nonsensicalElements">If any elements are non-sensical, an array 
    /// indicating which ones are</param>
    private static void ListReflectiveVisit(string name, IList list, Type elementType,
        TextWriter writer, int depth, Type originatingType,
        VariableModifiers flags = VariableModifiers.none,
        bool[] nonsensicalElements = null)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(writer != null);
      Contract.Requires(depth >= 0); // TWS: why shouldn't this be >= 1?
      Contract.Requires(nonsensicalElements == null || nonsensicalElements.Length == list.Count);

      if (depth > frontEndArgs.MaxNestingDepth || !frontEndArgs.ShouldPrintVariable(name))
      {
        return;
      }

      // We might not know the type, e.g. for non-generic ArrayList
      elementType = elementType ?? TypeManager.ObjectType;

      // Don't inspect fields on some calls.
      bool simplePrint = flags.HasFlag(VariableModifiers.to_string) ||
           flags.HasFlag(VariableModifiers.classname);

      // Write name of the list, which possibly is its path.
      writer.WriteLine(name);

      // If a reference to a list is null the reference is nonsensical.
      if (list == null)
      {
        flags |= VariableModifiers.nonsensical;
        // Write nonsensical
        writer.WriteLine(GetVariableValue(list, elementType, flags, name));
        // Write the mod bit
        writer.WriteLine(NonsensicalModifiedBit);
      }
      else
      {
        // Write the values of each element in the list, space separated, on a single line in 
        // brackets.
        StringBuilder builder = new StringBuilder();
        builder.Append("[");
        for (int i = 0; i < list.Count; i++)
        {
          VariableModifiers elementFlags = VariableModifiers.none;
          if (nonsensicalElements != null && nonsensicalElements[i])
          {
            elementFlags = VariableModifiers.nonsensical;
          }
          builder.Append(GetVariableValue(list[i], elementType, flags | elementFlags, name) + " ");
        }

        // Strip off the trailing space before adding the ], only if we ever added a space
        if (builder.Length > 1)
        {
          builder.Remove(builder.Length - 1, 1);
        }
        builder.Append("]");
        writer.WriteLine(builder);

        // Write mod bit. Dummy implementation; always writes safe value.
        writer.WriteLine(SafeModifiedBit);
      }

      if (simplePrint)
      {
        return;
      }

      if (typeManager.IsListImplementer(elementType))
      {
        // Daikon can't handle nested arrays. Just skip silently.
        return;
      }
      else if (typeManager.IsFSharpListImplementer(elementType))
      {
        // Daikon can't handle nested lists. Skip silently.
        return;
      }

      if (list == null)
      {
        // If the list was null then we can't visit the children, just print nonsensical for them.
        VisitNullListChildren(name, elementType, writer, depth, originatingType);
      }
      else
      {
        nonsensicalElements = nonsensicalElements ?? new bool[list.Count];
        VisitListChildren(name, list, elementType, writer, depth, nonsensicalElements, originatingType);
      }
    }

    /// <summary>
    /// Visit all children of a <code>null</code> list. Prints <code>nonsensical</code> for each variable value.
    /// </summary>
    /// <param name="name">name of the null list</param>
    /// <param name="elementType">Type of the elements that would be in the list</param>
    /// <param name="writer">Writer to write the results of the visit using to</param>
    /// <param name="depth">Current nesting depth</param>
    private static void VisitNullListChildren(string name, Type elementType,
        TextWriter writer, int depth, Type originatingType)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(writer != null);
      Contract.Requires(depth >= 0); // TWS: why shouldn't this be >= 1?

      foreach (FieldInfo field in
          elementType.GetSortedFields(frontEndArgs.GetInstanceAccessOptionsForFieldInspection(
              elementType, originatingType)))
      {
        if (!typeManager.ShouldIgnoreField(elementType, field))
        {
          ListReflectiveVisit(name + "." + field.Name, null,
            field.FieldType, writer, depth + 1, originatingType);
        }
      }

      foreach (FieldInfo staticElementField in
          elementType.GetSortedFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(
              elementType, originatingType)))
      {
        string staticFieldName = elementType.FullName + "." + staticElementField.Name;
        if (!typeManager.ShouldIgnoreField(elementType, staticElementField) &&
            !staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
        {
          staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
          ListReflectiveVisit(staticFieldName, null, staticElementField.FieldType, writer,
              staticFieldName.Count(c => c == '.'), originatingType);
        }
      }

      if (!elementType.IsSealed)
      {
        ListReflectiveVisit(name + "." + DeclarationPrinter.GetTypeMethodCall, null,
            TypeManager.TypeType, writer, depth + 1, originatingType,
            VariableModifiers.classname);
      }

      if (elementType == TypeManager.StringType)
      {
        ListReflectiveVisit(name + "." + DeclarationPrinter.ToStringMethodCall, null,
            TypeManager.StringType, writer, depth + 1, originatingType,
            VariableModifiers.to_string);
      }

      foreach (var method in typeManager.GetPureMethodsForType(elementType, originatingType))
      {
        // TODO 83: don't skip static methods for lists
        if (method.IsStatic)
        {
          continue;
        }

        ListReflectiveVisit(DeclarationPrinter.SanitizedMethodExpression(method, name), null,
          method.ReturnType, writer, depth + 1, originatingType);
      }
    }

    /// <summary>
    /// Visit the children of a non-null array, printing the value of each element's children.
    /// </summary>
    /// <param name="list">The list whose children to visit</param>
    /// <param name="nonsensicalElements">Indicator of which elements in the list are 
    /// nonsensical</param>
    /// <param name="name">name of the null array</param>
    /// <param name="elementType">Type of the elements that would be in the list</param>
    /// <param name="writer">Writer to write the results of the visit using to</param>
    /// <param name="depth">Current nesting depth</param>
    private static void VisitListChildren(string name, IList list, Type elementType,
        TextWriter writer, int depth, bool[] nonsensicalElements, Type originatingType)
    {
      Contract.Requires(list != null, "Cannot visit children of null list");
      Contract.Requires(list.Count == nonsensicalElements.Length, "Length mismatch between list length and non-sensical array");
      Contract.Requires(depth >= 0);
      Contract.Requires(writer != null);

      foreach (FieldInfo elementField in
          elementType.GetSortedFields(frontEndArgs.GetInstanceAccessOptionsForFieldInspection(
              elementType, originatingType)))
      {
        if (!typeManager.ShouldIgnoreField(elementType, elementField))
        {
          VisitListField(name, list, elementType, writer, depth, nonsensicalElements, elementField,
              originatingType);
        }
      }

      // Static fields will have the same value for every element so just visit them once
      foreach (FieldInfo elementField in
          elementType.GetSortedFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(
              elementType, originatingType)))
      {
        string staticFieldName = elementType.FullName + "." + elementField.Name;
        if (!typeManager.ShouldIgnoreField(elementType, elementField) &&
            !staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
        {
          staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
          ReflectiveVisit(staticFieldName, elementField.GetValue(null),
              elementField.FieldType, writer, staticFieldName.Count(c => c == '.'),
              originatingType);
        }
      }

      if (!elementType.IsSealed)
      {
        Type[] typeArray = new Type[list.Count];
        bool[] typeNonsensical = new bool[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
          typeNonsensical[i] = nonsensicalElements[i] || list[i] == null;
          typeArray[i] = typeNonsensical[i] ? null : list[i].GetType();
        }
        ListReflectiveVisit(name + "." + DeclarationPrinter.GetTypeMethodCall, typeArray,
            TypeManager.TypeType, writer, depth + 1, originatingType, VariableModifiers.classname,
            nonsensicalElements: typeNonsensical);
      }

      if (elementType == TypeManager.StringType)
      {
        string[] stringArray = new string[list.Count];
        bool[] stringNonsensical = new bool[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
          stringNonsensical[i] = nonsensicalElements[i] || list[i] == null;
          stringArray[i] = stringNonsensical[i] ? null : list[i].ToString();
        }
        ListReflectiveVisit(name + "." + DeclarationPrinter.ToStringMethodCall, stringArray,
            TypeManager.StringType, writer, depth + 1, originatingType, VariableModifiers.to_string,
            nonsensicalElements: stringNonsensical);
      }

      foreach (var method in typeManager.GetPureMethodsForType(elementType, originatingType))
      {
        // TODO 83: don't skip static methods for lists
        if (method.IsStatic)
        {
          continue;
        }

        var exprName = DeclarationPrinter.SanitizedMethodExpression(method, name);

        bool[] pureNonsensical = new bool[list.Count];
        object[] pureMethodResults = new object[list.Count];

        for (int i = 0; i < list.Count; i++)
        {
          pureNonsensical[i] = nonsensicalElements[i] || list[i] == null;
          pureMethodResults[i] = null;

          try
          {
            if (pureNonsensical[i] || !TryGetMethodValue(list[i], method, exprName, out pureMethodResults[i]))
            {
              pureNonsensical[i] = true;
            }
          }
          catch
          {
            if (frontEndArgs.RobustMode)
            {
              pureNonsensical[i] = true;
            }
            else
            {
              throw;
            }
          }
        }
        ListReflectiveVisit(exprName, pureMethodResults, method.ReturnType, 
          writer, depth + 1, originatingType, nonsensicalElements: pureNonsensical);
      }
    }

    /// <summary>
    /// Visit a given field for each element of a given list.
    /// </summary>
    /// <param name="name">Name of the list variable</param>
    /// <param name="list">The actual list variable</param>
    /// <param name="elementType">The type of the element of the list</param>
    /// <param name="writer">The writer used to print the visitation results</param>
    /// <param name="depth">The depth the fields will be printed at</param>
    /// <param name="nonsensicalElements">An array indicating corresponding 
    /// locations in the list containing non-sensical elements</param>
    /// <param name="elementField">The field to be inspected</param>
    private static void VisitListField(string name, IList list, Type elementType, TextWriter writer,
        int depth, bool[] nonsensicalElements, FieldInfo elementField, Type originatingType)
    {
      Contract.Requires(list != null, "Cannot visit children of null list");
      Contract.Requires(list.Count == nonsensicalElements.Length, "Length mismatch between list length and non-sensical array");
      Contract.Requires(depth >= 0);
      Contract.Requires(writer != null);

      // If we have a non-null list then build an array comprising the calls on each 
      // element. The array must be object type so it can take any children.
      Array childArray = Array.CreateInstance(TypeManager.ObjectType, list.Count);
      for (int i = 0; i < list.Count; i++)
      {
        childArray.SetValue(GetFieldValue(list[i], elementField, elementField.Name), i);
        nonsensicalElements[i] = (list[i] == null);
      }
      ListReflectiveVisit(name + "." + elementField.Name, childArray,
          elementField.FieldType, writer, depth + 1, originatingType,
          nonsensicalElements: nonsensicalElements);
    }

    /// <summary>
    /// Returns the hashcode for a value, using reference-based hashcode for reference types and a 
    /// value-based hashcode for value types.
    /// </summary>
    /// <param name="x">the value</param>
    /// <param name="type">the type to use when determining how to print</param>
    /// <returns>the hashcode for the value</returns>
    private static string GetHashCode(object x, Type type, string name)
    {
      Contract.Requires(type != null);
      Contract.Requires(x != null);
      Contract.Requires(!string.IsNullOrEmpty(name));
      Contract.Ensures(!string.IsNullOrWhiteSpace(Contract.Result<string>()));

      Contract.Assume(type != typeof(void), 
        "GetHashCode: declared type is void; expression: " + name + " runtime type: " + x.GetType().FullName);

      var xType = RuntimeType(x);

      if (type.IsValueType || type == typeof(ValueType))
      {
        // Use a value-based hashcode for value types
        Contract.Assert(xType.IsValueType,
          "Runtime value is not a value type. Expression: " + name + "Runtime Type: " + xType.ToString() + " Declared: " + type.Name);
        return x.GetHashCode().ToString(CultureInfo.InvariantCulture);
      }
      else if (xType.IsValueType && (type.IsGenericParameter || type.IsInterface))
      {
        // C#'s type system will enforce reasonable comparability for generic parameters and interfaces
        // TODO: explicitly check to see if generic parameter extends value type
        return x.GetHashCode().ToString(CultureInfo.InvariantCulture);
      }
      else
      {
        if (!xType.IsValueType)
        {
          // Use a reference-based hashcode for reference types
          // TODO: does this work properly for proxies? i.e., do we want the reference hash of the 
          // transparent proxy, or the reference it's proxying?
          return RuntimeHelpers.GetHashCode(x).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
          // Assume there's a type mismatch because we didn't have any type information.
          Contract.Assume(type.Equals(typeof(object)),
               "Runtime value is not a reference type. Runtime Type: " + x.GetType().ToString() + " Declared: " + type.Name);
          // Use the value's hashcode and hope it does something reasonable; the type system + comparability 
          // analysis should avoid spurious comparisons
          return x.GetHashCode().ToString(CultureInfo.InvariantCulture);
        }
      }
    }

    /// <summary>
    /// Generate a string representation of the value of variable, based on its type.
    /// </summary>
    /// <param name="variable">The object to print</param>
    /// <param name="type">The type to be used when determining how to print</param>
    /// <param name="flags">Any flags for the variable</param>
    /// <returns>String containing a daikon-valid representation of the variable's value</returns>
    private static string GetVariableValue(object x, Type type, VariableModifiers flags, string name)
    {
      Contract.Requires(type != null);
      Contract.Requires(!string.IsNullOrEmpty(name));

      Contract.Assume(type != typeof(void), 
        "Declared type of expression is void; expression: " + name + " runtime type: " + (x == null ? "<null>" : x.GetType().FullName));

      if (flags.HasFlag(VariableModifiers.nonsensical))
      {
        return "nonsensical";
      }
      else if (x == null)
      {
        return "null";
      }
      else if (type.IsEnum)
      {
        if (frontEndArgs.EnumUnderlyingValues)
        {
          return Convert.ChangeType(x, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture).ToString();
        }
        else
        {
          // Type is an enum, print out it's hash. Since were using a hashcode rep-type, we need to make sure the
          // hashcode is non-zero.
          SuppressOutput = true;
          int hash = type.GetHashCode() + x.GetHashCode();
          hash = hash == 0 ? hash + 1 : hash;
          string enumHash = hash.ToString(CultureInfo.InvariantCulture);
          SuppressOutput = false;
          return enumHash;
        }
      }
      else if (flags.HasFlag(VariableModifiers.classname) ||
          flags.HasFlag(VariableModifiers.to_string))
      {
        return PrepareString(x.ToString());
      }
      else if (type.IsValueType)
      {
        try
        {
            if (type == TypeManager.BooleanType)
            {
                return ((bool)x) ? "true" : "false";
            }
            else if (type == TypeManager.CharType)
            {
                return ((int)(char)x).ToString(CultureInfo.InvariantCulture);
            }
            else if (TypeManager.IsAnyNumericType(type))
            {
                return x.ToString();
            }
        }
        catch
        {
            Contract.Assume(false, "Error getting value " + name + "; expected: " + type.FullName + " actual: " +x.GetType().FullName);
        }
      }

      // Type is either an object or a user-defined struct, print out its hashcode.
      SuppressOutput = true;
      string hashcode = GetHashCode(x, type, name);
      SuppressOutput = false;
      return hashcode;

    }

    /// <summary>
    /// Reflectively get the value in fieldName from obj, null if obj is null.
    /// </summary>
    /// <param name="obj">Object to inspect</param>
    /// <param name="field">Field to get the value of</param>
    /// <param name="fieldName">Name of the field whose value to get</param>
    /// <returns>Value of the given field of obj, or null if obj was null
    /// </returns>
    private static object GetFieldValue(object obj, FieldInfo field, string fieldName)
    {
      Contract.Requires(field != null);
      Contract.Requires(!string.IsNullOrWhiteSpace(fieldName));
      Contract.Ensures((obj == null).Implies(Contract.Result<object>() == null));

      // TODO(#60): Duplicative with GetVariableValue code
      if (obj == null)
      {
        return null;
      }

      FieldInfo runtimeField;
      Type currentType = obj.GetType();

      // Ensure we are at the declared type, and not possibly a subtype.
      while ((currentType.Name != null) && (currentType.Name != field.DeclaringType.Name))
      {
        currentType = currentType.BaseType;
      }

      // Climb the supertypes as necessary to get the desired field.
      do
      {
        runtimeField = currentType.GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance);
        currentType = currentType.BaseType;
      } while (runtimeField == null && currentType != null);

      Contract.Assert(runtimeField != null, "Field " + fieldName + " not found in type or supertypes");
      return runtimeField.GetValue(obj);
    }

    /// <summary>
    /// Get the result returned by invoking the given method on the given object.
    /// </summary>
    /// <param name="obj">Object to invoke the method on</param>
    /// <param name="method">Method to invoke</param>
    /// <param name="methodName">Name of the method to invoke</param>
    /// <param name="name">Expression name for debugging purpose</param>
    /// <returns><c>true</c> if the result is sensical</returns>
    private static bool TryGetMethodValue(object obj, MethodInfo methodInfo, string name, out object result)
    {
      Contract.Requires(obj != null);
      Contract.Requires(methodInfo != null);
      Contract.Requires(name != null);

      Type objType = obj.GetType();
      
      Contract.Assume(methodInfo.DeclaringType != null, 
        "Method " + methodInfo.Name + " has no declaring type; expression type: " + objType.FullName);

      Func<string> callInfo = () =>
        "[Expession " + name + " Object Type: " + objType.FullName + " Method Name: " + methodInfo.Name + 
        " Declaring Type: " + methodInfo.DeclaringType + " Static?: " + methodInfo.IsStatic + 
        " Public?: " + methodInfo.IsPublic + "]";

      var paramCnt = methodInfo.GetParameters().Length;

      MethodInfo method = null;

      if (methodInfo.DeclaringType.IsInterface && !string.IsNullOrEmpty(methodInfo.DeclaringType.FullName))
      {
        // attempt to resolve an explicit interface method first
        method = objType.GetMethod(string.Join(".", methodInfo.DeclaringType.FullName, methodInfo.Name),
           TypeManager.PureMethodBindings, null,
           (paramCnt == 0 ? Type.EmptyTypes : new Type[] { objType }),
           null);
      }
     
      if (method == null)
      {
          method = objType.GetMethod(methodInfo.Name,
             TypeManager.PureMethodBindings, null,
             (paramCnt == 0 ? Type.EmptyTypes : new Type[] {objType}),
             null);
      }

      Contract.Assume(method != null, "Could not locate method " + methodInfo.Name + "; " + callInfo());

      var paramList = method.GetParameters();
      Contract.Assume(paramCnt == paramList.Length,
        "Parameter count mismatch. Expected: " + paramCnt +
        " Actual: " + paramList.Length + "; " + callInfo());

      Contract.Assume((!method.IsStatic && paramList.Length == 0) || (method.IsStatic && paramList.Length <= 1),
                       "Invalid number of parameters for method; " + callInfo());

      Contract.Assume(paramList.Length == 0 || paramList[0].ParameterType.IsAssignableFrom(objType),
                      "Cannot supply argument with runtime type " + objType.Name + " to method " + method.Name +
                      " with parameter type " + (paramList.Length > 0 ? paramList[0].ParameterType.FullName : "void") + "; " +
                      callInfo());

      result = null;
      SuppressOutput = true;
      try
      {
        result = method.Invoke(obj, paramList.Length == 1 ? new object[] {obj} : null);
      }
      catch (TargetInvocationException ex)
      {
        // If the _invoked method_ throws an exception, ignore it. 
        if (frontEndArgs.VerboseMode)
        {
          Console.WriteLine(
            string.Format("INFO: caught exception invoking {0} on declared type {1}: {2}",
            method.Name, objType.FullName, (ex.Message ?? "<no message>")));
        }
        return false;
      }
      catch (Exception ex)
      {
        Contract.Assume(false, string.Format("Unable to invoke {0}: {1}; {2}", 
          method.Name, (ex.Message ?? "<no message>"), callInfo()));
        
        throw;
      }
      finally
      {
        SuppressOutput = false;
      }
      return true;
    }


    /// <summary>
    /// Prepare the given string for printing to Daikon format.
    /// </summary>
    /// <param name="programPointName">Value of the string to prepare</param>
    /// <returns>String that can be output to a daikon datatrace file</returns>
    [Pure]
    private static string PrepareString(String str)
    {
      Contract.Requires(str != null);

      // Escape internal quotes, backslashes, newlines, and carriage returns.
      str = str.Replace("\\", "\\\\");
      str = str.Replace("\"", "\\\"");
      str = str.Replace("\n", "\\n");
      str = str.Replace("\r", "\\r");

      // Add quotes before and after string contents.
      StringBuilder builder = new StringBuilder();
      builder.Append("\"");
      builder.Append(str);
      builder.Append("\"");
      return builder.ToString();
    }

    /// <summary>
    /// Returns the runtime type of <code>obj</code>. If <code>obj</code> is a transparent proxy, 
    /// returns the proxied type.
    /// </summary>
    /// <param name="obj">an object</param>
    /// <returns>the runtime type of <code>obj</code></returns>
    [Pure]
    private static Type RuntimeType(object obj)
    {
      Contract.Requires(obj != null);
      Contract.Ensures(Contract.Result<Type>() != null);

      return RemotingServices.IsTransparentProxy(obj)
                    ? RemotingServices.GetRealProxy(obj).GetProxiedType()
                    : obj.GetType();
    }

    #endregion
  }
}
