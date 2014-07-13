﻿
namespace Celeriac
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;
  using System.Collections.ObjectModel;
  using System.Diagnostics;
  using System.Diagnostics.Contracts;
  using Celeriac.Contracts;
    using System.Diagnostics.CodeAnalysis;

  /// <summary>
  /// Describes the possible ways a single type could be declared. Operates as a union type over
  /// either a single type (the standard case), or a list of types. The list of types is appropriate 
  /// for when the type is generic and has multiple constraints, then each element in the list will
  /// be one of the contraints
  /// </summary>
  public class CeleriacTypeDeclaration
  {
    /// <summary>
    /// Single type of the declaration
    /// </summary>
    private readonly Type type;

    /// <summary>
    /// The List of types for the delcaration
    /// </summary>
    private readonly List<Type> list;

    /// <summary>
    /// The possible formats this declaration type could take on.
    /// </summary>
    internal enum DeclarationType { SingleClass, ListOfClasses }

    /// <summary>
    /// Internal record for what type of declaration this is
    /// </summary>
    private readonly DeclarationType declarationType;

    [ContractInvariantMethod]
    [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Called by CC framework")]
    private void ObjectInvariant()
    {
        Contract.Invariant((declarationType == DeclarationType.SingleClass).Implies(type != null));
        Contract.Invariant((declarationType == DeclarationType.ListOfClasses).Implies(list != null));
        
        Contract.Invariant(list == null || type == null);
        Contract.Invariant(list != null || type != null);
    }

    /// <summary>
    /// Create a new type delcaration holding a single type
    /// </summary>
    /// <param name="t">The single type to declare</param>
    public CeleriacTypeDeclaration(Type type)
    {
      Contract.Requires(type != null);
      Contract.Ensures(this.type == type);
      Contract.Ensures(this.declarationType == DeclarationType.SingleClass);
      this.type = type;
      this.declarationType = DeclarationType.SingleClass;
    }

    /// <summary>
    /// Create a new type declaration for a given list of types
    /// </summary>
    /// <param name="list">The list of types for the type declaration</param>
    public CeleriacTypeDeclaration(Collection<Type> list)
    {
      Contract.Requires(list != null);
      Contract.Requires(list.Count > 0);
      Contract.Requires(Contract.ForAll<Type>(list, t => t != null));
      Contract.Ensures(this.GetDeclarationType == DeclarationType.ListOfClasses);

      this.list = new List<Type>(list.Count);
      this.list.AddRange(list);
      this.declarationType = DeclarationType.ListOfClasses;
    }

    internal DeclarationType GetDeclarationType
    {
      [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Referenced from CCs")]
      get
      {
        return declarationType;
      }
    }

    /// <summary>
    /// Get the single type for this declaration.
    /// </summary>
    /// <returns>The single type this declaration describes</returns>
    /// <exception cref="InvalidOperationException">When this declaration was not created
    /// with a single class.</exception>
    internal Type GetSingleType
    {
      get
      {
        Contract.Requires(GetDeclarationType == DeclarationType.SingleClass);
        Contract.Ensures(Contract.Result<Type>() != null);
        return this.type;
      }
    }

    /// <summary>
    /// Get a list of all types described by this declaration.
    /// </summary>
    /// <returns>A list containing the single type if this declaration was created with a single
    /// type, or else the list of types if this declaration was created with such a list.</returns>
    /// <exception cref="InvalidOperationException">Occurs when the declaration was created
    /// with a method other than single type or list.</exception>
    public Collection<Type> GetAllTypes
    {
      get
      {
        Contract.Ensures(Contract.Result<Collection<Type>>() != null);
        Contract.Ensures(Contract.ForAll(Contract.Result<Collection<Type>>(), t => t != null));

        List<Type> resultList = new List<Type>();
        if (this.declarationType == DeclarationType.ListOfClasses)
        {
          resultList.AddRange(this.list);
        }
        else if (this.declarationType == DeclarationType.SingleClass)
        {
          resultList.Add(this.type);
        }
        return new Collection<Type>(resultList);
      }
    }
  }
}
