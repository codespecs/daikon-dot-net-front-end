﻿using EmilStefanov;
using Microsoft.Cci;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Comparability
{

    public class NameBuilder : CodeVisitor
    {
        public INamedTypeDefinition Type { get; private set; }
        public Dictionary<IExpression, string> NameTable { get; private set; }
        public HashSet<IExpression> StaticNames { get; private set; }
        public Dictionary<IExpression, HashSet<IExpression>> NamedChildren { get; private set; }
        public Dictionary<IExpression, IExpression> Parent { get; private set; }
        public IMetadataHost Host { get; private set; }

        /// <summary>
        /// Map from type to field, property, and methods referenced in <code>Type</code>. 
        /// </summary>
        public Dictionary<ITypeReference, HashSet<IExpression>> InstanceExpressions;
        
        /// <summary>
        /// Map from instance expressions to their respective types.
        /// </summary>
        public Dictionary<IExpression, ITypeReference> InstanceExpressionsReferredTypes;
       
        public NameBuilder(INamedTypeDefinition type, IMetadataHost host)
        {
            Type = type;
            Host = host;
            StaticNames = new HashSet<IExpression>();
            InstanceExpressions = new Dictionary<ITypeReference, HashSet<IExpression>>();
            InstanceExpressionsReferredTypes = new Dictionary<IExpression, ITypeReference>();
            NameTable = new Dictionary<IExpression, string>();
            NamedChildren = new Dictionary<IExpression, HashSet<IExpression>>();
            Parent = new Dictionary<IExpression, IExpression>();
        }

        public IEnumerable<string> Names(IEnumerable<IExpression> exprs)
        {
            foreach (var e in exprs)
            {
                if (NameTable.ContainsKey(e))
                {
                    yield return NameTable[e];
                }
            }
        }

        public HashSet<string> NamesForType(ITypeReference type, HashSet<IExpression> exprs)
        {
            return new HashSet<string>(Names(InstanceExpressions[type].Intersect(exprs)));
        }

        private void AddInstanceExpr(ITypeReference type, IExpression expr)
        {
            if (!InstanceExpressions.ContainsKey(type))
            {
                InstanceExpressions.Add(type, new HashSet<IExpression>());
            }
            InstanceExpressions[type].Add(expr);
            InstanceExpressionsReferredTypes.Add(expr, type);
        }

        private void TryAdd(IExpression expression, string name)
        {
            if (NameTable.ContainsKey(expression))
            {
                if (!name.Equals(NameTable[expression]))
                {
                    throw new Exception("Expression already exists in table with different name. Table: " + NameTable[expression] + " New: " + name); 
                }
                else
                {
                    // NO OP (when would this occur?)
                }
            }
            else
            {
                NameTable.Add(expression, name);
            }
        }

        /// <summary>
        /// Returns names that refer to this <code>Type</code>.
        /// </summary>
        /// <returns>names that refer to this <code>Type</code>.</returns>
        public HashSet<string> ThisNames()
        {
            return InstanceNames(Type);
        }

        /// <summary>
        /// Returns names that refer to any type.
        /// </summary>
        /// <returns>names that refer to any type.</returns>
        public HashSet<string> InstanceNames()
        {
            HashSet<string> result = new HashSet<string>();
            foreach (var typeRef in InstanceExpressions.Keys)
            {
                if (typeRef is INamedTypeDefinition)
                {
                    result.UnionWith(InstanceNames((INamedTypeDefinition)typeRef));
                }
            }
            return result;
        }

        public HashSet<string> InstanceNames(INamedTypeDefinition type)
        {
            return InstanceExpressions.Count == 0 ?
                new HashSet<string>() :
                new HashSet<string>(InstanceExpressions[type].Select(x => NameTable[x]));
        }

        private void AddChildren(IExpression parent, params IExpression[] exprs)
        {
            var children = exprs.Where(x => NameTable.ContainsKey(x));
            if (children.Any())
            {
                if (!NamedChildren.ContainsKey(parent))
                {
                    NamedChildren.Add(parent, new HashSet<IExpression>(children));
                }
                else
                {
                    NamedChildren[parent].UnionWith(children);
                }
            }
        }

        public override void Visit(IThisReference thisRef)
        {
            TryAdd(thisRef, "this");
            AddInstanceExpr(Type, thisRef);
            Parent.Add(thisRef, null);
        }

        public override void Visit(IBinaryOperation op)
        {
            AddChildren(op, op.LeftOperand, op.RightOperand);
        }

        public override void Visit(IAssignment op)
        {
            AddChildren(op, op.Source, op.Target);
            ResolveEnum(op.Target, op.Source);
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


        public override void Visit(ITargetExpression bounded)
        {
            HandleBundle(bounded, bounded.Definition, bounded.Instance);
        }

        public override void Visit(IBoundExpression bounded)
        {
            HandleBundle(bounded, bounded.Definition, bounded.Instance);
        }

        private void HandleBundle(IExpression outer, object definition, IExpression instance)
        {
            if (definition is IParameterDefinition)
            {
                TryAdd(outer, ((IParameterDefinition)definition).Name.Value);
            }
            else if (definition is IFieldReference)
            {
                var def = ((IFieldReference)definition);

                if (!def.ResolvedField.Attributes.Any(a => IsCompilerGenerated(def.ResolvedField)))
                {
                    if (def.IsStatic)
                    {
                        var container = def.ContainingType.ResolvedType;
                        // The front-end uses reflection-style names for inner types, need to be consistent here
                        var name = string.Join(".", TypeHelper.GetTypeName(container, NameFormattingOptions.UseReflectionStyleForNestedTypeNames), def.ResolvedField.Name);
                        TryAdd(outer, name);
                        // Console.WriteLine("Add static field " + name); 
                        AddInstanceExpr(container, outer);
                        StaticNames.Add(outer);
                    }
                    else
                    {
                        if (NameTable.ContainsKey(instance))
                        {
                            var name = NameTable[instance] + "." + def.ResolvedField.Name;
                            TryAdd(outer, name);
                            // Console.WriteLine("Add field " + name); 
                            AddInstanceExpr(Type, outer);
                        }
                        else
                        {
                            Console.WriteLine("Skip field (instance not named): " + def.ResolvedField.Name);
                        }
                    }
                }
            }
            else if (definition is IArrayIndexer)
            {
                var def = (IArrayIndexer)definition;
                if (NameTable.ContainsKey(def.IndexedObject))
                {
                    TryAdd(outer, NameTable[def.IndexedObject] + "[..]");

                    // propogate instance expression information
                    if (InstanceExpressionsReferredTypes.ContainsKey(def.IndexedObject))
                    {
                        AddInstanceExpr(InstanceExpressionsReferredTypes[def.IndexedObject], outer);
                    }
                }
                else
                {
                    Console.WriteLine("Skip array indexer (indexed object not named)");
                }
            }
            else if (definition is ILocalDefinition)
            {
                TryAdd(outer, "<local>" + ((ILocalDefinition)definition).Name.Value);
            }
            else
            {
                Console.WriteLine("WARNING: unexpected bundled type " + definition.GetType().Name);
            }
        }

        public override void Visit(IArrayIndexer arrayIndexer)
        {
            if (arrayIndexer.Indices.Count() == 1 && NameTable.ContainsKey(arrayIndexer.IndexedObject))
            {
                var arrayName = NameTable[arrayIndexer.IndexedObject];
                TryAdd(arrayIndexer, arrayName + "[..]");
            }
        }

        private void ResolveEnum(IExpression enumExpr, IExpression constantExpr)
        {
            var targetType = enumExpr.Type.ResolvedType;

            if (targetType.IsEnum && constantExpr is ICompileTimeConstant)
            {
                var constant = (ICompileTimeConstant)constantExpr;

                var value = targetType.Fields.FirstOrDefault(f => f.IsCompileTimeConstant && constant.Value.Equals(f.CompileTimeValue.Value));
                if (value != null)
                {
                    // The front-end uses reflection-style names for inner types, need to be consistent here
                    var name = string.Join(".", TypeHelper.GetTypeName(targetType, NameFormattingOptions.UseReflectionStyleForNestedTypeNames), value.Name);
                    TryAdd(constantExpr, name);
                    // Console.WriteLine("Add enum constant " + name);
                    AddInstanceExpr(targetType, constantExpr);
                    StaticNames.Add(constantExpr);
                }
                else
                {
                    Console.WriteLine("WARNING: Could not find enum constant for assignment");
                }
            }
        }

        public override void Visit(IMethodCall call)
        {
            var receiver = call.ThisArgument;

            if (!call.IsStaticCall && call.MethodToCall.ParameterCount == 0 && NameTable.ContainsKey(receiver))
            {               
                TryAdd(call, NameTable[call.ThisArgument] + "." + call.MethodToCall.Name + "()");
                Parent.Add(call, call.ThisArgument);

                // propogate the instance information
                if (InstanceExpressionsReferredTypes.ContainsKey(receiver))
                {
                    AddInstanceExpr(InstanceExpressionsReferredTypes[receiver], call);
                }
            }
        }

    }
}