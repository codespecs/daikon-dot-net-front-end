namespace PureMethods
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;


  public interface MyInterface
  {
      string MethodA();
      string MethodB();
      string PropertyA { get; }
      string PropertyB { get; }
  }

  public interface AnotherInterface
  {
      string MethodA();
  }

  public class InterfaceImplementor : MyInterface, AnotherInterface
  {
     
      string AnotherInterface.MethodA()
      {
          return "AnotherInterface.MethodA()";
      }

      public string MethodA()
      {
          return "MethodA()";
      }

      string MyInterface.MethodB()
      {
          return "MyInterface.MethodB()";
      }

      public string PropertyA { get { return "PropertyA"; } }
      string MyInterface.PropertyB { get { return "MyInterface.PropertyB"; } } 
  }


  public class PureMethods
  {
    public static int StaticPureMethod1() { return 0; }
    public static string StaticPureMethod2() { return "0"; }

    public static void Main(String[] args)
    {
      PrintHelloWorld();
      A a = new A(6);
      a.This.printN();
      var xx = new GenericAs<A>();
      xx.Add(1);
      xx.Add(2);

      var y = new InterfaceImplementor();
      (new PureMethods()).DoInterface(y);
      (new PureMethods()).DoInterface2(y);
      (new PureMethods()).DoClass(y);
      Console.WriteLine("Done");
    }

    public void DoInterface(MyInterface myInterface)
    {
        Console.WriteLine(myInterface.MethodA() + " Expected: MethodA()");
        Console.WriteLine(myInterface.MethodB() + " Expected: MyInterface.MethodB()");
    }

    public void DoInterface2(AnotherInterface anotherInterface)
    {
        Console.WriteLine(anotherInterface.MethodA() + " Expected: AnotherInterface.MethodA()");
    }

    public void DoClass(InterfaceImplementor implementor)
    {
        Console.WriteLine(implementor.MethodA() + " Expected: MethodA()");
        Console.WriteLine(((MyInterface)implementor).MethodB() + " Expected: MyInterface.MethodB()");
        Console.WriteLine(implementor.PropertyA + " Expected: PropertyA");
        
    }

    public static void PrintHelloWorld()
    {
      Console.WriteLine("Hello World");
    }
  }

  public class GenericAs<T> where T : A
  {
      private HashSet<A> genericSet = new HashSet<A>();

      public int Size
      {
        get { return genericSet.Count(); }
      }

      public void Add(int x)
      {
        genericSet.Add(new A(x));
      }
  }

  public class A
  {
    int n;
    List<int> genericList = new List<int>();
    public A(int n)
    {
      this.n = n;
    }

    public void printN()
    {
      genericList.Add((int)n);
      Console.WriteLine(this.n);
    }

    public int NPlusOne
    {
        get { return this.n+1; }
    }

    public A This
    {
      get { return this; }
    }

    public int PureMethod1() { return 0; }
    public string PureMethod2() { return "0"; }
    public int PureMethod3() { return this.n; }
    public A PureMethod4() { return this; }
  }
}
