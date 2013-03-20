namespace LinkedLists
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text;

  public class LinkedLists
  {
    public static void Main(String[] args)
    {
      Node head = new Node(0);
      Console.WriteLine(printLast(head));
      Node n = new Node(1);
      head.next = n;
      Console.WriteLine(printLast(head));
      head.next.next = new Node(2);
      Console.WriteLine(printLast(head));
      head.next.next.next = new Node(3);
      Console.WriteLine(printLast(head));
    }
    public static int printLast(Node n)
    {
      while (n.next != null)
      {
        n = n.next;
      }
      return n.val;
    }
  }

  public class Node
  {
    public Node next;
    public int val;
    public Node(int n)
    {
      this.val = n;
    }
  }
}
