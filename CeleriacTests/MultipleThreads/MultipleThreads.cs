using System;
using System.Threading;

class C
{
  int i;
  public C(int i) { this.i = i; }
  public void run()
  {
    Console.WriteLine("Thread " + i + " says hi");
    Console.WriteLine("Thread " + i + " says bye");
  }
}

class M
{
  public static void Main(String[] args)
  {
    Thread[] threads = new Thread[20];
    for (int i = 0; i < 20; ++i)
    {
      C c = new C(i);
      Thread t = new Thread(c.run);
      threads[i] = t;
      t.Start();
    }
    Console.WriteLine("Program Complete. Press enter to exit");
  }
}