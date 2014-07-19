using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Tests multiple interface implementation, primarily:
// 1. different names for parameters of the same method

namespace MultipleInterface
{
    public class BaseClass
    {
        public virtual int Bar(string arg)
        {
            return 0;
        }
    }

    public interface SameInterface
    {
        bool GreaterThan(int x, int y);
    }

    public interface MyInterface
    {
        bool GreaterThan(int rhs, int lhs);
    }

    public class MyInterfaceImpl : BaseClass, MyInterface, SameInterface
    {
        public override int Bar(string arg)
        {
            return 1;
        }

        public bool GreaterThan(int a, int b)
        {
            return a > b;
        }
    }

    public class OtherInterfaceImpl : MyInterface
    {
        public delegate int MyAction(int foo);

        public event MyAction MyEvent;

        public bool GreaterThan(int a, int b)
        {
			if (MyEvent != null)
			{
				MyEvent(a);
			}
            
            return a > b;
        }   
    }

    public class Program
    {
        public static void DoSomething(MyInterface i, MyInterfaceImpl c, OtherInterfaceImpl o)
        {
            i.GreaterThan(3, 4);
            c.GreaterThan(6, 2);
            o.GreaterThan(10, 10);
        }
        
        public static void Main(string[] args)
        {
            DoSomething(new MyInterfaceImpl(), new MyInterfaceImpl(), new OtherInterfaceImpl());
        }
    }
}
