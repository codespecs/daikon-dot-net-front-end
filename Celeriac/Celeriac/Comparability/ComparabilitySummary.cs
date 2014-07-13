using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EmilStefanov;
using System.Diagnostics.Contracts;
using Comparability;
using Celeriac.Contracts;
using Microsoft.Cci;
using System.Diagnostics.CodeAnalysis;

namespace Celeriac.Comparability
{
  [Serializable]
  public class TypeSummary
  {
    public string AssemblyQualifiedName { get; private set; }
    private readonly Dictionary<string, int> ids = new Dictionary<string, int>();
    private readonly Dictionary<string, HashSet<string>> arrayIndexes = new Dictionary<string, HashSet<string>>();
    private readonly DisjointSets comparability = new DisjointSets();

    [ContractInvariantMethod]
    [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Called by CC framework")]
    private void ObjectInvariant()
    {
      Contract.Invariant(Contract.ForAll(arrayIndexes.Keys, i => ids.ContainsKey(i)));
    }

    public TypeSummary(string assemblyQualifiedName, NameBuilder names, IEnumerable<MethodVisitor> methods)
    {
      Contract.Requires(!string.IsNullOrEmpty(assemblyQualifiedName));
      Contract.Requires(names != null);
      Contract.Requires(methods != null);
      Contract.Ensures(ids.Keys.SetEquals(names.ThisNames()));
      
      this.AssemblyQualifiedName = assemblyQualifiedName;

      // give a union-find id to each instance expression name
      foreach (var name in names.ThisNames())
      {
        ids.Add(name, comparability.AddElement());
      }

      // union the sets, according to each method's opinion
      foreach (var name in names.ThisNames())
      {
        HashSet<string> indexOpinion = new HashSet<string>();

        foreach (var method in methods)
        {
          var opinion = method.ComparabilitySet(name).Intersect(ids.Keys);
          string last = null;
          foreach (var other in opinion)
          {
            if (last != null)
            {
              comparability.Union(comparability.FindSet(ids[last]), comparability.FindSet(ids[name]));
            }
            last = other;
          }

          indexOpinion.UnionWith(method.IndexComparabilityOpinion(name).Intersect(names.ThisNames()));
        }

        if (indexOpinion.Count > 0)
        {
          arrayIndexes.Add(name, indexOpinion);
        }
      }
    }

    public int GetIndex(string arrayName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(arrayName));

      if (!ids.ContainsKey(arrayName))
      {
        ids.Add(arrayName, comparability.AddElement());
      }

      if (!arrayIndexes.ContainsKey(arrayName))
      {
        // create a dummy name for an index variable (that won't be comparable with anything)
        var synthetic = "<index>" + arrayName;

        // pretend we've seen the index
        ids.Add(synthetic, comparability.AddElement());

        var cmp = new HashSet<string>();
        cmp.Add(synthetic);
        arrayIndexes.Add(arrayName, cmp);
      }
      return Get(arrayIndexes[arrayName].First());
    }

    public int Get(string name)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      if (!ids.ContainsKey(name))
      {
        ids.Add(name, comparability.AddElement());
      }
      return comparability.FindSet(ids[name]);
    }
  }

  [Serializable]
  public class MethodSummary
  {
    /// <summary>
    /// The name of the method
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Assembly qualified names of parameter types
    /// </summary>
    public IEnumerable<string> ParameterTypes { get; private set; }

    public string DeclaringTypeName { get; private set; }

    private readonly Dictionary<string, int> ids = new Dictionary<string, int>();
    private readonly DisjointSets comparability = new DisjointSets();
    private readonly Dictionary<string, HashSet<string>> arrayIndexes = new Dictionary<string, HashSet<string>>();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="declaringTypeName"></param>
    /// <param name="name"></param>
    /// <param name="parameterTypes"></param>
    /// <param name="ids"></param>
    /// <param name="comparability"></param>
    /// <param name="arrayIndexes"></param>
    public MethodSummary(string declaringTypeName, string name, string[] parameterTypes, 
      Dictionary<string, int> ids, DisjointSets comparability, Dictionary<string, HashSet<string>> arrayIndexes)
    {
      this.Name = name;
      this.ParameterTypes = parameterTypes;
      this.ids = ids;
      this.comparability = comparability;
      this.arrayIndexes = arrayIndexes;
      this.DeclaringTypeName = declaringTypeName;
    }

    public bool Matches(TypeManager typeManager, IMethodDefinition method)
    {
      var paramTypes = method.Parameters.Select(p => typeManager.ConvertCCITypeToAssemblyQualifiedName(p.Type));
      return Name.Equals(method.Name.Value) && ParameterTypes.SequenceEqual(paramTypes);
    }

    public int GetComparability(string name)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Ensures(Contract.Result<int>() >= 0);

      return comparability.FindSet(GetId(name));
    }

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

    public int GetArrayIndexComparability(string arrayName)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(arrayName));
      Contract.Ensures(Contract.Result<int>() >= 0);

      if (!ids.ContainsKey(arrayName))
      {
        ids.Add(arrayName, comparability.AddElement());
      }

      if (!arrayIndexes.ContainsKey(arrayName))
      {
        var synthetic = "<index>" + arrayName;

        // create a dummy index
        var cmp = new HashSet<string>();
        cmp.Add(synthetic);
        arrayIndexes.Add(arrayName, cmp);
      }
      return GetComparability(arrayIndexes[arrayName].First());
    }

  }


}
