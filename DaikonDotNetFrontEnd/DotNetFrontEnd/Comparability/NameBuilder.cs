using EmilStefanov;
using Microsoft.Cci;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using DotNetFrontEnd;
using DotNetFrontEnd.Contracts;

namespace Comparability
{

  public class NameBuilder : CodeVisitor
  {
    public INamedTypeDefinition Type { get; private set; }
    public TypeManager TypeManager { get; private set; }
    public Dictionary<IExpression, string> NameTable { get; private set; }
    public HashSet<IExpression> StaticNames { get; private set; }
    public Dictionary<IExpression, HashSet<IExpression>> NamedChildren { get; private set; }
    public Dictionary<IExpression, IExpression> Parent { get; private set; }
    public IMetadataHost Host { get; private set; }
    public HashSet<IReturnStatement> AnonymousDelegateReturns { get; private set; }

    /// <summary>
    /// Map from type to field, property, and methods referenced in <code>Type</code>. 
    /// </summary>
    public Dictionary<ITypeReference, HashSet<IExpression>> InstanceExpressions { get; private set; }

    /// <summary>
    /// Map from instance expressions to their respective types.
    /// </summary>
    public Dictionary<IExpression, ITypeReference> InstanceExpressionsReferredTypes { get; private set; }

    private int methodCallCnt = 0;

    [ContractInvariantMethod]
    private void ObjectInvariants()
    {
      Contract.Invariant(methodCallCnt >= 0);
      Contract.Invariant(Host != null);
      Contract.Invariant(Type != null);
      Contract.Invariant(NamedChildren != null);
      Contract.Invariant(NameTable != null);
      Contract.Invariant(StaticNames != null);
      Contract.Invariant(Parent != null);
      Contract.Invariant(AnonymousDelegateReturns != null);
      Contract.Invariant(InstanceExpressionsReferredTypes != null);
      Contract.Invariant(InstanceExpressions != null);

      Contract.Invariant(Contract.ForAll(StaticNames, n => NameTable.ContainsKey(n)));
    }

    public NameBuilder(INamedTypeDefinition type, TypeManager typeManager)
    {
      Contract.Requires(type != null);
      Contract.Requires(typeManager != null);
      Contract.Ensures(Type == type);
      Contract.Ensures(TypeManager == typeManager);
      Contract.Ensures(Host == typeManager.Host);

      Type = type;
      TypeManager = typeManager;
      Host = typeManager.Host;
      StaticNames = new HashSet<IExpression>();
      InstanceExpressions = new Dictionary<ITypeReference, HashSet<IExpression>>();
      InstanceExpressionsReferredTypes = new Dictionary<IExpression, ITypeReference>();
      NameTable = new Dictionary<IExpression, string>();
      NamedChildren = new Dictionary<IExpression, HashSet<IExpression>>();
      Parent = new Dictionary<IExpression, IExpression>();
      AnonymousDelegateReturns = new HashSet<IReturnStatement>();
    }

    [Pure]
    public IEnumerable<string> Names(IEnumerable<IExpression> exprs)
    {
      Contract.Requires(exprs != null);
      Contract.Ensures(Contract.ForAll(Contract.Result<IEnumerable<string>>(), n => NameTable.ContainsValue(n)));
      foreach (var e in exprs)
      {
        if (NameTable.ContainsKey(e))
        {
          yield return NameTable[e];
        }
      }
    }

    [Pure]
    public HashSet<string> NamesForType(ITypeReference type, HashSet<IExpression> exprs)
    {
      Contract.Requires(type != null);
      Contract.Requires(exprs != null);
      Contract.Requires(InstanceExpressions.ContainsKey(type));
      Contract.Ensures(Contract.ForAll(Contract.Result<HashSet<string>>(), n => NameTable.ContainsValue(n)));

      return new HashSet<string>(Names(InstanceExpressions[type].Intersect(exprs)));
    }

    private void AddInstanceExpr(ITypeReference type, IExpression expr)
    {
      Contract.Requires(type != null);
      Contract.Requires(expr != null);
      Contract.Ensures(InstanceExpressions.ContainsKey(type));
      Contract.Ensures(InstanceExpressions[type].Contains(expr));
      Contract.Ensures(InstanceExpressionsReferredTypes.ContainsKey(expr));
      Contract.Ensures(InstanceExpressionsReferredTypes[expr] == type);

      if (!InstanceExpressions.ContainsKey(type))
      {
        InstanceExpressions.Add(type, new HashSet<IExpression>());
      }
      InstanceExpressions[type].Add(expr);

      if (!InstanceExpressionsReferredTypes.ContainsKey(expr))
      {
        InstanceExpressionsReferredTypes.Add(expr, type);
      }
      else
      {
        Debug.Assert(InstanceExpressionsReferredTypes[expr] == type);
      }
    }

