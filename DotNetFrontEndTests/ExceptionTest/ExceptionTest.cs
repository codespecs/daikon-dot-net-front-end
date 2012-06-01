using System;

namespace ExceptionTest
{

    public class ExceptionTest
    {
        public static void Main(String[] args)
        {
            bool square = false;
            int number = 4;
            // Test static exception try/catch/rethrow handling
            Console.WriteLine(Square(square, number));

            // Test class based try/catch/rethrow handling
            CubingClass c = new CubingClass(5);
            Console.WriteLine(c.DoCube());

            c = null;
            try
            {
                c.DoCube();
            }
            catch (NullReferenceException ex)
            {
                Console.WriteLine("Caught null reference exception");
            }

            try
            {
                throw new ArgumentException();
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("Argument exception caught locally");
            }

            try
            {
                throw new ArgumentException();
            }
            catch (ArgumentNullException ex)
            {
                Console.WriteLine("Argument exception caught incorrectly");
            }
            catch (Exception ex)
            {
                Console.WriteLine("General exception caught");
            }

            try
            {
                throw new ArgumentException();
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("Argument exception caught locally");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine("General exception caught");
            }
        }

        public static int Square(bool square, int n)
        {
            try
            {
                if (square)
                {
                    return n * n;
                }
                return n;
            }
            catch
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Takes a number, can cube it or not
    /// </summary>
    public class CubingClass
    {
        int val;
        public CubingClass(int val)
        {
            this.val = val;
        }

        public int DoCube()
        {
            return val * val * val;
        }

        public int DoNotCube()
        {
            return val;
        }
    }
}