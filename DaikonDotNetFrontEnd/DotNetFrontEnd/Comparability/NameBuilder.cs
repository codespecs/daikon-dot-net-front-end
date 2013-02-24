using EmilStefanov;
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
        public Dictionary<IExpression, HashSet<IExpression>> NamedChildren { get; private set; }
        public Dictionary<IExpression, IExpression> Parent { get; private set; }
        public IMetadataHost Host { get; private set; }

        public HashSet<IExpression> InstanceExpressions { get; private set; }
       
        public NameBuilder(INamedTypeDefinition type, IMetadataHost host)
        {
            Type = type;
            Host = host;
            InstanceExpressions = new HashSet<IExpression>();
            NameTable = new Dictionary<IExpression, string>();
            NamedChildren = new Dictionary<IExpression, HashSet<IExpression>>();
            Parent = new Dictionary<IExpression, IExpression>();
        }

        public HashSet<string> InstanceNames
        {
            get
            {
                return new HashSet<string>(InstanceExpressions.Select(x => NameTable[x]));
            }
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

        public override void Visit(IMethodBody addition)
        {
            base.Visit(addition);
        }

        public override void Visit(IMethodDefinition addition)
        {
            base.Visit(addition);
        }

        public override void Visit(IThisReference thisRef)
        {
            NameTable.Add(thisRef, "this");
            InstanceExpressions.Add(thisRef);
            Parent.Add(thisRef, null);
        }

        public override void Visit(IBinaryOperation op)
        {
            AddChildren(op, op.LeftOperand, op.RightOperand);
        }

        public override void Visit(IAssignment op)
        {
            AddChildren(op, op.Source, op.Target);
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
            if (bounded.Definition is IParameterDefinition)
            {
                NameTable.Add(bounded, ((IParameterDefinition)bounded.Definition).Name.Value);
                Parent.Add(bounded, bounded.Instance);
            }
            else if (bounded.Definition is IFieldReference)
            {
                var def = ((IFieldReference)bounded.Definition);

                if (!def.IsStatic &&
                    !def.ResolvedField.Attributes.Any(a => IsCompilerGenerated(def.ResolvedField)))
                {

                    if (NameTable.ContainsKey(bounded.Instance))
                    {
                        NameTable.Add(bounded, NameTable[bounded.Instance] + "." + def.ResolvedField.Name);
                        InstanceExpressions.Add(bounded);
                        Parent.Add(bounded, bounded.Instance);
                    }
                    else
                    {
                        Console.WriteLine("Skip field " + def.ResolvedField.Name);
                    }
                }
            }
            else if (bounded.Definition is ILocalDefinition)
            {
                NameTable.Add(bounded, "<local>" + ((ILocalDefinition)bounded.Definition).Name.Value);
            }
            else
            {
                Console.WriteLine("Miss (Target): " + bounded.Definition.GetType().Name);
            }
        }

        public override void Visit(IArrayIndexer arrayIndexer)
        {
            if (arrayIndexer.Indices.Count() == 1 && NameTable.ContainsKey(arrayIndexer.IndexedObject))
            {
                var arrayName = NameTable[arrayIndexer.IndexedObject];
                NameTable.Add(arrayIndexer, arrayName + "[..]");
            }
        }

        public override void Visit(IBoundExpression bounded)
        {
            if (bounded.Definition is IParameterDefinition)
            {
                NameTable.Add(bounded, ((IParameterDefinition) bounded.Definition).Name.Value);
            }
            else if (bounded.Definition is IFieldReference)
            {
                var def = ((IFieldReference)bounded.Definition);
                if (!def.IsStatic &&
                    !def.ResolvedField.Attributes.Any(a => IsCompilerGenerated(def.ResolvedField)))
                {

                    if (NameTable.ContainsKey(bounded.Instance))
                    {
                        NameTable.Add(bounded, NameTable[bounded.Instance] + "." + def.ResolvedField.Name);
                        InstanceExpressions.Add(bounded);
                    }
                    else
                    {
                        Console.WriteLine("Skip field " + def.ResolvedField.Name);
                    }
                }
            }
            else if (bounded.Definition is ILocalDefinition)
            {
                NameTable.Add(bounded, "<local>" + ((ILocalDefinition)bounded.Definition).Name.Value);
            }
            else
            {
                Console.WriteLine("Miss (Bound): " + bounded.Definition.GetType().Name);
            }
        }

        public override void Visit(IMethodCall call)
        {
            if (!call.IsStaticCall && call.MethodToCall.ParameterCount == 0 && NameTable.ContainsKey(call.ThisArgument))
            {
                NameTable.Add(call, NameTable[call.ThisArgument] + "." + call.MethodToCall.Name + "()");
                InstanceExpressions.Add(call);
                Parent.Add(call, call.ThisArgument);
            }
        }

    }
}
