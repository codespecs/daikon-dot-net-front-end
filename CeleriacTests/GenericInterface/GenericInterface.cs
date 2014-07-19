using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics.Contracts;

namespace GenericInterface
{
    public interface IFixedList
    {
        /// <summary>
        /// IFixedList: the current length of the list
        /// </summary>
        int Length { get; }

        /// <summary>
        /// IFixedList: add an element to the list
        /// </summary>
        void Add(object o);
    }

    [ContractClass(typeof(IFixedListContract<>))]
    public interface IFixedList<T> where T : struct
    {
        /// <summary>
        /// IFixedList`1: the current length of the list
        /// </summary>
        int Length { get; }

        /// <summary>
        /// IFixedList`1: the maximum length of the list
        /// </summary>
        int MaxLength { get; }

        /// <summary>
        /// IFixedList`1: and an element to the list
        /// </summary>
        void Add(T o);
    }

    [ContractClassFor(typeof(IFixedList<>))]
    public abstract class IFixedListContract<T> : IFixedList<T> where T : struct
    {

        public int Length
        {
            get { return default(int); }
        }

        public int MaxLength
        {
            get { return default(int); }
        }

        public void Add(T o)
        {
            
        }
    }

    public class FixedList : IFixedList
    {           
        private object[] data;
        private int cnt = 0;

        public FixedList(int size)
        {
            data = new object[size];
        }

        public int Length
        {
            get
            {
                return cnt;
            }
        }

        public void Add(object o)
        {
            data[cnt++] = o;
        }
    }

    public class FixedList<T> : IFixedList<T> where T : struct
    {
        private T[] data;
        private int cnt = 0;

        public FixedList(int size)
        {
            data = new T[size];
        }

        public int Length
        {
            get
            {
                return cnt;
            }
        }

        public int MaxLength
        {
            get
            {
                return data.Length;
            }
        }

        public void Add(T o)
        {
            data[cnt++] = o;
        }
    }

    public class Driver
    {
        public static void Main()
        {
            var x = new FixedList(10);
            x.Add(1);
            x.Add(new double[] { 10 });

            var y = new FixedList<double>(10);
            y.Add(1.0);
            y.Add(2.0);
            y.Add(3.0);
        }
    }
}
