
// TypeManager holds references to all .NET types we use and can convert
// between CCIMetadata and .NET types.

namespace DotNetFrontEnd
{
  using System;
  using System.Collections;
  using System.Collections.Generic;
  using System.Linq;
  using System.Reflection;
  using System.Text;
  using Microsoft.Cci;
  using Microsoft.Cci.MutableCodeModel;

  /// <summary>
  /// Keeps canoncial type references. Converts between CCIMetadata and .NET types.
  /// </summary>
  [Serializable]
  public class TypeManager
  {
    #region static readonly Types

    // Type stores used by ReflectionArgs and DeclarationPrinter
    // Keep these alphabatized.
    public static readonly Type BooleanType = typeof(bool);
    public static readonly Type ByteType = typeof(byte);
    public static readonly Type CharType = typeof(char);
    public static readonly Type DecimalType = typeof(decimal);
    public static readonly Type DoubleType = typeof(double);
    public static readonly Type FloatType = typeof(float);
    public static readonly Type IntType = typeof(int);
    public static readonly Type ListType = typeof(IList);
    public static readonly Type LongType = typeof(long);
    public static readonly Type ObjectType = typeof(object);
    public static readonly Type ShortType = typeof(short);
    public static readonly Type StringType = typeof(string);
    public static readonly Type TypeType = typeof(Type);
    public static readonly Type ULongType = typeof(ulong);
    public static readonly Type HashSetType = typeof(HashSet<object>);

    //  Misc. int types, not needed by DeclarationPrinter
    private static readonly Type sByteType = typeof(sbyte);
    private static readonly Type uShortType = typeof(ushort);
    private static readonly Type uIntType = typeof(uint);

    /// <summary>
    ///  Name of the interface all sets must implement. Use is similar to a type store, but getting
    ///  any specific set type would require an element type.
    /// </summary>
    private static readonly string SetInterfaceName = "ISet";

    /// <summary>
    ///  Name of the interface all maps must implement. Use is similar to a type store, but getting
    ///  any specific map type would require an element type.
    /// </summary>
    private static readonly string MapInterfaceName = "IDictionary";

    #endregion

    /// <summary>
    /// Don't print object definition program points or references to types whose name matches
    /// this signature, they are system generated.
    /// </summary>
    public readonly static string RegexForTypesToIgnoreForProgramPoint = "<*>";

    /// <summary>
    /// Front end args for the program this class is managing types for
    /// </summary>
    private FrontEndArgs frontEndArgs;

    [NonSerialized]
    private AssemblyIdentity assemblyIdentity;

    /// <summary>
    /// Map from types to whether or not they are list implementors
    /// Memoizes the lookup
    /// We use a bool-valued hashtable because there are essentially three states, IsList, NotList
    /// and Unknown. IsList types has a value of true in the table, NotList types have a value of 
    /// false, and Unknown types are those not in the table.
    /// </summary>
    private Dictionary<Type, bool> isListHashmap;

    /// <summary>
    /// Map from a type to whether it is an FSharpList. Memoizes the lookup.
    /// We use a bool-valued hashmap because there are essentially three states, IsLinkedList, 
    /// NotLinkedList and Unknown. 
    /// </summary>
    private Dictionary<Type, bool> isFSharpListHashmap;

    /// <summary>    
    /// Map from types to whether or not they are linked-list implementors
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are essentially three states, IsLinkedList, 
    /// NotLinkedList and Unknown. 
    /// </summary>
    private Dictionary<Type, bool> isLinkedListHashmap;

    /// <summary>
    /// Map from type to whether that type is a C# hashset.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private Dictionary<Type, bool> isSetHashmap;

    private Dictionary<Type, bool> isMapHashmap;

    /// <summary>
    /// Map from type to whether that type is a F# hashset.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private Dictionary<Type, bool> isFSharpSetHashmap;

