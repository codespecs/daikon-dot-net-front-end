using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Cci;
using EmilStefanov;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Celeriac.Comparability;
using Celeriac;
using Celeriac.Contracts;

namespace Comparability
{

  /// <summary>
  /// Computes a method's opininion about comparability
  /// </summary>
  public class MethodVisitor : CodeVisitor
  {
    public NameBuilder Names { get; private set; }
    public IMethodDefinition Method { get; private set; }

    private readonly Dictionary<string, int> ids = new Dictionary<string, int>();
    private readonly DisjointSets comparability = new DisjointSets();

    /// <summary>
    /// Map from collection element expression (e.g., array[..]) to its name index expressions (e.g., i where array[i]
    /// appears in the code).
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> collectionIndexes = new Dictionary<string, HashSet<string>>();

    private readonly HashSet<IReturnStatement> returns = new HashSet<IReturnStatement>();
    private readonly HashSet<IMethodCall> namedCalls = new HashSet<IMethodCall>();

    /// <summary>
    /// Map from instance expressions to their respective types.
    /// </summary>
    public Dictionary<IExpression, ITypeReference> ReferencedTypes { get; private set; }

    private readonly HashSet<IExpression> namedExpressions = new HashSet<IExpression>();

    public IMetadataHost Host { get; private set; }
    
    [ContractInvariantMethod]
    private void ObjectInvariant()
    {
      Contract.Invariant(Names != null);
      Contract.Invariant(Method != null);
      Contract.Invariant(Host != null);
      Contract.Invariant(ReferencedTypes != null);
      Contract.Invariant(Contract.ForAll(namedCalls, c => Names.NameTable.ContainsKey(c)));
      Contract.Invariant(Contract.ForAll(collectionIndexes.Keys, a => ids.ContainsKey(a)));
      // Array Indexes should correspond to the array contents entry: e.g., this.array[..] not this.array
      Contract.Invariant(Contract.ForAll(collectionIndexes.Keys, a => NameBuilder.IsElementsExpression(a)));
      Contract.Invariant(Contract.ForAll(collectionIndexes.Values, i => i.Count > 0));

      // Not true b/c a name is added whenever comparability is queried by Celeriacs's IL visitors
      // Contract.Invariant(Contract.ForAll(ids.Keys, n => n.Equals("return") || Names.NameTable.ContainsValue(n)));
    }

    public MethodVisitor(IMethodDefinition method, IMetadataHost host, NameBuilder names)
    {
      Contract.Requires(method != null);
      Contract.Requires(names != null);
      Contract.Requires(host != null);
      Contract.Ensures(Names == names);
      Contract.Ensures(Method == method);
      Contract.Ensures(Host == host);

      Names = names;
      Method = method;
      Host = host;
      ReferencedTypes = new Dictionary<IExpression, ITypeReference>();

      ids.Add("return", comparability.AddElement());

      foreach (var param in Method.Parameters)
      {
        ids.Add(param.Name.Value, comparability.AddElement());
      }
    }

    private void PropogateTypeReference(IExpression inner, IExpression outer)
    {
      Contract.Requires(inner != null);
      Contract.Requires(outer != null);

      if (ReferencedTypes.ContainsKey(inner))
      {
        AddTypeReference(ReferencedTypes[inner], outer);
      }
    }

    private void AddTypeReference(ITypeReference type, IExpression expr)
    {
      Contract.Requires(type != null);
      Contract.Requires(expr != null);
      Contract.Ensures(ReferencedTypes.ContainsKey(expr));
      Contract.Ensures(ReferencedTypes[expr] == type);

      if (!ReferencedTypes.ContainsKey(expr))
      {
        ReferencedTypes.Add(expr, type);
      }
      else
      {
        Contract.Assume(ReferencedTypes[expr] == type);
      }
    }

    private string NameForArg(IExpression arg)
    {
      Contract.Requires(arg != null);
      if (Names.NameTable.ContainsKey(arg))
      {
        return Names.NameTable[arg];
      }
      else
      {
        var names = new HashSet<string>(Names.Names(Expand(arg)));
        return names.Count == 1 ? names.First() : null;
      }
    }

