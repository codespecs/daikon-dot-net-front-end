//-----------------------------------------------------------------------
//<copyright file="GenericsTest.cs" company="">
//TODO: Update copyright text.
//</copyright>
//-----------------------------------------------------------------------

namespace Generics
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;

  #region Generic Class Test Code

  class BaseNodeMultiple<T, U>
  {
    T t;
    U u;
    public BaseNodeMultiple(T t, U u)
    {
      this.t = t;
      this.u = u;
    }
    public void printTypes()
    {
      Console.WriteLine(t.GetType());
      Console.WriteLine(u.GetType());
    }
  }

  class Node4<T> : BaseNodeMultiple<T, int>
  {
    public Node4(T t, int i) : base(t, i) { }
  }

  class Node5<T, U> : BaseNodeMultiple<T, U>
  {
    public Node5(T t, U u) : base(t, u) { }
  }

  #endregion

  // type parameter T in angle brackets
  class GenericList<T>
  {
    // The nested class is also generic on T.
    private class Node
    {
      // T used in non-generic constructor.
      public Node(T t)
      {
        next = null;
        data = t;
      }

      private Node next;
      public Node Next
      {
        get { return next; }
        set { next = value; }
      }

      // T as private member data type.
      private T data;

      // T as return type of property.
      public T Data
      {
        get { return data; }
        set { data = value; }
      }
    }

    private Node head;

    // constructor
    public GenericList()
    {
      head = null;
    }

    // T as method parameter type:
    public void AddHead(T t)
    {
      Node n = new Node(t);
      n.Next = head;
      head = n;
    }

    public IEnumerator<T> GetEnumerator()
    {
      Node current = head;

      while (current != null)
      {
        yield return current.Data;
        current = current.Next;
      }
    }

    // The following method returns the data value stored in the last node in
    // the list. If the list is empty, the default value for type T is
    // returned.
    public T GetLast()
    {
      // The value of temp is returned as the value of the method. 
      // The following declaration initializes temp to the appropriate 
      // default value for type T. The default value is returned if the 
      // list is empty.
      T temp = default(T);

      Node current = head;
      while (current != null)
      {
        temp = current.Data;
        current = current.Next;
      }
      return temp;
    }
  }

  class NodeItem<T> where T : System.IComparable<T>, new() { }

  class SpecialNodeItem<T> : NodeItem<T> where T : System.IComparable<T>, new() { }

  #region Generic Delegate Testing Code

  class DelegateTestExampleClass : IComparable<DelegateTestExampleClass>
  {
    public int CompareTo(DelegateTestExampleClass other)
    {
      throw new NotImplementedException();
    }
  }
  delegate void StackEventHandler<T, U>(T sender, U eventArgs);

  class DelegateTestStack<T>
  {
    public class StackEventArgs : System.EventArgs { }
    public event StackEventHandler<DelegateTestStack<T>, StackEventArgs> stackEvent;

    protected virtual void OnStackChanged(StackEventArgs a)
    {
      stackEvent(this, a);
    }
  }

  class DelegateTestClass
  {
    public void HandleStackChange<T>(DelegateTestStack<T> stack,
        DelegateTestStack<T>.StackEventArgs args) { }
  }

  #endregion

  public class GenericsTest
  {
    /// <summary>
    /// Test operation on generic delegates
    /// </summary>
    private static void DelegateTest()
    {
      DelegateTestStack<double> s = new DelegateTestStack<double>();
      DelegateTestClass o = new DelegateTestClass();
      s.stackEvent += o.HandleStackChange;
    }

    /// <summary>
    /// Swap the contents of the two given generic lists
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list1"></param>
    /// <param name="list2"></param>
    private static void Swap<T>(List<T> list1, List<T> list2)
    {
      List<T> list3 = new List<T>(list1);
      list1.Clear();
      list1.AddRange(list2);
      list2.Clear();
      list2.AddRange(list3);
    }

    /// <summary>
    /// Swap the two args if lhs > rhs
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="lhs"></param>
    /// <param name="rhs"></param>
    private static void SwapIfGreater<T>(ref T lhs, ref T rhs) where T : System.IComparable<T>
    {
      T temp;
      if (lhs.CompareTo(rhs) > 0)
      {
        temp = lhs;
        lhs = rhs;
        rhs = temp;
      }
    }

    /// <summary>
    /// Test modification of a generic list
    /// </summary>
    /// <param name="intList1"></param>
    private static void AddToList1(List<int> intList1)
    {
      intList1.Add(1);
      intList1.Add(2);
      intList1.Add(3);
    }

    /// <summary>
    /// Test the default keyword for two differently typed generic lists
    /// </summary>
    private static void DefaultTest()
    {
      // Test with an empty list of integers.
      GenericList<int> gll2 = new GenericList<int>();
      int intVal = gll2.GetLast();
      // The following line displays 0.
      System.Console.WriteLine(intVal);

      // Test with an empty list of strings.
      GenericList<string> gll4 = new GenericList<string>();
      string sVal = gll4.GetLast();
      // The following line displays a blank line.
      System.Console.WriteLine(sVal);
    }
    
    /// <summary>
    /// Test out parameter in generic methods
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <param name="smaller"></param>
    private static void PickSmaller<T>(T a, T b, out T smaller) where T : struct, System.IComparable<T>
    {
      if (a.CompareTo(b) < 0)
      {
        smaller = a;
      }
      else
      {
        smaller = b;
      }
    }

    private static void OutTest()
    {
      int a = 3;
      int b = -3;
      int c;
      PickSmaller<int>(a, b, out c);
      Console.WriteLine("Smaller of {0} and {1} is {2}", a, b, c);
    }

    /// <summary>
    /// Test generic reference parameters
    /// </summary>
    private static void SwapTest()
    {
      List<int> l1 = new List<int>(new int[] { 1, 2, 3 });
      List<int> l2 = new List<int>(new int[] { 4, 5, 6 });
      Console.WriteLine("Before Swap: " + l1[0] + " , " + l2[0]);
      Swap(l1, l2);
      Console.WriteLine("After  Swap: " + l1[0] + " , " + l2[0]);

      int a = 5;
      int b = 4;
      Console.WriteLine("Before Swap: " + a + " , " + b);
      SwapIfGreater(ref a, ref b);
      Console.WriteLine("After  Swap: " + a + " , " + b);
    }

    public static void Main()
    {
      // int is the type argument
      GenericList<int> list = new GenericList<int>();

      for (int x = 0; x < 10; x++)
      {
        list.AddHead(x);
      }

      foreach (int i in list)
      {
        System.Console.Write(i + " ");
      }
      System.Console.WriteLine("\nDone");

      // Declare a list of type string
      GenericList<string> list2 = new GenericList<string>();

      // Declare a list of type ExampleClass
      GenericList<DelegateTestExampleClass> list3 = new GenericList<DelegateTestExampleClass>();

      Node4<string> n4 = new Node4<string>("hello ", 4);
      Node5<Node4<string>, Node5<int, int>> n5 =
        new Node5<Node4<string>, Node5<int, int>>(
          new Node4<string>("hello", 5), new Node5<int, int>(6, 7));

      NodeItem<DelegateTestExampleClass> strNode = new NodeItem<DelegateTestExampleClass>();

      SwapTest();

      List<int> intList1 = new List<int>();
      AddToList1(intList1);

      DelegateTest();
      DefaultTest();
      OutTest();
    }
  }
}