    /// <summary>
    /// A map from assembly qualified names to the Type they describe
    /// Used to memoize type references (passed as names) from the IL rewriter
    /// </summary>
    private Dictionary<string, Type> nameTypeMap;

    /// <summary>
    /// Map from type to a set of keys for the pure methods to call for that type
    /// </summary>
    private Dictionary<Type, ISet<int>> pureMethodKeys;

    /// <summary>
    /// Map from key to method info for pure methods
    /// </summary>
    private Dictionary<int, MethodInfo> pureMethods;

    /// <summary>
    /// Used when adding methods to ensure that keys are unique
    /// </summary>
    private int globalPureMethodCount;

    /// <summary>
    /// Create a new TypeManager instance, will be able to resolve types of the given assembly 
    /// without registering the assembly with the GAC.
    /// </summary>
    /// <param name="args">The front-end args applicable to the types being managed here</param>
    public TypeManager(FrontEndArgs args)
    {
      this.frontEndArgs = args;

      this.isListHashmap = new Dictionary<Type, bool>();
      this.isFSharpListHashmap = new Dictionary<Type, bool>();
      this.isLinkedListHashmap = new Dictionary<Type, bool>();
      this.isSetHashmap = new Dictionary<Type, bool>();
      this.isFSharpSetHashmap = new Dictionary<Type, bool>();
      this.isMapHashmap = new Dictionary<Type, bool>();

      this.nameTypeMap = new Dictionary<string, Type>();
      this.pureMethodKeys = new Dictionary<Type, ISet<int>>();
      this.pureMethods = new Dictionary<int, MethodInfo>();
      if (this.frontEndArgs.PurityFile != null)
      {
        this.ProcessPurityMethods();
      }
      this.globalPureMethodCount = 0;
    }

    /// <summary>
    /// Process the purity methods list, building the map from type to pure methods for that type.
    /// </summary>
    private void ProcessPurityMethods()
    {
      foreach (String str in this.frontEndArgs.PurityMethods)
      {
        if (str.StartsWith("//")) { continue; }
        string[] methodDescriptions = str.Split(';');
        try
        {
          string typeName = methodDescriptions[0];
          string methodName = methodDescriptions[1];

          Type type = ConvertAssemblyQualifiedNameToType(typeName);
          // Pure methods have no parameters
          MethodInfo method = type.GetMethod(methodName, new Type[] { });
          if (method == null)
          {
            throw new ArgumentException("No method of name: " + methodName + " on type:" + typeName
                + " exists.");
          }
          if (!this.pureMethodKeys.ContainsKey(type))
          {
            pureMethodKeys[type] = new HashSet<int>();
          }
          pureMethodKeys[type].Add(++globalPureMethodCount);
          pureMethods[globalPureMethodCount] = method;
        }
        catch (IndexOutOfRangeException)
        {
          throw new Exception("Malformed purity file -- line with contents: " + str);
        }
      }
    }

    /// <summary>
    /// Defines a test to see if a type is an element of a given collection set (e.g. is the type
    /// a list, is the type a set, etc.)
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>Whether the type is an element of the set</returns>
    private delegate bool IsElementTest(Type type);

    /// <summary>
    /// Perform memoized lookup to see if the given type is a element of the given collection set.
    /// If the type isn't in the collection set already then perform the test and update the set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <param name="entries">Set to test against</param>
    /// <param name="test">Test to perform if type is not in test</param>
    /// <returns>Whether type is an element of the set, or if it passed the test</returns>
    private bool IsElementOfCollectionType(Type type, Dictionary<Type, bool> entries,
        IsElementTest test)
    {
      if (type == null)
      {
        throw new ArgumentNullException("type");
      }

      // Check in result table
      bool lookupResult;
      if (entries.TryGetValue(type, out lookupResult))
      {
        return lookupResult;
      }

      // If we haven't seen this type before, check each interface it implements.
      // Store result for memoization.
      bool testResult = test(type);
      entries.Add(type, testResult);
      return testResult;
    }