    /// <summary>
    /// Returns a mapping from parameter names to argument expressions.
    /// </summary>
    /// <param name="callsite">the callsite</param>
    /// <returns>a mapping from parameter names to argument expressions</returns>
    private Dictionary<string, string> ZipArguments(IMethodCall callsite)
    {
      Contract.Requires(callsite != null);

      var calleeDefinition = callsite.MethodToCall.ResolvedMethod;

      var paramsToArgs = new Dictionary<string, string>();
      foreach (var binding in calleeDefinition.Parameters.Zip(callsite.Arguments, (x, y) => Tuple.Create(x, y)))
      {
        var nameForArg = NameForArg(binding.Item2);
        if (nameForArg != null)
        {
          paramsToArgs.Add(binding.Item1.Name.Value, nameForArg);
        }
      }
      return paramsToArgs;
    }

    /// <summary>
    /// Applies comparability information from the given methods to this method. The analysis 
    /// both calls to the methods and inheritance relationships. Comparability information is 
    /// propogated up the override / implementation hierarchy.
    /// </summary>
    /// <param name="methodData">method summaries</param>
    /// <remarks>TWS: is the method return value being dropped from the comparability update?</remarks>
    /// <returns><c>true</c> if the comparability information changed</returns>
    public bool ApplyMethodSummaries(Dictionary<IMethodDefinition, MethodVisitor> methodData)
    {
      Contract.Requires(methodData != null);

      bool modified = false;

      foreach (var callsite in namedCalls)
      {
        var calleeDefinition = callsite.MethodToCall.ResolvedMethod;
        var argBindings = ZipArguments(callsite);
        argBindings.Add("return", Names.NameTable[callsite]);

        if (methodData.ContainsKey(calleeDefinition))
        {
          var calleeSummary = methodData[calleeDefinition];

          bool sameClass = calleeDefinition.ContainingType.ResolvedType == Names.Type;

          // Incorporate parameter opinion
          // 1. Generate opinion for parameter comparability
          // 2. Rebase using argument bindings and remove unused parameters
          // 3. Merge the comparability sets
          var names = Union(
              calleeSummary.ParameterNames, calleeSummary.StaticNames,
              (sameClass ? Names.ThisNames() : new HashSet<string>()));

          var opinion = calleeSummary.ForNames(names);
          var rebased = Filter(Rebase(opinion, argBindings), calleeSummary.ParameterNames);
          modified |= MergeOpinion(rebased);

          // TODO: update this method's opinion about the referenced type (possibly itself),
          // accounting for indirect comparability information via type references
        }
        else if (calleeDefinition.ParameterCount > 0)
        {
          // For methods with no comparability summary, use the types of the parameters as a basis
          // for comparability
          HashSet<IParameterDefinition> fix = new HashSet<IParameterDefinition>(calleeDefinition.Parameters);

          var opinion = ParameterTypeComparability(fix);
          var rebased = Filter(
              Rebase(opinion, argBindings),
              new HashSet<string>(calleeDefinition.Parameters.Select(p => p.Name.Value)));

          modified |= MergeOpinion(rebased);
        }
      }

      var overriding = methodData.Where(m => TypeManager.GetContractMethods(m.Key).Contains(Method));
      foreach (var o in overriding)
      {
        var bindings = new Dictionary<string,string>();
        for (int i = 0; i < Method.ParameterCount; i++)
        {
          // as above, generate a mapping from this parameters to the names in the summary to apply
          bindings.Add(o.Key.Parameters.ElementAt(i).Name.Value, Method.Parameters.ElementAt(i).Name.Value);
        }

        bool sameClass = o.Key.ContainingType.ResolvedType == Names.Type;

        // Incorporate parameter opinion
        // 1. Generate opinion for parameter comparability
        // 2. Rebase using argument bindings and remove unused parameters
        // 3. Merge the comparability sets
        var names = Union(
            o.Value.ParameterNames, o.Value.StaticNames,
            (sameClass ? Names.ThisNames() : new HashSet<string>()));

        var opinion = o.Value.ForNames(names);
        var rebased = Filter(Rebase(opinion, bindings), o.Value.ParameterNames);
     
        modified |= MergeOpinion(rebased);
      }

      return modified;
    }

