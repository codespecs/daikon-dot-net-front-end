using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Issue110
{
    class Program
    {
        static void Main(string[] args)
        {
            int result;
            Console.WriteLine(result = Calculator.add(2, 3));
            Console.WriteLine(result = Calculator.multiply(2, 3));
            Console.WriteLine(result = Calculator.divide(10, 2));
            Console.WriteLine(result = Calculator.Square(2));
        }
    }
    static class Calculator
    {
        public static int add(int a, int b)
        {
            return a + b;
        }
        public static int multiply(int a, int b)
        {
            return a * b;
        }
        public static int divide(int a, int b)
        {
            return a / b;
        }
        public static int Square(int a)
        {
            return a * a;
        }
    }
}
