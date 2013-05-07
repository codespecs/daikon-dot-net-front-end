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
using Celeriac;
using Celeriac.Contracts;

namespace Comparability
{
  /// <summary>
  /// Builds expression names for code elements in a type; these expession names correspond to the names in the
  /// DECLS file.
  /// </summary>
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

    private IMethodDefinition context = null;

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

    /// <summary>
    /// Construct a <see cref="NameBuilder"/> with no name information.
    /// </summary>
    /// <param name="type">The type to construct name information for</param>
    /// <param name="typeManager">Celeriac type information</param>
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

    /// <summary>
    /// Returns the expression names for the given expressions, skipping the unnamed
    /// expressions.
    /// </summary>
    /// <param name="exprs">expressions</param>
    /// <returns>the expression names for the given expressions</returns>
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
        var name = ((IParameterDefinition)definition).Name.Value;

        if (!string.IsNullOrEmpty(name))
        {
          TryAdd(outer, name);
        }
        else
        {
          // NO OP: implicit this of superclass is not named (e.g., for default ctor of subclass)
        }
      }
      else if (definition is IPropertyDefinition)
      {
        var name = ((IPropertyDefinition)definition).Name.Value;
        Contract.Assume(!string.IsNullOrWhiteSpace(name), Context());
        TryAdd(outer, name);
      }
      else if (definition is IFieldReference)
      {
        var field = ((IFieldReference)definition).ResolvedField;

        if (!(field is Dummy) && !field.Attributes.Any(a => TypeManager.IsCompilerGenerated(field)))
        {
          if (field.IsStatic)
          {
            var container = field.ContainingType.ResolvedType;
            // Celeriac uses reflection-style names for inner types, need to be consistent here
            var name = string.Join(".", TypeHelper.GetTypeName(container, NameFormattingOptions.UseReflectionStyleForNestedTypeNames), field.Name.Value);
            TryAdd(outer, name);
            AddInstanceExpr(container, outer);
            StaticNames.Add(outer);
          }
          else
          {
            Contract.Assume(instance != null, "Non-static field reference '" + field.Name + "' has no provided instance; " + Context());
            if (instance != null && NameTable.ContainsKey(instance))
            {
              var name = NameTable[instance] + "." + field.Name;
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
          TryAdd(outer, FormElementsExpression(NameTable[def.IndexedObject]));

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
        TryAdd(arrayIndexer, FormElementsExpression(arrayName));
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
          // Celeriac uses reflection-style names for inner types, need to be consistent here
          var name = string.Join(".", TypeHelper.GetTypeName(targetType, NameFormattingOptions.UseReflectionStyleForNestedTypeNames), value.Name);
          TryAdd(constantExpr, name);
          AddInstanceExpr(targetType, constantExpr);
          StaticNames.Add(constantExpr);
        }
        else
        {
          try
          {
            // Enum is defined in another assembly
            var enumType = TypeManager.ConvertAssemblyQualifiedNameToType(
              TypeManager.ConvertCCITypeToAssemblyQualifiedName(targetType)).GetSingleType;
            Contract.Assume(enumType.IsEnum, "CCI enum type resolved to non-enum type");
            var name = string.Join(".", enumType.FullName, enumType.GetEnumName(constant.Value));
            TryAdd(constantExpr, name);
          }
          catch (Exception)
          {
            // Issue #84: debug errors locating enums in other assemblies
          }
        }
      }
    }

    /// <summary>
    /// Returns true if <code>method</code> is a setter
    /// </summary>
    /// <param name="method">Method to test</param>
    /// <returns>True if the given method is an auto-generated setter, otherwise false</returns>
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

    /// <summary>
    /// Returns <c>true</c> if the name is the name of a collection elements expression, that is
    /// it ends with "[..]".
    /// </summary>
    /// <param name="name">the expression name</param>
    /// <returns><c>true</c> if the name is the name of a collection elements expression</returns>
    [Pure]
    public static bool IsElementsExpression(string name)
    {
      Contract.Requires(!string.IsNullOrWhiteSpace(name));
      return name.EndsWith("[..]");
    }

    /// <summary>
    /// Returns the elements expression name for the given collection/array name, i.e., the name with 
    /// [..] added to the end. 
    /// </summary>
    /// <param name="collectionName">the expression name</param>
    /// <returns> the elements expression name for the given collection/array name</returns>
    [Pure]
    public static string FormElementsExpression(string collectionName)
    {
      return collectionName + "[..]";
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

      // Check for indexes into a List
      if (!call.IsStaticCall && NameTable.ContainsKey(receiver))
      {
        foreach (var m in MemberHelper.GetImplicitlyImplementedInterfaceMethods(call.MethodToCall.ResolvedMethod))
        {
          var genericDef = TypeHelper.UninstantiateAndUnspecialize(m.ContainingTypeDefinition);
          if (TypeHelper.TypesAreEquivalent(genericDef, Host.PlatformType.SystemCollectionsGenericIList, true))
          {
            if (m.Name.Value.OneOf("get_Item")){
              name = FormElementsExpression(NameTable[call.ThisArgument]);
            }
          }
        }
      }

      // Check for indexes into a Dictionary
      if (!call.IsStaticCall && NameTable.ContainsKey(receiver))
      {
        var genericDef = TypeHelper.UninstantiateAndUnspecialize(receiver.Type);

        if (TypeHelper.TypesAreEquivalent(genericDef, Host.PlatformType.SystemCollectionsGenericDictionary, true))
        {
          if (callee.Name.Value.OneOf("get_Item"))
          {
            // ISSUE #91: this supports the dictionary[..].Value collection; does not support dictionary.Values
            name = FormElementsExpression(NameTable[call.ThisArgument]) + ".Value";
          }
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

    public override void Visit(IMethodDefinition method)
    {
      context = method;
    }

    private string Context()
    {
      return string.Format("Type: {0} Method: {1}", Type.Name.Value,
                           (context != null) ? context.Name.Value : "<no method>");
    }
  }
}
