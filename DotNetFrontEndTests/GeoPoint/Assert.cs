using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GeoPoint
{
    class Assert
    {
        public static void assertTrue(bool test)
        {
            if (!test)
            {
                throw new ArgumentException();
            }
        }

        public static void assertNotNull(object obj)
        {
            if (obj == null)
            {
                throw new ArgumentException();
            }
        }
    }
}
