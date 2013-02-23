using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Cci;
using Comparability;
using EmilStefanov;
using System.Reflection;

namespace DotNetFrontEnd.Comparability
{
    public class AssemblyComparability
    {
        public Dictionary<IMethodDefinition, MethodVisitor> MethodComparability { get;  set; }
        public Dictionary<INamedTypeDefinition, NameBuilder> TypeNames { get;  set;}
        private Dictionary<INamedTypeDefinition, Dictionary<string, int>> TypeComparability { get; set; }

        public AssemblyComparability(Microsoft.Cci.MutableCodeModel.Assembly decompiled, IMetadataHost host)
        {
            MethodComparability = new Dictionary<IMethodDefinition, MethodVisitor>();
            TypeNames = new Dictionary<INamedTypeDefinition, NameBuilder>();

            foreach (var type in decompiled.AllTypes)
            {
                var names = new NameBuilder(type, host);
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
                Console.WriteLine("Method Call Summary Iteration: " + round);
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


            TypeComparability = new Dictionary<INamedTypeDefinition, Dictionary<string, int>>();
            foreach (var type in decompiled.AllTypes)
            {
                var typeCmp = CalculateTypeComparability(
                    TypeNames[type],
                    type.Methods.Where(m => MethodComparability.ContainsKey(m)).Select(m => MethodComparability[m]));
                TypeComparability.Add(type, typeCmp);
            }
        }

        public int GetComparability(string name, INamedTypeDefinition type, DeclarationPrinter.VariableKind kind, IMethodDefinition method = null)
        {
            if (kind == DeclarationPrinter.VariableKind.field && type != null)
            {
                if (TypeNames.ContainsKey(type))
                {
                    return -2;
                    //return TypeComparability[type][name];
                }
                else
                {
                    return -1;
                }
            }
            else if (kind == DeclarationPrinter.VariableKind.variable && method != null)
            {
                if (MethodComparability.ContainsKey(method))
                {
                    return MethodComparability[method].Comparability[name];
                }
                else
                {
                    return -1;
                }
            }
            else
            {
                return -1;
            }
        }

        static Dictionary<string, int> CalculateTypeComparability(NameBuilder names, IEnumerable<MethodVisitor> methods)
        {
            // give a union-find id to each instance expression name
            var ids = new Dictionary<string, int>();
            DisjointSets comparability = new DisjointSets();
            foreach (var name in names.InstanceNames)
            {
                ids.Add(name, comparability.AddElement());
            }

            // union the sets, according to each method's opinion
            foreach (var name in names.InstanceNames)
            {
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
                }
            }

            // create the final lookup table
            Dictionary<string, int> result = new Dictionary<string, int>();
            foreach (var name in ids.Keys)
            {
                result.Add(name, comparability.FindSet(ids[name]));
            }
            return result;
        }

    }
}
