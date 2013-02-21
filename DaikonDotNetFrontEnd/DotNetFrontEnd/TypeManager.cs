// TypeManager holds references to all .NET types we use and can convert
// between CCIMetadata and .NET types.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Cci;
using Microsoft.Cci.MutableCodeModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;


namespace DotNetFrontEnd
{

    public static class TypeManagerExtensions
    {
        /// <summary>
        /// Returns the fields for the <code>System.Type</code> in alphabetical order by name. 
        /// </summary>
        /// <param name="type">the type</param>
        /// <param name="bindingAttr">binding constraints</param>
        /// <seealso cref="Type.GetFields"/>
        /// <returns>the fields for the <code>System.Type</code> in alphabetical order by name. </returns>
        public static FieldInfo[] GetSortedFields(this Type type, BindingFlags bindingAttr)
        {
            FieldInfo[] fields = type.GetFields(bindingAttr);
            Array.Sort(fields, delegate(FieldInfo lhs, FieldInfo rhs)
            {
                return lhs.Name.CompareTo(rhs.Name);
            });
            return fields;
        }
    }

  /// <summary>
  /// Keeps canonical type references. Converts between CCIMetadata and .NET types.
  /// </summary>
  [Serializable]
  public class TypeManager
  {
    #region static readonly Types

    // Type stores used by ReflectionArgs and DeclarationPrinter
    // Keep these alphabetized.
    public static readonly Type BigIntegerType = typeof(BigInteger);
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
    private static readonly string DictionaryInterfaceName = "IDictionary";

    #endregion

    /// <summary>
    /// Don't print object definition program points or references to types whose name matches
    /// this signature, they are system generated.
    /// </summary>
    public readonly static string RegexForTypesToIgnoreForProgramPoint = "<*>";

    /// <summary>
    /// When dec-type of a generic variable with multiple constraints is being printed, use this
    /// to separate each class. Needs to not exist otherwise in an assembly-qualified name.
    /// </summary>
    private readonly static char DecTypeMultipleConstraintSeparator = '|';

    /// <summary>
    /// Front end args for the program this class is managing types for
    /// </summary>
    private FrontEndArgs frontEndArgs;

    [NonSerialized]
    private AssemblyIdentity assemblyIdentity;

    #region Collection / Pure Method Memoization Caches

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

    /// <summary>
    /// Map from type to whether that type is a C# Dictionary.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private Dictionary<Type, bool> isDictionaryHashMap;

    /// <summary>
    /// Map from type to whether that type is a F# hashset.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private Dictionary<Type, bool> isFSharpSetHashmap;

    /// <summary>
    /// Map from type to whether that type is a F# map.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private Dictionary<Type, bool> isFSharpMapHashmap;

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

    #endregion

    /// <summary>
    /// A collection of values to ignore, where each value is of the form 
    /// "AssemblyQualifiedTypeName;ValueName"
    /// </summary>
    private ISet<string> ignoredValues;

    /// <summary>
    /// Used when adding methods to ensure that keys are unique
    /// </summary>
    private int globalPureMethodCount;

    /// <summary>
    /// needed to be able to map the contracts from a contract class proxy method to an abstract method
    /// </summary>
    public IMetadataHost Host { get; private set; }

    /// <summary>
    /// Create a new TypeManager instance, will be able to resolve types of the given assembly 
    /// without registering the assembly with the GAC.
    /// </summary>
    /// <param name="args">The front-end args applicable to the types being managed here</param>
    public TypeManager(IMetadataHost host, FrontEndArgs args)
    {
      this.frontEndArgs = args;
      this.Host = host;

      this.isListHashmap = new Dictionary<Type, bool>();
      this.isFSharpListHashmap = new Dictionary<Type, bool>();
      this.isLinkedListHashmap = new Dictionary<Type, bool>();
      this.isSetHashmap = new Dictionary<Type, bool>();
      this.isFSharpSetHashmap = new Dictionary<Type, bool>();
      this.isDictionaryHashMap = new Dictionary<Type, bool>();
      this.isFSharpMapHashmap = new Dictionary<Type, bool>();

      this.nameTypeMap = new Dictionary<string, Type>();
      this.pureMethodKeys = new Dictionary<Type, ISet<int>>();
      this.pureMethods = new Dictionary<int, MethodInfo>();
      this.ignoredValues = new HashSet<string>();
      this.PopulateIgnoredValues();
      this.ProcessPurityMethods();
      this.globalPureMethodCount = 0;
    }