    /// <summary>
    /// Compute comparability for a method's parameters based on the types of the parameters. Two parameters
    /// are considered comparable if one can be assigned to the other.
    /// </summary>
    /// <param name="parameters">the parameters</param>
    /// <seealso cref="TypeHelper.TypesAreAssignmentCompatible"/>
    /// <returns>comparability sets for the parameters</returns>
    public static HashSet<HashSet<string>> ParameterTypeComparability(IEnumerable<IParameterDefinition> parameters)
    {
      Contract.Requires(parameters != null);
      Contract.Ensures(Contract.Result<HashSet<HashSet<string>>>() != null);

      Dictionary<IParameterDefinition, int> ids = new Dictionary<IParameterDefinition, int>();
      DisjointSets cmp = new DisjointSets();
      foreach (var p in parameters)
      {
        ids.Add(p, cmp.AddElement());
      }

      foreach (var lhs in parameters)
      {
        Contract.Assume(ids.ContainsKey(lhs), "Error tracking parameter " + lhs.Name);
        foreach (var rhs in parameters)
        {
          Contract.Assume(ids.ContainsKey(rhs), "Error tracking parameter " + rhs.Name);
          if (TypeHelper.TypesAreAssignmentCompatible(lhs.Type.ResolvedType, rhs.Type.ResolvedType, true))
          {
            cmp.Union(cmp.FindSet(ids[lhs]), cmp.FindSet(ids[rhs]));
          }
        }
      }

      var result = new HashSet<HashSet<string>>(ids.Keys.GroupBy(p => cmp.FindSet(ids[p])).Select(g => new HashSet<string>(g.Select(p => p.Name.Value))));
      return result;
    }

    public static HashSet<string> Union(params IEnumerable<string>[] collections)
    {
      Contract.Requires(collections != null);
      Contract.Ensures(Contract.Result<HashSet<string>>() != null);
      return collections.Aggregate(new HashSet<string>(), (a, c) => new HashSet<string>(a.Union(c)));
    }

    public HashSet<string> ParameterNames
    {
      get
      {
        Contract.Ensures(Contract.Result<HashSet<string>>() != null);
        var ps = new HashSet<string>(Method.Parameters.Select(p => p.Name.Value));
        ps.Add("return");
        return new HashSet<string>(ids.Keys.Where(n => ps.Any(p => n.Equals(p) || n.StartsWith(p + "."))));
      }
    }

    /// <summary>
    /// Returns the comparability set for <code>names</code>, containing only those <code>names</code>.
    /// </summary>
    /// <param name="names"></param>
    /// <returns>the comparability set for <code>names</code>, containing only those <code>names</code></returns>
    private HashSet<HashSet<string>> ForNames(HashSet<string> names)
    {
      Contract.Requires(names != null);
      Contract.Ensures(Contract.Result<HashSet<HashSet<string>>>() != null);

      var cmp = Comparability;

      HashSet<HashSet<string>> result = new HashSet<HashSet<string>>();
      foreach (var group in names.Where(n => cmp.ContainsKey(n)).GroupBy(n => cmp[n]))
      {
        result.Add(new HashSet<string>(group.Intersect(names)));
      }
      return result;
    }

    public HashSet<HashSet<string>> Opinion
    {
      get
      {
        Contract.Ensures(Contract.Result<HashSet<HashSet<string>>>() != null);

        var cmp = Comparability;

        HashSet<HashSet<string>> result = new HashSet<HashSet<string>>();
        foreach (var group in ids.Keys.Where(n => cmp.ContainsKey(n)).GroupBy(n => cmp[n]))
        {
          result.Add(new HashSet<string>(group));
        }
        return result;
      }
    }

    /// <summary>
    /// Returns the comparability set for the given array expression.
    /// </summary>
    /// <param name="array">the array expression</param>
    /// <returns>the comparability set for the given array expression</returns>
    public HashSet<string> IndexComparabilityOpinion(string array)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(array));
      Contract.Ensures(Contract.Result<HashSet<string>>() != null);

      HashSet<string> indexes;