    /// <summary>
    /// Set the given identity assembly as the assembly to use when resolving types.
    /// </summary>
    /// <param name="identity">The identity to use during type resolution</param>
    /// <exception cref="ArgumentNullException">If identity is null</exception>
    /// <exception cref="InvalidOperationException">If assembly identity has been set and another 
    /// call to this method is executed.</exception>
    public void SetAssemblyIdentity(AssemblyIdentity identity)
    {
      if (identity == null)
      {
        throw new ArgumentNullException("identity");
      }
      if (this.assemblyIdentity == null)
      {
        this.assemblyIdentity = identity;
      }
      else
      {
        throw new InvalidOperationException("Attempt to re-set assembly identity");
      }
    }

    /// <summary>
    /// Explicit test for wheter the given type is an F# list.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the type is an F# list, false otherwise.</returns>
    private bool IsFSharpListTest(Type type)
    {
      if (this.frontEndArgs.ElementInspectArraysOnly)
      {
        return type.IsArray;
      }
      else
      {
        return type.Namespace == "Microsoft.FSharp.Collections" && type.Name.StartsWith("FSharpList");
      }
    }

    /// <summary>
    /// Check if the given type extends FSharpList, memoized
    /// </summary>
    /// <param name="type">The type to check</param>
    /// <returns>True if the type derives from FSharp.Collections.FSharpList, otherwise 
    /// false</returns>
    public bool IsFSharpListImplementer(Type type)
    {
      return IsElementOfCollectionType(type, this.isFSharpListHashmap, IsFSharpListTest);
    }

    /// <summary>
    /// Explicity test whether type is a C# list.
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>Whether type is a C# list</returns>
    private bool IsListTest(Type type)
    {
      return SearchForMatchingInterface(type, 
          interfaceToTest => interfaceToTest == TypeManager.ListType);
    }

    /// <summary>
    /// Check if the given type implements List, memoized
    /// </summary>
    /// <param name="type">The type to check</param>
    /// <returns>True if the type implements System.Collections.List, otherwise false</returns>
    public bool IsListImplementer(Type type)
    {
      return IsElementOfCollectionType(type, this.isListHashmap, IsListTest);
    }

    /// <summary>
    /// Whether the given type is a non-standard int type, meaning an int 
    /// type not captured by the daikon primitive type checker, these types
    /// are described above with the XType fields
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>True if the type is on the non int-types, otherwise false.</returns>
    public static bool IsNonstandardIntType(Type type)
    {
      return
          type == TypeManager.sByteType ||
          type == TypeManager.uShortType ||
          type == TypeManager.uIntType;
    }

    /// <summary>
    /// Explicity tests whether the given type is a C# linked list.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>Whether type is a linked list.</returns>
    private static bool IsLinkedListTest(Type type)
    {
      // Number of fields whose type matches the type being checked 
      int numOwnType = 0;
      FieldInfo[] fields = type.GetFields(System.Reflection.BindingFlags.Public
                                            | System.Reflection.BindingFlags.NonPublic
                                            | System.Reflection.BindingFlags.Instance);
      foreach (FieldInfo field in fields)
      {
        if (field.FieldType == type)
        {
          numOwnType++;
        }
      }

      // We have a linked list if there is exactly 1 field of the proper type.
      return numOwnType == 1;
    }

    /// <summary>
    /// Returns whether the given type is a "linked-list" type: having 1 field of its own type.
    /// </summary>
    /// <param name="type">The type to check for linked-listness.</param>
    /// <returns>True if the type meets the linked-list qualification, otherwise false</returns>
    public bool IsLinkedListImplementer(Type type)
    {
      return IsElementOfCollectionType(type, this.isLinkedListHashmap, IsLinkedListTest);
    }

