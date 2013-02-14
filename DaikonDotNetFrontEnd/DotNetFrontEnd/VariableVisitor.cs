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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using Microsoft.Cci;

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
    private static bool shouldSuppressOutput = false;

    /// <summary>
    /// The current nonce counter
    /// </summary>
    private static int globalNonce = 0;

    /// <summary>
    /// Lock to ensure that no more than one thread is writing at a time
    /// </summary>
    private static object WriterLock = new object();

    /// <summary>
    /// Collection of static fields that have been visited during this program point
    /// </summary>
    private static HashSet<string> staticFieldsVisitedForCurrentProgramPoint = new HashSet<string>();

    /// <summary>
    /// Collection of variables that have been visited during the current program point
    /// </summary>
    private static HashSet<string> variablesVisitedForCurrentProgramPoint = new HashSet<string>();

    /// <summary>
    /// The path to the assembly being instrumented. Necessary when DNFE is run in offline-mode
    /// </summary>
    private static string offlineAssemblyPath = null;

    /// <summary>
    /// The name of the assembly being instrumented. Necessary when DNFE is run in offline-mode
    /// </summary>
    private static string offlineAssemblyName = null;

    #endregion

    /// <summary>
    /// Set the canonical representation for refArgs. Allows decl printer and reflective visitor
    /// to reference the given args. Once this method is called the refArgs can't be changed.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown during an attempt to set args after they 
    /// have already been set.</exception>
    /// <param name="reflectionArgs">The reflection args to set.</param>
    public static void SetReflectionArgs(FrontEndArgs reflectionArgs)
    {
      if (frontEndArgs == null)
      {
        frontEndArgs = reflectionArgs;
      }
      else
      {
        throw new NotSupportedException("Attempt to set arguments on the reflector twice.");
      }

      if (frontEndArgs.SampleStart != FrontEndArgs.NoSampleStart)
      {
        occurenceCounts = new Dictionary<string, int>();
      }
    }

    /// <summary>
    /// Set the type manager for refArgs. Once this method is called the typeManager reference
    /// can't be changed.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown during an attempt to set the type manager
    /// after it has already been set.</exception>
    /// <param name="typeManager">The TypeManager to use for reflective visiting.</param>
    public static void SetTypeManager(TypeManager typeManager)
    {
      if (VariableVisitor.typeManager == null)
      {
        VariableVisitor.typeManager = typeManager;
      }
      else
      {
        throw new NotSupportedException("Attempt to set type manager on the reflector twice.");
      }
    }

    #region Methods to be called from program to be profiled

    /// <summary>
    /// Special version of VisitVariable with different parameter ordering for convenient calling 
    /// convention compatibility.
    /// </summary>
    /// <param name="variable">The object</param>
    /// <param name="name">The name of the variable</param>
    /// <param name="typeName">Description of the type</param>
    public static void ValueFirstVisitVariable(object variable, string name, string typeName)
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
    public static void VisitVariable(string name, object variable, string typeName)
    {
      // Make the traditional call, nothing special
      DoVisit(variable, name, typeName);
    }

    /// <summary>
    /// Visit all the static variables of the given type.
    /// </summary>
    /// <param name="typeName">Assembly-qualified name of the type to visit.</param>
    public static void PerformStaticInstrumentation(string typeName)
    {
      TextWriter writer = InitializeWriter();
      try
      {
        DNFETypeDeclaration typeDecl = typeManager.ConvertAssemblyQualifiedNameToType(typeName);
        if (typeDecl == null)
        {
          throw new ArgumentException("VariableVistor received invalid type name for static" +
            " instrumentation.", "typeName");
        }

        int depth = 0;
        foreach (Type type in typeDecl.GetAllTypes)
        {
          foreach (FieldInfo staticField in
           type.GetFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(type)))
          {
            if (!typeManager.ShouldIgnoreField(type, staticField.Name))
            {
              string staticFieldName = type.FullName + "." + staticField.Name;
              try
              {
                if (!staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
                {
                  staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
                  // TODO(#68):
                  // Static fields of generic types cause an exception, so don't visit them
                  object val = null;
                  VariableModifiers flags = VariableModifiers.none;
                  if (!type.ContainsGenericParameters)
                  {
                    val = staticField.GetValue(null);
                  }
                  else
                  {
                    flags = VariableModifiers.nonsensical;
                  }
                  ReflectiveVisit(staticFieldName, val,
                        staticField.FieldType, writer, staticFieldName.Count(c => c == '.'),
                        fieldFlags: flags);
                }
              }
              catch (ArgumentException)
              {
                Console.Error.WriteLine(" Name: " + staticFieldName + " Type: " + type + " Field Name: "
                    + staticField.Name + " Field Type: " + staticField.FieldType);
                // The field is declared in the decls so Daikon still needs a value, 
                ReflectiveVisit(staticFieldName, null,
                    staticField.FieldType, writer, depth + 1, VariableModifiers.nonsensical);
              }
            }
          }
        }
      }
      //catch (Exception ex)
      //{
      //  throw new VariableVisitorException(ex);
      //}
      finally
      {
        // Close the writer so it can be used elsewhere
        writer.Close();
      }
    }

    /// <summary>
    /// Perform DoVisit, except where the exception is null.
    /// Null exceptions should actually be treated as non-sensical.
    /// </summary>
    /// <param name="exception">An exception to reflectively visit</param>
    public static void VisitException(object exception)
    {
      // If the exception is null we actually want to print it as non-sensical.
      VariableModifiers flags = (exception == null ? VariableModifiers.nonsensical :
          VariableModifiers.none);
      // Make the traditional call, possibly add the nonsensicalElements flag.
      // Also, downcast everything to System.Object so we call GetType().
      // TODO(#15): Allow more inspection of exceptions
      DoVisit(exception, ExceptionVariableName, ExceptionTypeName, flags);
    }

    /// <summary>
    /// Acquire the lock on the writer.
    /// </summary>
    public static void AcquireLock()
    {
      Monitor.Enter(WriterLock);
    }

    /// <summary>
    /// Release the lock on the writer.
    /// </summary>
    public static void ReleaseLock()
    {
      Monitor.Exit(WriterLock);
    }

    /// <summary>
    /// Sets whether output should be suppressed
    /// </summary>
    /// <param name="shouldSuppress">True to not print any output during program execution,
    /// false for normal behavior</param>
    public static void SetOutputSuppression(bool shouldSuppress)
    {
      shouldSuppressOutput = shouldSuppress;
    }

    /// <summary>
    /// Set the invocation nonce for this method by storing it in an appropriate local var
    /// </summary>
    /// <returns>The invocation nonce for the method</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "programPointName"), MethodImpl(MethodImplOptions.Synchronized)]
    public static int SetInvocationNonce(string programPointName)
    {
      return globalNonce++;
    }

    /// <summary>
    /// Write the invocation nonce for the current method
    /// </summary>
    /// <param name="nonce">Nonce-value to print</param>
    public static void WriteInvocationNonce(int nonce)
    {
      TextWriter writer = InitializeWriter();
      writer.WriteLine("this_invocation_nonce");
      writer.WriteLine(nonce);
      writer.Close();
    }

    /// <summary>
    /// Write the given program point, sanitizing the name if necessary
    /// </summary>
    /// <param name="programPointName">Name of program point to print</param>
    /// <param name="label">Label used to differentiate the specific program point from other with 
    /// the same name.</param>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public static void WriteProgramPoint(string programPointName, string label)
    {
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
          shouldSuppressOutput = oldOccurrences % (int)Math.Pow(10,
            (oldOccurrences / frontEndArgs.SampleStart)) != 0;
        }
        occurenceCounts[programPointName] = oldOccurrences + 1;
      }
      TextWriter writer = InitializeWriter();
      if (programPointName == null)
      {
        throw new ArgumentNullException("programPointName");
      }
      writer.WriteLine();
      programPointName = DeclarationPrinter.SanitizeProgramPointName(programPointName) +
          (String.IsNullOrEmpty(label) ? String.Empty : label);
      writer.WriteLine(programPointName);
      writer.Close();
    }

    /// <summary>
    /// When the program is run in offline mode, that is with a saved binary, the first time
    /// the variable visitor is entered we need to determine the name and path of the assembly
    /// being profiled. This is necessary so the .NET type resolve can locate and load the types in 
    /// the assembly being profiled.
    /// </summary>
    /// <param name="assemblyName">Name of the assembly being profiled.</param>
    /// <param name="assemblyPath">Relative path to the rewritten assembly.</param>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public static void InitializeFrontEndArgs(string assemblyName, string assemblyPath, string arguments)
    {
      if (frontEndArgs == null)
      {
        offlineAssemblyName = assemblyName;
        offlineAssemblyPath = assemblyPath;
        frontEndArgs = new FrontEndArgs(arguments.Split());

        IMetadataHost host = new PeReader.DefaultHost();

        typeManager = new TypeManager(host, frontEndArgs);
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
    [MethodImpl(MethodImplOptions.Synchronized)]
    private static void DoVisit(object variable, string name, string typeName,
        VariableModifiers flags = VariableModifiers.none)
    {
      // TODO(#15): Can we pull any fields from exceptions?
      // Exceptions shouldn't be recursed any further down than the 
      // variable itself, because we only know that they are an exception.
      TextWriter writer = InitializeWriter();
      DNFETypeDeclaration typeDecl = typeManager.ConvertAssemblyQualifiedNameToType(typeName);

      try
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
          ReflectiveVisit(name, variable, type, writer, depth, flags);
        }
      }
      //catch (Exception ex)
      //{
      //  throw new VariableVisitorException(ex);
      //}
      finally
      {
        // Close the writer so it can be used elsewhere
        writer.Close();
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
      if (shouldSuppressOutput)
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
        writer = new StreamWriter(frontEndArgs.OutputLocation, true);
      }

      if (frontEndArgs.ForceUnixNewLine)
      {
        // Force UNIX new-line convention
        writer.NewLine = "\n";
      }

      return writer;
    }
    
    #region Reflective Visitor and helper methods

    private static bool IsTypeOf<T>(object t)
    {
        return (t is T);
    }

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
        TextWriter writer, int depth, VariableModifiers fieldFlags = VariableModifiers.none)
    {
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
        ProcessVariableAsList(name, obj, type, writer, depth);
      }
      else if (typeManager.IsFSharpListImplementer(type))
      {
        object[] result = null;
        if (obj != null)
        {
          result = TypeManager.ConvertFSharpListToCSharpArray(obj);
        }

        ProcessVariableAsList(name, result, result.GetType(), writer, depth);
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
        ProcessVariableAsList(name, convertedList, convertedList.GetType(), writer, depth);
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

        ProcessVariableAsList(name, entries, entries.GetType(), writer, depth);
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
        ProcessVariableAsList(name, entries, entries.GetType(), writer, depth);
      }
      else
      {
        PerformNonListInspection(name, obj, type, writer, depth, fieldFlags);
      }
    }

    /// <summary>
    /// Perform full inspection for non list variables, printing necessary
    /// values to the given writer. Includes children if any, and toString
    /// or HashCode calls.
    /// </summary>
    private static void PerformNonListInspection(string name, object obj, 
      Type type, TextWriter writer, int depth, VariableModifiers fieldFlags)
    {
      if (obj == null)
      {
        fieldFlags |= VariableModifiers.nonsensical;
      }

      PrintFieldValues(name, obj, type, writer, depth, fieldFlags);

      if (!type.IsSealed)
      {
        object xType = (obj == null ? null : obj.GetType());
        ReflectiveVisit(name + '.' + DeclarationPrinter.GetTypeMethodCall, xType,
            TypeManager.TypeType, writer, depth + 1,
            (obj == null ? VariableModifiers.nonsensical : VariableModifiers.none)
            | VariableModifiers.classname);
      }

      if (type == TypeManager.StringType)
      {
        object xString = (obj == null ? null : obj.ToString());
        ReflectiveVisit(name + '.' + DeclarationPrinter.ToStringMethodCall, xString,
            TypeManager.StringType, writer, depth + 1,
            fieldFlags | VariableModifiers.to_string);
      }

      if (!shouldSuppressOutput)
      {
        foreach (var item in typeManager.GetPureMethodsForType(type))
        {
          ReflectiveVisit(name + '.' + DeclarationPrinter.SanitizePropertyName(item.Value.Name),
              GetMethodValue(obj, item.Value, item.Value.Name), item.Value.ReturnType, writer,
                  depth + 1, fieldFlags: fieldFlags);
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
        ListReflectiveVisit(name + "[..]", (IList)expandedList, type, writer, depth, fieldFlags);
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
      TextWriter writer, int depth, VariableModifiers fieldFlags)
    {
      foreach (FieldInfo field in
          type.GetFields(frontEndArgs.GetInstanceAccessOptionsForFieldInspection(type)))
      {
        try
        {
          if (!typeManager.ShouldIgnoreField(type, field.Name))
          {
            ReflectiveVisit(name + "." + field.Name, GetFieldValue(obj, field, field.Name),
                field.FieldType, writer, depth + 1,
                fieldFlags | VariableModifiers.ignore_linked_list);
          }
        }
        catch (ArgumentException)
        {
          Console.Error.WriteLine(" Name: " + name + " Type: " + type + " Field Name: "
              + field.Name + " Field Type: " + field.FieldType);
          // The field is declared in the decls so Daikon still needs a value. 
          ReflectiveVisit(name + "." + field.Name, null,
              field.FieldType, writer, depth + 1, fieldFlags | VariableModifiers.nonsensical);
        }
      }

      foreach (FieldInfo staticField in
          type.GetFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(type)))
      {
        if (!typeManager.ShouldIgnoreField(type, staticField.Name))
        {
            
          string staticFieldName = type.FullName + "." + staticField.Name;
          
          try
          {
            
            if (!staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
            {
              staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
              ReflectiveVisit(staticFieldName, GetFieldValue(obj, staticField, staticField.Name),
                    staticField.FieldType, writer, staticFieldName.Count(c => c == '.'),
                    fieldFlags | VariableModifiers.ignore_linked_list);
            }
          }
          catch (ArgumentException)
          {
            Console.Error.WriteLine(" Name: " + name + " Type: " + type + " Field Name: "
                + staticField.Name + " Field Type: " + staticField.FieldType);
            // The field is declared in the decls so Daikon still needs a value. 
            ReflectiveVisit(staticFieldName, null,
                staticField.FieldType, writer, depth + 1, fieldFlags
                | VariableModifiers.nonsensical);
          }
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
      // Print variable's name.
      writer.WriteLine(name);

      // Print variable's value.
      writer.WriteLine(GetVariableValue(obj, type, fieldFlags));

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
      else
      {
        variablesVisitedForCurrentProgramPoint.Add(name);
      }

      if (depth > frontEndArgs.MaxNestingDepth ||
          !frontEndArgs.ShouldPrintVariable(name))
      {
        return true;
      }

      return false;
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
        TextWriter writer, int depth, VariableModifiers flags = VariableModifiers.none)
    {
      // Call GetType() on the list if necessary.
      Type elementType = TypeManager.GetListElementType(type);
      if (!elementType.IsSealed)
      {
        ReflectiveVisit(name + "." + DeclarationPrinter.GetTypeMethodCall, type,
            TypeManager.TypeType, writer, depth + 1,
            VariableModifiers.classname);
      }
      // Now visit each element.
      // Element inspection is at the same depth as the list.
      // null lists won't cast but we don't want to throw an exception for that
      if ((obj != null) && !(obj is IList))
      {
        throw new NotSupportedException("Can't list reflective visit something that isn't a"
            + " list. This is a bug in the reflector implementation.");
      }
      else
      {
        ListReflectiveVisit(name + "[..]", (IList)obj, elementType, writer, depth, flags);
      }
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
        TextWriter writer, int depth, VariableModifiers flags = VariableModifiers.none,
        bool[] nonsensicalElements = null)
    {
      if (depth > frontEndArgs.MaxNestingDepth ||
          !frontEndArgs.ShouldPrintVariable(name))
      {
        return;
      }

      // We might not know the type, e.g. for non-generic ArrayList
      if (elementType == null)
      {
        elementType = TypeManager.ObjectType;
      }

      // Don't inspect fields on some calls.
      bool simplePrint = flags.HasFlag(VariableModifiers.to_string)
          || flags.HasFlag(VariableModifiers.classname);

      // Write name of the list, which possibly is its path.
      writer.WriteLine(name);

      // If a reference to a list is null the reference is nonsensical.
      if (list == null)
      {
        flags |= VariableModifiers.nonsensical;
        // Write nonsensical
        writer.WriteLine(GetVariableValue(list, elementType, flags));
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
          builder.Append(GetVariableValue(list[i], elementType, flags | elementFlags) + " ");
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

      // If the list was null then we can't visit the children, just print nonsensical for them.
      if (list == null)
      {
        VisitNullListChildren(name, elementType, writer, depth);
      }
      else
      {
        if (nonsensicalElements == null)
        {
          nonsensicalElements = new bool[list.Count];
        }

        VisitListChildren(name, list, elementType, writer, depth, nonsensicalElements);
      }
    }

    /// <summary>
    /// Visit all children of a null list -- printing nonsensical at each step.
    /// </summary>
    /// <param name="name">name of the null array</param>
    /// <param name="elementType">Type of the elements that would be in the list</param>
    /// <param name="writer">Writer to write the results of the visit using to</param>
    /// <param name="depth">Current nesting depth</param>
    private static void VisitNullListChildren(string name, Type elementType,
        TextWriter writer, int depth)
    {
      foreach (FieldInfo staticElementField in
          elementType.GetFields(frontEndArgs.GetInstanceAccessOptionsForFieldInspection(elementType)))
      {
        ListReflectiveVisit(name + "." + staticElementField.Name, null,
            staticElementField.FieldType, writer, depth + 1);
      }

      foreach (FieldInfo staticElementField in
          elementType.GetFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(elementType)))
      {
        if (!typeManager.ShouldIgnoreField(elementType, staticElementField.Name))
        {
          string staticFieldName = elementType.FullName + "." + staticElementField.Name;
          if (!staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
          {
            staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
            ListReflectiveVisit(staticFieldName, null, staticElementField.FieldType, writer,
                staticFieldName.Count(c => c == '.'));
          }
        }
      }
      
      if (!elementType.IsSealed)
      {
        ListReflectiveVisit(name + "." + DeclarationPrinter.GetTypeMethodCall, null,
            TypeManager.TypeType, writer, depth + 1, VariableModifiers.classname);
      }

      if (elementType == TypeManager.StringType)
      {
        ListReflectiveVisit(name + "." + DeclarationPrinter.ToStringMethodCall, null,
            TypeManager.StringType, writer, depth + 1, VariableModifiers.to_string);
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
        TextWriter writer, int depth, bool[] nonsensicalElements)
    {
      foreach (FieldInfo elementField in
          elementType.GetFields(frontEndArgs.GetInstanceAccessOptionsForFieldInspection(elementType)))
      {
        if (!typeManager.ShouldIgnoreField(elementType, elementField.Name))
        {
          VisitListField(name, list, elementType, writer, depth, nonsensicalElements, elementField);
        }
      }

      // Static fields will have the same value for every element so just visit them once
      foreach (FieldInfo elementField in
          elementType.GetFields(frontEndArgs.GetStaticAccessOptionsForFieldInspection(elementType)))
      {
        if (!typeManager.ShouldIgnoreField(elementType, elementField.Name))
        {
          string staticFieldName = elementType.FullName + "." + elementField.Name;
          if (!staticFieldsVisitedForCurrentProgramPoint.Contains(staticFieldName))
          {
            staticFieldsVisitedForCurrentProgramPoint.Add(staticFieldName);
            ReflectiveVisit(staticFieldName, elementField.GetValue(null),
                elementField.FieldType, writer, staticFieldName.Count(c => c == '.'));
          }
        }
      }

      if (!elementType.IsSealed)
      {
        Type[] typeArray = new Type[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
          if (list[i] == null)
          {
            typeArray[i] = null;
          }
          else
          {
            typeArray[i] = list[i].GetType();
          }
          nonsensicalElements[i] = list[i] == null;
        }
        ListReflectiveVisit(name + "." + DeclarationPrinter.GetTypeMethodCall, typeArray,
            TypeManager.TypeType, writer, depth + 1, VariableModifiers.classname,
            nonsensicalElements: nonsensicalElements);
      }

      if (elementType == TypeManager.StringType)
      {
        string[] stringArray = new string[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
          if (list[i] == null)
          {
            stringArray[i] = null;
          }
          else
          {
            stringArray[i] = list[i].ToString();
          }
        }
        ListReflectiveVisit(name + "." + DeclarationPrinter.ToStringMethodCall, stringArray,
            TypeManager.StringType, writer, depth + 1, VariableModifiers.to_string);
      }

      foreach (var pureMethod in typeManager.GetPureMethodsForType(elementType))
      {
        string pureMethodName = DeclarationPrinter.SanitizePropertyName(pureMethod.Value.Name);
        object[] pureMethodResults = new object[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
          if (list[i] == null)
          {
            pureMethodResults[i] = null;
          }
          else
          {
            pureMethodResults[i] = GetMethodValue(list[i], pureMethod.Value, pureMethod.Value.Name);
          }
        }
        ListReflectiveVisit(name + "." + pureMethodName, pureMethodResults,
          pureMethod.Value.ReturnType, writer, depth + 1, 
          nonsensicalElements: nonsensicalElements);
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
        int depth, bool[] nonsensicalElements, FieldInfo elementField)
    {
      // If we have a non-null list then build an array comprising the calls on each 
      // element. The array must be object type so it can take any children.
      Array childArray = Array.CreateInstance(TypeManager.ObjectType, list.Count);
      for (int i = 0; i < list.Count; i++)
      {
        try
        {
          childArray.SetValue(GetFieldValue(list[i], elementField, elementField.Name), i);
        }
        catch (ArgumentException)
        {
          Console.Error.WriteLine(" Name: " + name + " Type: " +
            elementType + " Field Name: " + elementField.Name +
            " Field Type: " + elementField.FieldType);
          childArray.SetValue(null, i);
        }
        nonsensicalElements[i] = (list[i] == null);
      }
      ListReflectiveVisit(name + "." + elementField.Name, childArray,
          elementField.FieldType, writer, depth + 1,
          nonsensicalElements: nonsensicalElements);
    }

    /// <summary>
    /// Generate a string representation of the value of variable, based on its type.
    /// </summary>
    /// <param name="variable">The object to print</param>
    /// <param name="type">The type to be used when determining how to print</param>
    /// <param name="flags">Any flags for the variable</param>
    /// <returns>String containing a daikon-valid representation of the variable's value</returns>
    private static string GetVariableValue(object x, Type type, VariableModifiers flags)
    {
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
          return Convert.ChangeType(x, Enum.GetUnderlyingType(type),
            CultureInfo.InvariantCulture).ToString();
        }
        else
        {
          StringBuilder builder = new StringBuilder();
          builder.Append("\"");
          builder.Append(x.ToString());
          builder.Append("\"");
          return builder.ToString();
        }
      }
      else if (flags.HasFlag(VariableModifiers.classname) ||
          flags.HasFlag(VariableModifiers.to_string))
      {
        return PrepareString(x.ToString());
      }
      else if (type.IsValueType)
      {
        if (type == TypeManager.BooleanType)
        {
          if ((bool)x)
          {
            return "true";
          }
          else
          {
            return "false";
          }
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

      // Type is either an object or a user-defined struct, print out its hashcode.
      SetOutputSuppression(true);
      string hashcode = x.GetHashCode().ToString(CultureInfo.InvariantCulture);
      SetOutputSuppression(false);
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
    /// <exception cref="ArgumentException">If the field is not found in the object</exception>
    private static object GetFieldValue(object obj, FieldInfo field, string fieldName)
    {
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
            BindingFlags.Public | BindingFlags.NonPublic
          | BindingFlags.Static | BindingFlags.Instance);
        currentType = currentType.BaseType;
      } while (runtimeField == null && currentType != null);

      if (runtimeField == null)
      {
        throw new ArgumentException("Field " + fieldName + " not found in type or supertypes");
      }

      return runtimeField.GetValue(obj);
    }

    /// <summary>
    /// Get the result returned by invoking the given method on the given object.
    /// </summary>
    /// <param name="obj">Object to invoke the method on</param>
    /// <param name="method">Method to invoke</param>
    /// <param name="methodName">Name of the method to invoke</param>
    /// <returns>Result returned by the invoked function</returns>
    private static object GetMethodValue(object obj, MethodInfo method, string methodName)
    {
      // TODO(#60): Duplicative with GetVariableValue?
      if (obj == null)
      {
        return null;
      }

      MethodInfo runtimeMethod;
      Type currentType = obj.GetType();

      // Ensure we are at the declared type, and not possibly a subtype
      while ((currentType.Name != null) && (currentType.Name != method.DeclaringType.Name))
      {
        currentType = currentType.BaseType;
      }

      // Climb the supertypes as necessary to get the desired field
      do
      {
        runtimeMethod = currentType.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic
          | BindingFlags.Static | BindingFlags.Instance);
        currentType = currentType.BaseType;
      } while (runtimeMethod == null && currentType != null);

      if (runtimeMethod == null)
      {
        throw new ArgumentException("Method " + methodName + " not found in type or supertypes");
      }

      SetOutputSuppression(true);
      var val = runtimeMethod.Invoke(obj, null);
      SetOutputSuppression(false);
      return val;
    }

    /// <summary>
    /// Prepare the given string for printing to daikon format.
    /// </summary>
    /// <param name="programPointName">Value of the string to prepare</param>
    /// <returns>String that can be output to a daikon datatrace file</returns>
    private static string PrepareString(String str)
    {
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

    #endregion
  }
}
