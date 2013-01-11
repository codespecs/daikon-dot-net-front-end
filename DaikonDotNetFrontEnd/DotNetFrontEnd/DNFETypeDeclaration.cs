// Describes the possible ways a single type could be declared. Operates as a union type over
// either a single type (the standard case), or a list of types. The list of types is appropriate 
// for when the type is generic and has multiple constraints, then each element in the list will
// be one of the contraints.

namespace DotNetFrontEnd
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;
  using System.Collections.ObjectModel;

  public class DNFETypeDeclaration
  {
    /// <summary>
    /// Single type of the declaration
    /// </summary>
    private Type type;

    /// <summary>
    /// The List of types for the delcaration
    /// </summary>
    private List<Type> list;

    /// <summary>
    /// The possible formats this declaration type could take on.
    /// </summary>
    public enum DeclarationType { SingleClass, ListOfClasses }

    /// <summary>
    /// Internal record for what type of declaration this is
    /// </summary>
    DeclarationType declarationType;

    /// <summary>
    /// Create a new type delcaration holding a single type
    /// </summary>
    /// <param name="t">The single type to declare</param>
    public DNFETypeDeclaration(Type t)
    {
      this.type = t;
      this.declarationType = DeclarationType.SingleClass;
    }

    /// <summary>
    /// Create a new type declaration for a given list of types
    /// </summary>
    /// <param name="list">The list of types for the type declaration</param>
    public DNFETypeDeclaration(List<Type> list)
    {
      this.list = new List<Type>(list.Count);
      list.ForEach(x => this.list.Add(x));
      this.declarationType = DeclarationType.ListOfClasses;
    }

    /// <summary>
    /// Get the format of this delcaration
    /// </summary>
    /// <returns>The declaration type enum value corresponding to the type of declaration this
    /// declaration type was created with.</returns>
    public DeclarationType GetDeclartionType
    {
      get
      {
        return this.declarationType;
      }
    }
    
    /// <summary>
    /// Get the single type for this declaration.
    /// </summary>
    /// <returns>The single type this declaration describes</returns>
    /// <exception cref="InvalidOperationException">When this declaration was not created
    /// with a single class.</exception>
    public Type GetSingleType
    {
      get
      {
        if (this.declarationType != DeclarationType.SingleClass)
        {
          throw new InvalidOperationException("Can'type get a single type on a declaration object" +
              " that isn't a single class.");
        }
        return this.type;
      }
    }

    /// <summary>
    /// Get the list of types for this declaration.
    /// </summary>
    /// <returns>The list of types this delcaration describes</returns>
    /// <exception cref="InvalidOperationException">Occurs when this declaration
    /// was not created with a list of classes.</exception>
    public Collection<Type> GetListOfTypes
    {
      get
      {
        if (this.declarationType != DeclarationType.ListOfClasses)
        {
          throw new InvalidOperationException("Can'type get a list of types on a declaration object" +
              " that isn't a list of types.");
        }
        List<Type> resultList = new List<Type>(this.list.Count);
        list.ForEach(x => resultList.Add(x));
        return new Collection<Type>(resultList);
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
        // With the current two types this will always be true but won't be if new declaration types
        // are added.
        if ((this.declarationType != DeclarationType.ListOfClasses)
          && (this.declarationType != DeclarationType.SingleClass))
        {
          throw new InvalidOperationException("Can'type get a list of types on a declaration object" +
              " that isn't a list of types or a single type.");
        }
        List<Type> resultList = new List<Type>();
        if (this.declarationType == DeclarationType.ListOfClasses)
        {
          this.list.ForEach(x => resultList.Add(x));
        }
        else
        {
          resultList.Add(this.type);
        }
        return new Collection<Type>(resultList);
      }
    }
  }
}
