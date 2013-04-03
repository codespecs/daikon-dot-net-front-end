// DeclarationPrinter prints the declaration portion of the datatrace file
// It is called from ILRewriter.cs while the profiler is visiting the module.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Celeriac.Comparability;
using Microsoft.Cci;
using System.Diagnostics.Contracts;
using Celeriac.Contracts;

namespace Celeriac
{
  /// <summary>
  /// Prints the declaration portion of a datatrace
  /// </summary>
  public class DeclarationPrinter : IDisposable
  {
    // The arguments to be used in printing declarations
    private CeleriacArgs celeriacArgs;

    // Device used to write the declarations
    private TextWriter fileWriter;

    /// <summary>
    /// The type manager to be used for determining variable type.
    /// </summary>
    private TypeManager typeManager;

    private AssemblySummary comparabilityManager;

    /// <summary>
    /// Collection of static fields that have been visited at this program point.
    /// </summary>
    private HashSet<string> staticFieldsForCurrentProgramPoint;

    /// <summary>
    /// Collection of variables declared for the current program point
    /// </summary>
    private HashSet<string> variablesForCurrentProgramPoint;

    #region Constants

    /// <summary>
    /// Number of spaces per indentation
    /// </summary>
    private const int SpacesPerIndent = 2;

    /// <summary>
    /// Number of indentations for a standard data entry
    /// </summary>
    private const int IndentsForEntry = 2;

    private const bool EnableTrace = true;

    /// <summary>
    /// Number of indentations for the name of a variable
    /// </summary>
    private const int IndentsForName = 1;

    /// <summary>
    /// Until real comparability works (TODO(#22)) print a constant value everywhere.
    /// </summary>
    private const int ComparabilityConstant = -22;

    // Daikon is hard coded to recognize the Java full names for dec/rep types
    private const string DaikonStringName = "java.lang.String";
    private const string DaikonBoolName = "boolean";
    private const string DaikonIntName = "int";
    private const string DaikonHashCodeName = "hashcode";
    private const string DaikonDoubleName = "double";

    /// <summary>
    /// The value to print when we make a GetType() call on a variable
    /// </summary>
    public const string GetTypeMethodCall = "GetType()";

    /// <summary>
    /// The value to print when we make a ToString() call on a variable
    /// </summary>
    public const string ToStringMethodCall = "ToString()";

    /// <summary>
    /// The string that prefixes the generate method name for getter properties
    /// </summary>
    public const string GetterPropertyPrefix = "get_";

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
    internal enum VariableFlags
    {
      none = 0,
      synthetic = 1,
      classname = synthetic << 1,
      to_string = classname << 1,
      is_param = to_string << 1,
      no_dups = is_param << 1,
      not_ordered = no_dups << 1,
      is_property = not_ordered << 1,
      is_enum = is_property << 1,

      /// <summary>
      /// The variable reference is fixed, and the path to the variable fixed.
      /// </summary>
      is_reference_immutable = is_enum << 1,

      /// <summary>
      /// The variable value is fixed, and the path to the variable is fixed.
      /// </summary>
      is_value_immutable = is_reference_immutable << 1,
    }

    /// <summary>
    /// Create a new declaration printer with the given command line arguments. Also creates a new
    /// file to hold the output, if output is being written to a file.
    /// </summary>
    /// <param name="args">Arguments to use while printing the declaration file</param>
    /// <param name="typeManager">Type manager to use while printing the declaration file</param>
    public DeclarationPrinter(CeleriacArgs args, TypeManager typeManager, AssemblySummary comparabilityManager)
    {
      if (args == null)
      {
        throw new ArgumentNullException("args");
      }
      this.celeriacArgs = args;

      if (typeManager == null)
      {
        throw new ArgumentNullException("typeManager");
      }
      this.typeManager = typeManager;

      this.comparabilityManager = comparabilityManager;

      if (args.PrintOutput)
      {
        this.fileWriter = System.Console.Out;
      }
      else
      {
        if (!Directory.Exists(celeriacArgs.OutputLocation))
        {
          Directory.CreateDirectory(Path.GetDirectoryName(celeriacArgs.OutputLocation));
        }
        this.fileWriter = new StreamWriter(this.celeriacArgs.OutputLocation);
      }

      if (this.celeriacArgs.ForceUnixNewLine)
      {
        this.fileWriter.NewLine = "\n";
      }

      this.staticFieldsForCurrentProgramPoint = new HashSet<string>();
      this.variablesForCurrentProgramPoint = new HashSet<string>();

      this.PrintPreliminaries();
    }

    /// <summary>
    /// Print the declaration for the given variable, and all its children
    /// </summary>
    /// <param name="name">The name of the variable</param>
    /// <param name="type">The declared type of the variable</param> 
    /// <param name="kind">Optional, the daikon kind of the variable</param>
    /// <param name="flags">Optional, the daikon flags for the variable</param>
    /// <param name="enclosingVar">Optional, the daikon enclosing var of the variable 
    /// (its parent) </param>
    /// <param name="relativeName">Optional, the daikon relative name for the variable 
    /// (how to get to it) </param>
    /// <param name="nestingDepth">The nesting depth of the current variable. If not given 
    /// assumed to be the root value of 0.</param>
    private void DeclareVariable(string name, Type type,
        Type originatingType, VariableKind kind = VariableKind.variable,
        VariableFlags flags = VariableFlags.none,
        string enclosingVar = "", string relativeName = "", string parentName = "",
        int nestingDepth = 0,
        INamedTypeDefinition typeContext = null, IMethodDefinition methodContext = null)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null);
      Contract.Requires(nestingDepth >= 0);
      Contract.Requires(typeContext != null || methodContext != null);