    /// <summary>
    /// Add some default ignored values, especially those for system types.
    /// </summary>
    private void PopulateIgnoredValues()
    {
      // TODO(#57): Should be not .NET 4.0 specific
      this.ignoredValues.Add("System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;Empty");
      this.ignoredValues.Add("System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;MaxValue");
      this.ignoredValues.Add("System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;MinValue");
      this.ignoredValues.Add("System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;FalseString");
      this.ignoredValues.Add("System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;TrueString");
      this.ignoredValues.Add("System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;MinValue");
      this.ignoredValues.Add("System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;MaxValue");
      this.ignoredValues.Add("System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;Epsilon");
      this.ignoredValues.Add("System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;NaN");
      this.ignoredValues.Add("System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;NegativeInfinity");
      this.ignoredValues.Add("System.Double, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;PositiveInfinity");
      this.ignoredValues.Add("System.IntPtr, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;Zero");
    }

    /// <summary>
    /// Process the purity methods list, building the map from type to pure methods for that type.
    /// </summary>
    private void ProcessPurityMethods()
    {
      this.AddStandardPurityMethods();
      foreach (String str in this.frontEndArgs.PurityMethods)
      {
        if (str.StartsWith("//")) { continue; }
        string[] methodDescriptions = str.Split(';');
        try
        {
          AddPureMethod( methodDescriptions[0], methodDescriptions[1]);
        }
        catch (IndexOutOfRangeException)
        {
          throw new InvalidOperationException(
              "Malformed purity file -- line with contents: " + str);
        }
      }
    }

    /// <summary>
    /// Add the method described by the given method and type names as a pure method.
    /// If the method given matches an already pure method then take no externally-
    /// visible action.
    /// </summary>
    /// <param name="typeName">Assembly qualfied name of the type having the method
    /// to be marked as pure.</param>
    /// <param name="methodName">Name of the method to be marked as pure, with 
    /// no parens or type qualifier.</param>
    public void AddPureMethod(string typeName, string methodName)
    {
      // The user will declare a single type name
      Type type = ConvertAssemblyQualifiedNameToType(typeName).GetSingleType;
      // Pure methods have no parameters
      MethodInfo method = type.GetMethod(methodName,
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Static |
        BindingFlags.Instance
      );
      if (method == null)
      {
        throw new ArgumentException("No method of name: " + methodName + " on type:" + typeName
            + " exists.");
      }
      if (pureMethods.ContainsValue(method))
      {
        return;
      }
      if (!this.pureMethodKeys.ContainsKey(type))
      {
        pureMethodKeys[type] = new HashSet<int>();
      }
      pureMethodKeys[type].Add(++globalPureMethodCount);
      pureMethods[globalPureMethodCount] = method;
    }

    /// <summary>
    /// Add methods known to be pure to the list of purity methods
    /// </summary>
    private void AddStandardPurityMethods()
    {
      this.frontEndArgs.PurityMethods.Add("System.Collections.DictionaryEntry, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;get_Key");
      this.frontEndArgs.PurityMethods.Add("System.Collections.DictionaryEntry, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;get_Value");
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
    private static bool IsElementOfCollectionType(Type type, Dictionary<Type, bool> entries,
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
    /// Explicit test for whether the given type is an F# list.
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
    /// Explicitly test whether type is a C# list.
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
    /// Returns whether the given field should be ignored.
    /// </summary>
    /// <param name="type">Parent type of the field to test</param>
    /// <param name="field">The field to test</param>
    /// <returns>True if the field should be ignored, false otherwise</returns>
    public bool ShouldIgnoreField(Type parentType, FieldInfo field)
    {
        Debug.Assert(parentType != null);
        Debug.Assert(field != null);

        if (frontEndArgs.OmitParentDecType != null && frontEndArgs.OmitParentDecType.IsMatch(parentType.FullName))
        {
            return true;
        }
        else if (frontEndArgs.OmitDecType != null && 
                 field.FieldType.FullName != null && // is null if the current instance represents a generic type parameter, 
                                                     // an array type, pointer type, or byref type based on a type parameter, 
                                                     // or a generic type that is not a generic type definition but contains 
                                                     // unresolved type parameters.
                 frontEndArgs.OmitDecType.IsMatch(field.FieldType.FullName))
        {
            return true;
        }
      
        // TODO(#58): Should be able to switch this test off with a command line arg.
        return this.ignoredValues.Contains(parentType.AssemblyQualifiedName + ";" + field.Name);
    }

    /// <summary>
    /// Returns whether the given type is any of the numeric types, e.g. int, float, BigInteger.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the type is numeric, false otherwise</returns>
    public static bool IsAnyNumericType(Type type)
    {
      return type.IsValueType && (type == TypeManager.ByteType || type == TypeManager.CharType
        || type == TypeManager.DecimalType || type == TypeManager.DoubleType
        || type == TypeManager.FloatType || type == TypeManager.IntType
        || type == TypeManager.LongType || type == TypeManager.sByteType
        || type == TypeManager.ShortType || type == TypeManager.uIntType
        || type == TypeManager.ULongType || type == TypeManager.uShortType
        || type == TypeManager.BigIntegerType);
    }

    /// <summary>
    /// Explicitly tests whether the given type is a C# linked list.
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
      // The implementation appears to the test as a linked list.
      if (type.AssemblyQualifiedName != null && type.AssemblyQualifiedName.Contains("System"))
      {
        return false;
      }

      return IsElementOfCollectionType(type, this.isLinkedListHashmap, IsLinkedListTest);
    }

    /// <summary>
    /// Delegate used to see if an interface satisfies a condition
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
    /// Returns <code>true</code> if <paramref name="type"/> implements <code>ISet</code>.
    /// </summary>
    /// <param name="type">the type</param>
    /// <returns><code>true</code> if <paramref name="type"/> implements <code>ISet</code></returns>
    public bool IsSet(Type type)
    {
        return type.GetInterfaces().Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(ISet<>));
    }

