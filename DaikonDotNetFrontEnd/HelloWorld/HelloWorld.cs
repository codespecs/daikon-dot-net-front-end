namespace HelloWorld
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;
  using System.Diagnostics.Contracts;

  public class HelloWorld
  {
    string greeting = "Hello World";

    public HelloWorld()
    {
      this.greeting = "Hello World";
    }

    public static void Main(String[] args)
    {
      HelloWorld hw = new HelloWorld();
      Console.WriteLine(hw.Greeting);
    }

    public string Greeting
    {
      get { return this.greeting; }
    }
  }
}
