// -----------------------------------------------------------------------
// <copyright file="HelloWorld.cs" company="">
// TODO: Update copyright text.
// </copyright>
// -----------------------------------------------------------------------

namespace HelloWorld
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;

  public class HelloWorld
  {
    public static void Main(String[] args)
    {
      List<int> vals = new List<int>();
      // [ 3, 4, 6, 9, 10, 12, 15, 20 ]
      vals.Add(3);
      Console.WriteLine(intFuncMultipleReturns(vals, 1));
      vals.Add(4);
      Console.WriteLine(intFuncMultipleReturns(vals, 2));


      vals = new List<int>();
      // [ 3, 4, 6, 9, 10, 12, 15, 20 ]
      vals.Add(3);
      voidFuncMultipleReturns(vals, 1);
      vals.Add(4);
      voidFuncMultipleReturns(vals, 2);

      Console.WriteLine(intFuncSimple(true));
      Console.WriteLine(intFuncSimple(false));

      Console.WriteLine(multipleReturnsDelayed(0));
      Console.WriteLine(multipleReturnsDelayed(1));
      Console.WriteLine(multipleReturnsDelayed(-1));
      Console.WriteLine(multipleReturnsDelayed(-20));
      Console.WriteLine(multipleReturnsDelayed(1000));
    }

    private static string multipleReturnsDelayed(int n)
    {
      String sign;
      if (n == 0)
      {
        return "Z";
      }
      else if (n < 0)
      {
        return "N";
      }
      if (n > 100)
      {
        sign = "Big P";
      }
      else if (n > 0 && n < 5)
      {
        sign = "Little P";
      }
      else
      {
        sign = "P";
      }
      sign += ".";
      return sign;
    }

    private static int intFuncSimple(bool returnOne)
    {
      if (returnOne)
      {
        return 1;
      }
      return 0;
    }

    private static int intFuncMultipleReturns(List<Int32> vals, int a)
    {
      for (int i = 0; i < vals.Count; i++)
      {
        if (vals[i] == 4)
        {
          vals = null;
          Console.WriteLine("fizz");
          a = 3;
          return -1;
        }
        if (vals[i] == 3)
        {
          vals[i] = 4;
          Console.WriteLine("buzz");
        }
        Console.WriteLine("none");
        a = 4;
      }
      return -3;
    }

    private static void voidFuncMultipleReturns(List<Int32> vals, int a)
    {
      for (int i = 0; i < vals.Count; i++)
      {
        if (vals[i] == 4)
        {
          vals = null;
          Console.WriteLine("fizz");
          a = 3;
          return;
        }
        if (vals[i] == 3)
        {
          vals[i] = 4;
          Console.WriteLine("buzz");
        }
        Console.WriteLine("none");
        a = 4;
      }
    }
  }
}
