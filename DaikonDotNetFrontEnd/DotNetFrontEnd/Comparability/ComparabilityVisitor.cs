using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Cci;
using EmilStefanov;
using System.Diagnostics;

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

        /// <summary>
        /// Map from instance expressions to their respective types.
        /// </summary>
        public Dictionary<IExpression, ITypeReference> ReferencedTypes;

        private HashSet<IExpression> namedExpressions = new HashSet<IExpression>();
        
        public MethodVisitor(IMethodDefinition method, NameBuilder names)
        {
            Names = names;
            Method = method;
            ReferencedTypes = new Dictionary<IExpression, ITypeReference>();

            ids.Add("return", comparability.AddElement());
            
            foreach (var param in Method.Parameters)
            {
                ids.Add(param.Name.Value, comparability.AddElement());
            }
        }

        private void PropogateTypeReference(IExpression inner, IExpression outer)
        {
            if (ReferencedTypes.ContainsKey(inner))
            {
                AddTypeReference(ReferencedTypes[inner], outer);
            }
        }

        private void AddTypeReference(ITypeReference type, IExpression expr)
        {
            ReferencedTypes.Add(expr, type);
        }

        private string NameForArg(IExpression arg)
        {
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

        private Dictionary<string, string> ZipArguments(IMethodCall callsite)
        {
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

        public bool ApplyMethodSummaries(Dictionary<IMethodDefinition, MethodVisitor> methodData)
        {
            bool modified = false;

            foreach (var callsite in calls)
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

                    // update this method's opinion about the referenced type (possibly itself)
                    foreach (var referencedType in ReferencedTypes.Values)
                    {
                        // TODO account for indirect comparability information via type references
                    }

                    // TODO account for return value
                }
                else if (calleeDefinition.ParameterCount > 0)
                {   
                    HashSet<IParameterDefinition> fix = new HashSet<IParameterDefinition>(calleeDefinition.Parameters);

                    var opinion = ParameterTypeComparability(fix);
                    var rebased = Filter(
                        Rebase(opinion, argBindings), 
                        new HashSet<string>(calleeDefinition.Parameters.Select(p => p.Name.Value)));

                    modified |= MergeOpinion(rebased);

                    // TODO account for return value
                }
            }  
            
            if (modified)
            {
                // Console.WriteLine("Updated " + Method.Name);
            }

            return modified;
        }

        public static HashSet<HashSet<string>> ParameterTypeComparability(IEnumerable<IParameterDefinition> parameters)
        {
            Dictionary<IParameterDefinition, int> ids = new Dictionary<IParameterDefinition, int>();
            DisjointSets cmp = new DisjointSets();
            foreach (var p in parameters)
            {
                ids.Add(p, cmp.AddElement());
            }

            foreach (var lhs in parameters)
            {
                Debug.Assert(ids.ContainsKey(lhs), "Error tracking parameter " + lhs.Name);
                foreach (var rhs in parameters)
                {
                    Debug.Assert(ids.ContainsKey(rhs), "Error tracking parameter " + rhs.Name);
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
            return collections.Aggregate(new HashSet<string>(), (a, c) => new HashSet<string>(a.Union(c)));
        }

        public HashSet<string> ParameterNames
        {
            get
            {
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
            var cmp = Comparability;

            HashSet<HashSet<string>> result = new HashSet<HashSet<string>>();
            foreach (var group in names.Where(n => cmp.ContainsKey(n)).GroupBy(n => cmp[n]))
            {
                var cmpId = group.Key;
                result.Add(new HashSet<string>(group.Intersect(names)));
            }
            return result;
        }

        public HashSet<HashSet<string>> Opinion
        {
            get
            {
                var cmp = Comparability;

                HashSet<HashSet<string>> result = new HashSet<HashSet<string>>();
                foreach (var group in ids.Keys.Where(n => cmp.ContainsKey(n)).GroupBy(n => cmp[n]))
                {
                    var cmpId = group.Key;
                    result.Add(new HashSet<string>(group));
                }
                return result;
            }
        }

        public HashSet<string> IndexComparabilityOpinion(string array)
        {
            return arrayIndexes.ContainsKey(array) 
                   ? arrayIndexes[array] 
                   : new HashSet<string>();
        }

        public HashSet<string> StaticNames
        {
            get
            {
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
                return ForNames(ParameterNames);   
            }
        }

        public HashSet<HashSet<string>> InstanceVariableOpinion
        {
            get
            {
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

        public int GetArrayIndexComparability(string array)
        {
            if (!arrayIndexes.ContainsKey(array))
            {
                var synthetic = "<" + array + ">";

                // create a dummy index
                var cmp = new HashSet<string>();
                cmp.Add(synthetic);
                arrayIndexes.Add(array, cmp);
            }
            return GetComparability(arrayIndexes[array].First());
        }

        public int GetComparability(string name){
            return comparability.FindSet(GetId(name));
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

        private void HandleComposite(IExpression composite, object definition, IExpression instance)
        {
            if (instance != null)
            {
                PropogateTypeReference(instance, composite);
            }  
        }

        public override void Visit(IBoundExpression bound)
        {
            HandleComposite(bound, bound.Definition, bound.Instance);
        }

        public override void Visit(ITargetExpression target)
        {
            HandleComposite(target, target.Definition, target.Instance);
        }

        public override void Visit(IMethodCall call)
        {            
            var callee = call.MethodToCall.ResolvedMethod;

            if (NameBuilder.IsSetter(callee))
            {
                Mark(Expand(new[] { call, call.Arguments.First() }));
            }
            else
            {
                calls.Add(call);
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
                var arrayName = Names.NameTable[arrayIndexer.IndexedObject];

                if (!arrayIndexes.ContainsKey(arrayName))
                {
                    arrayIndexes.Add(arrayName, new HashSet<string>());
                }

                // mark array indexes as compatible
                var index = arrayIndexer.Indices.First();
                if (Names.NameTable.ContainsKey(index))
                {
                    if (arrayIndexes[arrayName].Add(Names.NameTable[index]))
                    {
                        // we haven't seen this index before, so re-mark indexes
                        Mark(arrayIndexes[arrayName]);
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

    }
}