    /// <summary>
    /// Explicitly tests whether type is a C# set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a C# test, false otherwise.</returns>
    private bool IsSetTest(Type type)
    {
      return SearchForMatchingInterface(type, 
          interfaceToTest => interfaceToTest.Name.Contains(SetInterfaceName));
    }

    /// <summary>
    /// Delegate used to see if an interface statifies a condition
    /// </summary>
    /// <param name="interfaceToCheck">Interface to test</param>
    /// <returns>True if the interface satisfies the condition, otherwise false</returns>
    private delegate bool InterfaceMatchTest(Type interfaceToCheck);

    /// <summary>
    /// Search the interfaces of type looking for an interface satisfying matchTest
    /// </summary>
    /// <param name="type">Type whose interfaces to investigate</param>
    /// <param name="matchTest">Test such that if any interfaces of type pass the test the return 
    /// is true</param>
    /// <returns>True if any of type's interfaces pass matchTest, otherwise false</returns>
    private bool SearchForMatchingInterface(Type type, InterfaceMatchTest matchTest)
    {
      // If we are only element inspecting arrays return that result.
      if (this.frontEndArgs.ElementInspectArraysOnly)
      {
        return type.IsArray;
      }
      else
      {
        Type[] interfaces = type.GetInterfaces();
        foreach (Type item in interfaces)
        {
          if (matchTest(item))
          {
            return true;
          }
        }
        return false;
      }
    }

    /// <summary>
    /// Memoized test whether type is in a C# Set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the type is a C# test, false otherwise</returns>
    public bool IsSet(Type type)
    {
      return IsElementOfCollectionType(type, this.isSetHashmap, IsSetTest);
    }

    /// <summary>
    /// Explicitly tests whether type is a F# set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a F# test, false otherwise.</returns>
    private bool IsFSharpSetTest(Type type)
    {
      if (this.frontEndArgs.ElementInspectArraysOnly)
      {
        return type.IsArray;
      }
      else
      {
        return type.Namespace == "Microsoft.FSharp.Collections" &&
          type.Name.StartsWith("FSharpSet");
      }
    }

    /// <summary>
    /// Memoized test whether type is in an F# Set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the type is an F# test, false otherwise</returns>
    public bool IsFSharpSet(Type type)
    {
      return IsElementOfCollectionType(type, this.isFSharpSetHashmap, IsFSharpSetTest);
    }

    public bool IsMap(Type type)
    {
      return IsElementOfCollectionType(type, this.isMapHashmap, IsMapTest);
    }

    private bool IsMapTest(Type type)
    {
      return SearchForMatchingInterface(type, interfaceToTest => 
          interfaceToTest.Name.StartsWith(MapInterfaceName));
    }

    /// <summary>
    /// Get a list of the pure methods that should be called for the given type.
    /// </summary>
    /// <param name="assemblyQualifiedName">Assembly qualified name of the type to get the pure 
    /// methods for</param>
    /// <returns>Map from key to method object of all the pure methods for the given type
    /// </returns>
    public Dictionary<int, MethodInfo> GetPureMethodsForType(String assemblyQualifiedName)
    {
      Type type = this.ConvertAssemblyQualifiedNameToType(assemblyQualifiedName);
      Dictionary<int, MethodInfo> result = new Dictionary<int, MethodInfo>();
      if (this.pureMethodKeys.ContainsKey(type))
      {
        foreach (int key in this.pureMethodKeys[type])
        {
          result.Add(key, pureMethods[key]);
        }
      }
      return result;
    }

