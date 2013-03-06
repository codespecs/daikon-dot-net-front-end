using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Cci;
using Comparability;
using EmilStefanov;
using System.Reflection;
using Microsoft.Cci.ILToCodeModel;
using System.Diagnostics.Contracts;
using DotNetFrontEnd.Contracts;

namespace DotNetFrontEnd.Comparability
{
  public class TypeSummary
  {
    private readonly Dictionary<string, int> ids = new Dictionary<string, int>();
    private readonly Dictionary<string, HashSet<string>> arrayIndexes = new Dictionary<string, HashSet<string>>();
    private readonly DisjointSets comparability = new DisjointSets();

    [ContractInvariantMethod]
    private void ObjectInvariant()
    {
      Contract.Invariant(Contract.ForAll(arrayIndexes.Keys, i => ids.ContainsKey(i)));
    }

    public TypeSummary(NameBuilder names, IEnumerable<MethodVisitor> methods)
    {
      Contract.Requires(names != null);
      Contract.Requires(methods != null);
      Contract.Ensures(ids.Keys.SetEquals(names.ThisNames()));

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
          arrayIndexes.Add(name, names.ThisNames());
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

  public class AssemblyComparability
  {
    public Dictionary<IMethodDefinition, MethodVisitor> MethodComparability { get; private set; }
    public Dictionary<INamedTypeDefinition, NameBuilder> TypeNames { get; private set; }
    private Dictionary<INamedTypeDefinition, TypeSummary> TypeComparability { get; set; }

    [ContractInvariantMethod]
    private void ObjectInvariant()
    {
      Contract.Invariant(MethodComparability != null);
      Contract.Invariant(TypeNames != null);
      Contract.Invariant(TypeComparability != null);
    }

    public AssemblyComparability(Microsoft.Cci.MutableCodeModel.Assembly decompiled, TypeManager typeManager, PdbReader reader)
    {
      Contract.Requires(decompiled != null);
      Contract.Requires(typeManager != null);
      Contract.Requires(reader != null);

      MethodComparability = new Dictionary<IMethodDefinition, MethodVisitor>();
      TypeNames = new Dictionary<INamedTypeDefinition, NameBuilder>();

      foreach (var type in decompiled.AllTypes)
      {
        var names = new NameBuilder(type, typeManager);
        new CodeTraverser() { PostorderVisitor = names }.Traverse(type);

        foreach (var method in type.Methods)
        {
          var compVisitor = new MethodVisitor(method, names);
          new CodeTraverser() { PreorderVisitor = compVisitor }.Traverse(method);
          MethodComparability.Add(method, compVisitor);
        }

        TypeNames.Add(type, names);
      }

      int round = 1;

      bool changed = false;
      do
      {
        // Console.WriteLine("Method Summary Propogation Round #" + round);
        changed = false;
        foreach (var type in decompiled.AllTypes)
        {
          foreach (var method in type.Methods)
          {
            if (MethodComparability[method].ApplyMethodSummaries(MethodComparability))
            {
              changed = true;
            }
          }
        }
        round++;
      } while (changed);


      TypeComparability = new Dictionary<INamedTypeDefinition, TypeSummary>();
      foreach (var type in decompiled.AllTypes)
      {

        var typeCmp = new TypeSummary(
            TypeNames[type],
            type.Methods.Where(m => MethodComparability.ContainsKey(m)).Select(m => MethodComparability[m]));
        TypeComparability.Add(type, typeCmp);

      }

      //foreach (var method in MethodComparability.Values)
      //{
      //    var interesting = method.Opinion.Where(x => x.Count > 1);
      //    if (interesting.Count() > 0)
      //    {
      //        Console.WriteLine("-- " + method.Method.Name);
      //        foreach (var x in interesting)
      //        {
      //            Console.WriteLine(string.Join(" ", x));
      //        }
      //        Console.WriteLine();
      //    }
      //}
    }

    public int GetElementComparability(string name, INamedTypeDefinition type, IMethodDefinition method)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null || method != null);
      Contract.Ensures(Contract.Result<int>() >= 0);

      if (method != null)
      {
        var match = MethodComparability.Keys.First(m => MemberHelper.MethodsAreEquivalent(m, method));
        return MethodComparability[match].GetArrayIndexComparability(name);
      }
      else
      {
        var match = TypeNames.Keys.First(t => TypeHelper.TypesAreEquivalent(t, type, true));
        return TypeComparability[match].GetIndex(name);
      }
    }

    internal int GetComparability(string name, INamedTypeDefinition type, DeclarationPrinter.VariableKind kind, IMethodDefinition method = null)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null || method != null);
      Contract.Ensures(Contract.Result<int>() >= 0);

      if (method != null)
      {
        var match = MethodComparability.Keys.FirstOrDefault(m => MemberHelper.MethodsAreEquivalent(m, method));
        return MethodComparability[match].GetComparability(name);
      }
      else
      {
        var match = TypeNames.Keys.FirstOrDefault(t => TypeHelper.TypesAreEquivalent(t, type, true));
        return TypeComparability[match].Get(name);
      }
    }
  }
}