    /// <summary>
    /// Explicitly tests whether type is a F# set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a F# set, false otherwise.</returns>
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
    /// <returns>True if the type is an F# set, false otherwise</returns>
    public bool IsFSharpSet(Type type)
    {
      return IsElementOfCollectionType(type, this.isFSharpSetHashmap, IsFSharpSetTest);
    }

    /// <summary>
    /// Explicitly tests whether type is a F# map.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a F# map, false otherwise.</returns>
    private bool IsFSharpMapTest(Type type)
    {
      if (this.frontEndArgs.ElementInspectArraysOnly)
      {
        return type.IsArray;
      }
      else
      {
        return type.Namespace == "Microsoft.FSharp.Collections" &&
          type.Name.StartsWith("FSharpMap");
      }
    }

    /// <summary>
    /// Memoized test whether type is in an F# Map.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the type is an F# map, false otherwise</returns>
    public bool IsFSharpMap(Type type)
    {
      return IsElementOfCollectionType(type, this.isFSharpMapHashmap, IsFSharpMapTest);
    }

    /// <summary>
    /// Memoized test whether the given type is a C# Dictionary.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the given type is a dictionary, otherwise false</returns>
    public bool IsDictionary(Type type)
    {
      return IsElementOfCollectionType(type, this.isDictionaryHashMap, IsDictionaryTest);
    }

    /// <summary>
    /// Explicitly tests whether type is a C# Dictionary.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a C# Dictionary, false otherwise.</returns>
    private bool IsDictionaryTest(Type type)
    {
      return SearchForMatchingInterface(type, interfaceToTest =>
          interfaceToTest.Name.EndsWith(DictionaryInterfaceName));
    }

    /// <summary>
    /// Get a list of the pure methods that should be called for the given type.
    /// </summary>
    /// <param name="cciType">CCI Type Reference to the type to get pure methods for</param>
    /// <returns>Map from key to method object of all the pure methods for the given type
    /// </returns>
    public Dictionary<int, MethodInfo> GetPureMethodsForType(ITypeReference cciType)
    {
      Dictionary<int, MethodInfo> result = new Dictionary<int, MethodInfo>();

      var typeName = this.ConvertCCITypeToAssemblyQualifiedName(cciType);

      var typeDecl = this.ConvertAssemblyQualifiedNameToType(typeName);
     
      var allTypes = typeDecl.GetAllTypes;

      foreach (Type type in allTypes)
      {
        foreach (var x in GetPureMethodsForType(type))
        {
          result.Add(x.Key, x.Value);
        }
      }
      return result;
    }

