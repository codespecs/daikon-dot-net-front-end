// DeclarationPrinter prints the declaration portion of the datatrace file
// It is called from ILRewriter.cs while the profiler is visiting the module.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNetFrontEnd
{
  /// <summary>
  /// Prints the declaration portion of a datatrace
  /// </summary>
  public class DeclarationPrinter : IDisposable
  {
    // The arguments to be used in printing declarations
    private FrontEndArgs frontEndArgs;

    // Device used to write the declarations
    private TextWriter fileWriter;

    /// <summary>
    /// The type manager to be used for determining variable type.
    /// </summary>
    private TypeManager typeManager;

    /// <summary>
    /// Collection of static fields that have been visited at this program point.
    /// </summary>
    private HashSet<string> staticFieldsForCurrentProgramPoint;

    #region Constants

    /// <summary>
    /// Number of spaces per indentation
    /// </summary>
    private const int SpacesPerIndent = 2;

    /// <summary>
    /// Number of indentations for a standard data entry
    /// </summary>
    private const int IndentsForEntry = 2;

    /// <summary>
    /// Number of indentations for the name of a variable
    /// </summary>
    private const int IndentsForName = 1;

    /// <summary>
    /// Until real comparability works (TODO(#22)) print a constant value everywhere.
    /// </summary>
    private const int ComparabilityConstant = 22;

    // Daikon is hard coded to recognize the Java full names for dec/rep types
    private const string DaikonStringName = "java.lang.String";
    private const string DaikonBoolName = "boolean";
    private const string DaikonIntName = "int";

    /// <summary>
    /// The value to print when we make a GetType() call on a variable
    /// </summary>
    public const string GetTypeMethodCall = "GetType()";

    /// <summary>
    /// The value to print when we make a ToString() call on a variable
    /// </summary>
    public const string ToStringMethodCall = "ToString()";

    #endregion

    /// <summary>
    /// The possible values for variable kind
    /// Violates enum naming convention for ease of printing.
    /// </summary>
    internal enum VariableKind
    {
      field,
      function,
      array,
      variable,
      Return, // Lowercase "R" in return is a keyword.
    }

    /// <summary>
    /// The possible flags we may specify for a a variable
    /// </summary>
    /// Flags is acceptable for use in VariableFlags because flags is a daikon term.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms",
      MessageId = "Flags"), Flags]
    public enum VariableFlags
    {
      none = 1,
      synthetic = none << 1,
      classname = synthetic << 1,
      to_string = classname << 1,
      is_param = to_string << 1,
    }

    /// <summary>
    /// Create a new declaration printer with the given command line arguments. Also creates a new
    /// file to hold the output, if output is being written to a file.
    /// </summary>
    /// <param name="args">Arguments to use while printing the declaration file</param>
    /// <param name="typeManager">Type manager to use while printing the declaration file</param>
    public DeclarationPrinter(FrontEndArgs args, TypeManager typeManager)
    {
      if (args == null)
      {
        throw new ArgumentNullException("args");
      }
      this.frontEndArgs = args;

      if (typeManager == null)
      {
        throw new ArgumentNullException("typeManager");
      }
      this.typeManager = typeManager;

      if (args.PrintOutput)
      {
        this.fileWriter = System.Console.Out;
      }
      else
      {
        this.fileWriter = new StreamWriter(this.frontEndArgs.OutputLocation);
      }

      if (this.frontEndArgs.ForceUnixNewLine)
      {
        this.fileWriter.NewLine = "\n";
      }

      this.staticFieldsForCurrentProgramPoint = new HashSet<string>();

      this.PrintPreliminaries();
    }

    private bool ShouldPrintParentPptIfNecessary(String parentTypeName)
    {
      return !String.IsNullOrEmpty(parentTypeName) &&
          !Regex.IsMatch(parentTypeName, TypeManager.RegexForTypesToIgnoreForProgramPoint);
    }

    /// <summary>
    /// Print the declaration for the given variable, and all its children
    /// </summary>
    /// <param name="name">The name of the variable</param>
    /// <param name="type">The declared type of the variable</param> 
    /// <param name="kind">Optional, the daikon kind of the varible</param>
    /// <param name="flags">Optional, the daikon flags for the variable</param>
    /// <param name="enclosingVar">Optional, the daikon enclosing var of the variable 
    /// (its parent) </param>
    /// <param name="relativeName">Optional, the daikon relative name for the variable 
    /// (how to get to it) </param>
    /// <param name="nestingDepth">The nesting depth of the current variable. If not given 
    /// assumed to be the root value of 0.</param>
    private void PrintVariable(string name, Type type,
        VariableKind kind = VariableKind.variable, VariableFlags flags = VariableFlags.none,
        string enclosingVar = "", string relativeName = "", string parentName = "",
        int nestingDepth = 0)
    {
      if (name.Length == 0)
      {
        throw new NotSupportedException("An error occurred in the instrumentation process"
            + " and a varible was encountered with no name.");
      }
      if (type == null)
      {
        throw new NotSupportedException("An error occurred in the instrumentation process"
            + " and type was null for varible named: " + name);
      }

      if ((kind == VariableKind.field || kind == VariableKind.array) &&
        enclosingVar.Length == 0)
      {
        throw new ArgumentException("Enclosing var requried for field and array and none"
            + " was present for varible named: " + name + " of kind: " + kind);
      }

      if (nestingDepth > this.frontEndArgs.MaxNestingDepth ||
          !this.frontEndArgs.ShouldPrintVariable(name))
      {
        return;
      }

      this.WritePair("variable", name, 1);

      PrintVarKind(kind, relativeName);

      if (enclosingVar.Length > 0)
      {
        this.WritePair("enclosing-var", enclosingVar, 2);
      }

      if (!type.IsArray)
      {
        this.WritePair("dec-type", GetDecType(type), 2);
      }
      else
      {
        this.WritePair("dec-type", GetDecType(type.GetElementType()) + "[]", 2);
      }

      PrintRepType(type, flags);

      this.PrintFlags(flags);

      // TODO(#4): Implement real comparability.
      this.WritePair("comparability", 22, 2);

      if (this.ShouldPrintParentPptIfNecessary(parentName))
      {
        this.WritePair("parent", parentName, 2);
      }

      // If the variable is an object, then look at its fields or elements.
      // Don't look at the fields if the variable is a ToString or Classname call.
      if (!(type.IsClass && !flags.HasFlag(VariableFlags.to_string) && !flags.HasFlag
          (VariableFlags.classname)))
      {
        return;
      }

      if (this.typeManager.IsListImplementer(type))
      {
        DeclareVariableAsList(name, type, parentName, nestingDepth);
      }
      else if (this.typeManager.IsFSharpListImplementer(type))
      {
        // We don't get information about generics, so all we know for sure is that the elements
        // are objects.
        // FSharpLists get converted into object[].
        DeclareVariableAsList(name, typeof(object[]), parentName, nestingDepth);
      }
      else if (frontEndArgs.LinkedLists && this.typeManager.IsLinkedListImplementer(type))
      {
        FieldInfo arrayListField = TypeManager.FindLinkedListField(type);
        PrintList(name + "." + arrayListField.Name + "[..]", type, name,
            VariableKind.array, nestingDepth: nestingDepth, parentName: parentName);
      }
      else if (this.typeManager.IsSetImplementer(type))
      {
        DeclareVariableAsList(name, type, parentName, nestingDepth);
      }
      else
      {
        foreach (FieldInfo field in
            type.GetFields(this.frontEndArgs.GetInstanceAccessOptionsForFieldInspection(type)))
        {
          PrintVariable(name + "." + field.Name, field.FieldType,
              VariableKind.field, enclosingVar: name, relativeName: field.Name,
              nestingDepth: nestingDepth + 1, parentName: parentName);
        }

        foreach (FieldInfo field in
            type.GetFields(this.frontEndArgs.GetStaticAccessOptionsForFieldInspection(type)))
        {
          string staticFieldName = type.Name + "." + field.Name;
          if (!this.staticFieldsForCurrentProgramPoint.Contains(staticFieldName))
          {
            this.staticFieldsForCurrentProgramPoint.Add(staticFieldName);
            PrintVariable(staticFieldName, field.FieldType, 
            nestingDepth: staticFieldName.Count(c => c == '.'));
          }
        }

        if (!type.IsSealed)
        {
          PrintVariable(name + "." + GetTypeMethodCall, TypeManager.TypeType,
              VariableKind.function, VariableFlags.classname |
              VariableFlags.synthetic, enclosingVar: name, relativeName: GetTypeMethodCall,
              nestingDepth: nestingDepth + 1, parentName: parentName);
        }
        if (type == TypeManager.StringType)
        {
          PrintVariable(name + "." + ToStringMethodCall, TypeManager.StringType,
              VariableKind.function, VariableFlags.to_string |
              VariableFlags.synthetic,
              enclosingVar: name, relativeName: ToStringMethodCall,
              nestingDepth: nestingDepth + 1, parentName: parentName);
        } // Close field foreach
      } // Close fields/elements foreach
    } // Close print-variable method
    
    /// <summary>
    /// Wraps all the declaration necessary for a variable that is a list. Print the GetType()
    /// call if necessary then descrives the contents of the list.
    /// </summary>
    /// <param name="name">Name of the list variable</param>
    /// <param name="type">Type of the list variable (Not element type)</param>
    /// <param name="parentName">Name of the parent</param>
    /// <param name="nestingDepth">Neesting depth for the list variable</param>
    private void DeclareVariableAsList(string name, Type type, string parentName, int nestingDepth)
    {
      Type elementType = TypeManager.GetListElementType(type);
      // Print the type of the list if it's not primitive
      if (!elementType.IsSealed)
      {
        PrintVariable(name + "." + GetTypeMethodCall, TypeManager.TypeType,
            VariableKind.function, VariableFlags.classname | VariableFlags.synthetic,
            enclosingVar: name, relativeName: GetTypeMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName);

      }
      PrintList(name + "[..]", elementType, name, VariableKind.array,
          nestingDepth: nestingDepth, parentName: parentName);
    }

    /// <summary>
    /// Print the daikon rep-type for a variable of a given type
    /// </summary>
    /// <param name="type">Type of the variable</param>
    /// <param name="flags">Flags may modify the type of the varible. i.e. in a ToString() 
    /// call</param>
    private void PrintRepType(Type type, VariableFlags flags)
    {
      string repType;
      if (type.IsEnum)
      {
        if (this.frontEndArgs.EnumUnderlyingValues)
        {
          repType = DaikonIntName;
        }
        else
        {
          repType = DaikonStringName;
        }
      }
      else if (flags.HasFlag(VariableFlags.to_string)
          || flags.HasFlag(VariableFlags.classname))
      {
        repType = DaikonStringName;
      }
      else
      {
        repType = GetRepType(type);
      }
      this.WritePair("rep-type", repType, 2);
    }

    /// <summary>
    /// Print the daikon var-kind
    /// </summary>
    /// <param name="kind"></param>
    /// <param name="relativeName">Daikon-name for the path to this variable </param>
    private void PrintVarKind(VariableKind kind, string relativeName)
    {
      if (kind == VariableKind.function || kind == VariableKind.field)
      {
        this.WritePair("var-kind", kind.ToString() + " " + relativeName, 2);
      }
      else if (kind == VariableKind.Return)
      {
        // Decapitalize the R in return.
        this.WritePair("var-kind", "return", 2);
      }
      else
      {
        this.WritePair("var-kind", kind.ToString(), 2);
      }
    }

    /// <summary>
    /// Print the declaration for the given list, and all its children
    /// </summary>
    /// <param name="name">The name of the list</param>
    /// <param name="elementType">The declared element type of the list</param> 
    /// <param name="enclosingVar">The daikon enclosing var of the variable (its parent)</param>
    /// <param name="kind">Optional. The daikon kind of the varible</param>
    /// <param name="flags">Optional. The daikon flags for the variable</param>       
    /// <param name="relativeName">Optional. The daikon relative name for the variable 
    /// (how to get to it) </param>
    /// <param name="nestingDepth">The nesting depth of the current variable. If not given 
    /// assumed to be the root value of 0.</param>
    private void PrintList(string name, Type elementType, string enclosingVar,
        VariableKind kind = VariableKind.array, VariableFlags flags = VariableFlags.none,
        string relativeName = "", string parentName = "", int nestingDepth = 0)
    {
      if (name.Length == 0)
      {
        throw new NotSupportedException("Reflection error resulted in list without name");
      }

      if (nestingDepth > this.frontEndArgs.MaxNestingDepth ||
          !this.frontEndArgs.ShouldPrintVariable(name))
      {
        return;
      }

      // We might not know the type, i.e. for non-generic ArrayList
      if (elementType == null)
      {
        elementType = TypeManager.ObjectType;
      }

      this.WritePair("variable", name, IndentsForName);

      this.PrintVarKind(kind, relativeName);

      // Lists must always have enclosing var
      if (enclosingVar.Length == 0)
      {
        throw new NotSupportedException("Enclosing var requried for list"
            + " but not found for list named: " + name);
      }
      else
      {
        this.WritePair("enclosing-var", enclosingVar, IndentsForEntry);
      }

      // All arrays daikon can handle are of dimension 1.
      this.WritePair("array", 1, IndentsForEntry);

      this.WritePair("dec-type", GetDecType(elementType) + "[]", IndentsForEntry);

      string repType;
      if (flags.HasFlag(VariableFlags.to_string) || flags.HasFlag(VariableFlags.classname))
      {
        repType = DaikonStringName;
      }
      else
      {
        repType = GetRepType(elementType);
      }
      this.WritePair("rep-type", repType + "[]", IndentsForEntry);

      this.PrintFlags(flags);

      // TODO(#4): Implement real comparability
      this.WritePair("comparability", ComparabilityConstant, IndentsForEntry);

      if (this.ShouldPrintParentPptIfNecessary(parentName))
      {
        this.WritePair("parent", parentName, IndentsForEntry);
      }

      // Print the fields for each element -- unless it's not a class or the list is synthetic
      if (!(elementType.IsClass && !flags.HasFlag(VariableFlags.synthetic)))
      {
        return;
      }

      if (this.typeManager.IsListImplementer(elementType))
      {
        // Daikon can't handle nested arrays. Silently skip.
        return;
      }
      else if (this.typeManager.IsFSharpListImplementer(elementType))
      {
        // Daikon can't handle nested lists. Silently skip.
        return;
      }

      foreach (FieldInfo field in
          elementType.GetFields(this.frontEndArgs.GetInstanceAccessOptionsForFieldInspection(elementType)))
      {
        PrintList(name + "." + field.Name, field.FieldType, name,
            VariableKind.field, relativeName: field.Name,
            nestingDepth: nestingDepth + 1, parentName: parentName);
      }
      foreach (FieldInfo field in
          elementType.GetFields(this.frontEndArgs.GetStaticAccessOptionsForFieldInspection(
              elementType)))
      {
        string staticFieldName = elementType.Name + "." + field.Name;
        if (!this.staticFieldsForCurrentProgramPoint.Contains(staticFieldName))
        {
          this.staticFieldsForCurrentProgramPoint.Add(staticFieldName);
          PrintVariable(staticFieldName, field.FieldType, 
              nestingDepth: staticFieldName.Count(c => c == '.'));
        }
      }

      if (!elementType.IsSealed)
      {
        PrintList(name + "." + GetTypeMethodCall, TypeManager.TypeType, name,
            VariableKind.function, VariableFlags.classname |
            VariableFlags.synthetic, relativeName: GetTypeMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName);
      }
      if (elementType == TypeManager.StringType)
      {
        PrintList(name + "." + ToStringMethodCall, TypeManager.StringType, name,
            VariableKind.function, VariableFlags.to_string |
            VariableFlags.synthetic,
            relativeName: ToStringMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName);
      }
    }

    /// <summary>
    /// Close the file wrtier
    /// </summary>
    public void CloseWriter()
    {
      this.fileWriter.Close();
    }

    /// <summary>
    /// Print the parent of the current object: "this"
    /// </summary>
    /// <param name="parentName">Name of the parent, as it would appear in the program point
    /// </param>
    /// <param name="parentObjectType">Assembly-qualified name of the type of the parent
    /// </param>
    public void PrintParentObject(string parentName, string parentObjectType)
    {
     
      // The 1 is hardcoded because there is only one object program point per type
      parentName = parentName + ":::OBJECT 1";
      // If we can't resolve the parent object type don't write anything
      Type type = typeManager.ConvertAssemblyQualifiedNameToType(parentObjectType); 
      if (type != null)
      {
        if (this.ShouldPrintParentPptIfNecessary(parentName))
        {
          this.WritePair("parent", "parent " + parentName);
        }
        this.PrintVariable("this", type, flags: VariableFlags.is_param, parentName: parentName);
      }
    }

    /// <summary>
    /// Write the entrace to a method call.
    /// </summary>
    /// <param name="methodName">Name of the program point being entered</param>
    public void PrintCallEntrance(string methodName)
    {
      this.WriteLine();
      this.WritePair("ppt", SanitizeProgramPointName(methodName));
      this.WritePair("ppt-type", "enter");
      this.staticFieldsForCurrentProgramPoint.Clear();
    }

    /// <summary>
    /// Write the program point name for an exit from a method call.
    /// </summary>
    /// <param name="methodName">The name of the program point being exited</param>
    public void PrintCallExit(string methodName)
    {
      this.WriteLine();
      this.WritePair("ppt", SanitizeProgramPointName(methodName));
      this.WritePair("ppt-type", "subexit");
      this.staticFieldsForCurrentProgramPoint.Clear();
    }

    /// <summary>
    /// Print a parameter, and its children, with the given name and the given assembly-qualified
    /// declared type.
    /// </summary>
    /// <param name="name">The name of the parameter to print</param>
    /// <param name="paramType">The assembly-qualified name of the program to print</param>
    public void PrintParameter(string name, string paramType)
    {
      Type type = typeManager.ConvertAssemblyQualifiedNameToType(paramType);
      if (type != null)
      {
        PrintVariable(name, type, flags: VariableFlags.is_param);
      }
    }

    /// <summary>
    /// Print a declaration for the result of a pure method call
    /// </summary>
    /// <param name="name">Name of the pure method</param>
    /// <param name="methodReturnType">Type the pure method returns</param>
    public void PrintPureMethod(string name, Type methodReturnType)
    {
      PrintVariable(name, methodReturnType, VariableKind.function, relativeName: name);
    }

    /// <summary>
    /// Print the declaration for a method's return value, and its children.
    /// </summary>
    /// <param name="name">Name of the return value, commonly "return"</param>
    /// <param name="returnType">Assembly qualified name of the return type</param>
    public void PrintReturn(string name, string returnType)
    {
      Type type = typeManager.ConvertAssemblyQualifiedNameToType(returnType);
      if (type != null)
      {
        PrintVariable(name, type, kind: VariableKind.Return, nestingDepth: 0);
      }
    }

    /// <summary>
    /// Print the declaration of the object with the given qualified assembly name
    /// </summary>
    /// <param name="objectName">How the object should be described in the declaraion</param>
    /// <param name="objectAssemblyQualifiedName">Assembly qualified name of the object,
    /// used to fetch the Type</param>
    public void PrintObjectDefinition(string objectName, string objectAssemblyQualifiedName)
    {
      Type objectType = typeManager.ConvertAssemblyQualifiedNameToType(objectAssemblyQualifiedName);
      if (objectType != null)
      {
        this.WriteLine();
        string nameToPrint = SanitizeProgramPointName(objectName + ":::OBJECT");
        if (frontEndArgs.ShouldPrintProgramPoint(nameToPrint))
        {
          this.WritePair("ppt", nameToPrint);
          this.WritePair("ppt-type", "object");
          PrintVariable("this", objectType, VariableKind.variable, VariableFlags.is_param);
        }
      }
    }

    /// <summary>
    /// Sanitize a program name for printing.
    /// </summary>
    /// <param name="programPointName">Name of the program point</param>
    /// <returns>Sanitized version with spaces and slashes escaped</returns>
    public static string SanitizeProgramPointName(string programPointName)
    {
      if (programPointName == null)
      {
        throw new ArgumentNullException("programPointName");
      }
      string result = programPointName.Replace("\\", "\\\\");
      return result.Replace(" ", "\\_");
    }

    #region Private Helper Methods
    /// <summary>
    /// Print a space separated pair of values, followed by a new line to the class writer.
    /// </summary>
    /// <param name="key">The first item to print</param>
    /// <param name="val">The second item to print, after a space</param>
    private void WritePair(object key, object val, int tabsToIndent = 0)
    {
      StringBuilder builder = new StringBuilder();
      for (int i = 0; i < tabsToIndent * SpacesPerIndent; i++)
      {
        builder.Append(" ");
      }

      // Add the pair, then a new line.
      this.fileWriter.WriteLine("{0}{1} {2}", builder, key, val);
    }

    /// <summary>
    /// Write a blank line to the class writer.
    /// </summary>
    private void WriteLine()
    {
      this.fileWriter.WriteLine();
    }

    /// <summary>
    /// Print the header information.
    /// </summary>
    private void PrintPreliminaries()
    {
      this.WritePair("// Declarations for", this.frontEndArgs.AssemblyName);
      this.WritePair("// Declarations written", DateTime.Now);
      this.fileWriter.WriteLine();
      this.WritePair("decl-version", "2.0");
      // TODO(#22): Update when we have real compatability.
      this.WritePair("var-comparability", "none");
      this.WritePair("input-language", "C#.NET");
    }

    /// <summary>
    /// Write the given flags to the class printer on a single line, space separated.
    /// </summary>
    /// <param name="variableFlags">The flags to print</param>
    private void PrintFlags(VariableFlags variableFlags)
    {
      StringBuilder flagsToPrint = new StringBuilder();

      foreach (VariableFlags candidateFlag in Enum.GetValues(typeof(VariableFlags)))
      {
        // Don't print the none flag.
        if (candidateFlag != VariableFlags.none)
        {
          // Print the flag followed by a space.
          if (variableFlags.HasFlag(candidateFlag))
          {
            flagsToPrint.Append(candidateFlag);
            flagsToPrint.Append(" ");
          }
        }
      }

      if (flagsToPrint.Length > 0)
      {
        this.WritePair("flags", flagsToPrint.ToString().TrimEnd(), IndentsForEntry);
      }
    }

    /// <summary>
    /// Get the correctly formatted dec-type for the given type.
    /// </summary>
    /// <param name="type">The type whose daikon-compliant name to get</param>
    /// <returns>If the type is standard, the java name for that type, else just 
    /// the type name</returns>
    private static string GetDecType(Type type)
    {
      if (type.IsEquivalentTo(TypeManager.BooleanType))
      {
        return DaikonBoolName;
      }
      else if (type == TypeManager.ByteType)
      {
        return "byte";
      }
      else if (type == TypeManager.DoubleType || type == TypeManager.DecimalType)
      {
        return "double";
      }
      else if (type == TypeManager.FloatType)
      {
        return "float";
      }
      else if (type == TypeManager.LongType)
      {
        return "long";
      }
      else if (type == TypeManager.ShortType)
      {
        return "short";
      }
      else if (type == TypeManager.CharType)
      {
        return "char";
      }
      else if (type.IsValueType && (type == TypeManager.IntType ||
        // There are a lot of types that could be ints, check the non-standard types as well.
          TypeManager.IsNonstandardIntType(type)))
      {
        return DaikonIntName;
      }
      else if (type == TypeManager.ObjectType)
      {
        return "java.lang.Object";
      }
      else if (type == TypeManager.StringType)
      {
        return DaikonStringName;
      }
      else if (type == TypeManager.TypeType)
      {
        return "java.lang.Class";
      }
      else
      {
        return type.ToString();
      }
    }

    /// <summary>
    /// Get the daikon rep-type for the given type.
    /// </summary>
    /// <param name="type">The .NET type whose daikon rep-type name to get</param>
    /// <returns>A rep-type for the given type, from the list of valid rep-types</returns>
    private static string GetRepType(Type type)
    {
      if (type.IsEquivalentTo(TypeManager.BooleanType))
      {
        return DaikonBoolName;
      }
      else if (Type.GetType("System.Double") == type || Type.GetType("System.Single") == type)
      {
        return "double";
      }
      else if (type.IsValueType)
      {
        // Only remaining rep-type is an int.
        // There are a lot of type that could be ints, so this acts as a catch-all.
        return DaikonIntName;
      }
      else
      {
        return "hashcode";
      }
    }

    #endregion

    public void Dispose()
    {
      this.Dispose(true);
      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Only relevant resource to dispose is the fileWriter.
    /// </summary>
    protected virtual void Dispose(bool disposeManagedResources)
    {
      this.fileWriter.Close();
    }
  }
}
