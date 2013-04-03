namespace SikuliExample
{
  using System;

  public class SikuliExample
  {
    public static void Main()
    {
      Console.Write("How many licks? ");
      TootisePop tp = new TootisePop(Int32.Parse(Console.ReadLine()));

      Console.WriteLine("Press enter to lick. ");
      int numLicks = 1;
      while (!tp.Eaten)
      {
        Console.ReadLine();
        tp.Lick();
        Console.WriteLine(numLicks++ + " so far");
      }
    }

  }

  public class TootisePop
  {
    int numLicksToCenter;
    int numLicksSoFar;
    public TootisePop(int numLicksToCenter)
    {
      this.numLicksToCenter = numLicksToCenter;
      this.numLicksSoFar = 0;
    }
    public bool Eaten
    {
      get { return this.numLicksToCenter < this.numLicksSoFar; }
    }
    public bool Lick()
    {
      this.numLicksSoFar++;
      return this.Eaten;
    }
  }
}
