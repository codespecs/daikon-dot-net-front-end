namespace AdvancedCollections
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;

  /// <summary>
  /// Perform tests on a variety of advanced collections.
  /// </summary>
  public class AdvancedCollections
  {
    public static void Main(String[] args)
    {
      SetTest();
    }

    /// <summary>
    /// Add various elements to a set and print its contents to screen.
    /// </summary>
    private static void SetTest()
    {
      HashSet<string> set = new HashSet<string>();
      AddToSet(set, "duck");
      AddToSet(set, "duck");
      AddToSet(set, "goose");
      AddToSet(set, "geese");
      DescribeSet(set);
    }

    private static void DescribeSet(HashSet<string> set)
    {
      Console.Write("[");
      foreach (string str in set)
      {
        Console.Write(" " + str);
      }
      Console.WriteLine(" ]");
    }

    public static HashSet<string> AddToSet(HashSet<string> set, string stringToAdd)
    {
      set.Add(stringToAdd);
      return set;
    }
  }
}
