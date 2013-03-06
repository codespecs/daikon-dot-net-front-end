using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics.Contracts;

namespace DotNetFrontEnd.Contracts
{
  public static class ContractExtensions
  {
    [Pure]
    public static bool Implies(this bool antecedent, bool consequent)
    {
      Contract.Ensures(Contract.Result<bool>() == (!antecedent || consequent));
      return !antecedent || consequent;
    }

    [Pure]
    public static bool OneOf<T>(this T x, T first, params T[] rest)
    {
      Contract.Requires(x != null);
      Contract.Requires(rest != null);
      Contract.Ensures(Contract.Result<bool>() == (x.Equals(first) || Contract.Exists(rest, e => x.Equals(e))));
      return x.Equals(first) || rest.Any(e => x.Equals(e));
    }

    // The type parameter K is inferred automatically from type of dictionary
    [Pure, System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter")]
    public static bool SetEquals<K,V>(this Dictionary<K, V>.KeyCollection keys, ISet<K> set)
    {
      Contract.Requires(keys != null);
      Contract.Requires(set != null);
      Contract.Ensures(Contract.Result<bool>() ==
          Contract.ForAll(keys, k => set.Contains(k)) && Contract.ForAll(set, k => keys.Contains(k)));
      return keys.All(k => set.Contains(k)) && set.All(k => keys.Contains(k));
    }
  }
}
