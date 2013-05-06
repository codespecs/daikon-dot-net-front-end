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
using Celeriac.Contracts;

namespace Celeriac.Comparability
{
  [Serializable]
  public class AssemblySummary
  {
    private Dictionary<string, TypeSummary> TypeComparability { get; set; }
    private Dictionary<string, HashSet<MethodSummary>> MethodComparability { get; set; }

    private AssemblySummary(IEnumerable<TypeSummary> types, IEnumerable<MethodSummary> methods)
    {
      Contract.Requires(types != null);
      Contract.Requires(methods != null);

      TypeComparability = new Dictionary<string, TypeSummary>();
      MethodComparability = new Dictionary<string, HashSet<MethodSummary>>();

      foreach (var t in types)
      {
        TypeComparability.Add(t.AssemblyQualifiedName, t);
      }

      foreach (var m in methods)
      {
        if (!MethodComparability.ContainsKey(m.DeclaringTypeName))
        {
          MethodComparability.Add(m.DeclaringTypeName, new HashSet<MethodSummary>());
        }
        MethodComparability[m.DeclaringTypeName].Add(m);
      }
    }

    private MethodSummary FindMethod(TypeManager typeManager, IMethodDefinition method)
    {

      var typeName = typeManager.ConvertCCITypeToAssemblyQualifiedName(method.ContainingTypeDefinition);
      var methodName = method.Name;
      
      try
      {
        return MethodComparability[typeName].First(m => m.Matches(typeManager, method));
      }
      catch
      {
        Console.Error.WriteLine("Type:" + typeName);
        Console.Error.WriteLine("Method: " + methodName);

        foreach (var p in method.Parameters)
        {
          Console.Error.WriteLine(typeManager.ConvertCCITypeToAssemblyQualifiedName(p.Type));
        }

        foreach (var x in MethodComparability[typeName])
        {
          Console.Error.WriteLine("In DB: " + x.Name);
          foreach (var p in x.ParameterTypes)
          {
            Console.WriteLine(p);
          }
        }
        throw;
      }
    }

    /// <summary>
    /// Returns the comparability set id for the given array variable, e.g., <c>this.array[..]</c>
    /// </summary>
    /// <param name="name">The name of the array, including "[..]"</param>
    /// <param name="typeManager">type information</param>
    /// <param name="type">type context</param>
    /// <param name="method">method context</param>
    /// <returns>the comparability set id for the given array variable</returns>
    public int GetIndexComparability(string name, TypeManager typeManager, ITypeReference type, IMethodDefinition method)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name) && name.EndsWith("[..]"));
      Contract.Requires(typeManager != null);
      Contract.Requires(type != null || method != null);
      Contract.Ensures(Contract.Result<int>() >= 0);

      if (method != null)
      {
        return FindMethod(typeManager, method).GetArrayIndexComparability(name);
      }
      else
      {
        var typeName = typeManager.ConvertCCITypeToAssemblyQualifiedName(type);
        return TypeComparability[typeName].GetIndex(name);
      }
    }

    /// <summary>
    /// Returns the comparability set id for the given expression. For array experessions, e.g., <c>this.array[..]</c>, 
    /// returns the comparability set id for the contents.
    /// </summary>
    /// <param name="name">The name of the experession</param>
    /// <param name="typeManager">type information</param>
    /// <param name="type">type context</param>
    /// <param name="method">method context, or <c>null</c></param>
    /// <returns></returns>
    internal int GetComparability(string name, TypeManager typeManager, INamedTypeDefinition type, IMethodDefinition method = null)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Requires(type != null || method != null);
      Contract.Ensures(Contract.Result<int>() >= 0);

      if (method != null)
      {
        return FindMethod(typeManager, method).GetComparability(name);
      }
      else
      {
        var typeName = typeManager.ConvertCCITypeToAssemblyQualifiedName(type);
        return TypeComparability[typeName].Get(name);
      }
    }


    public static AssemblySummary MakeSummary(Microsoft.Cci.MutableCodeModel.Module decompiled, TypeManager typeManager,
                                   PdbReader reader)
    {
      Contract.Requires(decompiled != null);
      Contract.Requires(typeManager != null);
      Contract.Requires(reader != null);

      var methodComparability = new Dictionary<IMethodDefinition, MethodVisitor>();
      var typeNames = new Dictionary<INamedTypeDefinition, NameBuilder>();

      foreach (var type in decompiled.AllTypes)
      {
        var names = new NameBuilder(type, typeManager);
        new CodeTraverser() { PostorderVisitor = names }.Traverse(type);

        foreach (var method in type.Methods)
        {
          var compVisitor = new MethodVisitor(method, names);
          new CodeTraverser() { PreorderVisitor = compVisitor }.Traverse(method);
          methodComparability.Add(method, compVisitor);
        }

        typeNames.Add(type, names);
      }

      int round = 1;

      bool changed = false;
      do
      {
        changed = false;
        foreach (var type in decompiled.AllTypes)
        {
          foreach (var method in type.Methods)
          {
            if (methodComparability[method].ApplyMethodSummaries(methodComparability))
            {
              changed = true;
            }
          }
        }
        round++;
      } while (changed);


      var typeComparability = new Dictionary<INamedTypeDefinition, TypeSummary>();
      foreach (var type in decompiled.AllTypes)
      {
        var typeCmp = new TypeSummary(
          typeManager.ConvertCCITypeToAssemblyQualifiedName(type),
          typeNames[type],
          type.Methods.Where(m => methodComparability.ContainsKey(m)).Select(m => methodComparability[m]));
        typeComparability.Add(type, typeCmp);
      }

      return new AssemblySummary(
        typeComparability.Values, 
        methodComparability.Values.Select(m => MethodVisitor.MakeSummary(typeManager, m)));
    }
  }
}
