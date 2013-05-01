// StackAr class
//
// CONSTRUCTION: with or without a capacity; default is 10
//
// ******************PUBLIC OPERATIONS*********************
// void push( x )         --> Insert x
// void pop( )            --> Remove most recently inserted item
// object top( )          --> Return most recently inserted item
// object topAndPop( )    --> Return and remove most recently inserted item
// bool isEmpty( )     --> Return true if empty; else false
// bool isFull( )      --> Return true if full; else false
// void makeEmpty( )      --> Remove all items
// ******************ERRORS********************************
// Overflow and Underflow thrown as needed

/**
 * Array-based implementation of the stack.
 * @author Mark Allen Weiss
 */
public sealed class StackAr
{

    /**
     * Construct the stack.
     * @param capacity the capacity.
     */
    public StackAr(int capacity)
    {
        theArray = new object[capacity];
        topOfStack = -1;
    }

    /**
     * Test if the stack is logically empty.
     * @return true if empty, false otherwise.
     * @observer // annotation added by Jeremy
     */
    public bool isEmpty()
    {
        return topOfStack == -1;
    }

    /**
     * Test if the stack is logically full.
     * @return true if full, false otherwise.
     * @observer // annotation added by Jeremy
     */
    public bool isFull()
    {
        return topOfStack == theArray.Length - 1;
    }

    /**
     * Make the stack logically empty.
     */
    public void makeEmpty()
    {
        for (int i = 0; i < topOfStack + 1; i++) { theArray[i] = null; }
        topOfStack = -1;
    }

    /**
     * Get the most recently inserted item in the stack.
     * Does not alter the stack.
     * @return the most recently inserted item in the stack, or null, if empty.
     * @observer // annotation added by Jeremy
     */
    public object top()
    {
        if (isEmpty())
            return null;
        return theArray[topOfStack];
    }

    /**
     * Remove the most recently inserted item from the stack.
     * @exception Underflow if stack is already empty.
     */
    public void pop()
    {
        if (isEmpty())
            throw new Underflow();
        theArray[topOfStack--] = null;
    }

    /**
     * Insert a new item into the stack, if not already full.
     * @param x the item to insert.
     * @exception Overflow if stack is already full.
     */
    public void push(object x)
    {
        if (isFull())
            throw new Overflow();
        theArray[++topOfStack] = x;
    }

    /**
     * Return and remove most recently inserted item from the stack.
     * @return most recently inserted item, or null, if stack is empty.
     */
    public object topAndPop()
    {
        if (isEmpty())
            return null;
        object topItem = top();
        theArray[topOfStack--] = null;
        return topItem;
    }

    private object[] theArray;
    private int topOfStack;

}
