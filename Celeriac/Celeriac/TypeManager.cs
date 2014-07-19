﻿// TypeManager holds references to all .NET types we use and can convert
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
using System.Diagnostics.Contracts;
using System.Runtime.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Celeriac
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
    [Pure]
    public static FieldInfo[] GetSortedFields(this Type type, BindingFlags bindingAttr)
    {
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<FieldInfo[]>() != null);
      Contract.Ensures(
          Contract.Result<FieldInfo[]>().Length == 0 ||
          Contract.ForAll(0, Contract.Result<FieldInfo[]>().Length - 1,
            i => Contract.Result<FieldInfo[]>()[i].Name.CompareTo(Contract.Result<FieldInfo[]>()[i + 1].Name) <= 0));

      FieldInfo[] fields = type.GetFields(bindingAttr);
      Array.Sort(fields, delegate(FieldInfo lhs, FieldInfo rhs)
      {
        return lhs.Name.CompareTo(rhs.Name);
      });
      return fields;
    }
  }

  /// <summary>
  /// Originating type to use when visiting variables other than 'this'. Ensures that visibility information
  /// is calculated correctly.
  /// </summary>
  /// <remarks>This class is not meant to be instantiated.</remarks>
  internal sealed class DummyOriginator
  {
    private DummyOriginator()
    {
      throw new NotSupportedException("Cannot instantiate Dummy Originator class");
    }
  }

  /// <summary>
  /// Keeps canonical type references. Converts between CCIMetadata and .NET types.
  /// </summary>
  [Serializable]
  public class TypeManager : IDisposable
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
    public static readonly Type GenericListType = typeof(IList<>);
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
    ///  Name of the interface all maps must implement. Use is similar to a type store, but getting
    ///  any specific map type would require an element type.
    /// </summary>
    private static readonly string DictionaryInterfaceName = "IDictionary";

    #endregion

    /// <summary>
    /// Don't print object definition program points or references to types whose name matches
    /// this signature, they are system generated.
    /// </summary>
    public readonly static Regex RegexForTypesToIgnoreForProgramPoint = new Regex(@"(\.<.*?>)|(^<.*?>)");

    /// <summary>
    /// Characters that appear in some compiler generated F# methods which cause issues.
    /// </summary>
    /// <remarks>TWS: are there no attributes for these?</remarks>
    public readonly static Regex FSharpCompilerGeneratedNameRegex = new Regex("@");

    /// <summary>
    /// Don't print object definition program points or references to the Code Contracts runtime
    /// </summary>
    public readonly static Regex CodeContractRuntimePpts = new Regex(@"System\.Diagnostics\.Contracts\.__ContractsRuntime.*?");

    /// <summary>
    /// When dec-type of a generic variable with multiple constraints is being printed, use this
    /// to separate each class. Needs to not exist otherwise in an assembly-qualified name.
    /// </summary>
    private readonly static char DecTypeMultipleConstraintSeparator = '|';

    /// <summary>
    /// Binding flags to use when checking for pure methods
    /// </summary>
    public static readonly BindingFlags PureMethodBindings =
      BindingFlags.Public | BindingFlags.NonPublic |
      BindingFlags.Instance |
      BindingFlags.Static | BindingFlags.FlattenHierarchy;

    /// <summary>
    /// Celeriac args for the program this class is managing types for
    /// </summary>
    private CeleriacArgs celeriacArgs;

    private System.Reflection.Assembly instrumentedAssembly = null;

    public AssemblyIdentity AssemblyIdentity { get; private set; }

    #region Collection / Pure Method Memoization Caches

    /// <summary>
    /// Map from types to whether or not they are list implementors
    /// Memoizes the lookup
    /// We use a bool-valued hashtable because there are essentially three states, IsList, NotList
    /// and Unknown. IsList types has a value of true in the table, NotList types have a value of 
    /// false, and Unknown types are those not in the table.
    /// </summary>
    private readonly Dictionary<Type, bool> isListHashmap = new Dictionary<Type, bool>();

    /// <summary>
    /// Map from a type to whether it is an FSharpList. Memoizes the lookup.
    /// We use a bool-valued hashmap because there are essentially three states, IsLinkedList, 
    /// NotLinkedList and Unknown. 
    /// </summary>
    private readonly Dictionary<Type, bool> isFSharpListHashmap = new Dictionary<Type, bool>();

    /// <summary>    
    /// Map from types to whether or not they are linked-list implementors
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are essentially three states, IsLinkedList, 
    /// NotLinkedList and Unknown. 
    /// </summary>
    private readonly Dictionary<Type, bool> isLinkedListHashmap = new Dictionary<Type, bool>();

    /// <summary>
    /// Map from type to whether that type is a C# hashset.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private readonly Dictionary<Type, bool> isSetHashmap = new Dictionary<Type, bool>();

    /// <summary>
    /// Map from type to whether that type is a C# Dictionary.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private readonly Dictionary<Type, bool> isDictionaryHashMap = new Dictionary<Type, bool>();

    /// <summary>
    /// Map from type to whether that type is a F# hashset.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private readonly Dictionary<Type, bool> isFSharpSetHashmap = new Dictionary<Type, bool>();

    /// <summary>
    /// Map from type to whether that type is a F# map.
    /// Memoizes the lookup
    /// We use a bool-valued hashmap because there are three states, true, false and unknown.
    /// </summary>
    private readonly Dictionary<Type, bool> isFSharpMapHashmap = new Dictionary<Type, bool>();

    /// <summary>
    /// A map from assembly qualified names to the Type they describe
    /// Used to memoize type references (passed as names) from the IL rewriter
    /// </summary>
    private readonly Dictionary<string, Type> nameTypeMap = new Dictionary<string, Type>();

    /// <summary>
    /// Map from type to set of pure methods
    /// </summary>
    /// <seealso cref="pureMethods"/>
    private readonly Dictionary<Type, ISet<MethodInfo>> pureMethodsForType = new Dictionary<Type, ISet<MethodInfo>>();

    /// <summary>
    /// All pure methods
    /// </summary>
    /// <seealso cref="pureMethodsForType"/>
    private readonly ISet<MethodInfo> pureMethods = new HashSet<MethodInfo>();

    #endregion

    /// <summary>
    /// A collection of values to ignore, where each value is of the form 
    /// "AssemblyQualifiedTypeName;ValueName"
    /// </summary>
    private readonly ISet<string> ignoredValues = new HashSet<string>();

    /// <summary>
    /// needed to be able to map the contracts from a contract class proxy method to an abstract method
    /// </summary>
    [NonSerialized]
    private IMetadataHost host;

    public IMetadataHost Host
    {
      get
      {
        Contract.Ensures(Contract.Result<IMetadataHost>() != null);
        Contract.Ensures(Contract.Result<IMetadataHost>() == host);
        return host;
      }
    }

    /// <summary>
    /// Store a the instrumented assembly, to use during type resolution. Also clears caches
    /// and purity method stores
    /// </summary>
    /// <param name="assembly">The assembly after instrumentation was added</param>
    public void SetInstrumentedAssembly(System.Reflection.Assembly assembly)
    {
      Contract.Assert(this.instrumentedAssembly == null);
      this.instrumentedAssembly = assembly;
      ResetCaches();
      ReloadMethodPurityInformation();

      AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(ResolveLocalAssembly);
    }

    private System.Reflection.Assembly ResolveLocalAssembly(object sender, ResolveEventArgs args)
    {
      // Search for the assembly in the same directory as the instrumented assembly
      // There's good reason to beleive that this is the assembly that's intended to be loaded by the program

      var absolute = Path.GetFullPath(this.celeriacArgs.AssemblyPath);
      var dir = Path.GetDirectoryName(absolute);
      var name = args.Name.Split(',')[0]; // the name includes the version number, hash and other extraneous information
      var path = Path.Combine(dir, name + ".dll");
      return System.Reflection.Assembly.LoadFile(path);
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
      Contract.Requires(identity != null);
      Contract.Requires(this.AssemblyIdentity == null, "Cannot reset assembly identity");
      Contract.Ensures(this.AssemblyIdentity == identity);
      this.AssemblyIdentity = identity;
    }

    /// <summary>
    /// Clear all cached information
    /// </summary>
    private void ResetCaches()
    {
      isListHashmap.Clear();
      isFSharpListHashmap.Clear();
      isLinkedListHashmap.Clear();
      isSetHashmap.Clear();
      isDictionaryHashMap.Clear();
      nameTypeMap.Clear();
    }

    /// <summary>
    /// Reload method purity information by looking up methods by name and assembly-qualified
    /// type information.
    /// </summary>
    private void ReloadMethodPurityInformation()
    {
      // Serialize type and method names
      var purity = new List<Tuple<string, string>>();
      foreach (var typeEntry in pureMethodsForType)
      {
        foreach (var method in typeEntry.Value)
        {
          purity.Add(new Tuple<string, string>(typeEntry.Key.AssemblyQualifiedName, method.Name));
        }
      }

      // Clear old information
      pureMethodsForType.Clear();
      pureMethods.Clear();

      // Reload purity information
      foreach (var entry in purity)
      {
        AddPureMethod(entry.Item1, entry.Item2);
      }
    }

    [OnDeserialized]
    private void Rehydrate(StreamingContext context)
    {
      InitHost();
    }

    // Disposed in this.Dispose()
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
    private void InitHost()
    {
      Contract.Ensures(this.host != null);
      this.host = celeriacArgs.IsPortableDll ? (IMetadataHost)new PortableHost() : new PeReader.DefaultHost();
    }

    [ContractInvariantMethod]
    [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Called by CC framework")]
    private void ObjectInvariants()
    {
      Contract.Invariant(celeriacArgs != null);
      Contract.Invariant(host != null);
    }

    /// <summary>
    /// Create a new TypeManager instance, will be able to resolve types of the given assembly 
    /// without registering the assembly with the GAC.
    /// </summary>
    /// <param name="args">The Celeriac args applicable to the types being managed here</param>
    /// <remarks><code>this.host</code> is a class member so can't be disposed here, anything that can be
    /// assigned to it is disposed in <code>this.Dispose()</code></remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
    public TypeManager(CeleriacArgs args)
    {
      Contract.Requires(args != null);

      this.celeriacArgs = args;
      InitHost();
      PopulateIgnoredValues();
      ProcessPurityMethods();
    }

    /// <summary>
    /// Add some default ignored values, especially those for system types.
    /// </summary>
    private void PopulateIgnoredValues()
    {
      this.ignoredValues.Add(typeof(string).AssemblyQualifiedName + ";Empty");
      this.ignoredValues.Add(typeof(Int32).AssemblyQualifiedName + ";MaxValue");
      this.ignoredValues.Add(typeof(Int32).AssemblyQualifiedName + ";MinValue");
      this.ignoredValues.Add(typeof(bool).AssemblyQualifiedName + ";FalseString");
      this.ignoredValues.Add(typeof(bool).AssemblyQualifiedName + ";TrueString");
      this.ignoredValues.Add(typeof(double).AssemblyQualifiedName + ";MinValue");
      this.ignoredValues.Add(typeof(double).AssemblyQualifiedName + ";MaxValue");
      this.ignoredValues.Add(typeof(double).AssemblyQualifiedName + ";Epsilon");
      this.ignoredValues.Add(typeof(double).AssemblyQualifiedName + ";NaN");
      this.ignoredValues.Add(typeof(double).AssemblyQualifiedName + ";NegativeInfinity");
      this.ignoredValues.Add(typeof(double).AssemblyQualifiedName + ";PositiveInfinity");
      this.ignoredValues.Add(typeof(IntPtr).AssemblyQualifiedName + ";Zero");
    }

    /// <summary>
    /// Process the purity methods list, building the map from type to pure methods for that type.
    /// Automatically includes Keys and Values properties for Dictionaries.
    /// </summary>
    private void ProcessPurityMethods()
    {
      this.celeriacArgs.PurityMethods.Add(typeof(DictionaryEntry).AssemblyQualifiedName + ";get_Key");
      this.celeriacArgs.PurityMethods.Add(typeof(DictionaryEntry).AssemblyQualifiedName + ";get_Value");

      foreach (String str in this.celeriacArgs.PurityMethods)
      {
        if (str.StartsWith("//")) { continue; }
        string[] methodDescriptions = str.Split(';');
        try
        {
          AddPureMethod(methodDescriptions[0], methodDescriptions[1]);
        }
        catch (IndexOutOfRangeException)
        {
          throw new InvalidOperationException(
              "Malformed purity file -- line with contents: " + str);
        }
        catch (InvalidOperationException)
        {
          // Skip over malformed purity entries instead of crashing.
          string error = string.Format("Warning: malformed purity entry '{0}'", str);
          Console.Error.WriteLine(error);
        }
      }
    }

    /// <summary>
    /// Add the given pure type/method pair to the list of pure methods. No change is made if the
    /// method was already in the pure method list.
    /// </summary>
    /// <param name="type">Type containing the method</param>
    /// <param name="method">MethodInfo of the pure method</param>
    /// <returns>True if the pure method was added, or false if the pure was already present</returns>
    public bool AddPureMethod(Type type, MethodInfo method)
    {
      Contract.Requires(type != null);
      Contract.Requires(method != null);
      Contract.Ensures(pureMethodsForType.ContainsKey(type) && pureMethodsForType[type].Contains(method));
      Contract.Ensures(pureMethods.Contains(method));

      if (pureMethods.Contains(method))
      {
        return false;
      }
      if (!this.pureMethodsForType.ContainsKey(type))
      {
        pureMethodsForType[type] = new HashSet<MethodInfo>();
      }
      pureMethodsForType[type].Add(method);
      pureMethods.Add(method);
      return true;
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
      Contract.Requires(!string.IsNullOrWhiteSpace(typeName));
      Contract.Requires(!string.IsNullOrWhiteSpace(methodName));

      try
      {
        // The user will declare a single type name
        Type type = ConvertAssemblyQualifiedNameToType(typeName).GetSingleType;
        Contract.Assert(type != null);

        // Try no parameters first
        MethodInfo method = type.GetMethod(methodName, PureMethodBindings, null, Type.EmptyTypes, null);
        if (method == null)
        {
          // TODO #80: right now we only support that methods with arguments in the same class
          method = type.GetMethod(methodName, PureMethodBindings, null, new Type[] { type }, null);
          Contract.Assume(method == null || method.IsStatic);
        }
        Contract.Assume(method != null, "No method of name " + methodName + " exists for type on type " + typeName);

        System.Reflection.Assembly instrumentedAssembly =
          System.Reflection.Assembly.LoadFrom(this.celeriacArgs.AssemblyPath);

        if (this.celeriacArgs.InferPurity)
        {
          // Get the types that implement/inherit from this.
          var implementedTypes =
              instrumentedAssembly.GetExportedTypes().Where(
                  a => type.IsAssignableFrom(a) && !a.Equals(type));

          foreach (var implementedType in implementedTypes)
          {
            // Get the method in the implementation that is/overrides the method in the interface/abstract class.
            System.Reflection.MethodInfo implementedMethod =
                implementedType.GetMethod(method.Name, method.GetParameters().Select(p => p.GetType()).ToArray());

            if (implementedMethod != null && implementedType.FullName != null)
            {
              // Try to also add the implementation method as pure.
              if (AddPureMethod(implementedType, implementedMethod))
              {
                string inferred =
                    string.Format("Inferred pure method {0}.{1}", implementedType.FullName, implementedMethod.Name);

                Debug.WriteLine(inferred);
                if (this.celeriacArgs.VerboseMode) Console.WriteLine(inferred);
              }
            }
          }

          // Get the types this type inherits or derives from.
          var parentTypes = type.GetInterfaces().ToList();
          if (type.BaseType != null)
          {
            parentTypes.Add(type.BaseType);
          }

          foreach (var parentType in parentTypes)
          {
            System.Reflection.MethodInfo interfaceMethod =
                parentType.GetMethod(method.Name, method.GetParameters().Select(p => p.GetType()).ToArray());

            if (interfaceMethod != null && parentType.FullName != null)
            {
              if (AddPureMethod(parentType, interfaceMethod))
              {
                string inferred =
                    string.Format("Inferred pure method {0}.{1}", parentType.FullName, interfaceMethod.Name);

                Debug.WriteLine(inferred);
                if (this.celeriacArgs.VerboseMode) Console.WriteLine(inferred);
              }
            }
          }
        }

        AddPureMethod(type, method);
      }
      catch (Exception ex)
      {
        if (celeriacArgs.RobustMode)
        {
          Console.WriteLine(string.Format("INFO: could not resolve method {0} for type {1}: {2}",
            methodName, typeName, (ex.Message ?? "<no message>")));
        }
        else
        {
          throw;
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
    private static bool IsElementOfCollectionType(Type type, Dictionary<Type, bool> entries, IsElementTest test)
    {
      Contract.Requires(type != null);
      Contract.Requires(entries != null);
      Contract.Ensures(entries.ContainsKey(type));

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
    /// Explicit test for whether the given type is an F# list.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the type is an F# list, false otherwise.</returns>
    private bool IsFSharpListTest(Type type)
    {
      Contract.Requires(type != null);
      if (this.celeriacArgs.ElementInspectArraysOnly)
      {
        return type.IsArray;
      }
      else
      {
        return type.Namespace != null && type.Namespace.Equals("Microsoft.FSharp.Collections") &&
               type.Name.StartsWith("FSharpList");
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
      Contract.Requires(type != null);
      return IsElementOfCollectionType(type, this.isFSharpListHashmap, IsFSharpListTest);
    }

    /// <summary>
    /// Explicitly test whether type is a C# list.
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>Whether type is a C# list</returns>
    private bool IsListTest(Type type)
    {
      Contract.Requires(type != null);
      return SearchForMatchingInterface(type, interfaceToTest => {
        if (interfaceToTest == TypeManager.ListType) return true;
        else if (interfaceToTest.IsGenericType && interfaceToTest.GetGenericTypeDefinition() == TypeManager.GenericListType) return true;
        else return false;
      });
    }

    /// <summary>
    /// Check if the given type implements List, memoized
    /// </summary>
    /// <param name="type">The type to check</param>
    /// <returns>True if the type implements System.Collections.List, otherwise false</returns>
    public bool IsListImplementer(Type type)
    {
      Contract.Requires(type != null);
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
      Contract.Requires(type != null);
      return type == TypeManager.sByteType || type == TypeManager.uShortType || type == TypeManager.uIntType;
    }

    /// <summary>
    /// Returns whether the given field should be ignored.
    /// </summary>
    /// <param name="type">Parent type of the field to test</param>
    /// <param name="field">The field to test</param>
    /// <returns>True if the field should be ignored, false otherwise</returns>
    public bool ShouldIgnoreField(Type parentType, FieldInfo field)
    {
      Contract.Requires(parentType != null);
      Contract.Requires(field != null);

      if (celeriacArgs.OmitParentDecType != null && celeriacArgs.OmitParentDecType.IsMatch(parentType.FullName))
      {
        return true;
      }
      else if (celeriacArgs.OmitDecType != null &&
               field.FieldType.FullName != null && // is null if the current instance represents a generic type parameter, 
        // an array type, pointer type, or byref type based on a type parameter, 
        // or a generic type that is not a generic type definition but contains 
        // unresolved type parameters.
               celeriacArgs.OmitDecType.IsMatch(field.FieldType.FullName))
      {
        return true;
      }
      else if (field.GetCustomAttributes(false).Any(x => x is CompilerGeneratedAttribute || x is DebuggerNonUserCodeAttribute))
      {
        return true;
      }

      // exclude fields that are backing automatically generated events
      var events = from e in parentType.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                   where e.Name.Equals(field.Name)
                   select e;
      if (events.Count() > 0)
      {
        return true;
      }

      // TODO(#58): Should be able to switch this test off with a command line arg.
      return this.ignoredValues.Contains(parentType.AssemblyQualifiedName + ";" + field.Name);
    }

    /// <summary>
    /// Returns <c>true</c> if an expression of type <paramref name="type"/> should link to its object invariant.
    /// </summary>
    /// <param name="type"></param>
    /// <returns><c>true</c> if an expression of type <paramref name="type"/> should link to its object invariant</returns>
    public static bool CanLinkToObjectType(Type type)
    {
      Contract.Requires(type != null);

      if (type.IsGenericParameter)
      {
        // false, since we can't create a DECL OBJECT for the type
        return false;
      }
      else if (type.IsArray)
      {
        // false, arrays are handled by sub-expressions?
        return false;
      }
      else
      {
        return true;
      }
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
      Contract.Requires(type != null);
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
      if (this.celeriacArgs.ElementInspectArraysOnly)
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
    /// Explicitly tests whether the given type is a C# Set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the given type is a generic C# set, false otherwise</returns>
    private bool IsSetTest(Type type)
    {
      return type.GetInterfaces().Any(
        x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(ISet<>));
    }

    /// <summary>
    /// Returns <code>true</code> if <paramref name="type"/> implements <code>ISet</code>.
    /// </summary>
    /// <param name="type">the type</param>
    /// <returns><code>true</code> if <paramref name="type"/> implements <code>ISet</code></returns>
    public bool IsSet(Type type)
    {
      Contract.Requires(type != null);
      return IsElementOfCollectionType(type, isSetHashmap, IsSetTest);
    }

    /// <summary>
    /// Explicitly tests whether type is a F# set.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a F# set, false otherwise.</returns>
    private bool IsFSharpSetTest(Type type)
    {
      Contract.Requires(type != null);

      if (this.celeriacArgs.ElementInspectArraysOnly)
      {
        return type.IsArray;
      }
      else
      {
        return type.Namespace != null && type.Namespace.Equals("Microsoft.FSharp.Collections") &&
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
      Contract.Requires(type != null);
      return IsElementOfCollectionType(type, this.isFSharpSetHashmap, IsFSharpSetTest);
    }

    /// <summary>
    /// Explicitly tests whether type is a F# map.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a F# map, false otherwise.</returns>
    private bool IsFSharpMapTest(Type type)
    {
      Contract.Requires(type != null);
      if (this.celeriacArgs.ElementInspectArraysOnly)
      {
        return type.IsArray;
      }
      else
      {
        return type.Namespace != null && type.Namespace.Equals("Microsoft.FSharp.Collections") &&
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
      Contract.Requires(type != null);
      return IsElementOfCollectionType(type, this.isFSharpMapHashmap, IsFSharpMapTest);
    }

    /// <summary>
    /// Memoized test whether the given type is a C# Dictionary.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if the given type is a dictionary, otherwise false</returns>
    public bool IsDictionary(Type type)
    {
      Contract.Requires(type != null);
      return IsElementOfCollectionType(type, this.isDictionaryHashMap, IsDictionaryTest);
    }

    /// <summary>
    /// Explicitly tests whether type is a C# Dictionary.
    /// </summary>
    /// <param name="type">Type to test</param>
    /// <returns>True if type is a C# Dictionary, false otherwise.</returns>
    private bool IsDictionaryTest(Type type)
    {
      Contract.Requires(type != null);
      return SearchForMatchingInterface(type, interfaceToTest =>
          interfaceToTest.Name.EndsWith(DictionaryInterfaceName));
    }

    /// <summary>
    /// Get a list of the pure methods that should be called for the given type sorted 
    /// by method name (sanitized for properties)
    /// </summary>
    /// <param name="type">Type to get the pure methods for</param>
    /// <param name="originatingType">
    ///    The type that called into the visitor (used to calculate visibility)
    /// </param>
    /// <returns>
    ///    Map from key to method object of all the pure methods for the given type.
    /// </returns>
    internal List<MethodInfo> GetPureMethodsForType(Type type, Type originatingType)
    {
      Contract.Requires(type != null);
      Contract.Requires(originatingType != null);
      Contract.Ensures(Contract.Result<List<MethodInfo>>() != null);

      // If the user provided a specific instantiation of a generic type, use it. 
      // Otherwise, use the generic type definition, e.g. List`1
      if (type.IsGenericType && !pureMethodsForType.ContainsKey(type))
      {
        type = type.GetGenericTypeDefinition();
      }

      var result = new List<MethodInfo>();
      if (this.pureMethodsForType.ContainsKey(type))
      {
        foreach (var method in this.pureMethodsForType[type])
        {
          // Ensure the pure method can be seen by the originating type if 
          // --std-visibility has been supplied.
          // TODO(#71): Add logic for more visibility types
          if (celeriacArgs.StdVisibility &&
              !method.IsPublic && !originatingType.FullName.Equals(type.FullName))
          {
            continue;
          }
          Contract.Assume(method.ReturnType != null && method.ReturnType != typeof(void),
            "Pure method " + method.Name + " has no return value; declaring type: " + method.DeclaringType.FullName);
          result.Add(method);
        }
      }

      result.Sort(delegate(MethodInfo lhs, MethodInfo rhs)
      {
        return DeclarationPrinter.SanitizePropertyName(lhs.Name).CompareTo(DeclarationPrinter.SanitizePropertyName(rhs.Name));
      });

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
    public CeleriacTypeDeclaration ConvertAssemblyQualifiedNameToType(string assemblyQualifiedName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(assemblyQualifiedName));
      Contract.Ensures(Contract.Result<CeleriacTypeDeclaration>() != null);

      // XXX: does this create duplicate handlers?
      AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(ResolveLocalAssembly);
      
      // TODO(#17): Passing around an assembly qualified name here may not be best because it is
      // difficult to build and parse. Consider creating a custom object to manage type identity
      // and use that as a parameter instead.

      // TODO(#108): For now, don't attempt to deal with bounds on generic parameters
      var regex = new Regex("^{[\\w\\W]*}");

      if (regex.IsMatch(assemblyQualifiedName))
      {
        var match = regex.Match(assemblyQualifiedName);
        Collection<Type> types = new Collection<Type>();
        foreach (var singleConstraint in match.Value.Split(DecTypeMultipleConstraintSeparator))
        {
          string updatedConstraint = singleConstraint.Replace("{", "").Replace("}", "");
          types.Add(this.ConvertAssemblyQualifiedNameToType(match.Result(updatedConstraint)).GetSingleType);
        }
        return new CeleriacTypeDeclaration(types);
      }

      // Memoized; if we've seen this string before return the type from before.
      // Otherwise get the type and save the result.
      try
      {
        // Assembly resolver -- load from self if necessary.
        Func<System.Reflection.AssemblyName, System.Reflection.Assembly> resolveAssembly = aName =>
        {
          if (aName.Name == this.celeriacArgs.AssemblyName)
          {
            // Type is in the instrumented assembly
            var absolute = Path.GetFullPath(this.celeriacArgs.AssemblyPath);
            return this.instrumentedAssembly ?? System.Reflection.Assembly.LoadFrom(absolute);
          }
          else
          {
            // Type is in a different assembly
            try
            {
              // Try to look up the assembly using the standard lookup mechanism. This won't work if Celeriac is being run
              // from a different directory.
              var found = System.Reflection.Assembly.Load(aName);
              Contract.Assume(found != null);
              return found;
            }
            catch
            {
              // Search for the assembly in the same directory as the instrumented assembly
              // There's good reason to beleive that this is the assembly that's intended to be loaded by the program
              var absolute = Path.GetFullPath(this.celeriacArgs.AssemblyPath);
              var me = this.instrumentedAssembly ?? System.Reflection.Assembly.LoadFrom(absolute);
              var dir = Path.GetDirectoryName(me.Location);
              var path = Path.Combine(dir, aName.Name + ".dll");
              return System.Reflection.Assembly.LoadFile(path);
            }
          }                
        };

        // Type resolver -- load the type from the assembly if we have one
        // Otherwise let .NET resolve it
        Func<System.Reflection.Assembly, string, bool, Type> resolveType = (assembly, name, ignoreCase) => {
          var throwOnError = false;
          var type = assembly == null ? Type.GetType(name, throwOnError, ignoreCase) : assembly.GetType(name, throwOnError, ignoreCase);
          return type;
        };

        Type result;
        if (!this.nameTypeMap.TryGetValue(assemblyQualifiedName, out result))
        {
          // Now get the type with the assembly-qualified name.
          // Load the assembly if necessary, then load the type from the assembly.
          result = Type.GetType(assemblyQualifiedName, resolveAssembly, resolveType, throwOnError: false);
       
          Contract.Assume(result != null, "Couldn't convert type with assembly qualified name: " + assemblyQualifiedName);
          this.nameTypeMap.Add(assemblyQualifiedName, result);
        }
        return new CeleriacTypeDeclaration(result);
      }
      catch (Exception ex)
      {
        // TODO(#17): this exception handler needs to be removed

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
    /// Returns the unique name for the type, suitable for use in the declarations file. 
    /// </summary>
    /// <param name="type">the type</param>
    /// <remarks>Includes the number of generic parameters, if any.</remarks>
    /// <returns>the unique name for the type</returns>
    public static string GetTypeName(ITypeReference type)
    {
      var def = TypeHelper.UninstantiateAndUnspecialize(type);
      return TypeHelper.GetTypeName(def, NameFormattingOptions.UseGenericTypeNameSuffix | NameFormattingOptions.SmartTypeName);
    }

    /// <summary>
    /// Returns the name for the type, suitable for use in the declarations file. If possible, use the <code>ITypeReference</code> form
    /// of this method. Names of generic types are of the form <c>Fully.Qualified.Namespace.SimpleName`N</c>, where <c>N</c> is the
    /// number of generic parameters.
    /// </summary>
    /// <param name="type">The type</param>
    /// <returns>the name for the type, suitable for use in the declarations file</returns>
    public static string GetTypeName(Type type)
    {
      Contract.Requires(type != null);
      Contract.Requires(!type.IsGenericParameter);
      Contract.Ensures(!String.IsNullOrEmpty(Contract.Result<string>()));

      type = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
      
      Contract.Assume(!String.IsNullOrEmpty(type.FullName), "Type has no qualified name (type is not a definition but contains generic parameters?): " + type);

      if (type.IsGenericTypeDefinition)
      {
        return type.FullName;
      }
      else if (type.IsGenericType)
      {
        Contract.Assume(type.FullName.Contains('['), "Generic type missing parameter list: " + type.FullName); 
        return type.FullName.Substring(0, type.FullName.IndexOf('['));
      }
      else
      {
        return type.FullName;
      }  
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
      Contract.Requires(type != null);

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
            typeSuffix.Insert(1, ",");
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
      if (namedType != null)
      {
        typeName = Microsoft.Cci.TypeHelper.GetTypeName(type,
          NameFormattingOptions.UseReflectionStyleForNestedTypeNames
        | NameFormattingOptions.UseGenericTypeNameSuffix);
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
      Contract.Requires(type != null);

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
    /// <remarks>Warning suppressed because we don't want to do anything fancy if an exception
    /// is thrown, that's what the rest of the calling method body is for.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
    private static string CheckSimpleCases(ITypeReference type)
    {
      Contract.Requires(type != null);
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
    private static string UpdateTypeNameForNestedTypeDefinitions(ITypeReference type, string typeName)
    {
      Contract.Requires(type != null);
      Contract.Requires(!string.IsNullOrWhiteSpace(typeName));
      Contract.Ensures(!string.IsNullOrWhiteSpace(Contract.Result<string>()));

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
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<AssemblyIdentity>() != null);

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
        Contract.Assume(this.AssemblyIdentity != null, "Assembly identity not set");
        identity = this.AssemblyIdentity;
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
      Contract.Requires(!string.IsNullOrWhiteSpace(typeName));
      Contract.Requires(castedType != null);
      Contract.Ensures(!string.IsNullOrWhiteSpace(Contract.Result<string>()));

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
      Contract.Requires(type != null);

      FieldInfo[] fields = type.GetFields(BindingFlags.Public |
                                          BindingFlags.NonPublic |
                                          BindingFlags.Instance);
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
    /// Creates a type reference anchored in the given assembly reference and whose names are relative to the given host.
    /// When the type name has periods in it, a structured reference with nested namespaces is created.
    /// </summary>
    /// <remarks>Adapted from Cci.TypeHelper.CreateTypeReference</remarks>
    private INamespaceTypeReference CreateTypeReference(AssemblyIdentity assembly, string typeName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(typeName));
      return CreateTypeReference(Host, assembly, typeName);
    }

    /// <summary>
    /// Creates a type reference anchored in the given assembly reference and whose names are relative to the given host.
    /// When the type name has periods in it, a structured reference with nested namespaces is created.
    /// </summary>
    /// <remarks>Adapted from Cci.TypeHelper.CreateTypeReference</remarks>
    public static INamespaceTypeReference CreateTypeReference(IMetadataHost host, AssemblyIdentity assembly, string typeName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(typeName));

      var assemblyReference =
        new Microsoft.Cci.Immutable.AssemblyReference(host, assembly);

      IUnitNamespaceReference ns = new Microsoft.Cci.Immutable.RootUnitNamespaceReference(assemblyReference);
      string[] names = typeName.Split('.');
      for (int i = 0, n = names.Length - 1; i < n; i++)
        ns = new Microsoft.Cci.Immutable.NestedUnitNamespaceReference(ns, host.NameTable.GetNameFor(names[i]));
      return new Microsoft.Cci.Immutable.NamespaceTypeReference(host, ns, host.NameTable.GetNameFor(names[names.Length - 1]), 0, false, false, true, PrimitiveTypeCode.NotPrimitive);
    }

    /// <summary>
    /// Returns <c>true</c> if <c>def</c> is marked with a compiler generated attribute, or its name contains
    /// a character suggesting that is is compiler generated.
    /// </summary>
    /// <param name="def">the type</param>
    /// <returns><c>true</c> if <c>def is compiler generate</c></returns>
    [Pure]
    public static bool IsCompilerGenerated(ITypeDefinition def)
    {
      Contract.Requires(def != null);
      return TypeHelper.IsCompilerGenerated(def) ||
             (def is INamedTypeDefinition && FSharpCompilerGeneratedNameRegex.IsMatch(((INamedTypeDefinition)def).Name.Value));
    }

    /// <summary>
    /// Returns <c>true</c> if <c>def</c> is marked with a compiler generated attribute, or its name contains
    /// a character suggesting that it is compiler generated.
    /// </summary>
    /// <param name="def">the member</param>
    /// <returns><c>true</c> if <c>def</c> is compiler generated</returns>
    [Pure]
    public bool IsCompilerGenerated(ITypeDefinitionMember def)
    {
      Contract.Requires(def != null);
      if (HasAttribute(def.Attributes, def.ContainingType.PlatformType.SystemRuntimeCompilerServicesCompilerGeneratedAttribute))
      {
        return true;
      }

      var systemDiagnosticsDebuggerNonUserCodeAttribute = CreateTypeReference(
        Host.ContractAssemblySymbolicIdentity, "System.Diagnostics.DebuggerNonUserCodeAttribute");
      if (HasAttribute(def.Attributes, systemDiagnosticsDebuggerNonUserCodeAttribute))
      {
        return true;
      }

      return FSharpCompilerGeneratedNameRegex.IsMatch(def.Name.Value);
    }

    /// <summary>
    /// Returns true if the given collection of attributes contains an attribute of the given type.
    /// </summary>
    /// <remarks>Same as Cci.AttributeHelper.Contains, except resolves types</remarks>
    private static bool HasAttribute(IEnumerable<ICustomAttribute> attributes, ITypeReference attributeType)
    {
      Contract.Requires(attributes != null);
      Contract.Requires(attributeType != null);

      foreach (ICustomAttribute attribute in attributes)
      {
        if (attribute == null) continue;
        if (TypeHelper.TypesAreEquivalent(attribute.Type, attributeType, resolveTypes: true)) return true;
      }
      return false;
    }

    /// <summary>
    /// Returns <c>true</c> if <c>def</c> is marked with an attribute that indicates that the method should
    /// not be instrumented, e.g., the ContractInvariantMethod attribute.
    /// </summary>
    /// <param name="def"></param>
    /// <returns>
    ///   <c>true</c> if <c>def</c> is marked with an attribute that indicates that the method should
    ///   not be instrumented
    /// </returns>
    [Pure]
    public bool IsNotInstrumentable(IReference def)
    {
      Contract.Requires(def != null);

      var contractAttributes = new string[] { 
        "ContractInvariantMethodAttribute", "ContractClassForAttribute", 
        "ContractArgumentValidatorAttribute", "ContractAbbreviatorAttribute"
      };

      foreach (var contractAttribute in contractAttributes)
      {
        var attributeRef = CreateTypeReference(Host.ContractAssemblySymbolicIdentity,
          string.Join(".", "System", "Diagnostics", "Contracts", contractAttribute));

        if (AttributeHelper.Contains(def.Attributes, attributeRef))
        {
          return true;
        }
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
      Contract.Ensures(Contract.Result<Type>() != null);

      // Element type is in HasElementType for arrays and a generic parameter for generic lists,
      // and if we have no type information let it be object
      Type elementType = ObjectType;
      if (type != null && type.HasElementType)
      {
        elementType = type.GetElementType();
      }
      else if (type != null && type.IsGenericType && type.GetGenericArguments().Length == 1)
      {
        elementType = type.GetGenericArguments()[0];
      }
      return elementType;
    }

    [NonSerialized]
    private static Dictionary<Type, bool> immutability = new Dictionary<Type, bool>()
    {
        { typeof(object), true },
        { typeof(string), true },
    };

    /// <summary>
    /// Returns <code>true</code> if the type is a primitive type or contains only readonly references
    /// to other immutable types.
    /// </summary>
    /// <param name="type">the type</param>
    /// <returns><code>true</code> if the type is a primitive type or contains only readonly references
    /// to other immutable types</returns>
    public static bool IsImmutable(Type type)
    {
      Contract.Requires(type != null);

      if (immutability.ContainsKey(type))
      {
        return immutability[type];
      }
      else if (type.IsPrimitive)
      {
        return true;
      }
      else
      {
        // don't recurse if a field references the containing type
        var fieldsAreImmutable = type.GetFields().All(f => f.IsLiteral || (f.IsInitOnly && (f.FieldType == type || IsImmutable(f.FieldType))));
        var propertiesAreReadOnly = type.GetProperties().All(p => !p.CanWrite);
        var result = fieldsAreImmutable && propertiesAreReadOnly;

        immutability.Add(type, result);
        return result;
      }
    }

    /// <summary>
    /// Returns the method definition which defines the contract for the specified method. For methods implementing
    /// an interface, this is the interface method. For methods overriding a method, this is the overridden method.
    /// If both cases are true, the interface is given preference.
    /// </summary>
    /// <param name="method"></param>
    /// <returns>the method definition which defines the contract for the specified method</returns>
    public static ReadOnlyCollection<IMethodDefinition> GetContractMethods(IMethodDefinition method)
    {
      var impl = from m in MemberHelper.GetImplicitlyImplementedInterfaceMethods(method)
                 where !(m.ContainingTypeDefinition is Dummy)
                 select m;

      if (impl.Count() > 0)
      {
        return impl.ToList().AsReadOnly();
      }

      var expl = from m in MemberHelper.GetExplicitlyOverriddenMethods(method)
                 where !(m.ResolvedMethod is Dummy) && !(m.ResolvedMethod.ContainingTypeDefinition is Dummy)
                 select m.ResolvedMethod;

      if (expl.Count() > 0)
      {
        return expl.ToList().AsReadOnly();
      }

      var baseMethod = MemberHelper.GetImplicitlyOverriddenBaseClassMethod(method);
      if (baseMethod != null && !(baseMethod.ContainingTypeDefinition is Dummy))
      {
        return new[] { baseMethod }.ToList().AsReadOnly();
      }

      return new List<IMethodDefinition>().AsReadOnly();
    }

    /// <summary>
    /// Returns the simple name of <paramref name="qualifiedName"/>, using simple names for all generic parameter types.
    /// </summary>
    /// <param name="qualifiedName">The qualified name</param>
    /// <returns>the simple name of <paramref name="qualifiedName"/>, using simple names for all generic parameter types</returns>
    [Pure]
    private static string RemoveTypeQualifiers(string qualifiedName)
    {
      Contract.Requires(!string.IsNullOrEmpty(qualifiedName));
      Contract.Ensures(!string.IsNullOrEmpty(Contract.Result<string>()));
      // NOT TRUE: Contract.Ensures(Contract.Result<string>().IndexOf('.') < 0); 
      // Counter-example: System.Collections.Generic.Dictionary<,>.KeyCollection

      var result = Regex.Replace(qualifiedName, "[^<,>]*", delegate (Match match){
        var str = match.Value;
        var simpleNameIndex = str.LastIndexOf('.');
        return simpleNameIndex > 0 ? str.Substring(simpleNameIndex + 1) : str;
      });

      return result;
    }

    /// <summary>
    /// Returns the name of <paramref name="type"/> in <paramref name="targetLanguage"/>. 
    /// </summary>
    /// <param name="type">The CLR type</param>
    /// <param name="sourceKind">The target source language</param>
    /// <returns>the name of <paramref name="type"/> in <paramref name="targetLanguage"/></returns>
    /// <exception cref="NotImplementedException">if <paramref name="targetLanguage"/> is not supported</exception>
    [Pure]
    public static string GetTypeSourceName(Type type, SourceLanguage targetLanguage, bool forceSimpleName)
    {
      Contract.Requires(type != null);
      Contract.Requires(targetLanguage == SourceLanguage.CSharp || forceSimpleName == false, "Simple names output is only enabled for C#");
      Contract.Ensures(!string.IsNullOrWhiteSpace(Contract.Result<string>()));

      if (targetLanguage == SourceLanguage.CSharp)
      {
        using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
        {
          var src = provider.GetTypeOutput(new System.CodeDom.CodeTypeReference(type));
          return forceSimpleName ? RemoveTypeQualifiers(src) : src;
        }
      }
      else if (targetLanguage == SourceLanguage.VBasic)
      {
        using (var provider = new Microsoft.VisualBasic.VBCodeProvider())
        {
          return provider.GetTypeOutput(new System.CodeDom.CodeTypeReference(type));
        }
      }
      else if (targetLanguage == SourceLanguage.FSharp)
      {
        throw new NotImplementedException("F# type name output is currently not supported");
      }
      else
      {
        return type.ToString();
      }
    }

    #region IDisposable Members


    public void Dispose()
    {
      this.Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposeManaged)
    {
      if (!disposeManaged)
      {
        return;
      }
      if (celeriacArgs.IsPortableDll)
      {
        ((PortableHost)host).Dispose();
      }
      else
      {
        ((PeReader.DefaultHost)host).Dispose();
      }
    }

    #endregion
  }
}
