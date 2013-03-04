namespace Enums
{
  using System;

  public class Enums
  {
    enum Days { Saturday, Sunday, Monday, Tuesday, Wednesday, Thursday, Friday };
    enum BoilingPoints { Celsius = 100, Fahrenheit = 212 };
    [FlagsAttribute]
    enum Colors { Red = 1, Green = 2, Blue = 4, Yellow = 8 };

    public static void Main()
    {
      Console.WriteLine("The days of the week, and their corresponding values in the Days Enum are:");
      PrintEnumType(typeof(Days));

      Console.WriteLine();
      Console.WriteLine("Enums can also be created which have values that represent some meaningful amount.");
      Console.WriteLine("The BoilingPoints Enum defines the following items, and corresponding values:");

      PrintEnumType(typeof(BoilingPoints));

      Colors myColors = Colors.Red | Colors.Blue | Colors.Yellow;
      Console.WriteLine(); 
      PrintColors(myColors);
    }

    private static void PrintColors(Colors myColors)
    {
      Console.WriteLine("myColors holds a combination of colors. Namely: {0}", myColors);
    }

    private static void PrintEnumType(Type weekdays)
    {
      foreach (string s in Enum.GetNames(weekdays))
      {
        string str = formatEnum(s, weekdays);
        Console.WriteLine("{0,-11}= {1}", s, str);
      }
    }

    private static string formatEnum(string s, Type enumType)
    {
      return Enum.Format(enumType, Enum.Parse(enumType, s), "d");
    }
  }
}
