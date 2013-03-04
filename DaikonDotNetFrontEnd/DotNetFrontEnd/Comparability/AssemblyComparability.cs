using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Cci;
using Comparability;
using EmilStefanov;
using System.Reflection;
using Microsoft.Cci.ILToCodeModel;

namespace DotNetFrontEnd.Comparability
{
    public class TypeSummary
    {
        private Dictionary<string, int> ids = new Dictionary<string,int>();
        
        private Dictionary<string, HashSet<string>> arrayIndexes = new Dictionary<string, HashSet<string>>();

        private DisjointSets comparability = new DisjointSets();

        public TypeSummary(NameBuilder names, IEnumerable<MethodVisitor> methods)
        {
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

        public int GetIndex(string name)
        {
            if (!arrayIndexes.ContainsKey(name))
            {
                // create a dummy index
                var synthetic = "<" + name + ">";
                
                ids.Add(synthetic, comparability.AddElement());

                var cmp = new HashSet<string>();
                cmp.Add(synthetic);
                arrayIndexes.Add(name, cmp);
            }
            return Get(arrayIndexes[name].First());
        }

        public int Get(string name)
        {
            if (!ids.ContainsKey(name))
            {
                ids.Add(name, comparability.AddElement());
            }
            return comparability.FindSet(ids[name]);
        }
    }

    public class AssemblyComparability
    {
        public Dictionary<IMethodDefinition, MethodVisitor> MethodComparability { get;  set; }
        public Dictionary<INamedTypeDefinition, NameBuilder> TypeNames { get;  set;}
        private Dictionary<INamedTypeDefinition, TypeSummary> TypeComparability { get; set; }

        public AssemblyComparability(Microsoft.Cci.MutableCodeModel.Assembly decompiled, IMetadataHost host, PdbReader reader)
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
            if (method != null)
            {
                var match = MethodComparability.Keys.First(m => MemberHelper.MethodsAreEquivalent(m, method));
                return MethodComparability[match].GetArrayIndexComparability(name);
            }
            else if (type != null)
            {
                var match = TypeNames.Keys.First(t => TypeHelper.TypesAreEquivalent(t, type, true));
                return TypeComparability[match].GetIndex(name);
            }
            else
            {
                throw new Exception("No type or method context provided for array variable '" + name + "'");
            }
        }

        internal int GetComparability(string name, INamedTypeDefinition type, DeclarationPrinter.VariableKind kind, IMethodDefinition method = null)
        {
            if (method != null)
            {
                var match = MethodComparability.Keys.FirstOrDefault(m => MemberHelper.MethodsAreEquivalent(m, method));
                return MethodComparability[match].GetComparability(name);
            }
            if (type != null)
            {
                var match = TypeNames.Keys.FirstOrDefault(t => TypeHelper.TypesAreEquivalent(t, type, true));
                return TypeComparability[match].Get(name);
            }
            else
            {
                throw new Exception("No type or method context provided for variable '" + name + "'");
            }
        }
    }
}
