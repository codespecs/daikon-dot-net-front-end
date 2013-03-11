    /**
     * A class that contains several sorting routines,
     * implemented as static methods.
     * Arrays are rearranged with smallest item first,
     * using CompareTo.
     * @author Mark Allen Weiss
     */
    public class Sort
    {
        /**
         * Simple insertion sort.
         * @param a an array of System.IComparable items.
         */
        public static void insertionSort( System.IComparable [ ] a )
        {
            int j;

/* 1*/      for( int p = 1; p < a.Length; p++ )
            {
/* 2*/          System.IComparable tmp = a[ p ];
/* 3*/          for( j = p; j > 0 && tmp.CompareTo( a[ j - 1 ] ) < 0; j-- )
/* 4*/              a[ j ] = a[ j - 1 ];
/* 5*/          a[ j ] = tmp;
            }
        }

        /**
         * Shellsort, using Shell's (poor) increments.
         * @param a an array of System.IComparable items.
         */
        public static void shellsort( System.IComparable [ ] a )
        {
            int j;

/* 1*/      for( int gap = a.Length / 2; gap > 0; gap /= 2 )
/* 2*/          for( int i = gap; i < a.Length; i++ )
                {
/* 3*/
                    System.IComparable tmp = a[i];
/* 4*/              for( j = i; j >= gap &&
                                tmp.CompareTo( a[ j - gap ] ) < 0; j -= gap )
/* 5*/                  a[ j ] = a[ j - gap ];
/* 6*/              a[ j ] = tmp;
                }
        }


        /**
         * Standard heapsort.
         * @param a an array of System.IComparable items.
         */
        public static void heapsort(System.IComparable[] a)
        {
/* 1*/      for( int i = a.Length / 2; i >= 0; i-- )  /* buildHeap */
/* 2*/          percDown( a, i, a.Length );
/* 3*/      for( int i = a.Length - 1; i > 0; i-- )
            {
/* 4*/          swapReferences( a, 0, i );            /* deleteMax */
/* 5*/          percDown( a, 0, i );
            }
        }

        /**
         * Internal method for heapsort.
         * @param i the index of an item in the heap.
         * @return the index of the left child.
         */
        private static int leftChild( int i )
        {
            return 2 * i + 1;
        }

        /**
         * Internal method for heapsort that is used in
         * deleteMax and buildHeap.
         * @param a an array of System.IComparable items.
         * @index i the position from which to percolate down.
         * @int n the logical size of the binary heap.
         */
        private static void percDown( System.IComparable [ ] a, int i, int n )
        {
            int child;
            System.IComparable tmp;

/* 1*/      for( tmp = a[ i ]; leftChild( i ) < n; i = child )
            {
/* 2*/          child = leftChild( i );
/* 3*/          if( child != n - 1 && a[ child ].CompareTo( a[ child + 1 ] ) < 0 )
/* 4*/              child++;
/* 5*/          if( tmp.CompareTo( a[ child ] ) < 0 )
/* 6*/              a[ i ] = a[ child ];
                else
/* 7*/              break;
            }
/* 8*/      a[ i ] = tmp;
        }

        /**
         * Mergesort algorithm.
         * @param a an array of System.IComparable items.
         */
        public static void mergeSort( System.IComparable [ ] a )
        {
            System.IComparable [ ] tmpArray = new System.IComparable[ a.Length ];

            mergeSort( a, tmpArray, 0, a.Length - 1 );
        }

        /**
         * Internal method that makes recursive calls.
         * @param a an array of System.IComparable items.
         * @param tmpArray an array to place the merged result.
         * @param left the left-most index of the subarray.
         * @param right the right-most index of the subarray.
         */
        private static void mergeSort( System.IComparable [ ] a, System.IComparable [ ] tmpArray,
                   int left, int right )
        {
            if( left < right )
            {
                int center = ( left + right ) / 2;
                mergeSort( a, tmpArray, left, center );
                mergeSort( a, tmpArray, center + 1, right );
                merge( a, tmpArray, left, center + 1, right );
            }
        }

        /**
         * Internal method that merges two sorted halves of a subarray.
         * @param a an array of System.IComparable items.
         * @param tmpArray an array to place the merged result.
         * @param leftPos the left-most index of the subarray.
         * @param rightPos the index of the start of the second half.
         * @param rightEnd the right-most index of the subarray.
         */
        private static void merge( System.IComparable [ ] a, System.IComparable [ ] tmpArray,
               int leftPos, int rightPos, int rightEnd )
        {
            int leftEnd = rightPos - 1;
            int tmpPos = leftPos;
            int numElements = rightEnd - leftPos + 1;

            // Main loop
            while( leftPos <= leftEnd && rightPos <= rightEnd )
                if( a[ leftPos ].CompareTo( a[ rightPos ] ) <= 0 )
                    tmpArray[ tmpPos++ ] = a[ leftPos++ ];
                else
                    tmpArray[ tmpPos++ ] = a[ rightPos++ ];

            while( leftPos <= leftEnd )    // Copy rest of first half
                tmpArray[ tmpPos++ ] = a[ leftPos++ ];

            while( rightPos <= rightEnd )  // Copy rest of right half
                tmpArray[ tmpPos++ ] = a[ rightPos++ ];

            // Copy tmpArray back
            for( int i = 0; i < numElements; i++, rightEnd-- )
                a[ rightEnd ] = tmpArray[ rightEnd ];
        }

        /**
         * Quicksort algorithm.
         * @param a an array of System.IComparable items.
         */
        public static void quicksort( System.IComparable [ ] a )
        {
            quicksort( a, 0, a.Length - 1 );
        }

        private static int CUTOFF = 3;

        /**
         * Method to swap to elements in an array.
         * @param a an array of objects.
         * @param index1 the index of the first object.
         * @param index2 the index of the second object.
         */
        public static void swapReferences( object [ ] a, int index1, int index2 )
        {
            object tmp = a[ index1 ];
            a[ index1 ] = a[ index2 ];
            a[ index2 ] = tmp;
        }

        /**
         * Return median of left, center, and right.
         * Order these and hide the pivot.
         */
        private static System.IComparable median3( System.IComparable [ ] a, int left, int right )
        {
            int center = ( left + right ) / 2;
            if( a[ center ].CompareTo( a[ left ] ) < 0 )
                swapReferences( a, left, center );
            if( a[ right ].CompareTo( a[ left ] ) < 0 )
                swapReferences( a, left, right );
            if( a[ right ].CompareTo( a[ center ] ) < 0 )
                swapReferences( a, center, right );

                // Place pivot at position right - 1
            swapReferences( a, center, right - 1 );
            return a[ right - 1 ];
        }

        /**
         * Internal quicksort method that makes recursive calls.
         * Uses median-of-three partitioning and a cutoff of 10.
         * @param a an array of System.IComparable items.
         * @param left the left-most index of the subarray.
         * @param right the right-most index of the subarray.
         */
        private static void quicksort( System.IComparable [ ] a, int left, int right )
        {
/* 1*/      if( left + CUTOFF <= right )
            {
/* 2*/          System.IComparable pivot = median3( a, left, right );

                    // Begin partitioning
/* 3*/          int i = left, j = right - 1;
/* 4*/          for( ; ; )
                {
/* 5*/              while( a[ ++i ].CompareTo( pivot ) < 0 ) { }
/* 6*/              while( a[ --j ].CompareTo( pivot ) > 0 ) { }
/* 7*/              if( i < j )
/* 8*/                  swapReferences( a, i, j );
                    else
/* 9*/                  break;
                }

/*10*/          swapReferences( a, i, right - 1 );   // Restore pivot

/*11*/          quicksort( a, left, i - 1 );    // Sort small elements
/*12*/          quicksort( a, i + 1, right );   // Sort large elements
            }
            else  // Do an insertion sort on the subarray
/*13*/          insertionSort( a, left, right );
        }

        /**
         * Internal insertion sort routine for subarrays
         * that is used by quicksort.
         * @param a an array of System.IComparable items.
         * @param left the left-most index of the subarray.
         * @param right the right-most index of the subarray.
         */
        private static void insertionSort( System.IComparable [ ] a, int left, int right )
        {
            for( int p = left + 1; p <= right; p++ )
            {
                System.IComparable tmp = a[ p ];
                int j;

                for( j = p; j > left && tmp.CompareTo( a[ j - 1 ] ) < 0; j-- )
                    a[ j ] = a[ j - 1 ];
                a[ j ] = tmp;
            }
        }

        /**
         * Quick selection algorithm.
         * Places the kth smallest item in a[k-1].
         * @param a an array of System.IComparable items.
         * @param k the desired rank (1 is minimum) in the entire array.
         */     
        public static void quickSelect( System.IComparable [ ] a, int k )
        {
            quickSelect( a, 0, a.Length - 1, k );
        }

        /**
         * Internal selection method that makes recursive calls.
         * Uses median-of-three partitioning and a cutoff of 10.
         * Places the kth smallest item in a[k-1].
         * @param a an array of System.IComparable items.
         * @param left the left-most index of the subarray.
         * @param right the right-most index of the subarray.
         * @param k the desired index (1 is minimum) in the entire array.
         */
        private static void quickSelect( System.IComparable [ ] a, int left,
                                                int right, int k )
        {
/* 1*/      if( left + CUTOFF <= right )
            {
/* 2*/          System.IComparable pivot = median3( a, left, right );

                    // Begin partitioning
/* 3*/          int i = left, j = right - 1;
/* 4*/          for( ; ; )
                {
/* 5*/              while( a[ ++i ].CompareTo( pivot ) < 0 ) { }
/* 6*/              while( a[ --j ].CompareTo( pivot ) > 0 ) { }
/* 7*/              if( i < j )
/* 8*/                  swapReferences( a, i, j );
                    else
/* 9*/                  break;
                }

/*10*/          swapReferences( a, i, right - 1 );   // Restore pivot

/*11*/          if( k <= i )
/*12*/              quickSelect( a, left, i - 1, k );
/*13*/          else if( k > i + 1 )
/*14*/              quickSelect( a, i + 1, right, k );
            }
            else  // Do an insertion sort on the subarray
/*15*/          insertionSort( a, left, right );
        }


        private static int NUM_ITEMS = 1000;
        private static int theSeed = 1;

        private static void checkSort( MyInteger [ ] a )
        {
            for( int i = 0; i < a.Length; i++ )
                if( a[ i ].intValue( ) != i )
                    System.Console.WriteLine( "Error at " + i );
            System.Console.WriteLine("Finished checksort");
        }


        public static void main( string [ ] args )
        {
            MyInteger [ ] a = new MyInteger[ NUM_ITEMS ];
            for( int i = 0; i < a.Length; i++ )
                a[ i ] = new MyInteger( i );

            for( theSeed = 0; theSeed < 20; theSeed++ )
            {
                Random.permute( a );
                Sort.insertionSort( a );
                checkSort( a );

                Random.permute( a );
                Sort.heapsort( a );
                checkSort( a );

                Random.permute( a );
                Sort.shellsort( a );
                checkSort( a );

                Random.permute( a );
                Sort.mergeSort( a );
                checkSort( a );

                Random.permute( a );
                Sort.quicksort( a );
                checkSort( a );

                Random.permute( a );
                Sort.quickSelect( a, NUM_ITEMS / 2 );
                System.Console.WriteLine(a[NUM_ITEMS / 2 - 1].intValue() + " " +
                                  NUM_ITEMS / 2 );
            }
        }
    }