    /// <summary>
    /// Associate <code>name</code> with <code>expression</code>.
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="name"></param>
    private void TryAdd(IExpression expression, string name)
    {
      Contract.Requires(expression != null);
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      Contract.Ensures(NameTable.ContainsKey(expression));
      Contract.Ensures(NameTable[expression].Equals(name));

      if (NameTable.ContainsKey(expression))
      {
        Contract.Assume(name.Equals(NameTable[expression]),
            "Expression already exists in table with different name. Table: " + NameTable[expression] + " New: " + name);
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
    [Pure]
    public HashSet<string> ThisNames()
    {
      Contract.Ensures(Contract.Result<HashSet<string>>() != null);
      Contract.Ensures(Contract.ForAll(Contract.Result<HashSet<string>>(), n => NameTable.ContainsValue(n)));
      return InstanceNames(Type);
    }

    /// <summary>
    /// Returns names that refer to any type.
    /// </summary>
    /// <returns>names that refer to any type.</returns>
    [Pure]
    public HashSet<string> InstanceNames()
    {
      Contract.Ensures(Contract.Result<HashSet<string>>() != null);
      Contract.Ensures(Contract.ForAll(Contract.Result<HashSet<string>>(), n => NameTable.ContainsValue(n)));

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

    [Pure]
    public HashSet<string> InstanceNames(INamedTypeDefinition type)
    {
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<HashSet<string>>() != null);
      Contract.Ensures((!InstanceExpressions.ContainsKey(type)).Implies(Contract.Result<HashSet<string>>().Count == 0));
      Contract.Ensures(Contract.ForAll(Contract.Result<HashSet<string>>(), n => NameTable.ContainsValue(n)));

      return InstanceExpressions.ContainsKey(type) ?
          new HashSet<string>(InstanceExpressions[type].Select(x => NameTable[x])) :
          new HashSet<string>();
    }

    private void AddChildren(IExpression parent, params IExpression[] exprs)
    {
      Contract.Requires(parent != null);
      Contract.Requires(exprs != null);

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

      if (!Parent.ContainsKey(thisRef))
      {
        Parent.Add(thisRef, null);
      }
    }

    public override void Visit(IBinaryOperation op)
    {
      AddChildren(op, op.LeftOperand, op.RightOperand);
    }

    public override void Visit(IUnaryOperation op)
    {
      AddChildren(op, op.Operand);
    }

    public override void Visit(IAssignment op)
    {
      AddChildren(op, op.Source, op.Target);
      ResolveEnum(op.Target, op.Source);
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
      Contract.Requires(outer != null);
      Contract.Requires(definition != null);

      if (definition is IParameterDefinition)
      {
        var paramName = ((IParameterDefinition)definition).Name.Value;
        Contract.Assume(!string.IsNullOrWhiteSpace(paramName));
        TryAdd(outer, paramName);
      }
      else if (definition is IPropertyDefinition)
      {
        var name = ((IPropertyDefinition)definition).Name.Value;
        Contract.Assume(!string.IsNullOrWhiteSpace(name));
        TryAdd(outer, name);
      }
      else if (definition is IFieldReference)
      {
        var def = ((IFieldReference)definition).ResolvedField;

        if (!def.Attributes.Any(a => TypeManager.IsCompilerGenerated(def)))
        {
          if (def.IsStatic)
          {
            var container = def.ContainingType.ResolvedType;
            // The front-end uses reflection-style names for inner types, need to be consistent here
            var name = string.Join(".", TypeHelper.GetTypeName(container, NameFormattingOptions.UseReflectionStyleForNestedTypeNames), def.Name.Value);
            TryAdd(outer, name);
            AddInstanceExpr(container, outer);
            StaticNames.Add(outer);
          }
          else
          {
            Contract.Assume(instance != null, "Non-static field reference '" + def.Name + "' has no provided instance");
            if (NameTable.ContainsKey(instance))
            {
              var name = NameTable[instance] + "." + def.Name;
              TryAdd(outer, name);
              AddInstanceExpr(Type, outer);
            }
            else
            {
              // NO OP (we aren't tracking the name of the instance)
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
          // NO OP (we aren't tracking the name of the instance)
        }
      }
      else if (definition is ILocalDefinition)
      {
        var def = (ILocalDefinition)definition;
        TryAdd(outer, "<local>" + def.Name.Value);
      }
      else if (definition is IAddressDereference)
      {
        // NO OP
      }
      else
      {
        throw new NotSupportedException("Comparability: Unexpected bundled type " + definition.GetType().Name);
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
      Contract.Requires(enumExpr != null);
      Contract.Requires(constantExpr != null);

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
          AddInstanceExpr(targetType, constantExpr);
          StaticNames.Add(constantExpr);
        }
        else
        {
          // Enum is defined in another assembly
          var enumType = TypeManager.ConvertAssemblyQualifiedNameToType(
            TypeManager.ConvertCCITypeToAssemblyQualifiedName(targetType)).GetSingleType;
          Contract.Assume(enumType.IsEnum, "CCI enum type resolved to non-enum type");
          var name = string.Join(".", enumType.FullName, enumType.GetEnumName(constant.Value));
          TryAdd(constantExpr, name);
        }
      }
    }
    /// <summary>
    /// Returns true if <code>method</code> is a setter
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    /// <remarks>MemberHelper.IsSetter requires that the method be public</remarks>
    public static bool IsSetter(IMethodDefinition method)
    {
      Contract.Requires(method != null);
      Contract.Ensures(Contract.Result<bool>() == (method.IsSpecialName && method.Name.Value.StartsWith("set_")));
      return method.IsSpecialName && method.Name.Value.StartsWith("set_");
    }

    /// <summary>
    /// Returns true if <code>method</code> is a getter
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    /// <remarks>MemberHelper.IsGetter requires that the method be public</remarks>
    public static bool IsGetter(IMethodDefinition method)
    {
      Contract.Requires(method != null);
      Contract.Ensures(Contract.Result<bool>() == (method.IsSpecialName && method.Name.Value.StartsWith("get_")));
      return method.IsSpecialName && method.Name.Value.StartsWith("get_");
    }

    public override void Visit(IMethodCall call)
    {
      var receiver = call.ThisArgument;
      var callee = call.MethodToCall.ResolvedMethod;

      if (callee is Dummy || callee.Name is Dummy)
      {
        return;
      }
      else if (NameTable.ContainsKey(call))
      {
        // TODO ##: can call occur more than once / be referentially equal?
        return;
      }

      var calleeName = callee.Name.Value;
      Contract.Assume(!string.IsNullOrWhiteSpace(calleeName));

      string name = null;
      if (!call.IsStaticCall && NameTable.ContainsKey(receiver))
      {
        if (callee.ParameterCount == 0)
        {
          name = NameTable[call.ThisArgument] + "." +
                           (IsGetter(callee) ? calleeName.Substring("get_".Length) : calleeName + "()");

        }
        else if (IsSetter(callee))
        {
          name = NameTable[call.ThisArgument] + "." + calleeName.Substring("set_".Length);
        }

        Parent.Add(call, call.ThisArgument);
        // propogate the instance information
        if (InstanceExpressionsReferredTypes.ContainsKey(receiver))
        {
          AddInstanceExpr(InstanceExpressionsReferredTypes[receiver], call);
        }
      }

      if (name == null)
      {
        // Assign a unique generated name (required for return value comparability)
        name = "<method>" + calleeName + "__" + methodCallCnt;
        methodCallCnt++;
      }

      TryAdd(call, name);
    }

    public override void Visit(IVectorLength length)
    {
      if (NameTable.ContainsKey(length.Vector))
      {
        TryAdd(length, NameTable[length.Vector] + ".Length");
        Parent.Add(length, length.Vector);
      }
    }

    public override void Visit(ISwitchStatement expr)
    {
      var exprType = expr.Expression.Type.ResolvedType;

      if (exprType.IsEnum)
      {
        foreach (var c in expr.Cases)
        {
          ResolveEnum(expr.Expression, c.Expression);
        }
      }
    }

    public override void Visit(IAnonymousDelegate del)
    {
      foreach (var r in del.Body.Statements.Where(s => s is IReturnStatement))
      {
        AnonymousDelegateReturns.Add((IReturnStatement)r);
      }
    }
  }
}