      if (collectionIndexes.TryGetValue(array, out indexes) && indexes.Count > 0)
      {
        return ComparabilitySet(indexes.First());
      }
      else
      {
        return new HashSet<string>();
      }
    }

    public HashSet<string> StaticNames
    {
      get
      {
        Contract.Ensures(Contract.Result<HashSet<string>>() != null);
        return new HashSet<string>(Names.Names(Names.StaticNames.Intersect(namedExpressions)));
      }
    }


    /// <summary>
    /// Comparability opinion containing only parameter names.
    /// </summary>
    public HashSet<HashSet<string>> ParameterOpinion
    {
      get
      {
        Contract.Ensures(Contract.Result<HashSet<HashSet<string>>>() != null);
        return ForNames(ParameterNames);
      }
    }

    public HashSet<HashSet<string>> InstanceVariableOpinion
    {
      get
      {
        Contract.Ensures(Contract.Result<HashSet<HashSet<string>>>() != null);
        return ForNames(Names.ThisNames());
      }
    }

    /// <summary>
    /// Update comparability information using the given comparability sets.
    /// </summary>
    /// <param name="opinion">the comparability sets</param>
    /// <returns><code>true</code> if a change occured</returns>
    public bool MergeOpinion(HashSet<HashSet<string>> opinion)
    {
      Contract.Requires(opinion != null);
      Contract.Requires(!opinion.Contains(null));

      bool changed = false;
      foreach (var cmp in opinion)
      {
        changed |= Mark(cmp.Select(n => GetId(n)));
      }
      return changed;
    }

    /// <summary>
    /// <code>true</code> iff the associated method returns a value.
    /// </summary>
    public bool ReturnsValue
    {
      get
      {
        return returns.Any(r => r.Expression != null);
      }
    }

    [Pure]
    private static string Rebase(string name, Dictionary<string, string> map)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(map != null);

      if (map.ContainsKey(name))
      {
        return map[name];
      }

      string key = map.Keys.FirstOrDefault(k => name.StartsWith(k + "."));

      if (key != null)
      {
        return map[key] + name.Substring(name.Length);
      }
      else
      {
        return name;
      }
    }

    private static HashSet<HashSet<string>> Filter(HashSet<HashSet<string>> sets, HashSet<string> names)
    {
      var result = new HashSet<HashSet<string>>();
      foreach (var x in sets)
      {
        result.Add(new HashSet<string>(x.Where(n => !names.Contains(n))));
      }
      return result;
    }

    private static HashSet<HashSet<string>> Rebase(HashSet<HashSet<string>> sets, Dictionary<string, string> map)
    {
      var result = new HashSet<HashSet<string>>();
      foreach (var x in sets)
      {
        result.Add(new HashSet<string>(x.Select(n => Rebase(n, map))));
      }
      return result;
    }

    /// <summary>
    /// Returns a map from named expressions with starting with <code>baseName</code> to their
    /// comparability sets.
    /// </summary>
    /// <param name="baseName"></param>
    /// <returns></returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
    public Dictionary<string, HashSet<string>> ComparabilitySets(string baseName)
    {
      var result = new Dictionary<string, HashSet<string>>();
      foreach (var name in ids.Keys.Where(n => n.StartsWith(baseName + ".")))
      {
        result.Add(name, ComparabilitySet(name));
      }
      return result;
    }

    /// <summary>
    /// Returns the names in the same comparability set as <code>name</code>. This set includes <code>name</code>.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public HashSet<string> ComparabilitySet(string name)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Ensures(Contract.Result<HashSet<string>>() != null);
      Contract.Ensures(Contract.Result<HashSet<string>>().Contains(name));

      if (ids.ContainsKey(name))
      {
        var groupId = comparability.FindSet(ids[name]);
        return new HashSet<string>(Comparability.Where(x => x.Value == groupId).Select(x => x.Key));
      }
      else
      {
        return new HashSet<string>(new[] { name });
      }
    }

    public Dictionary<string, int> Comparability
    {
      get
      {
        Dictionary<string, int> result = new Dictionary<string, int>();
        foreach (var name in ids.Keys)
        {
          result.Add(name, comparability.FindSet(ids[name]));
        }
        return result;
      }
    }

    private HashSet<IExpression> Expand(IEnumerable<IExpression> parents)
    {
      HashSet<IExpression> result = new HashSet<IExpression>();
      foreach (var p in parents)
      {
        result.UnionWith(Expand(p));
      }
      return result;
    }

    private HashSet<IExpression> Expand(IExpression parent)
    {
      HashSet<IExpression> result = new HashSet<IExpression>();

      result.Add(parent);
      if (Names.NamedChildren.ContainsKey(parent))
      {
        result.UnionWith(Names.NamedChildren[parent]);
      }

      return result;
    }

    /// <summary>
    /// Returns the compability set id for the expression with the given name.
    /// </summary>
    /// <param name="name">the expression</param>
    /// <returns>the compability set id for the expression with the given name</returns>
    public int GetComparability(string name)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Ensures(Contract.Result<int>() >= 0);

      return comparability.FindSet(GetId(name));
    }

    /// <summary>
    /// Returns the tracking id for the expression with the given name; if the expression
    /// is currently not tracked, a new id is created.
    /// </summary>
    /// <param name="name">the expression</param>
    /// <returns>the tracking id for the expression with the given name</returns>
    private int GetId(string name)
    {
      Contract.Requires(!string.IsNullOrEmpty(name));
      Contract.Ensures(ids.ContainsKey(name) && Contract.Result<int>() == ids[name]);
      Contract.Ensures(Contract.Result<int>() >= 0);

      if (!ids.ContainsKey(name))
      {
        int id = comparability.AddElement();
        ids.Add(name, id);
        return id;
      }
      else
      {
        return ids[name];
      }
    }

    /// <summary>
    /// Returns the id of a named expression, or <code>null</code> if the expression is not named.
    /// </summary>
    /// <param name="expr"></param>
    /// <returns>the id of a named expression, or <code>null</code> if the expression is not named.</returns>
    private int? GetId(IExpression expr)
    {
      if (Names.NameTable.ContainsKey(expr))
      {
        var name = Names.NameTable[expr];
        return GetId(name);
      }
      else
      {
        return null;
      }
    }

    /// <summary>
    /// Mark the expressions with the given ids as being in the same comparability set.
    /// </summary>
    /// <param name="idsToMark">the expression ids</param>
    /// <seealso cref="ids"/>
    /// <returns><c>true</c> if any changes to comparability were made</returns>
    private bool Mark(IEnumerable<int> idsToMark)
    {
      bool modified = false;
      int? last = null;
      foreach (var id in idsToMark)
      {
        if (last != null)
        {
          modified |= comparability.Union(comparability.FindSet(last.Value), comparability.FindSet(id));
        }
        last = id;
      }
      return modified;
    }

    /// <summary>
    /// Mark the expressions with the given names as being in the same comparability set.
    /// </summary>
    /// <param name="names">the expression names</param>
    /// <returns><c>true</c> if any changes to comparability were made</returns>
    private bool Mark(IEnumerable<string> names)
    {
      return Mark(names.Select(n => GetId(n)));
    }

    /// <summary>
    /// Mark the given expressions as being in the same comparability set; expressions without names
    /// are ignored.
    /// </summary>
    /// <param name="exprs">the expressions</param>
    /// <returns><c>true</c> if any changes to comparability were made</returns>
    private bool Mark(IEnumerable<IExpression> exprs)
    {
      return Mark(exprs.Select(x => GetId(x)).Where(x => x.HasValue).Select(x => x.Value));
    }

    private void HandleComposite(IExpression composite, IExpression instance)
    {
      if (instance != null)
      {
        PropogateTypeReference(instance, composite);
      }
    }

    public override void Visit(IBoundExpression bound)
    {
      HandleComposite(bound, bound.Instance);
    }

    public override void Visit(ITargetExpression target)
    {
      HandleComposite(target, target.Instance);
    }

    /// <summary>
    /// If the call is to a standard collection method, update the element and index comparability information
    /// </summary>
    /// <param name="call">the method call</param>
    private void HandleCollectionMethod(IMethodCall call)
    {
      var receiver = call.ThisArgument;
      var callee = call.MethodToCall.ResolvedMethod;

      IExpression index = null;
      var elements = new List<IExpression>();

      foreach (var m in MemberHelper.GetImplicitlyImplementedInterfaceMethods(callee))
      {
        var genericDef = TypeHelper.UninstantiateAndUnspecialize(m.ContainingTypeDefinition);

        // IEnumerable does not define any methods that affect comparability

        // ICollection
        if (TypeHelper.TypesAreEquivalent(genericDef, Host.PlatformType.SystemCollectionsGenericICollection, true))
        {
          if (m.Name.Value.OneOf("Add", "Remove", "Contains"))
          {
            elements.Add(call.Arguments.First());
          }
        }

        // IList
        if (TypeHelper.TypesAreEquivalent(genericDef, Host.PlatformType.SystemCollectionsGenericIList, true))
        {
          if (m.Name.Value.OneOf("IndexOf"))
          {
            elements.Add(call.Arguments.First());
          }
          else if (m.Name.Value.OneOf("get_Item", "set_Item", "RemoveAt"))
          {
            index = call.Arguments.First();
          }
          else if (m.Name.Value.OneOf("Insert"))
          {
            index = call.Arguments.ElementAt(0);
            elements.Add(call.Arguments.ElementAt(1));
          }
        }
      }

      if (index != null && Names.NameTable.ContainsKey(index))
      {
        var collectionName = Names.NameTable[receiver];
        var collectionContents = NameBuilder.FormElementsExpression(collectionName);

        // The collection reference may not have been used in a comparable way yet.
        if (!ids.ContainsKey(collectionContents))
        {
          ids.Add(collectionContents, comparability.AddElement());
        }

        if (!collectionIndexes.ContainsKey(collectionContents))
        {
          collectionIndexes.Add(collectionContents, new HashSet<string>());
        }

        if (collectionIndexes[collectionContents].Add(Names.NameTable[index]))
        {
          // we haven't seen this index before, so re-mark indexes
          Mark(collectionIndexes[collectionContents]);
        }
      }

      if (elements.Count > 0)
      {
        var collectionName = Names.NameTable[receiver];
        var collectionContents = NameBuilder.FormElementsExpression(collectionName);

        // The collection reference may not have been used in a comparable way yet.
        if (!ids.ContainsKey(collectionContents))
        {
          ids.Add(collectionContents, comparability.AddElement());
        }

        var names = new List<string>();
        names.Add(collectionContents);
        names.AddRange(Names.Names(elements));
        Mark(names);
      }
    }

    /// <summary>
    /// If the method call is a call to a Dictionary method, update the comparability information
    /// for keys and values.
    /// </summary>
    /// <remarks>
    ///   This uses <c>Key</c> and <c>Value</c> instead of <c>Keys</c> and <c>Values</c> since the declaration
    ///   printer uses <c>Key</c> and <c>Value</c> (it grabs it from the dictionary entry, as 
    /// </remarks>
    /// <param name="call">the method call</param>
    private void HandleDictionaryMethod(IMethodCall call)
    {
      var receiver = call.ThisArgument;
      var callee = call.MethodToCall.ResolvedMethod;

      var keys = new List<IExpression>();
      var values = new List<IExpression>();

      var genericDef = TypeHelper.UninstantiateAndUnspecialize(receiver.Type);

      if (TypeHelper.TypesAreEquivalent(genericDef, Host.PlatformType.SystemCollectionsGenericDictionary, true))
      {
        if (callee.Name.Value.OneOf("Remove", "TryGetValue", "ContainsKey", "set_Item", "get_Item"))
        {
          keys.Add(call.Arguments.First());
        }
        else if (callee.Name.Value.OneOf("Add"))
        {
          keys.Add(call.Arguments.ElementAt(0));
          values.Add(call.Arguments.ElementAt(1));
        }
      }

      var collectionName = Names.NameTable[receiver];  
      var xs = new [] { 
        Tuple.Create(collectionName + ".Keys", keys),
        Tuple.Create(NameBuilder.FormElementsExpression(collectionName) + ".Key", keys),
        Tuple.Create(collectionName + ".Values", values),
        Tuple.Create(NameBuilder.FormElementsExpression(collectionName) + ".Value", values)
      };

      foreach (var x in xs.Where(c => c.Item2.Count > 0))
      {
        // The collection reference may not have been used in a comparable way yet.
        if (!ids.ContainsKey(x.Item1))
        {
          ids.Add(x.Item1, comparability.AddElement());
        }

        var names = new List<string>();
        names.Add(x.Item1);
        names.AddRange(Names.Names(x.Item2));
        Mark(names);
      }
    }

    public override void Visit(IMethodCall call)
    {
      var receiver = call.ThisArgument;
      var callee = call.MethodToCall.ResolvedMethod;

      // Form comparability information for Collection classes
      if (!call.IsStaticCall && Names.NameTable.ContainsKey(receiver))
      {
        HandleCollectionMethod(call);
        HandleDictionaryMethod(call);
      }

      if (NameBuilder.IsSetter(callee))
      {
        // For setters, mark the property name as comparable with the argument
        Mark(Expand(new[] { call, call.Arguments.First() }));
      }

      if (!(callee is Dummy || callee.Name is Dummy))
      {
        namedCalls.Add(call);
      }

      if (!call.IsStaticCall)
      {
        PropogateTypeReference(call.ThisArgument, call);
      }
    }

    public override void Visit(IArrayIndexer arrayIndexer)
    {
      if (arrayIndexer.Indices.Count() == 1 && Names.NameTable.ContainsKey(arrayIndexer.IndexedObject))
      {
        // the array expression, e.g.: this.array
        var arrayName = Names.NameTable[arrayIndexer.IndexedObject];
        // the contents expression, e.g.: this.array[..]
        var arrayContents = NameBuilder.FormElementsExpression(arrayName);

        // mark array indexes as compatible
        var index = arrayIndexer.Indices.First();
        if (Names.NameTable.ContainsKey(index))
        {
          // The array reference may not have been used in a comparable way yet.
          if (!ids.ContainsKey(arrayContents))
          {
            ids.Add(arrayContents, comparability.AddElement());
          }

          if (!collectionIndexes.ContainsKey(arrayContents))
          {
            collectionIndexes.Add(arrayContents, new HashSet<string>());
          }

          if (collectionIndexes[arrayContents].Add(Names.NameTable[index]))
          {
            // we haven't seen this index before, so re-mark indexes
            Mark(collectionIndexes[arrayContents]);
          }
        }
        
        PropogateTypeReference(arrayIndexer.IndexedObject, arrayIndexer);
      }
    }


    public override void Visit(IReturnStatement ret)
    {
      if (ret.Expression != null && !Names.AnonymousDelegateReturns.Contains(ret))
      {
        returns.Add(ret);

        var expanded = new HashSet<string>(Names.Names(Expand(ret.Expression)));
        expanded.Add("return");
        Mark(expanded);
      }
    }

    public override void Visit(IThisReference thisRef)
    {
      AddTypeReference(Names.Type, thisRef);
    }

    public override void Visit(ILocalDeclarationStatement dec)
    {
      if (dec.InitialValue != null)
      {
        var expanded = new HashSet<string>(Names.Names(Expand(dec.InitialValue)));
        expanded.Add("<local>" + dec.LocalVariable.Name.Value);
        Mark(expanded);
      }
    }

    public override void Visit(IAssignment assignment)
    {
      var expanded = Expand(new[] { assignment.Source, assignment.Target });
      Mark(expanded);
    }

    public override void Visit(IExpression expr)
    {
      if (Names.NameTable.ContainsKey(expr))
      {
        namedExpressions.Add(expr);
      }
    }

    public override void Visit(ISwitchStatement expr)
    {
      HashSet<IExpression> cmp = new HashSet<IExpression>();
      cmp.Add(expr.Expression);
      cmp.UnionWith(expr.Cases.Select(c => c.Expression));
      Mark(Expand(cmp));
    }

    public override void Visit(IConditional conditional)
    {
      Mark(Expand(new[] { conditional.ResultIfTrue, conditional.ResultIfFalse }));
    }

    public override void Visit(IBinaryOperation binary)
    {
      var expanded = Expand(new[] { binary.LeftOperand, binary.RightOperand });
      Mark(expanded);
    }

    public static MethodSummary MakeSummary(TypeManager typeManager, MethodVisitor method)
    {
      return new MethodSummary(
        typeManager.ConvertCCITypeToAssemblyQualifiedName(method.Method.ContainingTypeDefinition),
        method.Method.Name.Value,
        method.Method.Parameters.Select(p => typeManager.ConvertCCITypeToAssemblyQualifiedName(p.Type)).ToArray(),
        method.ids,
        method.comparability,
        method.collectionIndexes);
    }

  }
}