    /// <summary>
    /// Convert a string to a type, with memoization
    /// </summary>
    /// <param name="assemblyQualifiedName">An assembly qualified name for a type in 
    /// the program to be profiled</param>
    /// <exception cref="Exception">Occurs if the conversion cannot be successfully compeleted.
    /// </exception>
    /// <returns>The type having that assembly qualified name, or null if the call was unsuccessful
    /// </returns>
    /// Warning supresesd because there is no way to avoid this call.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability",
      "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Reflection.Assembly.LoadFrom")]
    public Type ConvertAssemblyQualifiedNameToType(string assemblyQualifiedName)
    {
      // TODO(#17): Passing around an assembly qualified name here may not be best because it is
      // difficult to build and parse. Consider creating a custom object to manage type identity
      // and use that as a parameter instead.

      // Memoized; if we've seen this string before return the type from before.
      // Otherwise get the type and save the result.
      try
      {
        Type result;
        if (!this.nameTypeMap.TryGetValue(assemblyQualifiedName, out result))
        {
          // Now get the type with the assembly-qualified name.
          // Load the assembly if necessary, then load the type from the assembly.
          result = Type.GetType(
                      assemblyQualifiedName,
            // Assembly resolver -- load from self if necessary.
                      (aName) => aName.Name == this.frontEndArgs.AssemblyName ?
                          System.Reflection.Assembly.LoadFrom(this.frontEndArgs.AssemblyPath) :
                          System.Reflection.Assembly.Load(aName),
            // Type resolver -- load the type from the assembly if we have one
            // Otherwise let .NET resolve it
                      (assem, name, ignore) => assem == null ?
                          Type.GetType(name, false, ignore) :
                              assem.GetType(name, false, ignore),
                      false);
          if (result == null)
          {
            Console.Error.WriteLine("Couldn't convert type with assembly qualified name: " +
                assemblyQualifiedName);
          }
          this.nameTypeMap.Add(assemblyQualifiedName, result);
        }
        return result;
      }
      catch (Exception ex)
      {
        throw new Exception(String.Format("Unable to convert assembly qualified name {0} to a"
            + " Type.", assemblyQualifiedName), ex);
      }
    }

    #region ConvertCCITypeToAssemblyQualifiedNameMethods

    /// <summary>
    /// Get a Reflection Type from a CCI Type
    /// </summary>
    /// <param name="type">A reference to a CCI Type defined in the program to be profiled</param>
    /// <param name="deeplyInspectGenericParameters">Whether to investigate constraints on
    /// generic parmeters</param>
    /// <returns>A reflection type</returns>
    public string ConvertCCITypeToAssemblyQualifiedName(ITypeReference type,
      bool deeplyInspectGenericParameters = true)
    {
      //try
      //{
      string simpleTypeName = CheckSimpleCases(type);
      if (simpleTypeName != null)
      {
        return simpleTypeName;
      }

      // If the type is a vector, then proceed on the element type, but rememeber that it was a 
      // vector so the [] suffix can be added at the end.
      bool isVectorTypeReference = type is VectorTypeReference;
      if (isVectorTypeReference)
      {
        type = ((VectorTypeReference)type).ElementType;
      }

      // TODO(#16): In a future version it'd be nice to get some more
      // information out of these
      if (type is GenericTypeParameterReference || type is GenericMethodParameter
       || type is GenericTypeParameter)
      {
        if (deeplyInspectGenericParameters)
        {
          return PrintListOfGenericParameterClassesAndInterfaces(type);
        }
        // return "System.Object";
      }


      if (type is SpecializedNestedTypeReference)
      {
        type = ((SpecializedNestedTypeReference)type).UnspecializedVersion;
      }

      // Get the full name of the specified type
      string typeName = TypeHelper.GetTypeName(type,
        NameFormattingOptions.UseReflectionStyleForNestedTypeNames);

      typeName = UpdateTypeNameForNestedTypeDefinitions(type, typeName);

      INamedTypeDefinition namedType = type as INamedTypeDefinition;
      if (namedType != null && namedType.MangleName)
      {
        typeName = typeName + '`' + namedType.GenericParameterCount;
      }
      else if (type is GenericTypeInstanceReference)
      {
        var castedType = (GenericTypeInstanceReference)type;
        typeName = TypeHelper.GetTypeName(castedType.GenericType,
            NameFormattingOptions.UseReflectionStyleForNestedTypeNames) + '`' +
            castedType.GenericArguments.Count;
        if (deeplyInspectGenericParameters)
        {
          typeName = AddGenericTypeArguments(typeName, castedType);
        }
        type = castedType.GenericType;
      }

      if (isVectorTypeReference)
      {
        typeName = typeName + "[]";
      }

      AssemblyIdentity identity = DetermineAssemblyIdentity(type);

      return CompleteAssemblyQualifiedTypeNameProcessing(typeName, identity);
      //}
      //catch (Exception ex)
      //{
      //  throw new Exception(String.Format("Unable to convert CCI type named {0} to assembly"
      //      + " qualified name", type), ex);
      //}
    }

    /// <summary>
    /// For the given generic parameter type print a list of classes and interfaces extended / 
    /// implemented by the type. Fields/Pure Methods for all these classes should be declared
    /// and visited.
    /// </summary>
    /// <param name="type">Generic parameter to inspect</param>
    /// <returns>Array of form [a,b,...,x] containing comma separated assembly-qualified names
    /// of the classes or interfaces constraining the generic parameter</returns>
    private string PrintListOfGenericParameterClassesAndInterfaces(ITypeReference type)
    {
      GenericParameter gtp = (GenericParameter)type;
      if (gtp != null && gtp.Constraints != null && gtp.Constraints.Count > 0)
      {
        // Stub implementation: Just return 1 of the constraints for now.
        // Don't deeply inspect generic types here or else we'll infinite loop.
        return this.ConvertCCITypeToAssemblyQualifiedName(gtp.Constraints[0], false);
      }
      else
      {
        // Stub implementation
        return "System.Object";
      }
    }

    /// <summary>
    /// Once the typeName and assembly identity have been determined stitch them together in
    /// the format of an assembly qualified type name.
    /// </summary>
    /// <param name="typeName">Fully compelted type name for the type under investigation</param>
    /// <param name="identity">AssemblyIdentity for the type under investigation</param>
    /// <returns>Complete assembly-qualified name for the type under investigation</returns>
    private static string CompleteAssemblyQualifiedTypeNameProcessing(string typeName,
        AssemblyIdentity identity)
    {
      // Get the name of the assembly in assembly qualified form, remove the Name= at 
      // the front and the ) at the end
      string assemblyQualifiedForm =
          identity.ToString().Substring(
          identity.ToString().IndexOf('=') + 1).TrimEnd(new char[] { ')' });

      // Chop off the location part
      if (assemblyQualifiedForm.Contains("Location="))
      {
        assemblyQualifiedForm = assemblyQualifiedForm.Substring(0,
            assemblyQualifiedForm.IndexOf(", L", StringComparison.Ordinal));
      }

      return typeName + ", " + assemblyQualifiedForm;
    }

    /// <summary>
    /// Check if the name of the given type can be quickly determined, i.e. if it can be resolved
    /// by the system without any added information or if we can't determine any information
    /// about the type.
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>Name of the type if it is simple, and null otherwise</returns>
    private static string CheckSimpleCases(ITypeReference type)
    {
      try
      {
        if (Type.GetType(type.ToString()) != null)
        {
          return type.ToString();
        }
      }
      catch
      {
      }

      return null;
    }

    /// <summary>
    /// Update the given type name to account for nested type paramaters if the given type is a 
    /// NestedTypeDefinition.
    /// </summary>
    /// <param name="type">The type to process</param>
    /// <param name="typeName">Typename built up so far</param>
    /// <returns>Typename updated to reflected nested types if necessary</returns>
    private static string UpdateTypeNameForNestedTypeDefinitions(ITypeReference type,
        string typeName)
    {
      if (type is NestedTypeDefinition)
      {
        ITypeDefinition parentType = ((NestedTypeDefinition)type).ContainingTypeDefinition;
        if (parentType is INamedTypeDefinition)
        {
          INamedTypeDefinition parentTypeDef = parentType as INamedTypeDefinition;
          string parentTypeName = TypeHelper.GetTypeName(parentType,
                              NameFormattingOptions.UseReflectionStyleForNestedTypeNames);
          if (parentTypeDef != null && parentTypeDef.MangleName)
          {
            typeName = typeName.Replace(parentTypeName, parentTypeName + '`' +
              parentTypeDef.GenericParameterCount);
          }
        }
      }
      return typeName;
    }

    /// <summary>
    /// Determine the assembly identity for the given type
    /// </summary>
    /// <param name="type">Type to determine the assembly identity for</param>
    /// <returns>Assemnly identity for the given type</returns>
    private AssemblyIdentity DetermineAssemblyIdentity(ITypeReference type)
    {
      AssemblyIdentity identity;
      if (type is Microsoft.Cci.MutableCodeModel.NamespaceTypeReference)
      {
        identity = (AssemblyIdentity)((NamespaceTypeReference)type).ContainingUnitNamespace.Unit.
            UnitIdentity;
      }
      else if (type is NestedTypeReference &&
               ((NestedTypeReference)type).ContainingType is NamespaceTypeReference)
      {
        ITypeReference tr = ((NestedTypeReference)type).ContainingType;
        identity = (AssemblyIdentity)((NamespaceTypeReference)tr).ContainingUnitNamespace.Unit.
            UnitIdentity;
      }
      else
      {
        if (this.assemblyIdentity == null)
        {
          throw new ArgumentNullException("Assembly identity must be set");
        }
        identity = this.assemblyIdentity;
      }
      return identity;
    }

    /// <summary>
    /// Add the generic type arguments to a generic type instance
    /// </summary>
    /// <param name="typeName">Name of the type up to where the generic type arguments should be 
    /// placed</param>
    /// <param name="castedType">Generic type containing the type arguments</param>
    /// <returns>Type name with the generic arguments included</returns>
    private string AddGenericTypeArguments(string typeName, GenericTypeInstanceReference castedType)
    {
      StringBuilder builder = new StringBuilder();
      builder.Append(typeName);
      builder.Append("[");
      foreach (var genericTypeArgument in castedType.GenericArguments)
      {
        builder.Append("[");
        builder.Append(ConvertCCITypeToAssemblyQualifiedName(genericTypeArgument));
        builder.Append("]");
        builder.Append(",");
      }
      // Remove the extraneous comma
      builder.Remove(builder.Length - 1, 1);
      builder.Append("]");
      return builder.ToString();
    }

    #endregion

    /// <summary>
    /// Get the Field from a type that is linked-list that is of its own type.
    /// </summary>
    /// <param name="type">Type to get the field from, must be determined to be of linked-list 
    /// type</param>
    /// <returns>The field in type that is of Type type</returns>
    public static FieldInfo FindLinkedListField(Type type)
    {
      if (type == null)
      {
        throw new ArgumentNullException("type");
      }
      FieldInfo[] fields = type.GetFields(System.Reflection.BindingFlags.Public
                                             | System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance);
      foreach (FieldInfo field in fields)
      {
        // We know there is only 1 field of appropriate type so just return it.
        if (field.FieldType == type)
        {
          return field;
        }
      }
      throw new ArgumentException("Type has no field of its own type -- it's not a linked list.",
          "type");
    }

    /// <summary>
    /// Get the method object for the given key
    /// </summary>
    /// <param name="key">Integer key for the method</param>
    /// <returns>The corresponding MethodInfo</returns>
    public MethodInfo GetPureMethodValue(int key)
    {
      return pureMethods[key];
    }

    /// <summary>
    /// Returns whether the method with the given name is non-regular, that is was added by the 
    /// compiler.
    /// </summary>
    /// <param name="type">The type the method comes from</param>
    /// <param name="methodName">Name of the method to test</param>
    /// <returns>True if the method wasn't written by the programer, false otherwise</returns>
    public bool IsMethodCompilerGenerated(IMethodDefinition methodDef)
    {
      Type type = this.ConvertAssemblyQualifiedNameToType(
        this.ConvertCCITypeToAssemblyQualifiedName(methodDef.ContainingType));
      IEnumerable<IParameterDefinition> parameters = methodDef.Parameters;
      string methodName = methodDef.Name.ToString();

      if (type == null)
      {
        // TODO: Figure out better things to do here
        // throw new ArgumentNullException("type");
        return false;
      }
      // @ is not a valid character in a type name so it must be compiler generated
      if (type.Name.Contains("@")) { return true; }
      try
      {
        var cciTypes = parameters.ToArray();
        Type[] reflectionTypes = new Type[cciTypes.Length];
        int i = 0;
        foreach (var currType in cciTypes) // (int i = 0; i < paramTypes.Length; i++)
        {
          // Get the param type
          // Convert from CCI Type to .NET Type
          reflectionTypes[i] = ConvertAssemblyQualifiedNameToType(
              ConvertCCITypeToAssemblyQualifiedName(currType.Type));
          if (reflectionTypes[i] == null)
          {
            // If anything can't be resolved we are hosed
            reflectionTypes = null;
            break;
          }
          i++;
        }

        MethodInfo methodInfo;
        if (reflectionTypes != null)
        {
          methodInfo = type.GetMethod(methodName, reflectionTypes);
        }
        else
        {
          methodInfo = type.GetMethod(methodName);
        }
        if (methodInfo == null)
        {
          // TODO: Figure out better things to do here
          //throw new Exception(
          //  String.Format("Couldn't resolve method named {0} of type {1}", methodName, type.Name));
          return false;
        }
        var customAttrs = methodInfo.GetCustomAttributes(true);
        foreach (var attr in customAttrs)
        {
          if (attr.GetType() == typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute))
          {
            return true;
          }
        }
      }
      catch (AmbiguousMatchException ex)
      {
        // TODO: Figure out better things to do here
        Console.WriteLine(ex.Message);
      }
      return false;
    }

    /// <summary>
    /// Convert the given FSharp list to a C# array
    /// </summary>
    /// <param name="obj">FSharp list to convert</param>
    /// <returns>An array containing the same elements as the list, in the same order</returns>
    public static object[] ConvertFSharpListToCSharpArray(object obj)
    {
      Type type = obj.GetType();
      List<Object> newList = new List<object>();
      // N.B.: These methods are specific to the generic type of the list, so we can't 
      // get them ahead of time for all lists.
      MethodInfo isEmptyMethod = type.GetMethod("get_IsEmpty");
      MethodInfo tailMethod = type.GetMethod("get_Tail");
      MethodInfo headMethod = type.GetMethod("get_Head");
      while (!((bool)isEmptyMethod.Invoke(obj, null)))
      {
        newList.Add(headMethod.Invoke(obj, null));
        obj = tailMethod.Invoke(obj, null);
      }
      return newList.ToArray();
    }

    /// <summary>
    /// Get the element type of a list or array type.
    /// </summary>
    /// <param name="type">The collection type whose element to get</param>
    /// <returns>The element type of the collection, or Object if the type couldn't be determined.
    /// </returns>
    public static Type GetListElementType(Type type)
    {
      // Element type is in HasElementType for arrays and a generic parameter for generic lists,
      // and if we have no type information let it be object
      Type elementType;
      if (type.HasElementType)
      {
        elementType = type.GetElementType();
      }
      else if (type.IsGenericType && type.GetGenericArguments().Length == 1)
      {
        elementType = type.GetGenericArguments()[0];
      }
      else
      {
        elementType = TypeManager.ObjectType;
      }
      return elementType;
    }
  }
}
