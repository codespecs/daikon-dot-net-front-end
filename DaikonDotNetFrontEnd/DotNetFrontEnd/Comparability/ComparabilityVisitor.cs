using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Cci;
using EmilStefanov;

namespace Comparability
{

    /// <summary>
    /// Computes a method's opininion about comparability
    /// </summary>
    public class MethodVisitor : CodeVisitor
    {
        public NameBuilder Names { get; private set; }

        private Dictionary<string, int> ids = new Dictionary<string, int>();
        private DisjointSets comparability = new DisjointSets();
        public IMethodDefinition Method { get; private set; }

        private Dictionary<string, HashSet<string>> arrayIndexes = new Dictionary<string, HashSet<string>>();

        private HashSet<IReturnStatement> returns = new HashSet<IReturnStatement>();
        private HashSet<IMethodCall> calls = new HashSet<IMethodCall>();

        public MethodVisitor(IMethodDefinition method, NameBuilder names)
        {
            Names = names;
            Method = method;

            ids.Add("return", comparability.AddElement());

            foreach (var param in Method.Parameters)
            {
                ids.Add(param.Name.Value, comparability.AddElement());
            }
        }

        public bool ApplyMethodSummaries(Dictionary<IMethodDefinition, MethodVisitor> methodData)
        {
            bool modified = false;

            foreach (var callsite in calls)
            {
                var resolved = callsite.MethodToCall.ResolvedMethod;
                var sameClass = Method.ContainingTypeDefinition == resolved.ContainingTypeDefinition;
                
                if (methodData.ContainsKey(resolved))
                {
                    var data = methodData[resolved];

                    // incorporate instance variable opinion directly if methods are in the same type
                    if (sameClass)
                    {
                        modified |= MergeOpinion(data.InstanceVariableOpinion);
                    }

                    var paramsToArgs = new Dictionary<string, string>();
                    foreach (var binding in resolved.Parameters.Zip(callsite.Arguments, (x,y) => Tuple.Create(x,y)))
                    {
                        if (Names.NameTable.ContainsKey(binding.Item2))
                        {
                            paramsToArgs.Add(binding.Item1.Name.Value, Names.NameTable[binding.Item2]);
                        }
                    }
                        
                    // incorporate parameter opinion
                    var opinion = sameClass ? data.ParameterOpinionSameClass : data.ParameterOpinion;
                    var rebased = Rebase(opinion, paramsToArgs);
                    modified |= MergeOpinion(rebased);
                        
                    // TODO HANDLE RETURN METHODS
                }
            }

            if (modified)
            {
                Console.WriteLine("Updated " + Method.Name);
            }

            return modified;
        }

        public HashSet<string> ParameterNames
        {
            get
            {
                var ps = new HashSet<string>(Method.Parameters.Select(p => p.Name.Value));
                return new HashSet<string>(ids.Keys.Where(n => ps.Any(p => n.Equals(p) || n.StartsWith(p + "."))));
            }
        }

        private HashSet<HashSet<string>> ForNames(HashSet<string> names)
        {
            var cmp = Comparability;

            HashSet<HashSet<string>> result = new HashSet<HashSet<string>>();
            foreach (var group in names.Where(n => cmp.ContainsKey(n)).GroupBy(n => cmp[n]))
            {
                var cmpId = group.Key;
                result.Add(new HashSet<string>(group.Intersect(names)));
            }
            return result;
        }

        public HashSet<HashSet<string>> ParameterOpinionSameClass
        {
            get
            {
                var ps = ParameterNames;
                var cs = Names.InstanceNames;
                return ForNames(new HashSet<string>(ps.Union(cs)));
            }
        }

        public HashSet<HashSet<string>> ParameterOpinion
        {
            get
            {
                return ForNames(ParameterNames);   
            }
        }

        public HashSet<HashSet<string>> InstanceVariableOpinion
        {
            get
            {
                return ForNames(Names.InstanceNames);
            }
        }

        /// <summary>
        /// Update comparability information using the given comparability sets.
        /// </summary>
        /// <param name="opinion">the comparability sets</param>
        /// <returns><code>true</code> if a change occured</returns>
        public bool MergeOpinion(HashSet<HashSet<string>> opinion)
        {
            bool changed = false;
            foreach (var cmp in opinion)
            {
                changed |= Mark(cmp.Select(n => GetId(n)));
            }
            return changed;
        }

        public bool ReturnsValue
        {
            get
            {
                return returns.Any(r => r.Expression != null);
            }
        }


        private static string Rebase(string name, Dictionary<string, string> map)
        {
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

        private static string Rebase(string str, string baseName, string name)
        {
            if (str.Equals(baseName))
            {
                return name;
            }
            else if (str.StartsWith(baseName))
            {
                return name + str.Substring(baseName.Length);
            }
            else
            {
                return str;   
            }
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
            if (ids.ContainsKey(name))
            {
                var groupId = comparability.FindSet(ids[name]);
                return new HashSet<string>(Comparability.Where(x => x.Value == groupId).Select(x => x.Key));
            }
            else
            {
                return new HashSet<string>(new [] { name });
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

        private int GetId(string name)
        {
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

        private bool Mark(IEnumerable<int> ids)
        {
            bool modified = false;
            int? last = null;
            foreach (var id in ids)
            {
                if (last != null)
                {
                    modified |= comparability.Union(comparability.FindSet(last.Value), comparability.FindSet(id));
                }
                last = id;
            }
            return modified;
        }

        private bool Mark(IEnumerable<string> names)
        {
            return Mark(names.Select(n => GetId(n)));
        }

        private bool Mark(IEnumerable<IExpression> exprs)
        {
           return Mark(exprs.Select(x => GetId(x)).Where(x => x.HasValue).Select(x => x.Value));
        }

        public override void Visit(IMethodCall call)
        {
            calls.Add(call);
        }

        public override void Visit(IArrayIndexer arrayIndexer)
        {
            if (arrayIndexer.Indices.Count() == 1 && Names.NameTable.ContainsKey(arrayIndexer.IndexedObject))
            {
                var arrayName = Names.NameTable[arrayIndexer.IndexedObject];

                if (!arrayIndexes.ContainsKey(arrayName))
                {
                    arrayIndexes.Add(arrayName, new HashSet<string>());
                }

                var index = arrayIndexer.Indices.First();
                if (Names.NameTable.ContainsKey(index))
                {
                    if (arrayIndexes[arrayName].Add(Names.NameTable[index]))
                    {
                        Mark(arrayIndexes[arrayName]);
                    }
                }
            }
        }

        public override void Visit(IReturnStatement ret)
        {
            if (ret.Expression != null)
            {
                returns.Add(ret);
                
                var returnId = ids["return"];
                foreach (var id in returns.Select(r => GetId(r.Expression)).Where(x => x.HasValue))
                {
                    comparability.Union(comparability.FindSet(returnId), comparability.FindSet(id.Value));
                }
            }
        }

        public override void Visit(IAssignment assignment)
        {
            var expanded = Expand(new [] {assignment.Source, assignment.Target});
            Mark(expanded);
        }

        public override void Visit(IBinaryOperation binary)
        {
            var expanded = Expand(new[] { binary.LeftOperand, binary.RightOperand });
            Mark(expanded);
        }

    }
}
