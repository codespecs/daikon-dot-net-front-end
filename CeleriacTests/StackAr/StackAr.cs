public class StackArTester
{
    private static StackAr s = new StackAr(0);
    private static System.Random rnd = new System.Random(1000);

    public static void doNew(int size)
    {
        s = new StackAr(size); s.makeEmpty();
        observe();
        topOrPop();
    }

    public static void push()
    {
        try
        {
            s.push(rnd.Next(2) == 1 ? new object() : new MyInteger(0));
        }
        catch (Overflow) { }
        observe();
    }

    public static void topOrPop()
    {
        if (s.isEmpty() || rnd.Next(2) == 1) s.topAndPop();
        else try { s.pop(); }
            catch (Underflow) { }
        observe();
    }

    public static void observe()
    {
        s.isFull();
        s.isEmpty();
        s.top();
    }

    public static void fill(int n)
    {
        doNew(n);
        for (int i = 0; i < n; i++)
            push();
        if (rnd.Next(2) == 1)
            s.makeEmpty();
        while (!s.isEmpty())
            topOrPop();
        s.makeEmpty();
        observe();

        doNew(n);
        for (int i = 0; i <= n / 2; i++)
        {
            try
            {
                s.push(s);
                observe();
            }
            catch (Overflow) { }
        }
        s.makeEmpty();
    }

    public static void Main(string[] args)
    {
        //doNew(0);
        fill(4);
        /*
        for (int i = 0; i < 4; i++)
        {
            doNew(0);
            fill(i);
            fill(10);
            fill(20);
        }
        //*/
    }
}