      if (PerformEarlyExitChecks(name, kind, enclosingVar, nestingDepth))
      {
        return;
      }

      if (type.IsEnum)
      {
        flags = ExtendFlags(flags, VariableFlags.is_enum);
      }
      PrintSimpleDescriptors(name, type, kind, flags, enclosingVar, relativeName, parentName, typeContext, methodContext);

      // If the variable is an object, then look at its fields or elements.
      // Don't look at the fields if the variable is a ToString or Classname call.
      if (flags.HasFlag(VariableFlags.to_string) || flags.HasFlag(VariableFlags.classname))
      {
        return;
      }

      if (this.typeManager.IsListImplementer(type))
      {
        // It's not an array it's a set. Investigate element type.
        // Will resolve with TODO(#52).
        DeclareVariableAsList(name, type, parentName, nestingDepth, originatingType, typeContext: typeContext, methodContext: methodContext);
      }
      else if (this.typeManager.IsFSharpListImplementer(type))
      {
        // We don't get information about generics, so all we know for sure is that the elements
        // are objects.
        // FSharpLists get converted into object[].
        DeclareVariableAsList(name, typeof(object[]), parentName, nestingDepth, originatingType, typeContext: typeContext, methodContext: methodContext);
      }
      else if (this.typeManager.IsSet(type))
      {
        // It's not an array it's a set. Investigate element type.
        // Will resolve with TODO(#52).
        DeclareVariableAsList(name, type, parentName, nestingDepth,
            originatingType, VariableFlags.no_dups | VariableFlags.not_ordered,
            typeContext: typeContext, methodContext: methodContext);
      }
      else if (this.typeManager.IsFSharpSet(type))
      {
        // We don't get information about generics, so all we know for sure is that the elements
        // are objects.
        // FSharpLists get converted into object[].
        Type elementType = type.GetGenericArguments()[0];
        // It's not an array it's a set. Investigate element type.
        // Will resolve with TODO(#52).
        DeclareVariableAsList(name, Array.CreateInstance(elementType, 0).GetType(),
            parentName, nestingDepth, originatingType,
            VariableFlags.no_dups | VariableFlags.not_ordered,
            typeContext: typeContext, methodContext: methodContext);
      }
      else if (this.typeManager.IsDictionary(type))
      {
        // TODO(#54): Implement
        DeclareVariableAsList(name, typeof(List<DictionaryEntry>), parentName, nestingDepth,
          originatingType, VariableFlags.no_dups | VariableFlags.not_ordered,
          typeContext: typeContext, methodContext: methodContext);
      }
      else if (this.typeManager.IsFSharpMap(type))
      {
        DeclareVariableAsList(name, typeof(List<DictionaryEntry>), parentName, nestingDepth,
            originatingType, VariableFlags.no_dups | VariableFlags.not_ordered,
            typeContext: typeContext, methodContext: methodContext);
      }
      else
      {
        DeclarationChildPrinting(name, type, kind, flags, parentName, nestingDepth,
            originatingType, typeContext: typeContext, methodContext: methodContext);
      }
    }

    /// <summary>
    /// Prints the children fields of a variable, including static fields, pure methods,
    /// ToString(), GetType(), etc.
    /// </summary>
    /// <param name="name">Name of the variable</param>
    /// <param name="type">Type of the variable</param>
    /// <param name="kind">Daikon kind of the variable</param>
    /// <param name="flags">Variable flags</param>
    /// <param name="parentName">Name of the parent of the variable if any</param>
    /// <param name="nestingDepth">Nesting depth of the variable</param>
    private void DeclarationChildPrinting(string name, Type type, VariableKind kind,
      VariableFlags flags, string parentName, int nestingDepth, Type originatingType,
      INamedTypeDefinition typeContext = null, IMethodDefinition methodContext = null)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null);
      Contract.Requires(nestingDepth >= 0);
      Contract.Requires(typeContext != null || methodContext != null);

      foreach (FieldInfo field in
        type.GetSortedFields(this.celeriacArgs.GetInstanceAccessOptionsForFieldInspection(
            type, originatingType)))
      {
        if (!this.typeManager.ShouldIgnoreField(type, field))
        {
          // Propogate the reference immutability chain; if the field holds an immutable value, introduce an immutability flag
          var fieldFlags = ExtendFlags(
              MarkIf(field.IsLiteral || (flags.HasFlag(VariableFlags.is_reference_immutable) && field.IsInitOnly), VariableFlags.is_reference_immutable),
              MarkIf(field.IsLiteral || (flags.HasFlag(VariableFlags.is_reference_immutable) && field.IsInitOnly && TypeManager.IsImmutable(field.FieldType)), VariableFlags.is_value_immutable));

          DeclareVariable(name + "." + field.Name, field.FieldType, originatingType,
              VariableKind.field, enclosingVar: name, relativeName: field.Name,
              nestingDepth: nestingDepth + 1,
              flags: fieldFlags,
              parentName: parentName, typeContext: typeContext, methodContext: methodContext);
        }
      }

      PrintStaticFields(type, originatingType, typeContext, methodContext);

      if (!type.IsSealed)
      {
        var immutability = MarkIf(flags.HasFlag(VariableFlags.is_reference_immutable), VariableFlags.is_reference_immutable | VariableFlags.is_value_immutable);

        DeclareVariable(name + "." + GetTypeMethodCall, TypeManager.TypeType, originatingType,
            VariableKind.function,
          // Parent must be reference immutable to for ToType() to remain constant
            ExtendFlags(VariableFlags.classname, VariableFlags.synthetic, immutability),
            enclosingVar: name, relativeName: GetTypeMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName,
            typeContext: typeContext, methodContext: methodContext);
      }

      if (type == TypeManager.StringType)
      {
        var immutability = MarkIf(
            flags.HasFlag(VariableFlags.is_reference_immutable) && flags.HasFlag(VariableFlags.is_value_immutable), VariableFlags.is_value_immutable);

        DeclareVariable(name + "." + ToStringMethodCall, TypeManager.StringType,
            originatingType,
            VariableKind.function,
          // Parent must be value-immutable for ToString() value to remain constant (assuming it doesn't access outside state)
            ExtendFlags(VariableFlags.to_string, VariableFlags.synthetic, immutability),
            enclosingVar: name, relativeName: ToStringMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName,
            typeContext: typeContext, methodContext: methodContext);
      }

      foreach (var method in typeManager.GetPureMethodsForType(type, originatingType))
      {
        string methodName = DeclarationPrinter.SanitizePropertyName(method.Name);

        var pureMethodFlags = ExtendFlags(
            MarkIf(method.Name.StartsWith(GetterPropertyPrefix), VariableFlags.is_property),
            MarkIf(flags.HasFlag(VariableFlags.is_reference_immutable) && method.ReturnType.IsValueType, VariableFlags.is_reference_immutable),
            MarkIf(flags.HasFlag(VariableFlags.is_reference_immutable) && flags.HasFlag(VariableFlags.is_value_immutable) && TypeManager.IsImmutable(method.ReturnType), VariableFlags.is_value_immutable));

        DeclareVariable(SanitizedMethodExpression(method, name),
          method.ReturnType,
          originatingType,
          enclosingVar: name,
          relativeName: methodName,
          kind: VariableKind.function,
          flags: pureMethodFlags,
          nestingDepth: nestingDepth + 1,
          parentName: parentName,
          typeContext: typeContext, methodContext: methodContext);
      }

      // Don't look at linked-lists of synthetic variables or fields to prevent children 
      // also printing linked-lists, when they are really just deeper levels of the current 
      // linked list.
      if (((flags & VariableFlags.synthetic) == 0) && kind != VariableKind.field
          && celeriacArgs.LinkedLists
          && this.typeManager.IsLinkedListImplementer(type))
      {
        FieldInfo linkedListField = TypeManager.FindLinkedListField(type);
        PrintList(name + "[..]", linkedListField.FieldType, name, originatingType,
            VariableKind.array,
            nestingDepth: nestingDepth, parentName: parentName, flags: flags);
      }
    }

    private void PrintStaticFields(Type type, Type originatingType, INamedTypeDefinition typeContext, IMethodDefinition methodContext)
    {
      foreach (FieldInfo staticField in
          type.GetSortedFields(this.celeriacArgs.GetStaticAccessOptionsForFieldInspection(
              type, originatingType)))
      {
        string staticFieldName = type.FullName + "." + staticField.Name;
        if (!this.typeManager.ShouldIgnoreField(type, staticField) &&
            !this.staticFieldsForCurrentProgramPoint.Contains(staticFieldName))
        {
          this.staticFieldsForCurrentProgramPoint.Add(staticFieldName);

          var fieldFlags =
              ExtendFlags(
                MarkIf(staticField.IsLiteral || staticField.IsInitOnly, VariableFlags.is_reference_immutable),
                MarkIf(staticField.IsLiteral || (staticField.IsInitOnly && TypeManager.IsImmutable(staticField.FieldType)), VariableFlags.is_value_immutable));

          DeclareVariable(staticFieldName, staticField.FieldType,
            nestingDepth: staticFieldName.Count(c => c == '.'),
            originatingType: originatingType,
            flags: fieldFlags,
            typeContext: typeContext, methodContext: methodContext);
        }
      }
    }

    /// <summary>
    /// Print all the simple descriptors for this variable (name, flags, etc). Specifically excludes
    /// any list expansion, children elements, pure method calls etc.
    /// </summary>
    /// <param name="name">Name of the variable to be declared</param>
    /// <param name="type">(.NET) Type of the variable to be declared</param>
    /// <param name="kind">Daikon-kind of the variable to be declared</param>
    /// <param name="flags">Daikon-flags describing the variable</param>
    /// <param name="enclosingVar">Variable enclosing the one to be declared</param>
    /// <param name="relativeName">Relative name of the variable to be declared</param>
    /// <param name="parentName">Parent name of the variable to be declared</param>
    private void PrintSimpleDescriptors(string name, Type type, VariableKind kind,
      VariableFlags flags, string enclosingVar, string relativeName, string parentName,
      INamedTypeDefinition typeContext = null, IMethodDefinition methodContext = null)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null);
      Contract.Requires(typeContext != null || methodContext != null);

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

      this.PrintFlags(flags, type.IsValueType);

      if (comparabilityManager != null)
      {
        if (type.IsArray)
        {
          this.WritePair("comparability",
              comparabilityManager.GetComparability(name, typeManager, typeContext, methodContext) + "[" + comparabilityManager.GetElementComparability(name, typeManager, typeContext, methodContext) + "]",
              2);
        }
        else
        {
          this.WritePair("comparability", comparabilityManager.GetComparability(name, typeManager, typeContext, methodContext), 2);
        }
      }
      else
      {
        this.WritePair("comparability", ComparabilityConstant, 2);
      }


      if (ShouldPrintParentPptIfNecessary(parentName))
      {
        this.WritePair("parent", parentName, 2);
      }

    }

    /// <summary>
    /// Print the declaration for the given list, and all its children.
    /// </summary>
    /// <param name="name">The name of the list</param>
    /// <param name="elementType">The declared element type of the list</param> 
    /// <param name="enclosingVar">The daikon enclosing var of the variable (its parent)</param>
    /// <param name="kind">Optional. The daikon kind of the variable</param>
    /// <param name="flags">Optional. The daikon flags for the variable</param>       
    /// <param name="relativeName">Optional. The daikon relative name for the variable 
    /// (how to get to it) </param>
    /// <param name="nestingDepth">The nesting depth of the current variable. If not given 
    /// assumed to be the root value of 0.</param>
    private void PrintList(string name, Type elementType, string enclosingVar,
        Type originatingType, VariableKind kind = VariableKind.array,
        VariableFlags flags = VariableFlags.none,
        string relativeName = "", string parentName = "", int nestingDepth = 0,
        INamedTypeDefinition typeContext = null, IMethodDefinition methodContext = null)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));

      if (nestingDepth > this.celeriacArgs.MaxNestingDepth ||
          !this.celeriacArgs.ShouldPrintVariable(name))
      {
        return;
      }

      // We might not know the type, i.e. for non-generic ArrayList
      if (elementType == null)
      {
        elementType = TypeManager.ObjectType;
      }

      if (elementType.IsEnum)
      {
        flags = ExtendFlags(flags, VariableFlags.is_enum);
      }

      this.WritePair("variable", name, IndentsForName);

      this.PrintVarKind(kind, relativeName);

      // Lists must always have enclosing var
      if (enclosingVar.Length == 0)
      {
        throw new NotSupportedException("Enclosing var required for list"
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

      this.PrintFlags(flags, elementType.IsValueType);

      if (comparabilityManager != null)
      {
        this.WritePair(
            "comparability",
            comparabilityManager.GetElementComparability(enclosingVar, typeManager, typeContext, methodContext),
            IndentsForEntry);
      }
      else
      {
        this.WritePair("comparability", ComparabilityConstant, IndentsForEntry);
      }

      if (ShouldPrintParentPptIfNecessary(parentName))
      {
        this.WritePair("parent", parentName, IndentsForEntry);
      }

      // Print the fields for each element -- unless it's not a class or the list is synthetic
      if (flags.HasFlag(VariableFlags.synthetic))
      {
        return;
      }

      if (this.typeManager.IsListImplementer(elementType) ||
         (this.typeManager.IsFSharpListImplementer(elementType)))
      {
        // Daikon can't handle nested lists. Silently skip.
        return;
      }

      foreach (FieldInfo field in
          elementType.GetSortedFields(this.celeriacArgs.GetInstanceAccessOptionsForFieldInspection(
              elementType, originatingType)))
      {
        if (!this.typeManager.ShouldIgnoreField(elementType, field))
        {
          PrintList(name + "." + field.Name, field.FieldType, name,
              originatingType, VariableKind.field, relativeName: field.Name,
              nestingDepth: nestingDepth + 1, parentName: parentName,
              typeContext: typeContext, methodContext: methodContext);
        }
      }

      foreach (FieldInfo staticField in
          elementType.GetSortedFields(this.celeriacArgs.GetStaticAccessOptionsForFieldInspection(
              elementType, originatingType)))
      {
        string staticFieldName = elementType.FullName + "." + staticField.Name;
        if (!this.typeManager.ShouldIgnoreField(elementType, staticField) &&
            !this.staticFieldsForCurrentProgramPoint.Contains(staticFieldName))
        {
          this.staticFieldsForCurrentProgramPoint.Add(staticFieldName);
          DeclareVariable(staticFieldName, staticField.FieldType,
              originatingType: originatingType,
              nestingDepth: staticFieldName.Count(c => c == '.'),
              typeContext: typeContext, methodContext: methodContext);
        }
      }

      if (!elementType.IsSealed)
      {
        PrintList(name + "." + GetTypeMethodCall, TypeManager.TypeType, name,
            originatingType, VariableKind.function,
            ExtendFlags(VariableFlags.classname, VariableFlags.synthetic),
            relativeName: GetTypeMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName,
            typeContext: typeContext, methodContext: methodContext);
      }

      if (elementType == TypeManager.StringType)
      {
        PrintList(name + "." + ToStringMethodCall, TypeManager.StringType, name,
            originatingType, VariableKind.function,
            ExtendFlags(VariableFlags.to_string, VariableFlags.synthetic),
            relativeName: ToStringMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName,
            typeContext: typeContext, methodContext: methodContext);
      }

      foreach (var method in typeManager.GetPureMethodsForType(elementType, originatingType))
      {
        // TODO 83: don't skip static methods for lists
        if (method.IsStatic)
        {
          continue;
        }

        string methodName = DeclarationPrinter.SanitizePropertyName(method.Name);

        var pureMethodFlags = ExtendFlags(
          method.Name.StartsWith(GetterPropertyPrefix) ? VariableFlags.is_property : VariableFlags.none);

        PrintList(SanitizedMethodExpression(method, name),
          method.ReturnType, name,
          originatingType,
          relativeName: methodName,
          kind: VariableKind.function,
          flags: pureMethodFlags,
          nestingDepth: nestingDepth + 1, parentName: parentName,
          typeContext: typeContext, methodContext: methodContext);
      }
    }

    /// <summary>
    /// Close the file writer
    /// </summary>
    public void CloseWriter()
    {
      this.fileWriter.Close();
    }

    /// <summary>
    /// Print the declarations for the fields of the parent of the current object: "this".
    /// </summary>
    /// <param name="parentName">Name of the parent, as it would appear in the program point
    /// </param>
    /// <param name="parentObjectType">Assembly-qualified name of the type of the parent
    /// </param>
    public void PrintParentObjectFields(string parentName, string assemblyQualifiedName, INamedTypeDefinition typeContext, IMethodDefinition methodContext)
    {
      parentName = parentName + ":::OBJECT 1";

      CeleriacTypeDeclaration typeDecl =
          this.typeManager.ConvertAssemblyQualifiedNameToType(assemblyQualifiedName);

      foreach (Type type in typeDecl.GetAllTypes)
      {
        // If we can't resolve the parent object type don't write anything
        if (type != null)
        {
          this.DeclareVariable("this", type, type, parentName: parentName,
              flags: ExtendFlags(VariableFlags.is_param, VariableFlags.is_reference_immutable, MarkIf(TypeManager.IsImmutable(type), VariableFlags.is_value_immutable)),
              typeContext: typeContext, methodContext: methodContext);
        }
        else
        {
          // TODO(#47): Error handling?
        }
      }
    }

    /// <summary>
    /// Print the static fields of the parent of the current object.
    /// </summary>
    /// <param name="parentName">Name of the parent, as it would appear in the program point
    /// </param>
    /// <param name="parentObjectType">Assembly-qualified name of the type of the parent
    /// </param>
    public void PrintParentClassFields(string parentObjectType, IMethodDefinition method)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(parentObjectType));
      Contract.Requires(method != null);

      // TODO(#48): Parent type like we do for instance fields.
      CeleriacTypeDeclaration typeDecl =
          typeManager.ConvertAssemblyQualifiedNameToType(parentObjectType);
      foreach (Type type in typeDecl.GetAllTypes)
      {
        Contract.Assume(type != null, "Unable to resolve parent object type to a type.");
        DeclareStaticFieldsForType(type, type, null, methodContext: method); // TWS what type to use for context?
      }
    }

    /// <summary>
    /// Print the declarations for all fields of the given type.
    /// </summary>
    /// <param name="type">Type to print declarations of the static fields of</param>
    private void DeclareStaticFieldsForType(Type type, Type originatingType, INamedTypeDefinition typeContext, IMethodDefinition methodContext = null)
    {
      Contract.Requires(type != null);
      Contract.Requires(typeContext != null || methodContext != null);

      foreach (FieldInfo staticField in
        // type passed in as originating type so we get all the fields for it
        type.GetSortedFields(this.celeriacArgs.GetStaticAccessOptionsForFieldInspection(
            type, type)))
      {
        string staticFieldName = type.FullName + "." + staticField.Name;
        if (!this.typeManager.ShouldIgnoreField(type, staticField) &&
            !this.staticFieldsForCurrentProgramPoint.Contains(staticFieldName))
        {
          this.staticFieldsForCurrentProgramPoint.Add(staticFieldName);
          DeclareVariable(staticFieldName, staticField.FieldType, originatingType,
              nestingDepth: staticFieldName.Count(c => c == '.'),
              typeContext: typeContext, methodContext: methodContext);
        }
      }
    }

    /// <summary>
    /// Write the entrance to a method call.
    /// </summary>
    /// <param name="methodName">Name of the program point being entered</param>
    public void PrintCallEntrance(string methodName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(methodName));

      this.WriteLine();
      this.WritePair("ppt", SanitizeProgramPointName(methodName));
      this.WritePair("ppt-type", "enter");
      this.staticFieldsForCurrentProgramPoint.Clear();
      this.variablesForCurrentProgramPoint.Clear();
    }

    /// <summary>
    /// Write the program point name for an exit from a method call.
    /// </summary>
    /// <param name="methodName">The name of the program point being exited</param>
    public void PrintCallExit(string methodName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(methodName));

      this.WriteLine();
      this.WritePair("ppt", SanitizeProgramPointName(methodName));
      this.WritePair("ppt-type", "subexit");
      this.staticFieldsForCurrentProgramPoint.Clear();
      this.variablesForCurrentProgramPoint.Clear();
    }

    /// <summary>
    /// Print a parameter, and its children, with the given name and the given assembly-qualified
    /// declared type.
    /// </summary>
    /// <param name="name">The name of the parameter to print</param>
    /// <param name="paramType">The assembly-qualified name of the program to print</param>
    public void PrintParameter(string name, string paramType, IMethodDefinition methodDefinition)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(methodDefinition != null);

      CeleriacTypeDeclaration typeDecl = typeManager.ConvertAssemblyQualifiedNameToType(paramType);
      foreach (Type type in typeDecl.GetAllTypes)
      {
        if (type != null)
        {
          // TODO: the defining method should be the originator
          DeclareVariable(name, type, typeof(DummyOriginator), flags: VariableFlags.is_param, methodContext: methodDefinition);
        }
      }
    }

    /// <summary>
    /// Print the declaration for a method's return value, and its children.
    /// </summary>
    /// <param name="name">Name of the return value, commonly "return"</param>
    /// <param name="returnType">Assembly qualified name of the return type</param>
    public void PrintReturn(string name, string returnType, IMethodDefinition methodDefinition)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(!string.IsNullOrWhiteSpace(returnType));

      CeleriacTypeDeclaration typeDecl = typeManager.ConvertAssemblyQualifiedNameToType(returnType);
      foreach (Type type in typeDecl.GetAllTypes)
      {
        if (type != null)
        {
          // TODO: originator should be the type that defined the method
          DeclareVariable(name, type, typeof(DummyOriginator), kind: VariableKind.Return, nestingDepth: 0, methodContext: methodDefinition);
        }
      }
    }

    /// <summary>
    /// Print the declaration of the object with the given qualified assembly name
    /// </summary>
    /// <param name="objectName">How the object should be described in the declaration</param>
    /// <param name="objectAssemblyQualifiedName">Assembly qualified name of the object,
    /// used to fetch the Type</param>
    public void PrintObjectDefinition(string objectName, string objectAssemblyQualifiedName, INamedTypeDefinition type)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(objectName));
      Contract.Requires(!string.IsNullOrWhiteSpace(objectAssemblyQualifiedName));
      Contract.Requires(type != null);

      CeleriacTypeDeclaration objectTypeDecl =
          typeManager.ConvertAssemblyQualifiedNameToType(objectAssemblyQualifiedName);
      foreach (Type objectType in objectTypeDecl.GetAllTypes)
      {
        this.variablesForCurrentProgramPoint.Clear();
        if (objectType != null)
        {
          string nameToPrint = SanitizeProgramPointName(objectName + ":::OBJECT");
          if (celeriacArgs.ShouldPrintProgramPoint(nameToPrint))
          {
            this.WriteLine();
            this.WritePair("ppt", nameToPrint);
            this.WritePair("ppt-type", "object");
            this.WritePair("parent", "parent " + nameToPrint.Replace(":::OBJECT", ":::CLASS 1"));
            // Pass objectType in as originating type so we get all its private fields.
            this.DeclareVariable("this", objectType, objectType, VariableKind.variable,
                flags: ExtendFlags(VariableFlags.is_param, VariableFlags.is_reference_immutable, MarkIf(TypeManager.IsImmutable(objectType), VariableFlags.is_reference_immutable)),
                typeContext: type);
          }
        }
      }
    }

    /// <summary>
    /// Print declaration of the static fields of the given class.
    /// </summary>
    /// <param name="className">Name of the class whose static fields are being examined</param>
    /// <param name="objectAssemblyQualifiedName">The assembly qualified name of the object whose 
    /// static fields to print</param>
    public void PrintParentClassDefinition(string className, string objectAssemblyQualifiedName, INamedTypeDefinition typeContext)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(className));
      Contract.Requires(!string.IsNullOrWhiteSpace(objectAssemblyQualifiedName));
      Contract.Requires(typeContext != null);

      CeleriacTypeDeclaration objectTypeDecl =
          typeManager.ConvertAssemblyQualifiedNameToType(objectAssemblyQualifiedName);
      foreach (Type objectType in objectTypeDecl.GetAllTypes)
      {
        this.variablesForCurrentProgramPoint.Clear();
        string nameToPrint = SanitizeProgramPointName(className + ":::CLASS");
        if (celeriacArgs.ShouldPrintProgramPoint(nameToPrint))
        {
          this.WriteLine();
          this.WritePair("ppt", nameToPrint);
          this.WritePair("ppt-type", "class");
          DeclareStaticFieldsForType(objectType, objectType, typeContext);
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
      Contract.Requires(!string.IsNullOrWhiteSpace(programPointName));
      string result = programPointName.Replace("\\", "\\\\");
      return result.Replace(" ", "\\_");
    }

    /// <summary>
    /// In the IL property names have "get_" inserted in front of the property name. Including this
    /// prefix in the output is confusing to the user, who doesn't expect it. This method removes
    /// the prefix if necessary.
    /// </summary>
    /// <param name="propertyNameInIL">Name of the property as stored in IL</param>
    /// <returns>Property name as the developer would recognize it</returns>
    public static string SanitizePropertyName(string propertyNameInIL)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(propertyNameInIL));
      if (propertyNameInIL.StartsWith(GetterPropertyPrefix))
      {
        propertyNameInIL = propertyNameInIL.Replace(GetterPropertyPrefix, "");
      }
      return propertyNameInIL;
    }

    /// <summary>
    /// Print a reference to the parent program point of the given type, with a switch whether the 
    /// program point is a class or an object.
    /// </summary>
    /// <param name="type">Type to print the parent reference to</param>
    /// <param name="isObjectProgramPoint">True is the reference is to a parent object program point, 
    /// false if the reference is to a parent class program point</param>
    public void PrintParentName(Microsoft.Cci.ITypeReference type, bool isObjectProgramPoint)
    {
      Contract.Requires(type != null);

      string parentName = type.ToString();
      if (ShouldPrintParentPptIfNecessary(parentName))
      {
        if (isObjectProgramPoint)
        {
          this.WritePair("parent", "parent " + parentName + ":::OBJECT 1");
        }
        else
        {
          this.WritePair("parent", "parent " + parentName + ":::CLASS 1");
        }
      }
    }

    #region Private Helper Methods

    /// <summary>
    /// Mask with unset options at 0
    /// </summary>
    private static VariableFlags FlagMask
    {
      get
      {
        VariableFlags mask = 0;
        return ~mask;
      }
    }

    /// <summary>
    /// Concenince method, returning <code>flag </code> when <code>condition</code> is true.
    /// empty flags.
    /// </summary>
    /// <param name="condition">Condition to test</param>
    /// <param name="flag">Flags to return if condition is true</param>
    /// <returns>Flags iff contition is true, otherwise none</returns>
    private static VariableFlags MarkIf(bool condition, VariableFlags flag)
    {
      return condition ? flag : VariableFlags.none;
    }

    /// <summary>
    /// Returns union of <code>flags</code>, respecting the Celeriac command line options
    /// </summary>
    /// <param name="flags"></param>
    /// <returns>union of <code>flags</code>, respecting the Celeriac command line options</returns>
    private VariableFlags ExtendFlags(params VariableFlags[] flags)
    {
      Contract.Requires(flags != null);
      Contract.Ensures((flags.Length == 0).Implies(Contract.Result<VariableFlags>() == VariableFlags.none));

      var result = VariableFlags.none;
      foreach (var fs in flags)
      {
        result |= fs;
      }
      if (!celeriacArgs.IsPropertyFlags)
      {
        result &= (result | VariableFlags.is_property) ^ VariableFlags.is_property;
      }
      if (!celeriacArgs.IsEnumFlags)
      {
        result &= (result | VariableFlags.is_enum) ^ VariableFlags.is_enum;
      }
      if (!celeriacArgs.IsReadOnlyFlags)
      {
        result &= (result | VariableFlags.is_value_immutable) ^ VariableFlags.is_value_immutable;
        result &= (result | VariableFlags.is_reference_immutable) ^ VariableFlags.is_reference_immutable;
      }

      return result & FlagMask;
    }

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
      this.WritePair("// Declarations for", this.celeriacArgs.AssemblyName);
      this.WritePair("// Declarations written", DateTime.Now);
      this.fileWriter.WriteLine();
      this.WritePair("decl-version", "2.0");
      this.WritePair("var-comparability", celeriacArgs.StaticComparability ? "implicit" : "none");
      this.WritePair("input-language", "C#.NET");
    }

    /// <summary>
    /// Write the given flags to the class printer on a single line, space separated.
    /// </summary>
    /// <param name="variableFlags">The flags to print</param>
    private void PrintFlags(VariableFlags variableFlags, bool isValueType)
    {
      StringBuilder flagsToPrint = new StringBuilder();

      foreach (VariableFlags f in Enum.GetValues(typeof(VariableFlags)))
      {
        if (variableFlags.HasFlag(f) && f != VariableFlags.none)
        {
          var toPrint = f.ToString();
          if ((f == VariableFlags.is_reference_immutable && !isValueType) ||
              (f == VariableFlags.is_value_immutable && isValueType))
          {
            toPrint = "is_readonly";
          }
          else if (f == VariableFlags.is_reference_immutable || f == VariableFlags.is_value_immutable)
          {
            continue;
          }

          flagsToPrint.Append(toPrint).Append(" ");
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
    private string GetDecType(Type type)
    {
      Contract.Requires(type != null);

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
        string typeStr = type.ToString();
        if (this.celeriacArgs.FriendlyDecTypes)
        {
          typeStr = Regex.Replace(typeStr, @"`\d", "");
          typeStr = Regex.Replace(typeStr, @"\[", "<");
          typeStr = Regex.Replace(typeStr, @"\]", ">");
          // We messed up array specifications with our above replacements
          typeStr = Regex.Replace(typeStr, "<>", "[]");
          typeStr = Regex.Replace(typeStr, @"\+", @".");
        }
        return typeStr;
      }
    }

    /// <summary>
    /// Get the daikon rep-type for the given type.
    /// </summary>
    /// <param name="type">The .NET type whose daikon rep-type name to get</param>
    /// <returns>A rep-type for the given type, from the list of valid rep-types</returns>
    private string GetRepType(Type type)
    {
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<string>().OneOf(DaikonBoolName, DaikonIntName, DaikonStringName, DaikonHashCodeName, DaikonDoubleName));
      if (type.IsEquivalentTo(TypeManager.BooleanType))
      {
        return DaikonBoolName;
      }
      else if (TypeManager.DoubleType == type || TypeManager.FloatType == type
            || TypeManager.DecimalType == type)
      {
        return DaikonDoubleName;
      }
      else if (type.IsValueType && (type == TypeManager.IntType
            || type == TypeManager.ByteType || type == TypeManager.CharType
            || type == TypeManager.LongType || type == TypeManager.ShortType
            || type == TypeManager.ULongType || TypeManager.IsNonstandardIntType(type)))
      {
        return DaikonIntName;
      }
      else if (type.IsEnum && !celeriacArgs.EnumUnderlyingValues)
      {
        return DaikonHashCodeName;
      }
      else
      {
        return DaikonHashCodeName;
      }
    }

    /// <summary>
    /// Print the daikon rep-type for a variable of a given type
    /// </summary>
    /// <param name="type">Type of the variable</param>
    /// <param name="flags">Flags may modify the type of the variable. i.e. in a ToString() 
    /// call</param>
    private void PrintRepType(Type type, VariableFlags flags)
    {
      Contract.Requires(type != null);
      string repType;

      if (flags.HasFlag(VariableFlags.to_string) || flags.HasFlag(VariableFlags.classname))
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
    /// Returns whether the parentType name should be printed. This won't be printed if it's empty
    /// or matched a type to be ignored.
    /// </summary>
    /// <param name="parentTypeName">Parent name</param>
    /// <returns>True if the name should be printed, false otherwise</returns>
    private static bool ShouldPrintParentPptIfNecessary(String parentTypeName)
    {
      return !String.IsNullOrEmpty(parentTypeName) &&
             !TypeManager.RegexForTypesToIgnoreForProgramPoint.IsMatch(parentTypeName) &&
             !TypeManager.CodeContractRuntimePpts.IsMatch(parentTypeName);
    }

    /// <summary>
    /// Checks whether the declartion printing can be exited early, e.g. because a static variable
    /// has already been decalred for this program point, nesting depth is too great, etc.
    /// </summary>
    /// <param name="name">Name of the variable to potentially be declared</param>
    /// <param name="kind">Daikon kind of the variable to potentially be declared</param>
    /// <param name="enclosingVar">Encolsing var, if any of the variable to potentially be,
    /// declared, null or empty otherwise</param>
    /// <param name="nestingDepth">Nesting depth of the variable to potentially be declared</param>
    /// <returns>True if the variable should be skipped, otherwise false</returns>
    private bool PerformEarlyExitChecks(string name, VariableKind kind, string enclosingVar,
      int nestingDepth)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires((kind == VariableKind.field || kind == VariableKind.array).Implies(!string.IsNullOrWhiteSpace(enclosingVar)),
          "Enclosing field required for static fields and arrays");

      if (nestingDepth > this.celeriacArgs.MaxNestingDepth ||
          !this.celeriacArgs.ShouldPrintVariable(name))
      {
        return true;
      }

      if (this.variablesForCurrentProgramPoint.Contains(name))
      {
        return true;
      }
      else
      {
        this.variablesForCurrentProgramPoint.Add(name);
        return false;
      }
    }

    /// <summary>
    /// Wraps all the declaration necessary for a variable that is a list. Print the GetType()
    /// call if necessary then describes the contents of the list.
    /// </summary>
    /// <param name="name">Name of the list variable</param>
    /// <param name="type">Type of the list variable (not element type)</param>
    /// <param name="parentName">Name of the parent</param>
    /// <param name="nestingDepth">Nesting depth for the list variable</param>
    /// <param name="collectionFlags">Flags to describe the collection, e.g. no_dups, if any</param>
    private void DeclareVariableAsList(string name, Type type, string parentName,
        int nestingDepth, Type originatingType,
        VariableFlags collectionFlags = VariableFlags.none,
        INamedTypeDefinition typeContext = null, IMethodDefinition methodContext = null)
    {
      Type elementType = TypeManager.GetListElementType(type);
      // Print the type of the list if it's not primitive
      if (!elementType.IsSealed)
      {
        DeclareVariable(name + "." + GetTypeMethodCall, TypeManager.TypeType,
            originatingType,
            VariableKind.function, VariableFlags.classname | VariableFlags.synthetic,
            enclosingVar: name, relativeName: GetTypeMethodCall,
            nestingDepth: nestingDepth + 1, parentName: parentName,
            typeContext: typeContext, methodContext: methodContext);
      }

      if (type.IsArray && type.GetArrayRank() > 1)
      {
        // Daikon can't handle multidimensional arrays, so we skip them.
        return;
      }

      PrintList(name + "[..]", elementType, name, originatingType, VariableKind.array,
          nestingDepth: nestingDepth, parentName: parentName, flags: collectionFlags,
          typeContext: typeContext, methodContext: methodContext);
    }

    #endregion


    /// <summary>
    /// Returns a method expression string suitable for printing. 
    /// </summary>
    /// <param name="method">the method</param>
    /// <param name="parentName">the parent expression</param>
    /// <returns>a method expression string suitable for printing</returns>
    [Pure]
    public static string SanitizedMethodExpression(MethodInfo method, string parentName)
    {
      Contract.Requires(method != null);
      Contract.Requires(parentName != null);
      Contract.Ensures(Contract.Result<string>() != null);

      var methodName = DeclarationPrinter.SanitizePropertyName(method.Name);

      if (method.IsStatic)
      {
        Contract.Assume(method.GetParameters().Length <= 1);

        // TODO XXX: need to simplify the other standard types
        var declaringType = (method.DeclaringType == typeof(String)) ? "string" : method.DeclaringType.Name;

        if (method.GetParameters().Length == 0)
        {
          return string.Join(".", declaringType, methodName);
        }
        else
        {
          return string.Format("{0}.{1}({2})", declaringType, methodName, parentName);
        }
      }
      else
      {
        return string.Join(".", parentName, methodName);
      }
    }

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