    /// <summary>
    /// Get a list of the pure methods that should be called for the given type.
    /// </summary>
    /// <param name="type">Type to get the pure methods for</param>
    /// <returns>Map from key to method object of all the pure methods for the given type
    /// </returns>
    public Dictionary<int, MethodInfo> GetPureMethodsForType(Type type)
    {
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
    /// <exception cref="Exception">Occurs if the conversion cannot be successfully completed.
    /// </exception>
    /// <returns>The type having that assembly qualified name, or null if the call was unsuccessful
    /// </returns>
    /// Warning suppressed because there is no way to avoid this call.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability",
      "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Reflection.Assembly.LoadFrom")]
    public DNFETypeDeclaration ConvertAssemblyQualifiedNameToType(string assemblyQualifiedName)
    {
      // TODO(#17): Passing around an assembly qualified name here may not be best because it is
      // difficult to build and parse. Consider creating a custom object to manage type identity
      // and use that as a parameter instead.

      if (Regex.IsMatch(assemblyQualifiedName, "{[\\w\\W]*}"))
      {
        var match = Regex.Match(assemblyQualifiedName, "{[\\w\\W]*}");
        Collection<Type> types = new Collection<Type>();
        foreach (var singleConstraint in match.Value.Split(DecTypeMultipleConstraintSeparator))
        {
          string updatedConstraint = singleConstraint.Replace("{", "").Replace("}", "");
          types.Add(this.ConvertAssemblyQualifiedNameToType(match.Result(updatedConstraint))
              .GetSingleType);
        }
        return new DNFETypeDeclaration(types);
      }

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
        return new DNFETypeDeclaration(result);
      }
      catch (Exception ex)
      {
        // Attempt to continue by stripping generic information
        if (assemblyQualifiedName.Contains("[["))
        {
          return ConvertAssemblyQualifiedNameToType(
            Regex.Replace(assemblyQualifiedName, "\\[\\[[\\w\\W]*\\]\\]", ""));
        }
        throw new InvalidOperationException(String.Format("Unable to convert assembly qualified name {0} to a"
            + " Type.", assemblyQualifiedName), ex);
      }
    }

    #region ConvertCCITypeToAssemblyQualifiedNameMethods

    /// <summary>
    /// Get a Reflection Type from a CCI Type
    /// </summary>
    /// <param name="type">A reference to a CCI Type defined in the program to be profiled</param>
    /// <returns>A reflection type</returns>
    public string ConvertCCITypeToAssemblyQualifiedName(ITypeReference type)
    {
      return ConvertCCITypeToAssemblyQualifiedName(type, true);
    }

    /// <summary>
    /// Get a Reflection Type from a CCI Type
    /// </summary>
    /// <param name="type">A reference to a CCI Type defined in the program to be profiled</param>
    /// <param name="deeplyInspectGenericParameters">Whether to investigate constraints on
    /// generic parameters</param>
    /// <returns>A reflection type</returns>
    public string ConvertCCITypeToAssemblyQualifiedName(ITypeReference type,
      bool deeplyInspectGenericParameters)
    {
      //try
      //{
      string simpleTypeName = CheckSimpleCases(type);
      if (simpleTypeName != null)
      {
        return simpleTypeName;
      }

      // If the type is a vector or matrix, then proceed on the element type, but rememberer that it was a 
      // vector or matrix so suffix can be added at the end. These could be nested somewhat deeply so 
      // continue while we it's still a vector or matrix. Need to insert at the beginning of the suffix
      // each time.
      StringBuilder typeSuffix = new StringBuilder();
      while (type is VectorTypeReference || type is MatrixTypeReference)
      {
        if (type is VectorTypeReference)
        {
          type = ((VectorTypeReference)type).ElementType;
          typeSuffix.Insert(0, "[]");
        }
        if (type is MatrixTypeReference)
        {
          MatrixTypeReference mtr = ((MatrixTypeReference)type);
          typeSuffix.Insert(0, "[");
          for (int i = 0; i < mtr.Rank; i++)
          {
            typeSuffix.Insert(1,",");
          }
          // If someone went really crazy with their rank this could actually be negative :O
          Debug.Assert((int)mtr.Rank > 0);
          typeSuffix.Insert(1 + (int)mtr.Rank, "]");
          type = mtr.ElementType;
        }
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


      if (type is ISpecializedNestedTypeReference)
      {
        type = ((ISpecializedNestedTypeReference)type).UnspecializedVersion;
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
      else if (type is IGenericTypeInstanceReference)
      {
        var castedType = (IGenericTypeInstanceReference)type;
        typeName = TypeHelper.GetTypeName(castedType.GenericType,
            NameFormattingOptions.UseReflectionStyleForNestedTypeNames) + '`' +
            castedType.GenericArguments.Count();
        if (deeplyInspectGenericParameters)
        {
          typeName = AddGenericTypeArguments(typeName, castedType);
        }
        type = castedType.GenericType;
      }

      typeName += typeSuffix.ToString();

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
    /// <returns>If there is only one constraint then the name of the class of the constraint is
    /// printed. Else a list of form {a,b,...,x} containing comma separated assembly-qualified names
    /// of the classes or interfaces constraining the generic parameter is printed.</returns>
    private string PrintListOfGenericParameterClassesAndInterfaces(ITypeReference type)
    {
      GenericParameter gtp = (GenericParameter)type;
      if (gtp != null && gtp.Constraints != null)
      {
        if (gtp.Constraints.Count > 1)
        {
          StringBuilder builder = new StringBuilder();
          builder.Append("{");
          foreach (ITypeReference constraint in gtp.Constraints)
          {
            builder.Append(this.ConvertCCITypeToAssemblyQualifiedName(constraint, false));
            builder.Append(DecTypeMultipleConstraintSeparator);
          }
          // Remove the last comma
          builder.Remove(builder.Length - 1, 1);
          builder.Append("}");
          return builder.ToString();
        }
        else
        {
          // Don't deeply inspect generic types here or else we'll infinite loop.
          return this.ConvertCCITypeToAssemblyQualifiedName(gtp.Constraints[0], false);
        }
      }
      else
      {
        // Fall-back
        return "System.Object";
      }
    }

    /// <summary>
    /// Once the typeName and assembly identity have been determined stitch them together in
    /// the format of an assembly qualified type name.
    /// </summary>
    /// <param name="typeName">Fully completed type name for the type under investigation</param>
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
    /// Update the given type name to account for nested type parameters if the given type is a 
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
    /// <returns>Assembly identity for the given type</returns>
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
          throw new ArgumentException("Assembly identity must be set");
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
    private string AddGenericTypeArguments(string typeName, IGenericTypeInstanceReference castedType)
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
      throw new ArgumentException("Type has no staticField of its own type -- it's not a linked list.",
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
    /// Creates a type reference anchored in the given assembly reference and whose names are relative to the given host.
    /// When the type name has periods in it, a structured reference with nested namespaces is created.
    /// </summary>
    public static INamespaceTypeReference CreateTypeReference(IMetadataHost host, IAssemblyReference assemblyReference, string typeName)
    {
        IUnitNamespaceReference ns = new Microsoft.Cci.Immutable.RootUnitNamespaceReference(assemblyReference);
        string[] names = typeName.Split('.');
        for (int i = 0, n = names.Length - 1; i < n; i++)
            ns = new Microsoft.Cci.Immutable.NestedUnitNamespaceReference(ns, host.NameTable.GetNameFor(names[i]));
        return new Microsoft.Cci.Immutable.NamespaceTypeReference(host, ns, host.NameTable.GetNameFor(names[names.Length - 1]), 0, false, false, true, PrimitiveTypeCode.NotPrimitive);
    }

    private bool IsCompilerGenerated(IDefinition def)
    {
        var host = this.Host;
        if (AttributeHelper.Contains(def.Attributes, host.PlatformType.SystemRuntimeCompilerServicesCompilerGeneratedAttribute)) return true;
        var systemDiagnosticsDebuggerNonUserCodeAttribute = CreateTypeReference(host, new Microsoft.Cci.Immutable.AssemblyReference(host, host.ContractAssemblySymbolicIdentity), "System.Diagnostics.DebuggerNonUserCodeAttribute");
        return AttributeHelper.Contains(def.Attributes, systemDiagnosticsDebuggerNonUserCodeAttribute);
    }

    /// <summary>
    /// Returns <code>true</code> if <param name="methodDef"/> is compiler generated.
    /// </summary>
    /// <param name="methodDef">The method definition</param>
    /// <returns><code>true</code> if <param name="methodDef"/> is compiler generated.</returns>
    public bool IsMethodCompilerGenerated(IMethodDefinition methodDef)
    {
        return IsCompilerGenerated(methodDef);
    }

    /// <summary>
    /// Returns <code>true</code> if <param name="typeDef"/> is compiler generated.
    /// </summary>
    /// <param name="typeDef">The method definition</param>
    /// <returns><code>true</code> if <param name="typeDef"/> is compiler generated.</returns>
    public bool IsTypeCompilerGenerated(ITypeDefinition typeDef)
    {
        return IsCompilerGenerated(typeDef);
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
