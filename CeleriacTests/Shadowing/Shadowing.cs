namespace MyTestProject
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;

  public class Shadowing
  {
    public static void Main(String[] args)
    {
      A a = new A();
      B b = new B();
      fooA(a);
      fooA(b);
      fooB(b);
    }

    static void fooA(A a)
    {
      Console.WriteLine(a.publicA);
    }

    static void fooB(B b)
    {
      // Get the shadowed version of public a
      Console.WriteLine(b.publicA);
      b.publicA = ((A)b).publicA;
      Console.WriteLine(b.publicA);
    }
  }

  class A
  {
    private int privateA = 3;
    public int publicA = 4;
  }

  class B : A
  {
    private int privateA = 6;
    public new int publicA = 7;
  }
}
