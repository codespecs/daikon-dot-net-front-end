namespace PureMethods
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;

  public class PureMethods
  {
    public static int StaticPureMethod1() { return 0; }
    public static string StaticPureMethod2() { return "0"; }

    public static void Main(String[] args)
    {
      PrintHelloWorld();
      A a = new A(6);
      a.printN();
    }

    public static void PrintHelloWorld()
    {
      Console.WriteLine("Hello World");
    }
  }

  public class A
  {
    int n;
    public A(int n)
    {
      this.n = n;
    }

    public void printN()
    {
      Console.WriteLine(this.n);
    }

    public int PureMethod1() { return 0; }
    public string PureMethod2() { return "0"; }
    public int PureMethod3() { return this.n; }
    public A PureMethod4() { return this; }
  }
}
